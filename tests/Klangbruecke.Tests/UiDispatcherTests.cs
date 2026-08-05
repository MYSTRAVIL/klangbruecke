using Xunit;

namespace Klangbruecke.Tests;

public sealed class UiDispatcherTests
{
    // Moved to StaThread when TeardownMarshallingTests needed the same thing; kept as a local alias
    // so the bodies below read unchanged.
    private static void OnStaThread(Action action) => StaThread.Run(action);

    [Fact]
    public void Immediate_RunsTheActionSynchronously()
    {
        bool ran = false;

        new ImmediateUiDispatcher().Post(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public void Control_RunsSynchronouslyOnTheOwningThread()
    {
        OnStaThread(() =>
        {
            using var dispatcher = new ControlUiDispatcher();
            bool ran = false;

            dispatcher.Post(() => ran = true);

            Assert.True(ran);
        });
    }

    /// <summary>
    /// Posts from a worker thread and returns once the post has definitely been queued. The join
    /// matters: BeginInvoke has posted its window message by the time it returns, so a later pump
    /// on the owning thread is guaranteed to see it and the callers below cannot race.
    ///
    /// The join is also the one hazard here. Joining from an STA thread waits via
    /// CoWaitForMultipleHandles without COWAIT_DISPATCH_WINDOW_MESSAGES, so it does not pump - which
    /// is what makes the callers deterministic, but it also means that if Post were ever changed to
    /// a synchronous Invoke, the worker would block on a pump that is not running and this would
    /// deadlock rather than fail. A hung suite is worse than a red one: if these tests ever stop
    /// terminating, suspect Post, not the tests.
    /// </summary>
    private static void PostFromAnotherThread(IUiDispatcher dispatcher, Action action)
    {
        var worker = new Thread(() => dispatcher.Post(action));
        worker.Start();
        worker.Join();
    }

    [Fact]
    public void Control_RunsAPostFromAnotherThreadOnTheOwningThread()
    {
        OnStaThread(() =>
        {
            using var dispatcher = new ControlUiDispatcher();
            int owningThread = Environment.CurrentManagedThreadId;
            int ranOn = 0;

            PostFromAnotherThread(dispatcher, () => ranOn = Environment.CurrentManagedThreadId);
            Application.DoEvents();

            // Covers both halves on its own: an action that ran inline on the worker records the
            // worker's id, not the owning thread's, so this fails either way and a pre-pump assert
            // would add nothing.
            Assert.Equal(owningThread, ranOn);
        });
    }

    [Fact]
    public void Control_DropsAQueuedPostOnDisposal()
    {
        OnStaThread(() =>
        {
            var dispatcher = new ControlUiDispatcher();
            bool ran = false;

            PostFromAnotherThread(dispatcher, () => ran = true);
            dispatcher.Dispose();
            Application.DoEvents();

            // Destroying the handle completes queued entries without invoking them. TrayContext
            // leans on this to dispose the dispatcher before the tray icon its actions write to.
            Assert.False(ran);
        });
    }

    [Fact]
    public void Control_DoesNotThrowAfterDisposal()
    {
        OnStaThread(() =>
        {
            var dispatcher = new ControlUiDispatcher();
            dispatcher.Dispose();

            // The action must be dropped, not run: a disposed dispatcher has no UI thread left
            // to marshal onto.
            Assert.Null(Record.Exception(
                () => dispatcher.Post(() => throw new InvalidOperationException("must not run"))));
        });
    }

    [Fact]
    public void Control_DoesNotRunTheActionAfterDisposal()
    {
        OnStaThread(() =>
        {
            var dispatcher = new ControlUiDispatcher();
            dispatcher.Dispose();
            bool ran = false;

            dispatcher.Post(() => ran = true);

            // Asserted separately from the no-throw case above, which a Post that ran the action
            // and swallowed its exception would also satisfy.
            Assert.False(ran);
        });
    }

    /// <summary>
    /// The premise the whole single-threaded design rests on, and the one a comment in
    /// <c>IPowerNotifier</c> used to get wrong.
    ///
    /// Constructing any <see cref="Control"/> calls <c>WindowsFormsSynchronizationContext.InstallIfNeeded</c>,
    /// so the WinForms context is current from the moment this dispatcher exists - which in
    /// <c>Program.Main</c> is <b>before</b> <c>Application.Run</c> is entered, not after. Two things
    /// downstream depend on exactly that and on nothing else:
    ///
    /// <list type="number">
    /// <item><c>ConnectionManager</c> has no <c>ConfigureAwait</c> anywhere, so every one of its awaits
    /// resumes on whatever context was current when it was reached. Started on this thread, that is
    /// this context, and the no-locks contract holds. With no context installed the continuations
    /// would land on the threadpool and four state machines would be racing with nothing to show for
    /// it.</item>
    /// <item><c>SystemEvents</c> captures the context current at <c>+=</c> time, so a
    /// <c>PowerNotifier.Start()</c> reached from a constructor that runs before <c>Application.Run</c>
    /// still lands its <c>Resumed</c> on the UI thread. The "before Run gives the SystemEvents thread"
    /// shorthand was false for this app for precisely this reason.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Control_InstallsTheWinFormsSynchronizationContextOnTheThreadThatBuildsIt()
    {
        OnStaThread(() =>
        {
            // A freshly started thread has none. Asserted rather than assumed, because without it a
            // context installed by something else entirely would satisfy the check below.
            Assert.Null(SynchronizationContext.Current);

            using var dispatcher = new ControlUiDispatcher();

            Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);
        });
    }

    [Fact]
    public void Control_LetsExceptionsFromTheActionEscape()
    {
        OnStaThread(() =>
        {
            using var dispatcher = new ControlUiDispatcher();

            // The inline path must not swallow: InvalidOperationException from a tray write is the
            // exact failure this dispatcher exists to remove, so it has to stay visible if it returns.
            Assert.Throws<InvalidOperationException>(
                () => dispatcher.Post(() => throw new InvalidOperationException("boom")));
        });
    }
}
