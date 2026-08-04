using Klangbruecke.Diagnostics;
using Klangbruecke.Tests.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests;

/// <summary>
/// Captures posted actions instead of running them, so "did it go through Post?" is observable at
/// all - no assertion on the sink alone can distinguish a posted write from a direct one.
///
/// Every action is kept, not just the latest: a double that overwrote would let a multi-Show test
/// silently exercise only its final call and prove less than it appeared to.
/// </summary>
file sealed class DeferringUiDispatcher : IUiDispatcher
{
    public List<Action> Captured { get; } = new();

    public void Post(Action action) => Captured.Add(action);
}

/// <summary>Stands in for a dispatcher disposed mid-shutdown, which drops what it is handed.</summary>
file sealed class DroppingUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
    }
}

public sealed class StatusPresenterTests
{
    private static StatusPresenter Presenter(IUiDispatcher ui, out List<string> written)
    {
        var captured = new List<string>();
        written = captured;

        return new StatusPresenter(ui, captured.Add);
    }

    [Fact]
    public void Show_ReachesTheSinkOnlyThroughTheDispatcher()
    {
        var ui = new DeferringUiDispatcher();
        StatusPresenter presenter = Presenter(ui, out List<string> written);

        presenter.Show("connected");

        // The regression guard for the whole task: writing the tooltip without going through Post
        // is a cross-thread tray touch, and it would show up here as a sink call that has already
        // happened while the posted action is still sitting unrun.
        Assert.Empty(written);

        Assert.Single(ui.Captured);
        ui.Captured[0]();

        Assert.Equal(new[] { "Klangbruecke: connected" }, written);
    }

    [Fact]
    public void Show_CapsTheComposedString()
    {
        StatusPresenter presenter = Presenter(new ImmediateUiDispatcher(), out List<string> written);

        presenter.Show(new string('x', 500));

        Assert.Equal(96, written[0].Length);
        Assert.StartsWith("Klangbruecke: ", written[0]);
        Assert.EndsWith("...", written[0]);
    }

    [Theory]
    // 82 composes to exactly 96 and is left alone; 83 is the first length that has to be cut, and
    // cutting it must land back on 96 rather than overshooting the way the pre-review version did.
    [InlineData(82, 96)]
    [InlineData(83, 96)]
    [InlineData(500, 96)]
    [InlineData(0, 14)]
    public void Show_NeverExceedsTheCap(int messageLength, int expected)
    {
        StatusPresenter presenter = Presenter(new ImmediateUiDispatcher(), out List<string> written);

        presenter.Show(new string('x', messageLength));

        Assert.Equal(expected, written[0].Length);
    }

    [Fact]
    public void Show_LeavesAShortMessageWhole()
    {
        StatusPresenter presenter = Presenter(new ImmediateUiDispatcher(), out List<string> written);

        presenter.Show("Disconnected.");

        Assert.Equal("Klangbruecke: Disconnected.", written[0]);
    }

    [Fact]
    public void Last_ChangesOnlyWhenThePostRuns()
    {
        var ui = new DeferringUiDispatcher();
        StatusPresenter presenter = Presenter(ui, out _);

        presenter.Show("connected");

        // The menu reads this on the UI thread. Assigning it before the post would let the menu and
        // the tooltip disagree, and would write it from whichever thread raised the status.
        Assert.Equal("Idle", presenter.Last);

        ui.Captured[0]();

        Assert.Equal("connected", presenter.Last);
    }

    [Fact]
    public void Show_PostsOncePerCall()
    {
        var ui = new DeferringUiDispatcher();
        StatusPresenter presenter = Presenter(ui, out List<string> written);

        presenter.Show("first");
        presenter.Show("second");

        Assert.Equal(2, ui.Captured.Count);

        foreach (Action post in ui.Captured)
        {
            post();
        }

        // Both calls survive and keep their order. A dispatcher double that held only the latest
        // action would drop the first silently, which is the weakness this asserts against.
        Assert.Equal(new[] { "Klangbruecke: first", "Klangbruecke: second" }, written);
        Assert.Equal("second", presenter.Last);
    }

    [Fact]
    public void Show_LogsEvenWhenThePostIsDropped()
    {
        ILog original = Log.Current;
        var recording = new RecordingLog();
        Log.Current = recording;

        try
        {
            StatusPresenter presenter = Presenter(new DroppingUiDispatcher(), out List<string> written);

            presenter.Show("Disconnected.");

            // NAudio's RecordingStopped fires while the router is being disposed, so its post is
            // dropped by the dispatcher shutting down. The synchronous log line is then the only
            // record that it happened at all - logging inside the post would lose exactly the
            // messages worth keeping.
            Assert.Empty(written);
            Assert.Equal((LogLevel.Info, "Disconnected."), (recording.Entries[0].Level, recording.Entries[0].Message));
        }
        finally
        {
            Log.Current = original;
        }
    }

    private static (LogLevel Level, string Message) LogOf(Action<StatusPresenter> show)
    {
        ILog original = Log.Current;
        var recording = new RecordingLog();
        Log.Current = recording;

        try
        {
            show(new StatusPresenter(new ImmediateUiDispatcher(), _ => { }));

            return (recording.Entries[0].Level, recording.Entries[0].Message);
        }
        finally
        {
            Log.Current = original;
        }
    }

    // The whole point of StatusMessage. Every component status used to reach the file at Info, so a
    // route that failed to start logged the throw at Error with a stack and then, one line later, the
    // same event at Info - and a reader grepping [ERR] got half the story. The presenter cannot infer
    // this; the component has to carry it.
    [Theory]
    [InlineData(LogLevel.Info)]
    [InlineData(LogLevel.Warn)]
    [InlineData(LogLevel.Error)]
    public void Show_LogsAtTheLevelTheComponentSupplied(LogLevel level)
    {
        Assert.Equal(
            (level, "capture stopped"),
            LogOf(presenter => presenter.Show(new StatusMessage("capture stopped", level))));
    }

    // A status with no level stated is ordinary progress. Pinned because the alternative - defaulting
    // to Warn "to be safe" - is what makes a severity column meaningless in the other direction.
    [Fact]
    public void Show_DefaultsToInfoWhenNoLevelIsGiven()
    {
        Assert.Equal((LogLevel.Info, "connected"), LogOf(presenter => presenter.Show("connected")));
    }

    // The tooltip is capped at 96 characters; the log is not. Messages are long precisely when they
    // carry the detail worth keeping - an exception message, a policy explanation - so logging the
    // composed tooltip would truncate exactly the entries a failure is diagnosed from.
    [Fact]
    public void Show_LogsTheWholeMessageEvenThoughTheTooltipIsCut()
    {
        string long_ = new('x', 500);

        Assert.Equal((LogLevel.Error, long_), LogOf(presenter => presenter.Show(long_, LogLevel.Error)));
    }
}
