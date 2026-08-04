using Xunit;

namespace Klangbruecke.Tests;

public sealed class UiDispatcherTests
{
    /// <summary>
    /// WinForms controls want an STA thread. xunit v2 gives tests an MTA thread and has no
    /// built-in way to change that, so the STA thread is created explicitly here rather than
    /// via a custom test framework.
    /// </summary>
    private static void OnStaThread(Action action)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }
    }

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
    /// on the owning thread is guaranteed to see it and the assertions below cannot race.
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

            // Nothing has pumped yet, so an action that has already run went inline on the worker -
            // the whole defect this class exists to remove.
            Assert.Equal(0, ranOn);

            Application.DoEvents();

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
