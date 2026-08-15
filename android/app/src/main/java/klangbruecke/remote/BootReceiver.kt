package klangbruecke.remote

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/**
 * Starts the always-on [RemoteService] on device boot, and again after the app updates, so the PC can
 * connect whenever it comes into range without the user ever opening the app. The design calls for a
 * 24/7 foreground service; this is what makes it survive a reboot.
 *
 * BOOT_COMPLETED is one of the few background-start exemptions Android grants a foreground service, so
 * starting it from here is allowed. The service is idempotent - [RemoteService.onStartCommand] no-ops
 * if it is already running - so a redundant broadcast is harmless.
 *
 * Note: a *force-stopped* app (Settings > Force stop, or `adb install -r`) receives no broadcasts until
 * it is next launched by hand; that is Android's contract and no receiver can escape it. A normal
 * reboot or a Play/normal update both deliver here.
 */
class BootReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent?) {
        when (intent?.action) {
            Intent.ACTION_BOOT_COMPLETED,
            Intent.ACTION_MY_PACKAGE_REPLACED,
            -> startService(context)
        }
    }

    private fun startService(context: Context) {
        try {
            val service = Intent(context, RemoteService::class.java)
                .setAction(RemoteService.ACTION_START)
            context.startForegroundService(service)
            Log.i(TAG, "Boot/update: started RemoteService.")
        } catch (t: Throwable) {
            // A background-start restriction or a revoked BLUETOOTH_CONNECT can throw; never crash here.
            Log.w(TAG, "Failed to start RemoteService on boot/update.", t)
        }
    }

    private companion object {
        const val TAG = "BootReceiver"
    }
}
