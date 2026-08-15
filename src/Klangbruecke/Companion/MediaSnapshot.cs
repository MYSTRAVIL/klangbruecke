namespace Klangbruecke.Companion;

/// <summary>
/// The now-playing content the PC side holds. Text and <see cref="IsPlaying"/> are the Phase 2 fields;
/// Phase 3 adds <see cref="DurationMs"/> and album art (<see cref="ArtHash"/> plus the resolved
/// <see cref="Art"/> bytes). Position is deliberately <em>not</em> here - it is transient timeline data
/// that flows through <see cref="PlaybackUpdate"/> and the SMTC timeline, not content that identifies a
/// track. Keeping the snapshot to what SMTC's <c>DisplayUpdater</c> renders keeps the fold honest.
///
/// <see cref="ArtHash"/> is the phone's opaque per-track key; <see cref="Art"/> is null until the bytes
/// have been fetched (<see cref="ArtCache"/> miss -&gt; RequestArt -&gt; AlbumArt), so the text shows
/// immediately and the image fills in when it arrives.
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
    bool HasSession,
    long DurationMs = 0,
    string? ArtHash = null,
    byte[]? Art = null)
{
    /// <summary>No session, no text. What the PC shows when there is nothing on the phone to show.</summary>
    public static MediaSnapshot Empty { get; } = new("", "", "", false, false);
}
