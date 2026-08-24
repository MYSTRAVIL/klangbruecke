namespace Klangbruecke.Connection;

/// <summary>
/// The rate limit on automatic self-restarts, so a wedge a restart cannot cure degrades to a few
/// relaunches and then a quiet handover to the manual tray item - never a boot loop.
///
/// Pure and static: the persisted set of recent restart instants lives in <c>Settings</c> and the
/// clock is passed in, so every rule here is a value a test can assert without a real timer or a real
/// file. The watchdog (<see cref="SinkWedgeWatchdog"/>) asks <see cref="Allows"/> before it restarts
/// and folds <see cref="Record"/> back into settings when it does; the set survives the restart it
/// records, which is the whole point - an in-memory counter would reset to zero on every relaunch and
/// bound nothing.
/// </summary>
public static class RestartBudget
{
    /// <summary>The rolling window the cap is counted over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>
    /// The most auto-restarts allowed inside <see cref="Window"/>. Three, because one clears the
    /// measured wedge and a second covers a relaunch that itself came up wedged; a third inside an hour
    /// means the restart is not fixing it, and going on would be a loop rather than a recovery.
    /// </summary>
    public const int MaxPerWindow = 3;

    /// <summary>
    /// May an auto-restart happen at <paramref name="now"/>, given the ones already on record?
    ///
    /// True when fewer than <see cref="MaxPerWindow"/> of them fall inside the trailing
    /// <see cref="Window"/>. Restarts older than the window, and any dated in the future (a clock that
    /// moved back), are ignored - see <see cref="Prune"/>.
    /// </summary>
    public static bool Allows(IEnumerable<DateTimeOffset> recent, DateTimeOffset now)
        => Prune(recent, now).Count < MaxPerWindow;

    /// <summary>
    /// The set to persist after an auto-restart at <paramref name="now"/>: the still-relevant prior
    /// restarts plus this one, oldest first. Pruning on write keeps the stored list from growing without
    /// bound over the life of an install.
    /// </summary>
    public static List<DateTimeOffset> Record(IEnumerable<DateTimeOffset> recent, DateTimeOffset now)
    {
        List<DateTimeOffset> kept = Prune(recent, now);
        kept.Add(now);
        return kept;
    }

    /// <summary>
    /// The recorded restarts that still count at <paramref name="now"/>: inside the window and not in
    /// the future, oldest first.
    ///
    /// The future guard is not paranoia - <c>IScheduler.Now</c> follows the wall clock, which a user or
    /// an NTP correction can move backwards, and a restart stamped "later than now" would otherwise sit
    /// in the window forever and never age out.
    /// </summary>
    public static List<DateTimeOffset> Prune(IEnumerable<DateTimeOffset> recent, DateTimeOffset now)
    {
        List<DateTimeOffset> kept = new();

        foreach (DateTimeOffset t in recent)
        {
            if (t <= now && now - t < Window)
            {
                kept.Add(t);
            }
        }

        kept.Sort();
        return kept;
    }
}
