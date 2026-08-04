using System.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests;

/// <summary>
/// Characterizes the deadlock that decides how AudioRouter tears a half-dead route down.
///
/// Read the scope honestly: these tests exercise SelfJoiningDevice against ControlUiDispatcher, not
/// AudioRouter. Reverting AudioRouter.RequestTeardown to an inline Stop() would leave them green.
/// They document why the indirection exists and prove the dispatcher is the right instrument for it;
/// they are not a regression guard on the call site. Guarding that would mean injecting fakes for
/// WasapiCapture and WasapiOut, which is a seam Stage 1 owns.
///
/// The hazard, measured against NAudio 2.2.1 rather than reasoned about: WasapiOut captures
/// SynchronizationContext.Current in its constructor, and when that was null RaisePlaybackStopped
/// invokes the handler directly on the play thread. WasapiOut.Dispose calls Stop, which does
/// playThread.Join() whenever playbackState is not already Stopped - which PlayThread leaves it on
/// every abnormal exit, so on the failure path that is a thread joining itself. A probe against the
/// real library parked there and never came back.
///
/// Reproducing that with real NAudio would need an audio endpoint, so these tests use the smallest
/// object with the same two properties.
/// </summary>
public sealed class TeardownMarshallingTests
{
    // Long enough that a slow machine cannot fail the positive case, short enough that the negative
    // case does not dominate the suite. Parallelization is disabled assembly-wide, so this is paid
    // serially.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    /// <summary>
    /// WasapiOut reduced to the two properties that make disposal from its own stopped event a
    /// deadlock: the event is raised on the worker thread, and disposal joins that worker.
    /// </summary>
    private sealed class SelfJoiningDevice
    {
        private readonly Thread _worker;
        private readonly ManualResetEventSlim _handlerReturned = new(false);

        // Volatile because the timeout path reads it without ever waiting on the event above, so
        // there is no happens-before edge to inherit.
        private volatile Exception? _handlerFault;

        public event Action? Stopped;

        public SelfJoiningDevice()
        {
            _worker = new Thread(() =>
            {
                try
                {
                    // The equivalent of RaisePlaybackStopped with a null SynchronizationContext: the
                    // handler runs here, on the very thread Dispose is about to join.
                    Stopped?.Invoke();
                }
                catch (Exception ex)
                {
                    // Recorded, never rethrown. An exception escaping a thread delegate tears the
                    // whole test process down, and the case that would throw here - a self-join that
                    // starts throwing instead of blocking - is exactly the "this fake no longer
                    // reproduces NAudio" case these tests exist to catch. Catching it has to make the
                    // suite red, not kill it; the same reason the waits below are bounded.
                    _handlerFault = ex;
                }

                _handlerReturned.Set();
            })
            {
                IsBackground = true,
            };
        }

        /// <summary>Whatever the stopped handler threw, for a failure message to name.</summary>
        public Exception? HandlerFault => _handlerFault;

        public void Start() => _worker.Start();

        /// <summary>WasapiOut.Dispose -> Stop -> playThread.Join(). From the worker it never returns.</summary>
        public void Dispose() => _worker.Join();

        /// <summary>
        /// False means the handler is still stuck. Bounded on purpose: a hung suite is worse than a
        /// red one, so a deadlock has to be asserted rather than suffered.
        /// </summary>
        public bool WaitForHandler(TimeSpan timeout) => _handlerReturned.Wait(timeout);
    }

    [Fact]
    public void DisposingInlineFromTheStoppedHandlerDeadlocks()
    {
        var device = new SelfJoiningDevice();

        // The shape AudioRouter would have had if the handlers called Stop() directly.
        device.Stopped += () => device.Dispose();
        device.Start();

        bool returned = device.WaitForHandler(Patience);

        // Asserting the bug, which is what gives the test below its teeth: if this ever starts
        // returning, the fake has stopped reproducing NAudio's hazard and the guard proves nothing.
        // The fault is named in the message because a self-join that throws is the likeliest way for
        // that to happen, and an unexplained "expected False" would send the next reader hunting.
        Assert.False(
            returned,
            "the naive shape must still deadlock, or the test below is vacuous. Handler fault: " +
            (device.HandlerFault?.ToString() ?? "none - the join simply returned"));
    }

    [Fact]
    public void PostingTheDisposalThroughTheDispatcherDoesNotDeadlock()
    {
        StaThread.Run(() =>
        {
            using var dispatcher = new ControlUiDispatcher();
            var device = new SelfJoiningDevice();
            bool disposed = false;

            device.Stopped += () => dispatcher.Post(() =>
            {
                device.Dispose();
                disposed = true;
            });

            device.Start();

            // The whole point of the indirection: Post only queues, so the worker is free to run to
            // completion and the join that follows finds a thread that has already exited.
            Assert.True(
                device.WaitForHandler(Patience),
                "Post must not run the teardown on the thread that raised the event");

            // A handler that threw would also have "returned" above, and the assert below would then
            // fail for a reason that has nothing to do with marshalling.
            Assert.Null(device.HandlerFault);

            PumpUntil(() => disposed, Patience);

            Assert.True(disposed, "the queued teardown never ran");
        });
    }

    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();

        while (!done() && elapsed.Elapsed < timeout)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }
}
