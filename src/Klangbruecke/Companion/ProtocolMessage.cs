using System.Text.Json.Serialization;

namespace Klangbruecke.Companion;

/// <summary>
/// The one byte that says what a frame is. Defined once here and mirrored byte-for-byte by the
/// Android side (<c>Protocol.kt</c>); the values are wire constants, so they are pinned literally
/// rather than left to the compiler. Phone-to-PC frames sit in the <c>0x0x</c> range, PC-to-phone in
/// <c>0x1x</c>. <see cref="AlbumArt"/> is the one binary frame; everything else is UTF-8 JSON.
/// </summary>
internal enum MessageType : byte
{
    Hello = 0x01,
    NowPlaying = 0x02,
    PlaybackState = 0x03,
    AlbumArt = 0x04,
    Command = 0x10,
    RequestArt = 0x11,
}

/// <summary>
/// The typed payloads that ride inside a frame. Each is a JSON DTO with its wire names pinned by
/// <see cref="JsonPropertyNameAttribute"/> rather than left to a serializer's casing policy, because
/// the field names are a contract with the Android side and must not drift if the options change.
///
/// These are DTOs, not the app's model: <see cref="MediaSnapshot"/> is the model, and
/// <see cref="MediaProtocol.DecodeInbound"/> is the one place a wire DTO becomes one.
/// </summary>
internal sealed record HelloPayload(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("pcName")] string PcName);

internal sealed record NowPlayingPayload(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("album")] string Album,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("hasSession")] bool HasSession,
    [property: JsonPropertyName("artHash")] string? ArtHash = null);

internal sealed record PlaybackStatePayload(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("positionMs")] long PositionMs,
    [property: JsonPropertyName("timestampMs")] long TimestampMs,
    [property: JsonPropertyName("speed")] double Speed);

// The seek Command carries positionMs; the four parameterless actions omit it. WhenWritingNull keeps
// a non-seek Command frame byte-identical to Phase 2's, so the phone parses either without a schema bump.
internal sealed record CommandPayload(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("positionMs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? PositionMs = null);

internal sealed record RequestArtPayload(
    [property: JsonPropertyName("artHash")] string ArtHash);
