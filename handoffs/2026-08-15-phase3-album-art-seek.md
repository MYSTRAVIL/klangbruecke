# Handoff — Phone media remote, Phase 3 (album art + seek)

_Written 2026-08-15. The MVP (Phase 1 + Phase 2) is done, verified on hardware, and beta-installed.
This brief hands off Phase 3._

> **Note on dates:** today is 2026-08-15. The spec/plan/FINDINGS files carry a `2026-08-12` date in
> their text (an authoring slip) — the timeline is otherwise correct. Don't re-litigate the dates.

## Goal

Add **album art** and a **seek bar** to the phone media remote — the optional companion feature that
mirrors the phone's now-playing into a PC SMTC session (rendered by ModernFlyouts / the native media
overlay) and lets the PC drive transport over Bluetooth RFCOMM. The transport + now-playing-text MVP
already works end-to-end. Phase 3 adds the two remaining pieces of the "full remote" the user asked for.
Volume is explicitly **out of scope** (dropped — the flyout has no per-session volume and PC volume
already controls the A2DP-rendered loudness).

## Current status

**Phases 1 and 2 are complete, committed on `main`, and verified on real hardware:**
- **Phase 1 gate — PASS** (FINDINGS §20): RFCOMM coexists with A2DP music + HFP calls without
  disruption; a self-published SMTC session renders in ModernFlyouts and receives media keys.
- **Phase 2 MVP — done** (FINDINGS §21): PC `Companion/` module (TDD, 4334 tests green), real Android
  companion app (`android/`, APK builds), wired into the app (opt-in `PhoneRemoteEnabled` setting +
  "Phone remote" tray toggle). Proven live: phone's track → GSMTC-visible PC session; PC `Next`/`Previous`
  and synthesized media keys drive the phone (dumpsys-confirmed).
- **Beta installed:** packaged build bumped to **0.3.0.0** and installed on this PC (music + calls +
  phone remote all live together). Companion currently **enabled** (`settings.json` has
  `PhoneRemoteEnabled: true`). The phone has `klangbruecke.remote` installed and its `RemoteService`
  running.

Nothing is pushed to a remote or released. There is **no Phase 3 plan or spec yet** — writing one is
step 1.

## Decisions already made (do not relitigate)

- **Transport: Bluetooth RFCOMM only.** No Wi-Fi, no transport abstraction for one.
- **Companion is owned in the composition root** (`Program.cs` → `PhoneRemote`), **not** inside
  `ConnectionManager` (that class is lock-free/single-threaded/delicate — keep it pristine). See
  FINDINGS §21.
- **Threading:** `RfcommCompanionTransport.FrameReceived`/`Disconnected` fire off-thread and are
  marshaled onto the UI thread by `UiMarshalingTransport`; `CompanionLink` is then single-threaded like
  the rest of `Connection/`. Outbound (`CommandRequested`→`SendAsync`) needs no marshaling.
- **SDP discovery must be UNCACHED** (`BluetoothDevice.GetRfcommServicesForIdAsync(..., Uncached)`) —
  a service advertised after pairing is invisible to the cached selector (FINDINGS §20.2). First connect
  is ~14 s.
- **Album art plan (from the spec):** phone advertises an `artHash` in `NowPlaying`; PC requests the
  blob only on cache-miss (`RequestArt`); art is a **raw binary frame**, downscaled (~400px) JPEG.
- **Seek plan (from the spec):** phone sends `PlaybackState{position, timestamp, speed}` only on
  play/pause/seek (NEVER streamed — power); PC writes it into SMTC's timeline once and ModernFlyouts
  interpolates the bar. SMTC `PlaybackPositionChangeRequested` → a `Command{seek, positionMs}` to the
  phone.
- **Power efficiency is a hard Android constraint:** event-driven only, no timers, no wakelocks,
  blocking I/O. Do not add a position-streaming loop.

## Next steps (ordered)

1. **Read the spec and the two design records first:**
   `docs/superpowers/specs/2026-08-12-phone-media-remote-design.md` (the whole feature),
   `docs/FINDINGS.md` §19–21 (what's empirically true), and
   `docs/superpowers/companion-followups.md` (deferred items — seek validation #4 and the SMTC no-Show
   note #5 are Phase-3-relevant).
2. **Invoke `superpowers:writing-plans`** and write `docs/superpowers/plans/2026-08-15-phase3-art-seek.md`.
   Do NOT re-brainstorm — the spec already decided the design; this is a plan for the two additions.
3. **Extend the wire protocol** (keep PC `MediaProtocol.cs` as the SOURCE OF TRUTH; mirror in Android
   `Protocol.kt`):
   - `NowPlaying` gains `durationMs` (already present) + `artHash` (nullable).
   - New `AlbumArt` binary frame: `[artHash][JPEG bytes]`. New `RequestArt` frame `{artHash}`.
   - `PlaybackState` gains real `positionMs`/`timestampMs`/`speed` (currently only status is used).
   - Reserve/confirm the message-type bytes (existing: Hello=0x01, NowPlaying=0x02, PlaybackState=0x03,
     Command=0x10). Note the Command JSON field is **`command`** not `action`, status strings are
     **`playing`/`paused`** — match the C# exactly.
4. **PC side** (`src/Klangbruecke/Companion/`, TDD, follow the seam+fake pattern):
   `MediaSnapshot` (+ art bytes/hash, position/duration/timestamp), `ArtCache` (hash→bytes),
   `MediaProtocol` (encode/decode the new frames), `CompanionLink` (request art on cache-miss; fold
   position; forward seek), `SmtcPublisher` (set `DisplayUpdater.Thumbnail` from art bytes;
   `UpdateTimelineProperties` from position; handle `PlaybackPositionChangeRequested`).
5. **Android side** (`android/app/src/main/java/klangbruecke/remote/`): `MediaBridge` reads
   `MediaMetadata` album-art bitmap → downscale + JPEG + hash → `artHash` in `NowPlaying`; answers
   `RequestArt` with an `AlbumArt` frame; reads `PlaybackState` position and sends on play/pause/seek;
   applies `seekTo` on a seek command.
6. **Hardware-verify** (adb is yours; phone is Pixel `MYSTRAPIX9`): use/extend the integration harness
   at `<scratchpad>/companion-harness/` (it `<Compile Include>`s the real Companion files). Confirm:
   album art shows in ModernFlyouts, and — critically — the **seek bar renders and scrubs with LIVE
   advancing position** (followup #4; a static position did NOT surface a scrubber in Phase 1). Verify
   `PlaybackPositionChangeRequested` fires and the phone seeks (dumpsys).
7. **Bump the version** (0.3.0.0 → 0.3.1.0), rebuild the MSIX (`packaging/Build-Msix.ps1`) + APK, and
   re-install for beta, when Phase 3 is verified.

## Key files & paths

- Spec: `docs/superpowers/specs/2026-08-12-phone-media-remote-design.md`
- Plans: `docs/superpowers/plans/2026-08-12-phase1-coexistence-smtc-gate.md`,
  `.../2026-08-12-phase2-mvp-channel.md` (Phase 3 = new plan)
- Findings: `docs/FINDINGS.md` §19 (AVRCP NO-GO), §20 (gate), §21 (MVP verified)
- Follow-ups: `docs/superpowers/companion-followups.md`
- PC module: `src/Klangbruecke/Companion/` (MediaProtocol, MediaSnapshot, CompanionLink,
  RfcommCompanionTransport, SmtcPublisher, ISmtcPublisher, ICompanionTransport, PhoneRemote,
  UiMarshalingTransport, MediaCommand, ProtocolMessage) + tests in
  `tests/Klangbruecke.Tests/Companion/` and fakes in `tests/Klangbruecke.Tests/Fakes/`
- Composition root: `src/Klangbruecke/Program.cs` (RunTray), tray: `src/Klangbruecke/TrayContext.cs`,
  settings: `src/Klangbruecke/Config/Settings.cs`
- Android app: `android/` (Gradle 8.7, AGP 8.5.2, Kotlin 1.9.24, compileSdk 34; package
  `klangbruecke.remote`). Build: set `$env:JAVA_HOME="C:\Program Files\Microsoft\jdk-17.0.20.8-hotspot"`
  and `$env:ANDROID_HOME="C:\Users\MYSTRAVIL\AppData\Local\Android\Sdk"`, then `.\gradlew.bat assembleDebug`.
- Integration harness (throwaway, in the session scratchpad; a NEW session gets a new scratchpad —
  rebuild it from the Phase-2 pattern if needed): a WinExe that `<Compile Include>`s the real Companion
  files + `Diagnostics/{ILog,Log,Teardown}.cs`, `Connection/BackoffSchedule.cs`, `Platform/IScheduler.cs`,
  supplies a `ConsoleLog`+`TimerScheduler`, and wraps `SmtcPublisher` so `Publish` marshals onto the
  message-loop thread. Drive it non-interactively (it can't take keystrokes under the tool).
- Packaging: `packaging/Build-Msix.ps1` (Release), signs with `packaging/KlangbrueckeDev.pfx`; install
  via **Windows PowerShell 5.1** (`powershell.exe`, NOT pwsh — pwsh can't load the Appx module):
  `Add-AppxPackage -Path artifacts\Klangbruecke.msix`. Relaunch via
  `explorer.exe "shell:AppsFolder\Klangbruecke_vwcm37s2b7kd8!Klangbruecke"`.

## Open questions / risks / gotchas

- **Seek is the real unknown.** Phase 1's static-position probe did not surface a working scrubber in
  ModernFlyouts. Phase 3 must prove it with a real, advancing position feed. If ModernFlyouts still
  won't scrub, investigate whether it needs `PlaybackStatus=Playing` + periodic timeline updates vs
  pure interpolation — but do NOT add a phone-side position stream (power). Consider updating SMTC
  timeline on the PC side from an interpolated local clock if needed.
- **Wire compatibility is load-bearing.** Read the C# `MediaProtocol.cs` as the source of truth and
  mirror it exactly in Kotlin. A field-name mismatch fails silently.
- **Album art size.** Downscale on the phone (~400px, JPEG) before sending; re-send only on track change
  (hash), fetched lazily on cache-miss. Don't push art on every state tick.
- **Do NOT touch `ConnectionManager`** or add `ConfigureAwait(false)` anywhere in `Companion/` or
  `Connection/` — both break the single-threaded contract.
- **Multi-agent git hazard (project memory):** if you fan out subagents that EDIT the repo, run them
  ONE AT A TIME and verify the branch after each — parallel agents corrupt the shared working dir.
  Read-only/scratchpad subagents can run in parallel.
- **Environment left as:** installed packaged app 0.3.0.0 running; `settings.json` has
  `PhoneRemoteEnabled: true`; phone `RemoteService` running; USB debugging on. The two SetupActivity
  bugs (followups #1, #2) are Phase 4, not Phase 3.
- **Commit proactively** per logical unit (project convention commits to `main`). Push only on request.
