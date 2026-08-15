package klangbruecke.remote

import android.content.ComponentName
import android.content.Context
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log

/**
 * Reads the phone's currently-active media session and applies transport commands to it.
 *
 * The read side is event-driven (Global Constraints: no polling, no timers): we bind to the first
 * active [MediaController], register a [MediaController.Callback], and invoke [onChanged] whenever
 * the metadata or playback state moves. The set of active sessions can itself change (a new app
 * takes over), so we also listen for that and re-bind.
 *
 * All framework callbacks are routed to the main Looper via an explicit Handler, so this class is
 * safe to construct and drive from the service's background thread (which has no Looper of its own).
 */
class MediaBridge(context: Context) {

    /**
     * An immutable read of the active session. [positionMs] is the *live* position interpolated to the
     * moment of the read, with [timestampMs] the elapsed-realtime clock it was taken at, so the PC can
     * re-base its own advancing seek bar from it. [artHash] keys the album art the PC fetches lazily.
     */
    data class Snapshot(
        val title: String,
        val artist: String,
        val album: String,
        val durationMs: Long,
        val isPlaying: Boolean,
        val hasSession: Boolean,
        val artHash: String? = null,
        val positionMs: Long = 0L,
        val timestampMs: Long = 0L,
        val speed: Double = 1.0,
    ) {
        companion object {
            val EMPTY = Snapshot("", "", "", 0L, isPlaying = false, hasSession = false)
        }
    }

    /** Invoked (on the main Looper) whenever the active session's metadata or playback state changes. */
    var onChanged: (() -> Unit)? = null

    private val sessionManager =
        context.getSystemService(Context.MEDIA_SESSION_SERVICE) as MediaSessionManager
    private val listenerComponent =
        ComponentName(context.applicationContext, KlangbrueckeNotificationListener::class.java)
    private val mainHandler = Handler(Looper.getMainLooper())

    private var controller: MediaController? = null

    // The current track's art, computed once per metadata change (event-driven) and held until the PC
    // asks for it. Null when the track has no art.
    private var artHash: String? = null
    private var artJpeg: ByteArray? = null

    private val controllerCallback = object : MediaController.Callback() {
        override fun onPlaybackStateChanged(state: PlaybackState?) {
            // This session pausing may mean another active app is now the one playing - re-pick, so
            // the PC follows whatever is actually playing rather than staying stuck on a paused app.
            bindToBest(safeActiveSessions())
            onChanged?.invoke()
        }

        override fun onMetadataChanged(metadata: MediaMetadata?) {
            refreshArt(metadata)
            onChanged?.invoke()
        }

        override fun onSessionDestroyed() {
            // The app we were watching went away. Fall back to whatever is active now.
            unbind()
            bindToBest(safeActiveSessions())
            onChanged?.invoke()
        }
    }

    private val sessionsChangedListener =
        MediaSessionManager.OnActiveSessionsChangedListener { controllers ->
            bindToBest(controllers)
            onChanged?.invoke()
        }

    /** Begins watching. Requires the notification-listener to be enabled or throws SecurityException. */
    fun start() {
        sessionManager.addOnActiveSessionsChangedListener(
            sessionsChangedListener,
            listenerComponent,
            mainHandler,
        )
        bindToBest(safeActiveSessions())
    }

    fun stop() {
        sessionManager.removeOnActiveSessionsChangedListener(sessionsChangedListener)
        unbind()
    }

    /** The current read of the active session, or [Snapshot.EMPTY] when nothing is playing. */
    fun currentSnapshot(): Snapshot {
        val c = controller ?: return Snapshot.EMPTY
        val metadata = c.metadata
        val state = c.playbackState
        val title = metadata?.getString(MediaMetadata.METADATA_KEY_TITLE).orEmpty()
        val artist = metadata?.getString(MediaMetadata.METADATA_KEY_ARTIST).orEmpty()
        val album = metadata?.getString(MediaMetadata.METADATA_KEY_ALBUM).orEmpty()
        val duration = metadata?.getLong(MediaMetadata.METADATA_KEY_DURATION) ?: 0L
        val isPlaying = state?.state == PlaybackState.STATE_PLAYING
        val speed = state?.playbackSpeed?.toDouble() ?: 1.0
        val now = SystemClock.elapsedRealtime()
        val position = livePosition(state, now, isPlaying, speed)
        return Snapshot(
            title, artist, album, duration, isPlaying, hasSession = true,
            artHash = artHash, positionMs = position, timestampMs = now, speed = speed,
        )
    }

    /**
     * Interpolates the framework's last-reported position up to [now] so the PC gets a live value to
     * re-base its seek bar from. A paused track reports its frozen position as-is; this is a read, not a
     * timer - it runs only when we build a snapshot, so it adds no wakeups.
     */
    private fun livePosition(state: PlaybackState?, now: Long, isPlaying: Boolean, speed: Double): Long {
        state ?: return 0L
        if (!isPlaying) return state.position
        val elapsed = now - state.lastPositionUpdateTime
        return state.position + (elapsed * speed).toLong()
    }

    /** The current track's (hash, jpeg), or null if there is no art. Answers a RequestArt. */
    fun currentArt(): Pair<String, ByteArray>? {
        val h = artHash ?: return null
        val j = artJpeg ?: return null
        return h to j
    }

    /** Applies a transport command (one of Protocol.COMMAND_*) to the active session. No-op if none. */
    fun apply(action: String) {
        val controls = controller?.transportControls ?: return
        when (action) {
            Protocol.COMMAND_PLAY -> controls.play()
            Protocol.COMMAND_PAUSE -> controls.pause()
            Protocol.COMMAND_NEXT -> controls.skipToNext()
            Protocol.COMMAND_PREVIOUS -> controls.skipToPrevious()
            else -> Log.w(TAG, "Ignoring unknown command action: $action")
        }
    }

    /** Seeks the active session to [positionMs] (the PC's Protocol.COMMAND_SEEK). No-op if none. */
    fun applySeek(positionMs: Long) {
        controller?.transportControls?.seekTo(positionMs)
    }

    /** Recomputes the cached art for the current metadata. Tries album art, then art, then display icon. */
    private fun refreshArt(metadata: MediaMetadata?) {
        val bitmap = metadata?.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
            ?: metadata?.getBitmap(MediaMetadata.METADATA_KEY_ART)
            ?: metadata?.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
        val encoded = AlbumArtCodec.encode(bitmap)
        artHash = encoded?.first
        artJpeg = encoded?.second
    }

    private fun safeActiveSessions(): List<MediaController> =
        try {
            sessionManager.getActiveSessions(listenerComponent)
        } catch (se: SecurityException) {
            // Notification access not granted yet. Treat as "no sessions" rather than crashing.
            Log.w(TAG, "getActiveSessions denied (notification access not granted?)", se)
            emptyList()
        }

    private fun bindToBest(controllers: List<MediaController>?) {
        val next = pickBest(controllers)
        if (next?.sessionToken == controller?.sessionToken) return
        unbind()
        controller = next
        controller?.registerCallback(controllerCallback, mainHandler)
        // A freshly-bound controller already has metadata; compute its art so the first snapshot carries it.
        refreshArt(controller?.metadata)
    }

    /**
     * Prefer the session that is actually PLAYING, falling back to the framework's first (most-recent).
     * With two active apps - the test phone had Spotify and Poweramp both active - <c>getActiveSessions()[0]</c>
     * is whichever the framework ordered first, which need not be the one playing.
     */
    private fun pickBest(controllers: List<MediaController>?): MediaController? {
        if (controllers.isNullOrEmpty()) return null
        return controllers.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING }
            ?: controllers.first()
    }

    private fun unbind() {
        controller?.unregisterCallback(controllerCallback)
        controller = null
        artHash = null
        artJpeg = null
    }

    private companion object {
        const val TAG = "MediaBridge"
    }
}
