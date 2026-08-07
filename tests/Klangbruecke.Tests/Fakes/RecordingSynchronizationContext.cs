namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// A <see cref="SynchronizationContext"/> that queues instead of running, so "did the continuation
/// come back to the thread that started it?" is observable at all.
///
/// <b>What this exists to catch.</b> <c>ConnectionManager</c>, <c>MusicHalf</c> and <c>CallsHalf</c>
/// hold four state machines and not one lock, and the whole of what makes that correct is that every
/// <c>await</c> in them resumes on the thread the turn started on. That rests on there being no
/// <c>ConfigureAwait(false)</c> anywhere on those paths - a one-token change that looks like a
/// performance tidy-up, breaks the no-locks contract outright, and, without something like this,
/// leaves the entire suite green: every double answers instantly, so under the default
/// <see cref="SynchronizationContext"/> the continuation runs inline on the test thread either way and
/// no assertion can tell the two apart.
///
/// Installing this makes them tell apart, and in two different ways depending on the await. A seam
/// answered from another thread posts its continuation here when the context was captured and runs it
/// on the answering thread when it was not. An await further up the chain has nothing to answer it
/// from another thread - but installing a custom context also stops the runtime inlining a suppressed
/// continuation at all (<c>AwaitTaskContinuation.IsValidLocationForInlining</c> refuses while one is
/// current), so that one goes to the threadpool. Both are off the turn's own thread, which is what the
/// tests assert on.
///
/// <b>What it does not cover:</b> three awaits that are the last one in their turn, so their whole
/// continuation is that turn's tail - <c>EnforceConnectPermission</c> and a <c>Publish</c> that
/// recomputes from scratch. Both are level-triggered and idempotent by design, and by the time the
/// tail runs the halves have already announced whatever they changed. See the map in
/// <c>ConnectionManagerTests</c>, which names all three.
///
/// <b>Not thread-safe by accident.</b> <see cref="Post"/> is called from whichever thread completes
/// the work, and <see cref="Drain"/> from the test thread, so the queue is locked. That is the one
/// place in this suite where a lock is right: the object under test forbids them, this does not
/// pretend to be part of it.
///
/// Public, in Fakes, and not <c>file</c>-scoped, following every other double in here.
/// </summary>
public sealed class RecordingSynchronizationContext : SynchronizationContext
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queued = new();

    /// <summary>How many are queued and not yet run.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _queued.Count;
            }
        }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_gate)
        {
            _queued.Enqueue(() => d(state));
        }
    }

    /// <summary>
    /// <see cref="Post"/>'s blocking twin, and it queues rather than running inline for the same
    /// reason: a continuation that reached this object at all is the fact being measured, and running
    /// one here on the caller's thread would be the very thing the assertion is looking for.
    ///
    /// <b>That breaks <see cref="SynchronizationContext.Send"/>'s contract, which is to have completed
    /// the callback by the time it returns</b>, and it is deliberate rather than an oversight - but a
    /// caller cannot tell. Nothing on the paths these tests drive uses <c>Send</c>: an <c>await</c>
    /// continuation is always <c>Post</c>, and the app's own marshalling goes through
    /// <c>IUiDispatcher</c>. A future test that does use <c>Send</c> and expects the work to have
    /// happened will get silently deferred work rather than a failure, so give this an override that
    /// runs inline - or a separate double - before reaching for it.
    /// </summary>
    public override void Send(SendOrPostCallback d, object? state) => Post(d, state);

    /// <summary>
    /// Runs everything queued, on the calling thread, in order - including anything those callbacks
    /// queue in turn, which is what lets one drain carry a multi-await turn to its end.
    ///
    /// The caller must have this context installed while draining, exactly as a message loop does.
    /// </summary>
    public void Drain()
    {
        while (true)
        {
            Action next;

            lock (_gate)
            {
                if (_queued.Count == 0)
                {
                    return;
                }

                next = _queued.Dequeue();
            }

            next();
        }
    }
}
