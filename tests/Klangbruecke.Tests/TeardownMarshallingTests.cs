using System.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests;

/// <summary>
/// The regression guard for the deadlock that decides how AudioRouter tears a half-dead route down.
///
/// AudioRouter.RequestTeardown posts through IUiDispatcher instead of calling Stop() from the
/// stopped-event handler. That indirection looks like ceremony and would be the first thing a later
/// change removes, so the hazard it answers is pinned here rather than left to a comment.
///
/// The hazard, measured against NAudio 2.2.1 rather than reasoned about: WasapiOut captures
/// SynchronizationContext.Current in its constructor, and when that was null RaisePlaybackStopped
/// invokes the handler directly on the play thread. WasapiOut.Dispose calls Stop, which does
/// playThread.Join() whenever playbackState is not already Stopped - and PlayThread assigns Stopped
/// only on its normal-completion path, never from the catch, so on the failure path that is a thread
/// joining itself. A probe against the real library parked there and never came back.
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

        public event Action? Stopped;

        public SelfJoiningDevice()
        {
            _worker = new Thread(() =>
            {
                // The equivalent of RaisePlaybackStopped with a null SynchronizationContext: the
                // handler runs here, on the very thread Dispose is about to join.
                Stopped?.Invoke();
                _handlerReturned.Set();
            })
            {
                IsBackground = true,
            };
        }

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

        // Asserting the bug, which is what gives the test below its teeth: if this ever starts
        // returning, the fake has stopped reproducing NAudio's hazard and the guard proves nothing.
        Assert.False(
            device.WaitForHandler(Patience),
            "the naive shape must still deadlock, or the test below is vacuous");
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
