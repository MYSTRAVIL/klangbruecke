namespace Klangbruecke.Companion;

/// <summary>
/// The now-playing state as the PC side holds it - the Phase 2 fields only. No album art, no
/// duration, no position: those are Phase 3, and an immutable record with exactly the fields SMTC
/// renders keeps the fold in <see cref="MediaProtocol.DecodeInbound"/> honest about what a frame
/// can and cannot change.
///
/// <see cref="HasSession"/> is the difference between "the phone has a media app in the foreground"
/// and "it does not". <see cref="Empty"/> is the second case, and the state the link publishes the
/// moment the transport drops - an SMTC session left showing the last track after the phone has gone
/// is the failure this field exists to prevent.
/// </summary>
internal sealed record MediaSnapshot(
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    bool HasSession)
{
    /// <summary>No session, no text. What the PC shows when there is nothing on the phone to show.</summary>
    public static MediaSnapshot Empty { get; } = new("", "", "", false, false);
}
