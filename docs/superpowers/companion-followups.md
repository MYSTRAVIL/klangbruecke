# Companion — follow-ups / known issues

Running list of defects and deferred work found while building the phone media remote. Fold into the
Phase 3 (art + seek) and Phase 4 (polish) plans.

## Android SetupActivity (Phase 4 — setup UX)

Reported 2026-08-12 during first hardware run:

1. **"Grant Notification Access" ignores already-granted state and does not live-refresh.** The button
   does not check whether `KlangbrueckeNotificationListener` is already an enabled listener, and does
   not update after the user returns from the system settings screen. Fix: re-check the granted state in
   `onResume()` (read `Settings.Secure.enabled_notification_listeners` / `NotificationManagerCompat
   .getEnabledListenerPackages`) and reflect it in the button label + status line; hide/disable the
   button once granted.

2. **"Grant Bluetooth + Notifications" button does nothing.** Tapping it has no visible effect. Likely
   causes to check: the runtime-permission request is a no-op when already granted (so it needs an
   already-granted branch that updates the UI instead of silently doing nothing), or the click handler
   isn't wired / requests the wrong permission set. Fix: on click, if all needed runtime permissions are
   already granted, update status ("already granted") rather than no-op; otherwise request and reflect
   the result in `onRequestPermissionsResult`.

General: the setup screen should be a live status dashboard (each requirement shows granted/not-granted
and updates on resume), not fire-and-forget buttons.

## MediaBridge (Phase 2/3)

3. **Multiple active media sessions — pick order is undefined.** On the test phone both Spotify and
   Poweramp were `active=true` simultaneously. `getActiveSessions()[0]` picks whichever the framework
   orders first (most-recent), which may not be the one actually playing. Consider: prefer the session
   whose `PlaybackState == PLAYING`, falling back to index 0; and re-evaluate when the active set
   changes.

## SMTC publisher (Phase 3)

4. **Seek not yet validated.** The Phase-1 SMTC probe (FINDINGS 20.1) proved render + transport buttons
   but not the scrubber (static position). Validate `PlaybackPositionChangeRequested` + timeline
   interpolation with real, advancing phone position when the art+seek work lands.

5. **HWND-without-Show deviation.** `SmtcPublisher` creates its hidden window without `Show()`. Confirmed
   render path works with Show() in the spike; verify the no-Show path is GSMTC-visible during C1. If a
   session fails to appear, the fallback is a brief `Show()`/`Hide()`.
