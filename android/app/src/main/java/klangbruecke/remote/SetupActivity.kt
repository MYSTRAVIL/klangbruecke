package klangbruecke.remote

import android.Manifest
import android.app.Activity
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/**
 * The one screen. It is a checklist, not an app: grant Notification Access, grant Bluetooth (and
 * notifications), then start the background service. The service does the real work; this Activity
 * only sets it up and shows whether the pieces are in place.
 *
 * Built in code rather than XML to keep the module dependency-free (no AndroidX / no res wiring) -
 * the platform Activity already gives runtime-permission APIs at minSdk 26.
 */
class SetupActivity : Activity() {

    private lateinit var statusView: TextView
    private lateinit var notificationButton: Button
    private lateinit var permissionButton: Button
    private lateinit var serviceButton: Button

    private val mainHandler = Handler(Looper.getMainLooper())

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildUi())
    }

    override fun onResume() {
        super.onResume()
        refreshStatus()
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray,
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        refreshStatus()
    }

    // --- UI ----------------------------------------------------------------------------------

    private fun buildUi(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(24), dp(24), dp(24), dp(24))
        }

        root.addView(heading("Klangbruecke Remote"))
        root.addView(
            body(
                "This app lets your PC show and control what is playing on your phone, over " +
                    "Bluetooth. Complete the two grants below, then start the service. It runs " +
                    "quietly in the background and reconnects on its own.",
            ),
        )

        root.addView(sectionLabel("1. Notification access"))
        root.addView(
            body(
                "Needed to read the phone's active media session (title, artist, play/pause). " +
                    "It does not read your notifications - the permission is just how Android " +
                    "gates media-session access.",
            ),
        )
        notificationButton = button("Grant notification access") {
            startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        }
        root.addView(notificationButton)

        root.addView(sectionLabel("2. Bluetooth + notifications"))
        root.addView(
            body(
                "Bluetooth Connect lets the service host the link to your paired PC. The " +
                    "notification permission keeps its status shown while it runs.",
            ),
        )
        permissionButton = button("Grant Bluetooth + notifications") {
            requestRuntimePermissions()
        }
        root.addView(permissionButton)

        root.addView(sectionLabel("3. Background service"))
        serviceButton = button("Start remote service") {
            if (RemoteService.isRunning) stopService() else startService()
            // The service flips isRunning shortly after; re-read so the label catches up.
            mainHandler.postDelayed(::refreshStatus, 400)
        }
        root.addView(serviceButton)

        root.addView(sectionLabel("Status"))
        statusView = body("").apply {
            setTextIsSelectable(false)
        }
        root.addView(statusView)

        return ScrollView(this).apply { addView(root) }
    }

    private fun heading(text: String) = TextView(this).apply {
        this.text = text
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 22f)
        setPadding(0, 0, 0, dp(12))
    }

    private fun sectionLabel(text: String) = TextView(this).apply {
        this.text = text
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
        setPadding(0, dp(20), 0, dp(4))
    }

    private fun body(text: String) = TextView(this).apply {
        this.text = text
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
        setPadding(0, 0, 0, dp(4))
    }

    private fun button(text: String, onClick: () -> Unit) = Button(this).apply {
        this.text = text
        gravity = Gravity.CENTER
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply {
            topMargin = dp(6)
        }
        setOnClickListener { onClick() }
    }

    // --- Actions + status --------------------------------------------------------------------

    private fun requestRuntimePermissions() {
        val wanted = mutableListOf<String>()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED
        ) {
            wanted += Manifest.permission.BLUETOOTH_CONNECT
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            wanted += Manifest.permission.POST_NOTIFICATIONS
        }
        if (wanted.isEmpty()) {
            refreshStatus()
        } else {
            requestPermissions(wanted.toTypedArray(), REQ_RUNTIME_PERMISSIONS)
        }
    }

    private fun startService() {
        val intent = Intent(this, RemoteService::class.java).setAction(RemoteService.ACTION_START)
        startForegroundService(intent)
    }

    private fun stopService() {
        val intent = Intent(this, RemoteService::class.java).setAction(RemoteService.ACTION_STOP)
        startService(intent)
    }

    private fun refreshStatus() {
        val notif = isNotificationAccessGranted()
        val bt = isBluetoothConnectGranted()
        val post = isPostNotificationsGranted()
        val running = RemoteService.isRunning

        serviceButton.text = if (running) "Stop remote service" else "Start remote service"

        statusView.text = buildString {
            appendLine(mark(notif) + " Notification access")
            appendLine(mark(bt) + " Bluetooth Connect permission")
            appendLine(mark(post) + " Post-notifications permission")
            append(mark(running) + " Remote service running")
            if (running && (!notif || !bt)) {
                append("\n\nHeads up: the service is running but a grant is missing, so it may not")
                append(" see media or stay visible. Finish the grants above.")
            }
        }
    }

    private fun mark(ok: Boolean): String = if (ok) "[x]" else "[ ]"

    private fun isNotificationAccessGranted(): Boolean {
        val flat = Settings.Secure.getString(contentResolver, "enabled_notification_listeners") ?: return false
        val self = ComponentName(this, KlangbrueckeNotificationListener::class.java)
        return flat.split(":").any { ComponentName.unflattenFromString(it) == self }
    }

    private fun isBluetoothConnectGranted(): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED
        } else {
            true // legacy BLUETOOTH is a normal (install-time) permission on API 30-.
        }

    private fun isPostNotificationsGranted(): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        } else {
            true
        }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    private companion object {
        const val REQ_RUNTIME_PERMISSIONS = 1001
    }
}
