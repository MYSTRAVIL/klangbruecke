using System.Collections.Concurrent;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// <see cref="ControlUiDispatcher"/>'s shape, with a test thread standing in for the message loop:
/// inline when the caller is already on it, queued for <see cref="Drain"/> when it is not.
///
/// This exists because <c>ConnectionManager</c> has exactly one piece of work it deliberately runs
/// off the UI thread - the 152-282 ms endpoint enumeration - and its answer comes back through
/// <see cref="IUiDispatcher.Post"/> from a threadpool thread. Under
/// <see cref="ImmediateUiDispatcher"/> that answer would be applied <em>on the threadpool thread</em>,
/// waking a half and republishing state concurrently with the test that is asserting about it: the
/// suite would be certifying a single-threaded contract while breaking it, and the flakiness would
/// land on whoever wrote the next presence-changing test rather than on whoever caused it.
///
/// So a test drives everything else exactly as it did before - events raised on the test thread run
/// inline - and pumps the one asynchronous answer explicitly. Everything the manager does still
/// happens on one thread; the only thing that crosses is a bool.
///
/// <see cref="Posts"/> is also what pins the contract that every inbound event is posted before any
/// state is touched.
/// </summary>
public sealed class MarshallingUiDispatcher : IUiDispatcher
{
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<Action> _queued = new();
    private int _posts;

    /// <summary>Every <see cref="Post"/>, whichever thread it came from.</summary>
    public int Posts => Volatile.Read(ref _posts);

    /// <summary>Something has been posted from another thread and is waiting to be run.</summary>
    public bool HasQueuedWork => !_queued.IsEmpty;

    public void Post(Action action)
    {
        Interlocked.Increment(ref _posts);

        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            action();
            return;
        }

        _queued.Enqueue(action);
    }

    /// <summary>
    /// Runs what has arrived from other threads, on the caller's thread, and reports how many ran.
    ///
    /// One bounded batch, as <see cref="DeferringUiDispatcher.Drain"/> is: work posted during the
    /// drain waits for the next call rather than being collected by this one, so a manager that
    /// re-posted itself forever fails a test on the count instead of hanging the suite.
    /// </summary>
    public int Drain()
    {
        int available = _queued.Count;
        int ran = 0;

        while (ran < available && _queued.TryDequeue(out Action? action))
        {
            action();
            ran++;
        }

        return ran;
    }
}
