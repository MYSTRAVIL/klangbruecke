package klangbruecke.remote

import android.graphics.Bitmap
import java.io.ByteArrayOutputStream
import java.security.MessageDigest

/**
 * Turns a MediaSession album-art bitmap into the wire form: a downscaled (~400 px longest edge) JPEG
 * plus a stable content hash. Called once per track change (event-driven, from MediaBridge) and cached;
 * the JPEG is only put on the wire when the PC asks for it (RequestArt on a cache-miss). Returns null
 * when there is no art.
 *
 * The hash is the PC's opaque cache key - it must change iff the image changes, which hashing the final
 * JPEG bytes guarantees. The PC never recomputes it, so the algorithm is ours alone.
 */
object AlbumArtCodec {
    private const val MAX_EDGE = 400
    private const val QUALITY = 85

    fun encode(bitmap: Bitmap?): Pair<String, ByteArray>? {
        if (bitmap == null) return null

        val scaled = downscale(bitmap)
        val out = ByteArrayOutputStream()
        scaled.compress(Bitmap.CompressFormat.JPEG, QUALITY, out)
        if (scaled !== bitmap) scaled.recycle()

        val jpeg = out.toByteArray()
        return hash(jpeg) to jpeg
    }

    private fun downscale(bitmap: Bitmap): Bitmap {
        val longest = maxOf(bitmap.width, bitmap.height)
        if (longest <= MAX_EDGE) return bitmap
        val ratio = MAX_EDGE.toFloat() / longest
        val w = (bitmap.width * ratio).toInt().coerceAtLeast(1)
        val h = (bitmap.height * ratio).toInt().coerceAtLeast(1)
        return Bitmap.createScaledBitmap(bitmap, w, h, true)
    }

    private fun hash(bytes: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(bytes)
        val sb = StringBuilder(16)
        for (i in 0 until 8) sb.append("%02x".format(digest[i]))
        return sb.toString()
    }
}
