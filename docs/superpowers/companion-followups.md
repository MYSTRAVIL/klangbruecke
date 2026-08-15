# Companion — follow-ups / known issues

Running list of defects and deferred work found while building the phone media remote. Fold into the
Phase 3 (art + seek) and Phase 4 (polish) plans.

## Android SetupActivity (Phase 4 — setup UX)

Reported 2026-08-12 during first hardware run:

1. ~~**"Grant Notification Access" ignores already-granted state and does not live-refresh.**~~
   **RESOLVED 2026-08-15 (FINDINGS §22).** The grant buttons now reflect state: once granted, the button
   flips to a disabled "✓ Notification access granted" on `onResume`.

2. ~~**"Grant Bluetooth + Notifications" button does nothing.**~~ **RESOLVED 2026-08-15 (FINDINGS §22).**
   Confirmed real on hardware (perms granted on-device but the button stayed "Grant…" and silently
   no-op'd). Fixed the same way — disabled "✓ granted" state — plus the service now auto-starts once its
   grants are in place, so the common path needs no taps.

General: the setup screen is now a live status dashboard (each requirement shows granted/not-granted and
updates on resume), not fire-and-forget buttons.

## MediaBridge (Phase 2/3)

3. ~~**Multiple active media sessions — pick order is undefined.**~~ **RESOLVED 2026-08-15 (FINDINGS §22).**
   Re-confirmed live (Spotify + Poweramp both active). `MediaBridge.pickBest` now prefers the session
   whose `PlaybackState == PLAYING`, falls back to index 0, and re-picks when the bound session pauses or
   the active set changes.

## SMTC publisher (Phase 3)

4. ~~**Seek not yet validated.**~~ **RESOLVED 2026-08-15 (FINDINGS §22).** Validated on hardware: the
   scrubber renders, advances, and scrubs. The key was feeding a live, advancing position — done
   PC-side via a `CompanionLink` interpolation tick (`UpdateTimelineProperties` + `PlaybackStatus=Playing`
   + non-zero `PlaybackRate`; a zero duration yields no scrubber). `PlaybackPositionChangeRequested`
   fires and drives a `seek` Command to the phone.

5. **HWND-without-Show deviation.** `SmtcPublisher` creates its hidden window without `Show()`. Confirmed
   render path works with Show() in the spike; verify the no-Show path is GSMTC-visible during C1. If a
   session fails to appear, the fallback is a brief `Show()`/`Hide()`.
