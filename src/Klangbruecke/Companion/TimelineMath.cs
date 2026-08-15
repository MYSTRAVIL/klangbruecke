namespace Klangbruecke.Companion;

/// <summary>
/// The one calculation the advancing seek bar needs, pulled out pure so it is tested without a clock,
/// a scheduler or SMTC. The phone sends a live position on play/pause/seek; the PC advances it locally
/// by <c>elapsed x speed</c> so the bar keeps moving while the phone stays silent (the Android power
/// constraint). Clamped to <c>[0, durationMs]</c> when a duration is known so a slightly-fast local
/// clock can never run the bar off the end.
/// </summary>
internal static class TimelineMath
{
    public static long PositionAt(long basePositionMs, TimeSpan elapsed, double speed, long durationMs)
    {
        long pos = basePositionMs + (long)(elapsed.TotalMilliseconds * speed);

        if (pos < 0)
        {
            pos = 0;
        }

        if (durationMs > 0 && pos > durationMs)
        {
            pos = durationMs;
        }

        return pos;
    }
}
