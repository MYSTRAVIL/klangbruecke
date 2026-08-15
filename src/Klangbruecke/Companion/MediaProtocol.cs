using System.Buffers.Binary;
using System.Text.Json;

namespace Klangbruecke.Companion;

/// <summary>
/// The wire format, and nothing else. Pure and static so the codec can be walked frame-by-frame in a
/// test with no socket, no phone and no allocation the test cannot see.
///
/// Frame = <c>[4-byte big-endian length][1-byte type][payload]</c>, where the length counts
/// <c>type + payload</c> - not itself. Every claim in that sentence is a place a length-prefixed
/// protocol goes wrong if it is off by one, so the length is written and read through
/// <see cref="BinaryPrimitives"/> rather than by hand, and <see cref="TryReadFrame"/> refuses to hand
/// back a frame it has not fully received.
/// </summary>
internal static class MediaProtocol
{
    /// <summary>The single supported protocol version. Sent in <c>Hello</c>; must match the phone.</summary>
    public const int ProtocolVersion = 1;

    // Reading is case-insensitive as a courtesy to a hand-rolled peer; writing goes through the DTOs'
    // pinned JsonPropertyName, so the options here never decide an outbound field name.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static byte[] EncodeCommand(MediaCommand command)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new CommandPayload(WireName(command)), JsonOptions);
        return Frame(MessageType.Command, payload);
    }

    public static byte[] EncodeHello(int protocolVersion, string pcName)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new HelloPayload(protocolVersion, pcName), JsonOptions);
        return Frame(MessageType.Hello, payload);
    }

    /// <summary>Asks the phone for the JPEG behind an art hash. Sent only on an <see cref="ArtCache"/> miss.</summary>
    public static byte[] EncodeRequestArt(string artHash)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new RequestArtPayload(artHash), JsonOptions);
        return Frame(MessageType.RequestArt, payload);
    }

    /// <summary>
    /// A seek is a <see cref="MessageType.Command"/> like the four transport actions, but it carries a
    /// target position - so it is encoded here rather than through the parameterless
    /// <see cref="EncodeCommand"/> / <see cref="MediaCommand"/> path.
    /// </summary>
    public static byte[] EncodeSeek(long positionMs)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new CommandPayload("seek", positionMs), JsonOptions);
        return Frame(MessageType.Command, payload);
    }

    /// <summary>
    /// Consumes one whole frame from the front of an accumulation buffer, advancing the span past it.
    /// Returns false and touches nothing when the buffer does not yet hold a complete frame - the
    /// normal case on a partial socket read - so the caller can append more bytes and try again.
    /// </summary>
    public static bool TryReadFrame(
        ref ReadOnlySpan<byte> buffer,
        out MessageType type,
        out ReadOnlyMemory<byte> payload)
    {
        type = default;
        payload = default;

        if (buffer.Length < 4)
        {
            return false;
        }

        int bodyLength = BinaryPrimitives.ReadInt32BigEndian(buffer);

        // A body has to carry at least its type byte, and the whole frame has to have arrived. Both
        // are "wait for more", not "malformed": a stream that split a frame mid-flight is ordinary.
        if (bodyLength < 1 || buffer.Length < 4 + bodyLength)
        {
            return false;
        }

        type = (MessageType)buffer[4];

        // A ReadOnlyMemory cannot be carved from a span - the span may be stack-only - so the payload
        // is copied out. The frames here are a few dozen bytes; the copy is not the cost that matters.
        payload = buffer.Slice(5, bodyLength - 1).ToArray();

        buffer = buffer.Slice(4 + bodyLength);
        return true;
    }

    /// <summary>
    /// Folds a NowPlaying frame into the running snapshot: it replaces the text and sets
    /// <see cref="MediaSnapshot.HasSession"/>, <see cref="MediaSnapshot.DurationMs"/> and
    /// <see cref="MediaSnapshot.ArtHash"/> from the frame. It clears <see cref="MediaSnapshot.Art"/> to
    /// null - a NowPlaying means a (possibly) new track, and the old track's image must never carry
    /// over; the link re-resolves art from the cache (or asks for it) after this fold.
    /// </summary>
    public static MediaSnapshot DecodeNowPlaying(ReadOnlyMemory<byte> payload, MediaSnapshot prior)
    {
        NowPlayingPayload np = Deserialize<NowPlayingPayload>(payload);
        return prior with
        {
            Title = np.Title,
            Artist = np.Artist,
            Album = np.Album,
            HasSession = np.HasSession,
            DurationMs = np.DurationMs,
            ArtHash = np.ArtHash,
            Art = null,
        };
    }

    /// <summary>
    /// Decodes a PlaybackState frame into the play flag plus the timeline data. Returned as a
    /// <see cref="PlaybackUpdate"/> rather than folded into the snapshot because position is transient
    /// timeline news, not track content - the link routes it to the SMTC timeline, and a play/pause
    /// must never blank the title.
    /// </summary>
    public static PlaybackUpdate DecodePlaybackState(ReadOnlyMemory<byte> payload)
    {
        PlaybackStatePayload ps = Deserialize<PlaybackStatePayload>(payload);
        return new PlaybackUpdate(ps.Status == "playing", ps.PositionMs, ps.TimestampMs, ps.Speed);
    }

    /// <summary>
    /// Reads the binary AlbumArt body - <c>[2-byte big-endian hashLen][hash UTF-8][JPEG bytes]</c> -
    /// mirroring the phone's <c>encodeAlbumArt</c>. Returns false on a truncated or garbled body rather
    /// than throwing: a bad art frame must be dropped, never allowed to take the link down.
    /// </summary>
    public static bool TryReadAlbumArt(ReadOnlyMemory<byte> payload, out string hash, out byte[] jpeg)
    {
        hash = string.Empty;
        jpeg = Array.Empty<byte>();

        ReadOnlySpan<byte> span = payload.Span;
        if (span.Length < 2)
        {
            return false;
        }

        int hashLen = (span[0] << 8) | span[1];
        if (hashLen < 0 || span.Length < 2 + hashLen)
        {
            return false;
        }

        hash = System.Text.Encoding.UTF8.GetString(span.Slice(2, hashLen));
        jpeg = span.Slice(2 + hashLen).ToArray();
        return true;
    }

    private static byte[] Frame(MessageType type, ReadOnlySpan<byte> payload)
    {
        int bodyLength = 1 + payload.Length; // type + payload
        var frame = new byte[4 + bodyLength];

        BinaryPrimitives.WriteInt32BigEndian(frame, bodyLength);
        frame[4] = (byte)type;
        payload.CopyTo(frame.AsSpan(5));

        return frame;
    }

    private static T Deserialize<T>(ReadOnlyMemory<byte> payload)
        => JsonSerializer.Deserialize<T>(payload.Span, JsonOptions)
           ?? throw new FormatException($"A {typeof(T).Name} payload deserialized to null.");

    private static string WireName(MediaCommand command) => command switch
    {
        MediaCommand.Play => "play",
        MediaCommand.Pause => "pause",
        MediaCommand.Next => "next",
        MediaCommand.Previous => "previous",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown media command."),
    };
}
