namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// Runs the action inline, like <see cref="ImmediateUiDispatcher"/>, and counts it.
///
/// The count is the only way to assert the contract that makes <c>ConnectionManager</c> a
/// single-threaded class with no locks in it: every inbound event - a watcher edge, a connection
/// state, an endpoint notification, a stopped route, a resume - is posted before any state is
/// touched. A handler that touched state directly would still pass every behavioural test in the
/// suite, on the test's own thread, and then deadlock or tear on the WASAPI play thread.
///
/// Public, in Fakes, and not <c>file</c>-scoped, following every other double in here.
/// </summary>
public sealed class CountingUiDispatcher : IUiDispatcher
{
    public int Posts { get; private set; }

    public void Post(Action action)
    {
        Posts++;
        action();
    }
}
