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
/// Installing this makes them tell apart. A read completed from another thread posts its continuation
/// here when the context was captured, and runs it on the completing thread when it was not.
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

    /// <summary>How many callbacks have been posted here, ever. Never decremented by a drain.</summary>
    public int PostCount { get; private set; }

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
            PostCount++;
            _queued.Enqueue(() => d(state));
        }
    }

    /// <summary>
    /// <see cref="Post"/>'s blocking twin, and it queues rather than running inline for the same
    /// reason: a continuation that reached this object at all is the fact being measured, and running
    /// one here on the caller's thread would be the very thing the assertion is looking for.
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
