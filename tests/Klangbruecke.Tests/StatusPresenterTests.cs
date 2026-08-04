using Klangbruecke.Diagnostics;
using Klangbruecke.Tests.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests;

/// <summary>Captures the posted action instead of running it, so "did it go through Post?" is observable.</summary>
file sealed class DeferringUiDispatcher : IUiDispatcher
{
    public Action? Captured { get; private set; }

    public void Post(Action action) => Captured = action;
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

        ui.Captured!();

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

        ui.Captured!();

        Assert.Equal("connected", presenter.Last);
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
}
