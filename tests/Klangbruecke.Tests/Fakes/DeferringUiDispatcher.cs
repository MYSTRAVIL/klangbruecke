namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// Captures posted actions instead of running them, and runs them only when a test says so.
///
/// This is the only dispatcher that can express the race <see cref="Klangbruecke.Audio.AudioRouter"/>
/// is built around: a teardown that was posted while one route was alive and arrives after another
/// has started. <see cref="ImmediateUiDispatcher"/> closes that window by construction - the teardown
/// runs before the raise returns - so a test using it cannot tell a router that checks for the race
/// from one that does not.
///
/// Public, in Fakes, and not <c>file</c>-scoped, so more than one test class can reach it.
/// </summary>
public sealed class DeferringUiDispatcher : IUiDispatcher
{
    /// <summary>Everything posted and not yet drained, oldest first.</summary>
    public List<Action> Captured { get; } = new();

    public void Post(Action action) => Captured.Add(action);

    /// <summary>
    /// Runs everything captured so far, in order, and returns how many ran.
    ///
    /// One bounded batch: an action that posts during the drain is captured for the next call rather
    /// than run by this one. That matches <see cref="FakeScheduler.Advance"/>, and it means a router
    /// that re-posted itself forever would fail a test on the count instead of hanging the suite.
    /// </summary>
    public int Drain()
    {
        Action[] due = Captured.ToArray();
        Captured.Clear();

        foreach (Action action in due)
        {
            action();
        }

        return due.Length;
    }
}
