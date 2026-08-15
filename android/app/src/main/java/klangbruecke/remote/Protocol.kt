package klangbruecke.remote

import org.json.JSONObject

/**
 * The wire format, and nothing else. A byte-for-byte mirror of the PC side
 * (src/Klangbruecke/Companion/MediaProtocol.cs + ProtocolMessage.cs). If this drifts from the C#,
 * the link silently stops working, so every constant here is a wire constant and is pinned literally.
 *
 * Frame = [4-byte big-endian length][1-byte type][payload]. The length counts type + payload,
 * not itself. Payloads are UTF-8 JSON. [readFrame] refuses to return a frame it has not fully
 * received, which is the normal case on a partial socket read.
 */
object Protocol {

    /** The single supported protocol version, sent in Hello. Must match the phone/PC. */
    const val PROTOCOL_VERSION = 1

    // Message type bytes. Mirror of MessageType in ProtocolMessage.cs. Phone->PC in 0x0x, PC->phone in 0x1x.
    const val TYPE_HELLO: Byte = 0x01
    const val TYPE_NOW_PLAYING: Byte = 0x02
    const val TYPE_PLAYBACK_STATE: Byte = 0x03
    const val TYPE_ALBUM_ART: Byte = 0x04
    const val TYPE_COMMAND: Byte = 0x10
    const val TYPE_REQUEST_ART: Byte = 0x11

    // Command action strings (CommandPayload.command in the C#). The PC sends these; we consume them.
    const val COMMAND_PLAY = "play"
    const val COMMAND_PAUSE = "pause"
    const val COMMAND_NEXT = "next"
    const val COMMAND_PREVIOUS = "previous"
    const val COMMAND_SEEK = "seek"

    // PlaybackState.status strings. The C# maps status == "playing" -> IsPlaying=true; anything else
    // is treated as paused. We emit exactly these two.
    const val STATUS_PLAYING = "playing"
    const val STATUS_PAUSED = "paused"

    /** One decoded frame: its type byte and its raw (JSON) payload. */
    data class Frame(val type: Byte, val payload: ByteArray) {
        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is Frame) return false
            return type == other.type && payload.contentEquals(other.payload)
        }

        override fun hashCode(): Int = 31 * type.toInt() + payload.contentHashCode()
    }

    /** A parsed frame plus how many bytes it consumed from the front of the buffer. */
    data class ParsedFrame(val frame: Frame, val consumed: Int)

    /**
     * Wraps a type byte and a payload in the length-prefixed frame. The length written is
     * 1 + payload.size (the type byte plus the payload), big-endian, via explicit shifts so the
     * byte order can never depend on the platform.
     */
    fun encodeFrame(type: Byte, payload: ByteArray): ByteArray {
        val bodyLength = 1 + payload.size // type + payload
        val frame = ByteArray(4 + bodyLength)
        frame[0] = (bodyLength ushr 24).toByte()
        frame[1] = (bodyLength ushr 16).toByte()
        frame[2] = (bodyLength ushr 8).toByte()
        frame[3] = bodyLength.toByte()
        frame[4] = type
        System.arraycopy(payload, 0, frame, 5, payload.size)
        return frame
    }

    fun encodeHello(protocolVersion: Int, name: String): ByteArray {
        // Field names mirror HelloPayload in ProtocolMessage.cs. The PC ignores this frame's body in
        // Phase 2, but the schema is kept identical so it need not change if that ever matters.
        val json = JSONObject()
            .put("protocolVersion", protocolVersion)
            .put("pcName", name)
        return encodeFrame(TYPE_HELLO, json.toString().toByteArray(Charsets.UTF_8))
    }

    fun encodeNowPlaying(
        title: String,
        artist: String,
        album: String,
        durationMs: Long,
        hasSession: Boolean,
        artHash: String?,
    ): ByteArray {
        // Field names + order mirror NowPlayingPayload in ProtocolMessage.cs. A null artHash goes as JSON
        // null, which the C# reads as "no art".
        val json = JSONObject()
            .put("title", title)
            .put("artist", artist)
            .put("album", album)
            .put("durationMs", durationMs)
            .put("hasSession", hasSession)
            .put("artHash", artHash ?: JSONObject.NULL)
        return encodeFrame(TYPE_NOW_PLAYING, json.toString().toByteArray(Charsets.UTF_8))
    }

    fun encodePlaybackState(
        status: String,
        positionMs: Long,
        timestampMs: Long,
        speed: Double,
    ): ByteArray {
        // Field names mirror PlaybackStatePayload in ProtocolMessage.cs.
        val json = JSONObject()
            .put("status", status)
            .put("positionMs", positionMs)
            .put("timestampMs", timestampMs)
            .put("speed", speed)
        return encodeFrame(TYPE_PLAYBACK_STATE, json.toString().toByteArray(Charsets.UTF_8))
    }

    /**
     * Binary AlbumArt body: [2-byte big-endian hashLen][hash UTF-8][jpeg]. The exact layout
     * MediaProtocol.TryReadAlbumArt reads on the PC. Only the body is built here; encodeFrame adds the
     * outer length prefix + type byte.
     */
    fun encodeAlbumArt(hash: String, jpeg: ByteArray): ByteArray {
        val h = hash.toByteArray(Charsets.UTF_8)
        val body = ByteArray(2 + h.size + jpeg.size)
        body[0] = (h.size ushr 8).toByte()
        body[1] = h.size.toByte()
        System.arraycopy(h, 0, body, 2, h.size)
        System.arraycopy(jpeg, 0, body, 2 + h.size, jpeg.size)
        return encodeFrame(TYPE_ALBUM_ART, body)
    }

    /** Reads the action out of a Command payload (CommandPayload.command in the C#). */
    fun decodeCommand(payload: ByteArray): String =
        JSONObject(String(payload, Charsets.UTF_8)).getString("command")

    /** Reads positionMs out of a seek Command payload (CommandPayload.positionMs in the C#). */
    fun decodeSeekPositionMs(payload: ByteArray): Long =
        JSONObject(String(payload, Charsets.UTF_8)).getLong("positionMs")

    /** Reads the requested hash out of a RequestArt payload (RequestArtPayload.artHash in the C#). */
    fun decodeRequestArt(payload: ByteArray): String =
        JSONObject(String(payload, Charsets.UTF_8)).getString("artHash")

    /**
     * Consumes one whole frame from the front of [buffer] (bytes in [0, length)). Returns null and
     * touches nothing when the buffer does not yet hold a complete frame - the caller appends more
     * bytes and tries again.
     */
    fun readFrame(buffer: ByteArray, length: Int = buffer.size): ParsedFrame? {
        if (length < 4) return null

        val bodyLength =
            ((buffer[0].toInt() and 0xFF) shl 24) or
                ((buffer[1].toInt() and 0xFF) shl 16) or
                ((buffer[2].toInt() and 0xFF) shl 8) or
                (buffer[3].toInt() and 0xFF)

        // A body must carry at least its type byte, and the whole frame must have arrived. Both are
        // "wait for more", not "malformed": a stream that split a frame mid-flight is ordinary.
        if (bodyLength < 1 || length < 4 + bodyLength) return null

        val type = buffer[4]
        val payload = buffer.copyOfRange(5, 4 + bodyLength) // payload = bodyLength - 1 bytes
        return ParsedFrame(Frame(type, payload), 4 + bodyLength)
    }
}
