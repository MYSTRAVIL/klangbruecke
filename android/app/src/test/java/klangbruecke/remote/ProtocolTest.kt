package klangbruecke.remote

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The wire format is a contract with the already-committed PC side
 * (src/Klangbruecke/Companion/MediaProtocol.cs). These tests pin the byte layout: big-endian
 * length prefix, the type byte, and a readFrame that refuses to hand back an incomplete frame.
 */
class ProtocolTest {

    private fun bigEndianLength(frame: ByteArray): Int =
        ((frame[0].toInt() and 0xFF) shl 24) or
            ((frame[1].toInt() and 0xFF) shl 16) or
            ((frame[2].toInt() and 0xFF) shl 8) or
            (frame[3].toInt() and 0xFF)

    @Test
    fun nowPlaying_framesBigEndianLengthAndType() {
        val frame = Protocol.encodeNowPlaying("Song", "Artist", "Album", 1000L, true)

        // [len(4)][type(1)][json...]; length counts type + payload, not itself.
        assertEquals(frame.size - 4, bigEndianLength(frame))
        assertEquals(Protocol.TYPE_NOW_PLAYING, frame[4])

        val json = JSONObject(String(frame, 5, frame.size - 5, Charsets.UTF_8))
        assertEquals("Song", json.getString("title"))
        assertEquals("Artist", json.getString("artist"))
        assertEquals("Album", json.getString("album"))
        assertEquals(1000L, json.getLong("durationMs"))
        assertTrue(json.getBoolean("hasSession"))
    }

    @Test
    fun playbackState_carriesStatusFields() {
        val frame = Protocol.encodePlaybackState(Protocol.STATUS_PLAYING, 0L, 0L, 1.0)
        assertEquals(Protocol.TYPE_PLAYBACK_STATE, frame[4])

        val json = JSONObject(String(frame, 5, frame.size - 5, Charsets.UTF_8))
        assertEquals("playing", json.getString("status"))
        assertEquals(0L, json.getLong("positionMs"))
        assertEquals(0L, json.getLong("timestampMs"))
        assertEquals(1.0, json.getDouble("speed"), 0.0001)
    }

    @Test
    fun hello_carriesVersionAndName() {
        val frame = Protocol.encodeHello(Protocol.PROTOCOL_VERSION, "Pixel")
        assertEquals(Protocol.TYPE_HELLO, frame[4])

        val json = JSONObject(String(frame, 5, frame.size - 5, Charsets.UTF_8))
        assertEquals(1, json.getInt("protocolVersion"))
        assertEquals("Pixel", json.getString("pcName"))
    }

    @Test
    fun readFrame_returnsNull_whenIncomplete() {
        val full = Protocol.encodeNowPlaying("T", "A", "Al", 0L, true)
        val partial = full.copyOfRange(0, full.size - 2)
        assertNull(Protocol.readFrame(partial))
    }

    @Test
    fun readFrame_returnsNull_whenLengthPrefixItselfIncomplete() {
        // Fewer than the 4 bytes needed to even read the length must be "wait", not a crash.
        assertNull(Protocol.readFrame(byteArrayOf(0, 0)))
    }

    @Test
    fun readFrame_parsesOneFrame_andReportsConsumed() {
        val full = Protocol.encodeNowPlaying("T", "A", "Al", 0L, true)
        val parsed = Protocol.readFrame(full)
        assertTrue(parsed != null)
        assertEquals(Protocol.TYPE_NOW_PLAYING, parsed!!.frame.type)
        assertEquals(full.size, parsed.consumed)
    }

    private fun commandFrame(action: String): ByteArray {
        val payload = JSONObject().put("command", action).toString().toByteArray(Charsets.UTF_8)
        return Protocol.encodeFrame(Protocol.TYPE_COMMAND, payload)
    }

    @Test
    fun readFrame_drainsTwoConcatenatedFrames() {
        val a = commandFrame("play")
        val b = commandFrame("pause")
        val combined = a + b

        val first = Protocol.readFrame(combined)!!
        assertEquals(Protocol.TYPE_COMMAND, first.frame.type)
        assertEquals(a.size, first.consumed)

        val rest = combined.copyOfRange(first.consumed, combined.size)
        val second = Protocol.readFrame(rest)!!
        assertEquals(Protocol.TYPE_COMMAND, second.frame.type)
        assertEquals(b.size, second.consumed)
    }

    @Test
    fun decodeCommand_readsActionString() {
        val frame = commandFrame("next")
        // strip [len(4)][type(1)] to get the payload the service hands to decodeCommand
        val payload = frame.copyOfRange(5, frame.size)
        assertEquals("next", Protocol.decodeCommand(payload))
    }

    @Test
    fun readFrame_handlesLengthAcrossMultipleBytes() {
        // A payload over 255 bytes exercises the high bytes of the big-endian length.
        val long = "x".repeat(300)
        val frame = Protocol.encodeNowPlaying(long, "", "", 0L, true)
        assertEquals(frame.size - 4, bigEndianLength(frame))
        assertTrue(bigEndianLength(frame) > 255)
        val parsed = Protocol.readFrame(frame)!!
        assertEquals(frame.size, parsed.consumed)
    }
}
