package klangbruecke.remote

import android.service.notification.NotificationListenerService

/**
 * Deliberately empty. We never read a single notification here - the class exists only because
 * declaring an (enabled) NotificationListenerService is what grants this app permission to call
 * MediaSessionManager.getActiveSessions(). Its ComponentName is the credential MediaBridge passes
 * to that call.
 */
class KlangbrueckeNotificationListener : NotificationListenerService()
