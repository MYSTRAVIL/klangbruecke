package klangbruecke.remote

import android.content.ComponentName
import android.content.Context
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
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

    /** An immutable read of the active session. Album/duration are carried now; only text + isPlaying are used in Phase 2. */
    data class Snapshot(
        val title: String,
        val artist: String,
        val album: String,
        val durationMs: Long,
        val isPlaying: Boolean,
        val hasSession: Boolean,
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

    private val controllerCallback = object : MediaController.Callback() {
        override fun onPlaybackStateChanged(state: PlaybackState?) {
            onChanged?.invoke()
        }

        override fun onMetadataChanged(metadata: MediaMetadata?) {
            onChanged?.invoke()
        }

        override fun onSessionDestroyed() {
            // The app we were watching went away. Fall back to whatever is active now.
            unbind()
            bindToFirst(safeActiveSessions())
            onChanged?.invoke()
        }
    }

    private val sessionsChangedListener =
        MediaSessionManager.OnActiveSessionsChangedListener { controllers ->
            bindToFirst(controllers)
            onChanged?.invoke()
        }

    /** Begins watching. Requires the notification-listener to be enabled or throws SecurityException. */
    fun start() {
        sessionManager.addOnActiveSessionsChangedListener(
            sessionsChangedListener,
            listenerComponent,
            mainHandler,
        )
        bindToFirst(safeActiveSessions())
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
        return Snapshot(title, artist, album, duration, isPlaying, hasSession = true)
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

    private fun safeActiveSessions(): List<MediaController> =
        try {
            sessionManager.getActiveSessions(listenerComponent)
        } catch (se: SecurityException) {
            // Notification access not granted yet. Treat as "no sessions" rather than crashing.
            Log.w(TAG, "getActiveSessions denied (notification access not granted?)", se)
            emptyList()
        }

    private fun bindToFirst(controllers: List<MediaController>?) {
        val next = controllers?.firstOrNull()
        if (next?.sessionToken == controller?.sessionToken) return
        unbind()
        controller = next
        controller?.registerCallback(controllerCallback, mainHandler)
    }

    private fun unbind() {
        controller?.unregisterCallback(controllerCallback)
        controller = null
    }

    private companion object {
        const val TAG = "MediaBridge"
    }
}
