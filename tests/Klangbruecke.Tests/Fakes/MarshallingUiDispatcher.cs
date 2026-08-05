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
    /// <summary>
    /// The thread itself, not its id. Managed thread ids are unique only among <em>live</em> threads
    /// and are reused once one exits, so a comparison against a recorded id can answer "yes, you are
    /// the UI thread" to a threadpool thread that inherited the number - which would run
    /// <c>ApplyEndpointPresence</c> off the test thread, the exact hazard this class exists to
    /// prevent, intermittently and in whichever test happened to be running.
    /// </summary>
    private readonly Thread _uiThread = Thread.CurrentThread;

    private readonly ConcurrentQueue<Action> _queued = new();
    private int _posts;

    /// <summary>Every <see cref="Post"/>, whichever thread it came from.</summary>
    public int Posts => Volatile.Read(ref _posts);

    /// <summary>Something has been posted from another thread and is waiting to be run.</summary>
    public bool HasQueuedWork => !_queued.IsEmpty;

    public void Post(Action action)
    {
        if (ReferenceEquals(Thread.CurrentThread, _uiThread))
        {
            Interlocked.Increment(ref _posts);
            action();
            return;
        }

        // Enqueued before it is counted, and the order is the whole of it: counted first, a test that
        // waits on Posts and then drains can find the queue still empty, drain nothing, and go on to
        // assert against work that has not run. Enqueued first, every count a test can observe has
        // work behind it.
        _queued.Enqueue(action);
        Interlocked.Increment(ref _posts);
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
