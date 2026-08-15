package klangbruecke.remote

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothServerSocket
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import java.io.Closeable
import java.io.IOException
import java.io.OutputStream
import java.util.UUID

/**
 * Foreground service that hosts the RFCOMM server the PC connects to.
 *
 * Design (Global Constraints): event-driven and blocking. No wakelocks, no timers, no position
 * streaming. One background thread sits in a blocking accept()/read() loop; media changes arrive on
 * the main Looper via [MediaBridge.onChanged] and are written out from there. The two threads share
 * one socket - one reads, the other writes - which is the standard, supported RFCOMM pattern; writes
 * are serialised with [writeLock].
 *
 * Loop-accept: after a client disconnects we go straight back to accept() on the same server socket,
 * so the PC can reconnect without the service restarting.
 */
class RemoteService : Service() {

    private lateinit var mediaBridge: MediaBridge

    @Volatile private var running = false
    private var serverThread: Thread? = null
    private var serverSocket: BluetoothServerSocket? = null
    private var clientSocket: BluetoothSocket? = null

    // The live client's output stream, set while a client is connected. Read from the main thread
    // (onMediaChanged) and the accept thread; guarded for writes by writeLock.
    @Volatile private var clientOut: OutputStream? = null
    private val writeLock = Any()

    override fun onCreate() {
        super.onCreate()
        mediaBridge = MediaBridge(this)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        if (running) return START_STICKY

        running = true
        isRunning = true
        createChannel()
        startForegroundCompat(buildNotification("Waiting for PC..."))

        mediaBridge.onChanged = { onMediaChanged() }
        try {
            mediaBridge.start()
        } catch (se: SecurityException) {
            Log.w(TAG, "MediaBridge.start denied - notification access not granted yet", se)
        }

        serverThread = Thread(::serverLoop, "klangbruecke-rfcomm").also { it.start() }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        running = false
        isRunning = false
        mediaBridge.onChanged = null
        try {
            mediaBridge.stop()
        } catch (t: Throwable) {
            Log.w(TAG, "MediaBridge.stop failed", t)
        }
        // Closing the sockets unblocks accept()/read() on the server thread so it can exit.
        closeQuietly(clientSocket)
        closeQuietly(serverSocket)
        serverThread?.interrupt()
        super.onDestroy()
    }

    // --- RFCOMM server -----------------------------------------------------------------------

    @SuppressLint("MissingPermission") // BLUETOOTH_CONNECT is declared and granted via SetupActivity.
    private fun serverLoop() {
        val adapter = (getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter
        if (adapter == null) {
            Log.e(TAG, "No Bluetooth adapter; stopping.")
            stopSelf()
            return
        }

        try {
            serverSocket = adapter.listenUsingRfcommWithServiceRecord(SERVICE_NAME, SERVICE_UUID)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to open RFCOMM server socket; stopping.", e)
            stopSelf()
            return
        }
        Log.i(TAG, "on_srv_rfc_listen_started")

        while (running) {
            val socket = try {
                serverSocket?.accept() ?: break
            } catch (e: IOException) {
                // The usual cause is serverSocket.close() during shutdown -> leave the loop.
                if (running) Log.w(TAG, "accept() failed", e)
                break
            }
            serveClient(socket)
            // Client gone: loop straight back to accept() and re-listen.
        }
        closeQuietly(serverSocket)
        serverSocket = null
    }

    /** Serves exactly one connected client until it disconnects, then returns. */
    private fun serveClient(socket: BluetoothSocket) {
        clientSocket = socket
        Log.i(TAG, "on_srv_rfc_client_connected")
        updateNotification("Connected to PC")

        try {
            val input = socket.inputStream
            val output = socket.outputStream
            clientOut = output

            // Greeting: Hello, then the current now-playing so the PC has state immediately.
            sendFrame(output, Protocol.encodeHello(Protocol.PROTOCOL_VERSION, deviceName()))
            sendSnapshot(output, mediaBridge.currentSnapshot())

            // Blocking read loop: accumulate bytes, drain whole frames, apply Commands.
            var pending = ByteArray(0)
            val chunk = ByteArray(2048)
            while (running) {
                val n = input.read(chunk)
                if (n < 0) break // peer closed the stream
                if (n == 0) continue
                pending += chunk.copyOfRange(0, n)

                while (true) {
                    val parsed = Protocol.readFrame(pending) ?: break
                    handleFrame(parsed.frame)
                    pending = pending.copyOfRange(parsed.consumed, pending.size)
                }
            }
        } catch (e: IOException) {
            Log.i(TAG, "on_srv_rfc_client_disconnected", e)
        } catch (t: Throwable) {
            Log.w(TAG, "Client serving loop failed", t)
        } finally {
            clientOut = null
            closeQuietly(socket)
            if (clientSocket === socket) clientSocket = null
            if (running) updateNotification("Waiting for PC...")
        }
    }

    private fun handleFrame(frame: Protocol.Frame) {
        if (frame.type != Protocol.TYPE_COMMAND) return // Phase 2 only receives Commands
        val action = try {
            Protocol.decodeCommand(frame.payload)
        } catch (e: Exception) {
            Log.w(TAG, "Malformed Command frame ignored", e)
            return
        }
        mediaBridge.apply(action)
    }

    /** Fired on the main Looper by MediaBridge when the session changes. Pushes a fresh snapshot. */
    private fun onMediaChanged() {
        val output = clientOut ?: return
        try {
            sendSnapshot(output, mediaBridge.currentSnapshot())
        } catch (e: IOException) {
            // Peer went away between the null-check and the write; the read loop will notice and clean up.
            Log.i(TAG, "onMediaChanged send failed (peer gone?)", e)
        }
    }

    private fun sendSnapshot(output: OutputStream, snapshot: MediaBridge.Snapshot) {
        sendFrame(
            output,
            Protocol.encodeNowPlaying(
                snapshot.title,
                snapshot.artist,
                snapshot.album,
                snapshot.durationMs,
                snapshot.hasSession,
            ),
        )
        val status = if (snapshot.isPlaying) Protocol.STATUS_PLAYING else Protocol.STATUS_PAUSED
        // position/timestamp/speed are Phase 3 concerns; send inert values so the frame stays valid.
        sendFrame(output, Protocol.encodePlaybackState(status, 0L, 0L, 1.0))
    }

    private fun sendFrame(output: OutputStream, frame: ByteArray) {
        synchronized(writeLock) {
            output.write(frame)
            output.flush()
        }
    }

    private fun deviceName(): String = Build.MODEL ?: "Android"

    // --- Foreground notification -------------------------------------------------------------

    private fun createChannel() {
        val manager = getSystemService(NotificationManager::class.java)
        val channel = NotificationChannel(
            CHANNEL_ID,
            "Klangbruecke Remote",
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = "Keeps the phone media remote connected to the PC."
            setShowBadge(false)
        }
        manager.createNotificationChannel(channel)
    }

    private fun buildNotification(text: String): Notification {
        val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT

        // Reach SetupActivity without a compile-time reference to it (it is the app's launcher).
        val launch = packageManager.getLaunchIntentForPackage(packageName)
        val contentIntent = launch?.let {
            PendingIntent.getActivity(this, 0, it, flags)
        }

        val stopIntent = Intent(this, RemoteService::class.java).setAction(ACTION_STOP)
        val stopPending = PendingIntent.getService(this, 1, stopIntent, flags)

        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("Klangbruecke Remote")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setOngoing(true)
            .apply { if (contentIntent != null) setContentIntent(contentIntent) }
            .addAction(Notification.Action.Builder(null, "Stop", stopPending).build())
            .build()
    }

    private fun startForegroundCompat(notification: Notification) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(
                NOTIFICATION_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private fun updateNotification(text: String) {
        getSystemService(NotificationManager::class.java)
            .notify(NOTIFICATION_ID, buildNotification(text))
    }

    private fun closeQuietly(closeable: Closeable?) {
        try {
            closeable?.close()
        } catch (ignored: IOException) {
        }
    }

    companion object {
        val SERVICE_UUID: UUID = UUID.fromString("6f5e4d3c-2b1a-4c8d-9e7f-0a1b2c3d4e5f")
        const val SERVICE_NAME = "Klangbruecke"
        const val CHANNEL_ID = "klangbruecke-remote"
        const val NOTIFICATION_ID = 1

        const val ACTION_START = "klangbruecke.remote.action.START"
        const val ACTION_STOP = "klangbruecke.remote.action.STOP"

        /** Read by SetupActivity to show a live status line. */
        @Volatile
        var isRunning = false
            private set

        private const val TAG = "RemoteService"
    }
}
