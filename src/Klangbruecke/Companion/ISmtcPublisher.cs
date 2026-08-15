namespace Klangbruecke.Companion;

/// <summary>
/// Position, duration and rate for the SMTC timeline - the geometry and speed of the seek bar
/// ModernFlyouts draws. Separate from <see cref="MediaSnapshot"/> because it changes on a different
/// beat: content changes on a track change, the timeline advances every tick.
/// </summary>
internal sealed record TimelineState(long PositionMs, long DurationMs, bool IsPlaying, double Speed);

/// <summary>
/// The PC's now-playing surface as <see cref="CompanionLink"/> sees it: publish a snapshot into the
/// system's media session (which ModernFlyouts and the native overlay render), keep its seek bar
/// advancing, and be told when the user presses a transport button, a media key, or scrubs the seek bar
/// so the link can forward it to the phone. The <c>SystemMediaTransportControls</c> interop - the
/// FINDINGS §20.1 vtable recipe and the hidden window it needs - lives below this seam in
/// <c>SmtcPublisher</c>.
/// </summary>
internal interface ISmtcPublisher : IDisposable
{
    /// <summary>
    /// Create or update the session's content (text, art, play state) from the snapshot. A snapshot
    /// with <see cref="MediaSnapshot.HasSession"/> false tears the session down rather than showing a
    /// blank one - the phone has nothing playing, and the PC should show nothing.
    /// </summary>
    void Publish(MediaSnapshot snapshot);

    /// <summary>
    /// Set the SMTC timeline so ModernFlyouts draws and advances the scrubber. Called once when a
    /// PlaybackState arrives and then on every interpolation tick while playing.
    /// </summary>
    void UpdateTimeline(TimelineState timeline);

    /// <summary>A transport button or media key was pressed on the PC. Forward it to the phone.</summary>
    event EventHandler<MediaCommand>? CommandRequested;

    /// <summary>The user scrubbed the SMTC seek bar. Forward the target position (ms) to the phone.</summary>
    event EventHandler<long>? SeekRequested;
}
