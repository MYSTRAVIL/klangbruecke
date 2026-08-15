using System.Text.Json.Serialization;

namespace Klangbruecke.Companion;

/// <summary>
/// The one byte that says what a frame is. Defined once here and mirrored byte-for-byte by the
/// Android side (<c>Protocol.kt</c>); the values are wire constants, so they are pinned literally
/// rather than left to the compiler. Art / seek / RequestArt are reserved for Phase 3 and are
/// deliberately absent - a type this side cannot produce is a type it cannot be asked to handle.
/// </summary>
internal enum MessageType : byte
{
    Hello = 0x01,
    NowPlaying = 0x02,
    PlaybackState = 0x03,
    Command = 0x10,
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
    [property: JsonPropertyName("hasSession")] bool HasSession);

internal sealed record PlaybackStatePayload(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("positionMs")] long PositionMs,
    [property: JsonPropertyName("timestampMs")] long TimestampMs,
    [property: JsonPropertyName("speed")] double Speed);

internal sealed record CommandPayload(
    [property: JsonPropertyName("command")] string Command);
