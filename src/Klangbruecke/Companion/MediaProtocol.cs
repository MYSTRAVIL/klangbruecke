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
    /// Folds one inbound frame into the running snapshot. NowPlaying replaces the text and sets
    /// <see cref="MediaSnapshot.HasSession"/> from the frame; PlaybackState touches only
    /// <see cref="MediaSnapshot.IsPlaying"/>, leaving the text alone - the two frames carry different
    /// news and a play/pause must never blank the title. The bool is true when the frame was a
    /// playback-state-only update.
    /// </summary>
    public static (MediaSnapshot snapshot, bool isPlaybackOnly) DecodeInbound(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        MediaSnapshot prior)
    {
        switch (type)
        {
            case MessageType.NowPlaying:
            {
                NowPlayingPayload np = Deserialize<NowPlayingPayload>(payload);
                MediaSnapshot snapshot = prior with
                {
                    Title = np.Title,
                    Artist = np.Artist,
                    Album = np.Album,
                    HasSession = np.HasSession,
                };
                return (snapshot, false);
            }

            case MessageType.PlaybackState:
            {
                PlaybackStatePayload ps = Deserialize<PlaybackStatePayload>(payload);
                MediaSnapshot snapshot = prior with { IsPlaying = ps.Status == "playing" };
                return (snapshot, true);
            }

            default:
                // Hello, Command, or anything reserved for a later phase: nothing to fold. The prior
                // snapshot stands unchanged rather than being blanked.
                return (prior, false);
        }
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
