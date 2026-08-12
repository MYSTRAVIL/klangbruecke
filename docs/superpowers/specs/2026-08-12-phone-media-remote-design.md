# Design — Phone media remote (optional companion app)

_Written 2026-08-12. Status: approved design, ready for an implementation plan._

## Goal

Let the PC see and control the phone's music: now-playing metadata (title / artist / album /
album art), transport (play / pause / next / previous), and seek. This is FINDINGS Item 4 —
previously WONTFIX over pure Bluetooth because Windows exposes no AVRCP Controller API and does not
forward media keys to the phone (FINDINGS §19, media-key sliver empirically closed 2026-08-12).

The wall §19 documented is narrow: *doing this PC-side-only over Bluetooth AVRCP* is impossible.
It is fully achievable with a small piece of software on the phone plus our own protocol — the same
route Phone Link, KDE Connect, and Sefirah all take. This design takes that route while keeping
Klangbruecke's identity intact.

## Decisions locked during brainstorming

- **Audience:** ships to all users, but **not** via Play Store — the APK is attached to the GitHub
  release and sideloaded. The app and protocol must therefore work on arbitrary modern Android, not
  just the author's Pixel.
- **Phone side:** build our own thin companion app. Rejected adopting KDE Connect (heavyweight,
  forces its own TLS-over-Wi-Fi transport and pairing) and Sefirah (Win11-oriented PC half; we are
  on 19045 deliberately).
- **Transport:** Bluetooth **RFCOMM only**. No Wi-Fi, and no transport abstraction for a Wi-Fi path
  we do not want. Rationale: RFCOMM rides the same radio and bond the audio already uses, so the
  remote works *wherever the audio works* (car, kitchen, on the go) with no shared-LAN requirement.
  BLE GATT rejected (throughput too low for album art).
- **No custom PC UI.** Klangbruecke publishes a **System Media Transport Controls (SMTC)** session
  mirroring the phone; Windows' native media overlay and **ModernFlyouts** (which the author already
  runs) render it. No flyout, no window — the app stays 100% tray/headless.
- **Scope:** transport, now-playing text, album art, seek. **Volume dropped** — SMTC/the flyout has
  no per-session volume slider, and PC system volume already controls the A2DP-rendered loudness, so
  a separate phone-volume control would be redundant and homeless.
- **Power efficiency is a first-class constraint** on the Android side (see below).

## Why the media-key control resurrects

This morning's test (FINDINGS §19) proved injected media keys do nothing to the phone — because
there was **no local SMTC session for the keys to target**. Once Klangbruecke publishes its own SMTC
session, the keyboard's play/pause/next/prev land on *that* session, and we forward them to the phone
over RFCOMM. We obtain transport control via a completely different door than AVRCP. This does not
contradict §19: we are not reading the phone's session over Bluetooth, we are synthesizing a local
one from data the companion sends.

## System shape

```
┌─────────────────────────┐        Bluetooth RFCOMM          ┌──────────────────────────┐
│  Android companion app  │   (custom SDP service UUID)      │  Klangbruecke tray app   │
│  "Klangbruecke Remote"  │◄────────────────────────────────►│  (new Companion/ module) │
│                         │                                  │                          │
│  • foreground service   │   phone = RFCOMM SERVER (listens) │  • RFCOMM client         │
│  • reads MediaSession   │   PC    = RFCOMM CLIENT (connects)│  • publishes SMTC session│
│  • applies commands     │                                  │  • no UI (tray only)     │
└─────────────────────────┘                                  └──────────────────────────┘
                                                     Windows media overlay / ModernFlyouts
                                                     render the SMTC session; media keys
                                                     target it; we forward events to phone.
```

**Roles — phone listens, PC connects.** Mirrors the app's existing ethos (*the PC initiates*). The
phone advertises the service and waits; the always-on tray app connects when the phone is present and
re-initiates with `BackoffSchedule` after sleep / reboot / range-loss. This reuses existing presence
detection and backoff instead of inventing a reconnect path, and sidesteps the phone-initiated-
reconnect fragility that defined the predecessor app's bug.

**The link is independent of the audio.** RFCOMM is its own channel, separate from A2DP (AVDTP) and
HFP (SCO). It carries only small control messages and the occasional album-art JPEG; it never touches
the audio path. That independence is exactly what the coexistence gate must prove.

## Android companion app

A deliberately minimal app: one setup screen plus a foreground service. The setup screen exists only
to grant two permissions (**Notification Access** — required for `MediaSessionManager` — and
**Bluetooth Connect** on Android 12+), explain plainly why, and show connection status. No browsing,
no settings sprawl. It is the phone-side mirror of the tray app: headless-with-a-status-readout.

The service does three things: advertise the RFCOMM service via a `BluetoothServerSocket` on our SDP
UUID and accept the PC's connection; read the active `MediaSession`
(`MediaSessionManager.getActiveSessions()` → `MediaController`); relay commands back to it
(`MediaController.getTransportControls()`).

### Power efficiency — built to cost ~nothing at idle

- **100% event-driven, zero polling.** Metadata / playback state come from `MediaController.Callback`
  (`onMetadataChanged` / `onPlaybackStateChanged`). There is no timer anywhere in the app.
- **Blocking I/O so threads sleep.** The server socket blocks in `accept()`; the read loop blocks on
  the socket. A blocked thread consumes no CPU and creates no wakeups.
- **No wakelocks, ever.** We never fight Doze. Idle with nothing playing and the PC away → the phone
  sleeps normally; incoming data or a media callback wakes only the thread that needs it.
- **Position is never streamed.** On play/pause/seek we send one `PlaybackState`; the PC interpolates
  the seek bar locally. A steadily-playing or paused track sends nothing.
- **Album art downscaled, hashed, sent once per track**, and only when the PC asks (cache-miss).
- **Piggybacks the live audio link.** While music is routed to the PC, the Bluetooth ACL link is
  already up for A2DP; RFCOMM rides it at near-zero incremental radio cost.

Net idle cost is essentially just the mandatory ongoing foreground-service notification.

### Honest constraints

- The **ongoing notification is non-negotiable** — Android kills background socket holders, so a
  persistent socket requires a foreground service with a visible notification.
- **Notification Access is a scary-looking permission**; the setup flow must explain why in plain
  language.
- A possible v2 refinement (not v1): gate the foreground service on "A2DP actually connected to this
  PC" so it only runs when the remote could be useful. Deferred to keep v1 simple.

## PC side — `Companion/` module + SMTC publisher

New `Companion/` namespace, mirroring the existing seam+fake pattern (every ABI touch is behind an
interface with a fake and a contract test, exactly like `IAudioDeviceFactory` /
`FakeAudioDeviceFactory`):

- **`ICompanionTransport` / `RfcommCompanionTransport`** — thin adapter over
  `Windows.Devices.Bluetooth.Rfcomm.RfcommDeviceService` + `StreamSocket`. The only class that
  touches the live Bluetooth ABI. Exposes connect / send / received-frames event / disconnect.
- **`MediaProtocol`** — pure framing + encode/decode (length-prefixed frames → typed messages). No
  I/O, no ABI. Unit-tested like `TransportMatcher` / `BackoffSchedule`.
- **`CompanionLink`** — orchestrator: watches phone presence (reuses the manager's presence signal),
  connects through the transport with `BackoffSchedule`, runs the read loop, projects incoming
  messages into a `MediaSnapshot`, turns UI/SMTC events into outbound command frames. Modeled on
  `MusicHalf` / `CallsHalf`.
- **`MediaSnapshot`** — immutable now-playing state (title, artist, album, artHash, playing,
  positionMs + timestamp, durationMs). Projection unit-tested.
- **`ArtCache`** — album art keyed by hash; art fetched once per track.
- **Manifest note (load-bearing).** Opening RFCOMM from the packaged app requires the `bluetooth`
  device capability in `packaging/AppxManifest.xml`; the SDP service UUID must also be declared. The
  implementation plan must add these — RFCOMM connect fails silently without them.
- **`ISmtcPublisher` / `SmtcPublisher`** — wraps `SystemMediaTransportControls` obtained via
  `ISystemMediaTransportControlsInterop::GetForWindow` on a hidden message window (we are packaged,
  so this is clean). Pushes `MediaSnapshot` into the session: `DisplayUpdater` MusicProperties, the
  thumbnail (album art), `PlaybackStatus`, and `UpdateTimelineProperties` (position / duration →
  the seek bar ModernFlyouts draws). Raises `ButtonPressed` (play/pause/next/prev) and
  `PlaybackPositionChangeRequested` (seek), which `ConnectionManager` forwards to the phone.

**Ownership stays honest.** `ConnectionManager` owns `CompanionLink` and `SmtcPublisher` as seams,
exposes the current `MediaSnapshot` + change events and command methods
(`MediaPlayPause()` / `MediaNext()` / `MediaPrevious()` / `MediaSeek(ms)`). `TrayContext` calls
exactly one manager method per interaction and holds no new state. `Settings` gets one
`PhoneRemoteEnabled` flag, written only via the manager. The tray gets at most a "Phone remote"
on/off toggle; non-users see nothing.

## Wire protocol

RFCOMM is a stream, so we frame it: **`[4-byte big-endian length][1-byte type][payload]`**.
Control/metadata payloads are UTF-8 JSON; album art is a raw binary payload (no base64). Everything is
event-driven; nothing is sent on a timer.

**Phone → PC**
- `Hello` — `{protocolVersion, appVersion, phoneName}` — handshake / version negotiation.
- `NowPlaying` — `{title, artist, album, durationMs, artHash?, hasSession}` — on track change.
  `hasSession:false` → nothing playing → PC tears down its SMTC session (flyout goes empty).
- `PlaybackState` — `{status: playing|paused, positionMs, timestampMs, speed}` — only on
  play/pause/seek.
- `AlbumArt` — binary: `[artHash][JPEG bytes]` — only in reply to `RequestArt`.

**PC → Phone**
- `Hello` — `{protocolVersion, pcName}`.
- `Command` — `{action: play|pause|next|previous|seek, positionMs?}`. Discrete `play`/`pause`
  rather than a toggle: SMTC `ButtonPressed` already resolves a media-key play/pause toggle into a
  concrete Play or Pause given the session's current status, and `MediaController` has both, so the
  phone applies them directly without re-deriving state.
- `RequestArt` — `{artHash}` — only on cache-miss.

**Position is set, not streamed.** On each play/pause/seek the phone sends one `PlaybackState`; the PC
writes it into SMTC's timeline once. Windows records the update time and ModernFlyouts advances the
seek bar by interpolation (`position + elapsed × speed`). A normally-playing track sends nothing yet
the bar still moves — this is how we honor "very power efficient" without a position feed.

**No app-level heartbeat.** A periodic ping would wake the phone for nothing. Drops are detected the
cheap way: the RFCOMM read ends (EOF/exception) when the link falls, and the tray app already tracks
phone presence via `LinkMonitor`. Add a minimal ping only if half-open connections prove real in
testing.

**Versioning.** The `Hello` exchange carries `protocolVersion`, so a newer app on either end can
negotiate or degrade rather than misparse. The APK ships in the same release as the PC build, but the
phone updates on its own schedule, so the handshake earns its keep.

## Trust model

The phone is already BT-bonded to the PC for audio, and RFCOMM connects only between bonded devices,
so the PC connects to the specific phone it already tracks (`Settings.PhoneDeviceId`). The phone app
advertises our SDP UUID and, on the **first** connection, shows a one-time
"Allow `<PC name>` to control media?" and remembers the approved PC by address; afterwards it is
silent. No accounts, no TLS. The bond plus that one-time approval is the entire boundary — appropriate
for a sideloaded tool. Any app on the phone could discover the SDP record, but only a bonded device
can open the socket and the phone gates the first connect.

## The coexistence gate (hard go/no-go, built first)

Because the Wi-Fi fallback was ruled out, RFCOMM coexistence with the audio is a **hard gate**, not an
assumption.

1. **RFCOMM-coexistence probe.** A throwaway Android RFCOMM echo server + a throwaway PC RFCOMM
   client. With music routed over A2DP **and** an HFP call up, open the channel, push bytes both
   ways, and verify (a) audio is uninterrupted and the call mic still works, and (b) data
   round-trips. Record it in FINDINGS. **If RFCOMM disturbs the audio, stop and regroup — no silent
   Wi-Fi pivot.**
2. **SMTC probe (phone-free).** Publish a dummy SMTC session from a packaged test build; confirm it
   renders in ModernFlyouts + the native overlay (art / title / seek) and that hardware media keys
   hit our `ButtonPressed` handler. Validates the entire PC-render path before any protocol byte
   exists.

Nothing else proceeds until both pass.

## Failure handling

Rides the patterns already in the app:

- **Link drop** (range / sleep / reboot) → reconnect via `BackoffSchedule`, gated on phone presence;
  tear down the SMTC session so the flyout clears.
- **`NowPlaying{hasSession:false}`** → same teardown.
- **Notification Access revoked** → phone reports "not authorized," PC publishes nothing, phone app
  surfaces the fix.
- **Malformed frame** → log, drop the connection, reconnect. **Never crash the tray** (the existing
  `Teardown`/guard discipline).
- **Resume from sleep** → the existing `PowerNotifier` path re-establishes the link.

## Testing

Follows the project's TDD shape:

- **PC pure-logic units:** `MediaProtocol` framing/encode/decode, `MediaSnapshot` projection,
  SMTC-event→command mapping, `ArtCache`.
- **PC contract tests:** `ICompanionTransport` against a fake socket (mirroring
  `FakeAudioSinkService`); `ISmtcPublisher` fake for the manager wiring without the ABI.
- **Android:** unit-test the MediaSession→message mapping and command application; instrumented/manual
  for the real socket + `MediaSessionManager`.
- **Hardware end-to-end (FINDINGS-style):** reboot reconnect and phone-returns-to-range especially
  (the historically fragile paths); plus skip/seek from the flyout, media keys, and album art shows.

## Build order

1. **Gate:** RFCOMM-coexistence probe + SMTC probe. Nothing proceeds until both pass.
2. **MVP channel:** minimal APK (foreground service, RFCOMM server, read title/artist + apply
   transport) ↔ PC `Companion/` module publishing a text-only SMTC session. Proves control +
   metadata end-to-end, including media keys.
3. **Art + seek:** lazy album-art fetch / cache / thumbnail, and timeline / interpolated seek.
4. **Polish:** phone setup UX (permission explainers), tray on/off toggle + optionality gating,
   reconnect / reboot / resume hardening, APK attached to the GitHub release.

## Out of scope (v1)

- Phone media **volume** control (dropped — see Decisions).
- Wi-Fi / LAN transport and any transport abstraction for it.
- Play Store distribution.
- iOS (companion is Android-only; iOS restricts background BT + MediaRemote too heavily).
- Gating the phone foreground service on A2DP-connected state (possible v2).
