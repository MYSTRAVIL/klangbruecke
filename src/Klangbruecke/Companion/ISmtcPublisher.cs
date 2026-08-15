namespace Klangbruecke.Companion;

/// <summary>
/// The PC's now-playing surface as <see cref="CompanionLink"/> sees it: publish a snapshot into the
/// system's media session (which ModernFlyouts and the native overlay render), and be told when the
/// user presses a transport button or a media key so the link can forward it to the phone. The
/// <c>SystemMediaTransportControls</c> interop - the FINDINGS §20.1 vtable recipe and the hidden
/// window it needs - lives below this seam in <c>SmtcPublisher</c>.
/// </summary>
internal interface ISmtcPublisher : IDisposable
{
    /// <summary>
    /// Create or update the session from the snapshot. A snapshot with
    /// <see cref="MediaSnapshot.HasSession"/> false tears the session down rather than showing a
    /// blank one - the phone has nothing playing, and the PC should show nothing.
    /// </summary>
    void Publish(MediaSnapshot snapshot);

    /// <summary>A transport button or media key was pressed on the PC. Forward it to the phone.</summary>
    event EventHandler<MediaCommand>? CommandRequested;
}
