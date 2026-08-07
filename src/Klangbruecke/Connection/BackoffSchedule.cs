namespace Klangbruecke.Connection;

/// <summary>
/// 2, 4, 8, 16, 30, then 60 seconds forever. Reset on success.
///
/// Deliberately owns no timer: <see cref="Klangbruecke.Platform.IScheduler"/> does the waiting, so
/// this stays a value-like object that the tests can walk fifty steps in no time at all. Each half
/// that retries holds its own instance - the music half's connect, the music half's route (a WASAPI
/// route can die while the Bluetooth link is fine) and the calls half's registration all back off
/// independently.
/// </summary>
public sealed class BackoffSchedule
{
    // A table rather than a formula: the sequence stops doubling at 16 -> 30 and again at 60, so any
    // closed form would need two special cases to say what six numbers say plainly. The last entry
    // is the ceiling every later attempt reads.
    private static readonly TimeSpan[] Delays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    };

    /// <summary>Failures recorded since the last <see cref="Reset"/>. Zero on a fresh instance.</summary>
    public int Attempt { get; private set; }

    /// <summary>
    /// How long to wait before the next attempt. Reading it does not change it - only
    /// <see cref="Advance"/> and <see cref="Reset"/> move the schedule.
    /// </summary>
    public TimeSpan CurrentDelay => Delays[Math.Min(Attempt, Delays.Length - 1)];

    /// <summary>Records a failed attempt: the next <see cref="CurrentDelay"/> is the longer one.</summary>
    public void Advance() => Attempt++;

    /// <summary>Back to the start. Called the moment the half comes up.</summary>
    public void Reset() => Attempt = 0;
}
