# Phase 2 — MVP Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the phone-media-remote end-to-end at its minimum: the phone's **now-playing text** (title/artist) appears in the PC's SMTC session (rendered by ModernFlyouts/native overlay), and **transport control** (play/pause/next/previous) from the PC drives the phone's real media app — over Bluetooth RFCOMM.

**Architecture:** A real Android companion app (foreground service, loop-accepting RFCOMM server, reads the active `MediaSession`, applies transport commands) talks to a new PC `Companion/` module (RFCOMM client with uncached SDP discovery, a framed protocol codec, and an SMTC publisher) over the link proven in FINDINGS §20. No album art, no seek, no volume — those are Phase 3+. The PC module follows the repo's seam+fake+contract TDD pattern; `ConnectionManager` owns it as one more seam.

**Tech Stack:** PC — .NET 8 `net8.0-windows10.0.19041.0`, WinForms host, `Windows.Devices.Bluetooth.Rfcomm` + `StreamSocket`, `SystemMediaTransportControls` via interop, xUnit (existing `Klangbruecke.Tests`). Android — Kotlin, JDK 17 + Android SDK 34, `MediaSessionManager` (Notification Access), `BluetoothServerSocket`, foreground service.

## Global Constraints

- PC target `net8.0-windows10.0.19041.0`; **do not raise the floor**.
- ASCII **`Klangbruecke`** everywhere (folder, namespace, ids) — no umlaut.
- **Shared protocol constants**, defined once per side and identical across both:
  - RFCOMM SDP UUID `6f5e4d3c-2b1a-4c8d-9e7f-0a1b2c3d4e5f`, service name `Klangbruecke`.
  - Frame = `[4-byte big-endian length][1-byte type][payload]`. Length counts `type + payload`.
  - Message type bytes: `Hello=0x01`, `NowPlaying=0x02`, `PlaybackState=0x03`, `Command=0x10`. (Art/seek/RequestArt types are reserved for Phase 3 and NOT implemented here.)
  - JSON payloads are UTF-8. `protocolVersion = 1`.
- **Discovery must be uncached** (FINDINGS §20.2): `BluetoothDevice.GetRfcommServicesForIdAsync(id, BluetoothCacheMode.Uncached)`. The cached `DeviceInformation` selector does NOT see a service advertised after pairing.
- **SMTC interop recipe is fixed** (FINDINGS §20.1): call `GetForWindow` via a `delegate* unmanaged[Stdcall]` vtable pointer with `riid = IID_IInspectable` (`AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90`), then `MarshalInspectable<SystemMediaTransportControls>.FromAbi`. Do NOT use `Marshal.GetObjectForIUnknown` (removed in .NET 5+) or `typeof(SystemMediaTransportControls).GUID` as the riid (→ E_NOINTERFACE).
- **The tray app never shows a window and never crashes** — all new seams dispose via `Teardown.Quietly`, all background loops guard-and-log, mirroring existing `Connection/` code.
- **Android service must loop-accept** — re-listen after each client disconnect (the §20 spike's one-shot accept was a spike limitation).
- **Power efficiency (Android):** event-driven only (`MediaController.Callback`), blocking I/O, no wakelocks, no timers, no position streaming (position/seek are Phase 3 anyway).

---

## File Structure

**PC (in repo):**
- `src/Klangbruecke/Companion/MediaCommand.cs` — enum of transport actions.
- `src/Klangbruecke/Companion/MediaSnapshot.cs` — immutable now-playing model (Phase 2 fields only).
- `src/Klangbruecke/Companion/ProtocolMessage.cs` — typed message records + `MessageType` enum.
- `src/Klangbruecke/Companion/MediaProtocol.cs` — pure framing + encode/decode.
- `src/Klangbruecke/Companion/ICompanionTransport.cs` — seam: connect/send/received-event/disconnect.
- `src/Klangbruecke/Companion/RfcommCompanionTransport.cs` — the RFCOMM ABI adapter (uncached discovery).
- `src/Klangbruecke/Companion/ISmtcPublisher.cs` — seam: Publish(MediaSnapshot)/Clear + Command event.
- `src/Klangbruecke/Companion/SmtcPublisher.cs` — the SMTC ABI adapter (interop recipe).
- `src/Klangbruecke/Companion/CompanionLink.cs` — orchestrator (transport ↔ protocol ↔ snapshot ↔ publisher), backoff reconnect.
- `tests/Klangbruecke.Tests/Companion/MediaProtocolTests.cs`, `MediaSnapshotTests.cs`, `CompanionLinkTests.cs`, plus `Fakes/FakeCompanionTransport.cs`, `Fakes/FakeSmtcPublisher.cs`.

**Android (in repo, new Gradle project under `android/`):**
- `android/` — Gradle 8.7 wrapper, `settings.gradle.kts`, `build.gradle.kts`.
- `android/app/src/main/AndroidManifest.xml` — permissions + service + notification-listener.
- `.../java/klangbruecke/remote/RemoteService.kt` — foreground service: RFCOMM loop-accept + read/write.
- `.../java/klangbruecke/remote/MediaBridge.kt` — `MediaSessionManager` read + `MediaController` apply.
- `.../java/klangbruecke/remote/Protocol.kt` — frame encode/decode (mirror of `MediaProtocol`).
- `.../java/klangbruecke/remote/KlangbrueckeNotificationListener.kt` — enables `getActiveSessions`.
- `.../java/klangbruecke/remote/SetupActivity.kt` — permission grants + status.

---

## PART A — PC Companion module (TDD, self-verifiable via `dotnet test`)

### Task A1: MediaCommand + MediaSnapshot (pure data)

**Files:**
- Create: `src/Klangbruecke/Companion/MediaCommand.cs`, `src/Klangbruecke/Companion/MediaSnapshot.cs`
- Test: `tests/Klangbruecke.Tests/Companion/MediaSnapshotTests.cs`

**Interfaces:**
- Produces: `enum MediaCommand { Play, Pause, Next, Previous }`; `record MediaSnapshot(string Title, string Artist, string Album, bool IsPlaying, bool HasSession)` with a static `MediaSnapshot Empty` where `HasSession=false`.

- [ ] **Step 1: Write the failing test**
```csharp
public class MediaSnapshotTests
{
    [Fact]
    public void Empty_HasNoSession()
    {
        Assert.False(MediaSnapshot.Empty.HasSession);
        Assert.Equal("", MediaSnapshot.Empty.Title);
    }

    [Fact]
    public void Snapshot_RoundTripsFields()
    {
        var s = new MediaSnapshot("T", "A", "Al", IsPlaying: true, HasSession: true);
        Assert.Equal("T", s.Title);
        Assert.True(s.IsPlaying);
        Assert.True(s.HasSession);
    }
}
```
- [ ] **Step 2: Run — expect FAIL** (`dotnet test --filter MediaSnapshotTests`) — types not defined.
- [ ] **Step 3: Implement** `MediaCommand` enum and the `MediaSnapshot` record with `Empty => new("", "", "", false, false)`.
- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** `git add ... && git commit -m "feat(companion): MediaCommand + MediaSnapshot"`

### Task A2: ProtocolMessage + MediaProtocol codec (pure)

**Files:**
- Create: `src/Klangbruecke/Companion/ProtocolMessage.cs`, `src/Klangbruecke/Companion/MediaProtocol.cs`
- Test: `tests/Klangbruecke.Tests/Companion/MediaProtocolTests.cs`

**Interfaces:**
- Consumes: `MediaCommand`, `MediaSnapshot`.
- Produces:
  - `enum MessageType : byte { Hello=0x01, NowPlaying=0x02, PlaybackState=0x03, Command=0x10 }`
  - `static byte[] MediaProtocol.EncodeCommand(MediaCommand c)` → a full frame.
  - `static byte[] MediaProtocol.EncodeHello(int protocolVersion, string pcName)` → a full frame.
  - `static bool MediaProtocol.TryReadFrame(ref ReadOnlySpan<byte> buffer, out MessageType type, out ReadOnlyMemory<byte> payload)` — consumes one frame from an accumulation buffer; returns false if incomplete.
  - `static (MediaSnapshot snapshot, bool isPlaybackOnly) DecodeInbound(MessageType type, ReadOnlyMemory<byte> payload, MediaSnapshot prior)` — folds a NowPlaying/PlaybackState frame into an updated snapshot (PlaybackState only flips `IsPlaying`; NowPlaying replaces text + sets HasSession from the frame).

- [ ] **Step 1: Write failing tests**
```csharp
public class MediaProtocolTests
{
    [Fact]
    public void EncodeCommand_FramesTypeAndPayload()
    {
        var frame = MediaProtocol.EncodeCommand(MediaCommand.Next);
        // [len(4)][type(1)][json...]; len covers type+payload
        int len = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
        Assert.Equal(frame.Length - 4, len);
        Assert.Equal((byte)MessageType.Command, frame[4]);
    }

    [Fact]
    public void TryReadFrame_ReturnsFalse_WhenIncomplete()
    {
        var full = MediaProtocol.EncodeCommand(MediaCommand.Play);
        ReadOnlySpan<byte> partial = full.AsSpan(0, full.Length - 2);
        Assert.False(MediaProtocol.TryReadFrame(ref partial, out _, out _));
    }

    [Fact]
    public void TryReadFrame_ParsesOneFrame_AndAdvances()
    {
        var a = MediaProtocol.EncodeCommand(MediaCommand.Play);
        var b = MediaProtocol.EncodeCommand(MediaCommand.Pause);
        var combined = a.Concat(b).ToArray();
        ReadOnlySpan<byte> span = combined;
        Assert.True(MediaProtocol.TryReadFrame(ref span, out var t1, out _));
        Assert.Equal(MessageType.Command, t1);
        Assert.Equal(b.Length, span.Length); // advanced past frame a
    }

    [Fact]
    public void DecodeInbound_NowPlaying_ReplacesText()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"Al\",\"durationMs\":0,\"hasSession\":true}");
        var (snap, _) = MediaProtocol.DecodeInbound(MessageType.NowPlaying, payload, MediaSnapshot.Empty);
        Assert.Equal("T", snap.Title);
        Assert.True(snap.HasSession);
    }

    [Fact]
    public void DecodeInbound_PlaybackState_OnlyFlipsIsPlaying()
    {
        var prior = new MediaSnapshot("T", "A", "Al", false, true);
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"status\":\"playing\",\"positionMs\":0,\"timestampMs\":0,\"speed\":1.0}");
        var (snap, _) = MediaProtocol.DecodeInbound(MessageType.PlaybackState, payload, prior);
        Assert.True(snap.IsPlaying);
        Assert.Equal("T", snap.Title); // text unchanged
    }
}
```
- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement** `ProtocolMessage.cs` (`MessageType`) and `MediaProtocol.cs`. Use `System.Text.Json` for payloads. Big-endian length via `BinaryPrimitives.WriteInt32BigEndian`. `TryReadFrame` reads the 4-byte length, checks `buffer.Length >= 4 + len`, slices type+payload, advances the ref span. `DecodeInbound` deserializes small DTOs; map `status=="playing"` → `IsPlaying=true`.
- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** `"feat(companion): framed protocol codec"`.

### Task A3: ICompanionTransport seam + FakeCompanionTransport

**Files:**
- Create: `src/Klangbruecke/Companion/ICompanionTransport.cs`
- Create: `tests/Klangbruecke.Tests/Fakes/FakeCompanionTransport.cs`

**Interfaces:**
- Produces:
```csharp
interface ICompanionTransport : IDisposable
{
    Task<bool> TryConnectAsync(CancellationToken ct);   // discover (uncached) + connect
    Task SendAsync(byte[] frame, CancellationToken ct);
    event EventHandler<byte[]> FrameReceived;           // one decoded frame's raw bytes (type+payload, no length)
    event EventHandler Disconnected;
    bool IsConnected { get; }
}
```
- `FakeCompanionTransport`: records sent frames, lets tests raise `FrameReceived`/`Disconnected`, and a settable `TryConnectAsync` result.

- [ ] **Step 1** Write `ICompanionTransport.cs` (interface only — no test yet; it's a seam).
- [ ] **Step 2** Write `FakeCompanionTransport.cs` implementing it with public hooks (`Raise(byte[])`, `RaiseDisconnected()`, `List<byte[]> Sent`, `bool NextConnectResult`).
- [ ] **Step 3** Build to confirm it compiles: `dotnet build`.
- [ ] **Step 4: Commit** `"feat(companion): transport seam + fake"`.

### Task A4: ISmtcPublisher seam + FakeSmtcPublisher

**Files:**
- Create: `src/Klangbruecke/Companion/ISmtcPublisher.cs`
- Create: `tests/Klangbruecke.Tests/Fakes/FakeSmtcPublisher.cs`

**Interfaces:**
- Produces:
```csharp
interface ISmtcPublisher : IDisposable
{
    void Publish(MediaSnapshot snapshot);   // create/update session; if !HasSession, tear down
    event EventHandler<MediaCommand> CommandRequested;   // button/media-key -> forward to phone
}
```
- `FakeSmtcPublisher`: records `Published` snapshots; `RaiseCommand(MediaCommand)`.

- [ ] **Step 1** Write the interface. **Step 2** Write the fake. **Step 3** `dotnet build`. **Step 4** Commit `"feat(companion): SMTC publisher seam + fake"`.

### Task A5: CompanionLink orchestrator (TDD against fakes)

**Files:**
- Create: `src/Klangbruecke/Companion/CompanionLink.cs`
- Test: `tests/Klangbruecke.Tests/Companion/CompanionLinkTests.cs`

**Interfaces:**
- Consumes: `ICompanionTransport`, `ISmtcPublisher`, `MediaProtocol`, `IScheduler`/`BackoffSchedule` (existing).
- Produces: `CompanionLink(ICompanionTransport, ISmtcPublisher, ...)` with `Start()`, `Dispose()`. Behavior: on connect, sends `Hello`; on `FrameReceived`, decodes and `Publish`es the folded snapshot; on `ISmtcPublisher.CommandRequested`, `SendAsync(EncodeCommand(...))`; on `Disconnected`, `Publish(MediaSnapshot.Empty)` and schedule a reconnect via `BackoffSchedule`.

- [ ] **Step 1: Write failing tests**
```csharp
public class CompanionLinkTests
{
    [Fact]
    public async Task OnConnect_SendsHello()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, /* scheduler etc. */);
        await link.StartAsync();
        Assert.Contains(t.Sent, f => f[4] == (byte)MessageType.Hello);
    }

    [Fact]
    public async Task NowPlayingFrame_PublishesSnapshot()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, ...);
        await link.StartAsync();
        t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":0,\"hasSession\":true}"));
        Assert.Equal("T", p.Published.Last().Title);
    }

    [Fact]
    public async Task SmtcCommand_SendsCommandFrame()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, ...);
        await link.StartAsync();
        p.RaiseCommand(MediaCommand.Next);
        Assert.Contains(t.Sent, f => f[4] == (byte)MessageType.Command);
    }

    [Fact]
    public async Task OnDisconnect_ClearsSession()
    {
        var t = new FakeCompanionTransport { NextConnectResult = true };
        var p = new FakeSmtcPublisher();
        using var link = new CompanionLink(t, p, ...);
        await link.StartAsync();
        t.RaiseDisconnected();
        Assert.False(p.Published.Last().HasSession);
    }
}
```
(`FrameOf` is a small test helper that builds a frame from a type + JSON string.)
- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement** `CompanionLink`. Accumulate received bytes, use `MediaProtocol.TryReadFrame` in a loop, fold via `DecodeInbound`, `Publish`. Guard every callback; log via `Log`. Reconnect on disconnect with the existing `BackoffSchedule` + `IScheduler` (match `MusicHalf`'s construction — read that file for the exact seam types).
- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** `"feat(companion): CompanionLink orchestrator"`.

### Task A6: RfcommCompanionTransport (ABI adapter — build + integration-verify)

**Files:**
- Create: `src/Klangbruecke/Companion/RfcommCompanionTransport.cs`

**Interfaces:**
- Consumes: the UUID constant, `MediaProtocol` (for length-framing on the read side).
- Produces: `ICompanionTransport` over `StreamSocket`.

- [ ] **Step 1: Implement** using the **uncached** discovery from §20.2 and a background read loop that accumulates bytes and raises `FrameReceived` per complete frame (reuse `MediaProtocol.TryReadFrame`). Key skeleton:
```csharp
var sel = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
foreach (var info in await DeviceInformation.FindAllAsync(sel)) {
    using var bt = await BluetoothDevice.FromIdAsync(info.Id);
    var r = await bt.GetRfcommServicesForIdAsync(
        RfcommServiceId.FromUuid(UUID), BluetoothCacheMode.Uncached);
    if (r.Services.Count > 0) { /* connect StreamSocket to r.Services[0] */ break; }
}
```
Read loop: `DataReader` with `InputStreamOptions.Partial`, append to a growing buffer, drain frames.
- [ ] **Step 2: Build** `dotnet build`.
- [ ] **Step 3: Integration-verify (self, via adb).** Temporarily point a tiny console harness (or a `--selftest` switch) at the running Phase 2 Android app; confirm `TryConnectAsync` returns true and a `Hello` from the phone raises `FrameReceived`. (Full end-to-end is Task C1.)
- [ ] **Step 4: Commit** `"feat(companion): RFCOMM transport (uncached discovery)"`.

### Task A7: SmtcPublisher (ABI adapter — the §20.1 interop)

**Files:**
- Create: `src/Klangbruecke/Companion/SmtcPublisher.cs`

**Interfaces:**
- Produces: `ISmtcPublisher` bound to a hidden HWND (reuse the tray app's message loop; create a hidden `Form` or a message-only window owned by the module).

- [ ] **Step 1: Implement** the interop `GetForWindow` exactly per FINDINGS §20.1 (vtable function pointer, `IID_IInspectable`, `MarshalInspectable.FromAbi`). `Publish`: if `!HasSession` → `smtc.IsEnabled=false` + `DisplayUpdater.ClearAll()+Update()`; else set MusicProperties Title/Artist, `PlaybackStatus` from `IsPlaying`, enable Play/Pause/Next/Previous, `Update()`. `ButtonPressed` → map `SystemMediaTransportControlsButton` to `MediaCommand` and raise `CommandRequested`.
- [ ] **Step 2: Build.**
- [ ] **Step 3: Integration-verify (self, via GSMTC).** From a harness, `Publish` a snapshot, then enumerate `GlobalSystemMediaTransportControlsSessionManager.RequestAsync().GetSessions()` and assert a session with our AUMID and the expected title exists — proves it published without needing eyes. Human check (yours): it renders in ModernFlyouts.
- [ ] **Step 4: Commit** `"feat(companion): SMTC publisher"`.

---

## PART B — Android companion app

### Task B1: Scaffold the real Gradle project under `android/`

**Files:** `android/settings.gradle.kts`, `android/build.gradle.kts`, `android/app/build.gradle.kts`, wrapper, `local.properties` (gitignored), `AndroidManifest.xml`.

- [ ] **Step 1** Scaffold (Gradle 8.7, AGP 8.5.2, Kotlin 1.9.24, compileSdk 34, minSdk 26, targetSdk 34, `namespace/applicationId = klangbruecke.remote`). Add `.gitignore` for `android/.gradle`, `android/app/build`, `android/local.properties`.
- [ ] **Step 2** `AndroidManifest.xml`: permissions `BLUETOOTH_CONNECT`, legacy `BLUETOOTH`/`BLUETOOTH_ADMIN` (`maxSdkVersion=30`), `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_CONNECTED_DEVICE`, `POST_NOTIFICATIONS`; declare `SetupActivity` (launcher), `RemoteService` (`foregroundServiceType="connectedDevice"`), and `KlangbrueckeNotificationListener` (`BIND_NOTIFICATION_LISTENER_SERVICE`, intent-filter `android.service.notification.NotificationListenerService`).
- [ ] **Step 3** `./gradlew assembleDebug` → BUILD SUCCESSFUL (self-verify, no phone).
- [ ] **Step 4** Commit `"feat(android): scaffold companion app"`.

### Task B2: Protocol.kt (mirror the wire format)

**Files:** `.../remote/Protocol.kt` + a JVM unit test `.../test/ProtocolTest.kt`.

- [ ] **Step 1** Write a failing JVM unit test: encoding a `NowPlaying` produces `[len][0x02][json]` with big-endian length; `readFrame` on a split buffer returns null until complete. **Step 2** Run `./gradlew test` → FAIL. **Step 3** Implement `Protocol.kt` (same type bytes, big-endian length, `org.json` or kotlinx-serialization for payloads). **Step 4** `./gradlew test` → PASS. **Step 5** Commit `"feat(android): protocol codec + tests"`.

### Task B3: MediaBridge.kt — read session + apply commands

**Files:** `.../remote/MediaBridge.kt`, `.../remote/KlangbrueckeNotificationListener.kt`.

- [ ] **Step 1** Implement `KlangbrueckeNotificationListener` (empty `NotificationListenerService` — its existence grants `getActiveSessions`). **Step 2** `MediaBridge`: `MediaSessionManager.getActiveSessions(ComponentName(this, KlangbrueckeNotificationListener::class))` → first controller; expose `currentSnapshot()` (title/artist/album + isPlaying via `PlaybackState.state`), register a `MediaController.Callback` to invoke an `onChanged` lambda, and `apply(command)` → `controller.transportControls.play()/pause()/skipToNext()/skipToPrevious()`. **Step 3** Build. **Step 4** Commit `"feat(android): MediaSession bridge"`.

### Task B4: RemoteService.kt — foreground service + RFCOMM loop-accept

**Files:** `.../remote/RemoteService.kt`.

- [ ] **Step 1** Foreground service with an ongoing notification (channel `klangbruecke-remote`). On start, open `listenUsingRfcommWithServiceRecord("Klangbruecke", UUID)`; **loop**: `accept()` → serve one client (read frames → `MediaBridge.apply`; `MediaController.Callback.onChanged` → send `NowPlaying`+`PlaybackState`) → on disconnect, loop back to `accept()` (re-listen). Send `Hello` + an initial `NowPlaying` on connect. Blocking I/O, no wakelocks, no timers (Global Constraints). **Step 2** Build. **Step 3** Commit `"feat(android): foreground RFCOMM service"`.

### Task B5: SetupActivity.kt — permission grants + status

**Files:** `.../remote/SetupActivity.kt` + layout.

- [ ] **Step 1** A single screen: buttons/links to grant **Notification Access** (`ACTION_NOTIFICATION_LISTENER_SETTINGS`) and **Bluetooth Connect** (runtime), a plain-language why, a Start/Stop for `RemoteService`, and a live status line. **Step 2** `assembleDebug`. **Step 3** Commit `"feat(android): setup screen"`.

---

## PART C — Integration & wiring (mostly self-verified via adb)

### Task C1: End-to-end MVP on hardware

- [ ] **Step 1 (self, adb):** `adb install -r android/app/build/outputs/apk/debug/app-debug.apk`; grant Notification Access via `adb shell cmd notification allow_listener klangbruecke.remote/klangbruecke.remote.KlangbrueckeNotificationListener` and `pm grant ... BLUETOOTH_CONNECT`; start the service (`adb shell am start-foreground-service ...` or launch SetupActivity + Start); confirm `on_srv_rfc_listen_started` in logcat.
- [ ] **Step 2 (self, adb):** start real media on the phone (`adb shell am start` a track / `input keyevent 126` play); read `adb shell dumpsys media_session` to know the true title/artist.
- [ ] **Step 3 (self):** run the PC app (or the module harness). Assert via GSMTC enumeration that a session appears with the **same title/artist** `dumpsys` reported → now-playing text path proven.
- [ ] **Step 4 (self, adb):** trigger a PC-side `Command` (Next/Pause) through the module; re-read `dumpsys media_session` and confirm the phone's playback state/track changed → transport-control path proven.
- [ ] **Step 5 (you):** the subjective confirm — the track shows in ModernFlyouts and the keyboard media keys drive the phone. (I'll set it up and hand off.)
- [ ] **Step 6** Record the MVP result in FINDINGS (extend §20 or add §21) and commit.

### Task C2: Wire into ConnectionManager + Settings (opt-in)

**Files:** `src/Klangbruecke/Connection/ConnectionManager.cs`, `src/Klangbruecke/Config/Settings.cs`, `src/Klangbruecke/TrayContext.cs` (one menu toggle).

- [ ] **Step 1** Add `Settings.PhoneRemoteEnabled` (default false). **Step 2** `ConnectionManager` constructs + owns `CompanionLink` (real `RfcommCompanionTransport` + `SmtcPublisher`) when enabled; disposes via `Teardown.Quietly`. **Step 3** `TrayContext`: a "Phone remote" checkbox item calling one manager method (mirror the Sounds/AutoReconnect items). **Step 4** Run existing test suite (`dotnet test`) green. **Step 5** Commit `"feat(companion): opt-in wiring + tray toggle"`.

---

## Self-Review

- **Spec coverage:** MVP scope of the spec ("transport + now-playing text end-to-end", SMTC publish, RFCOMM, opt-in) is covered by A1–A7 (PC), B1–B5 (Android), C1–C2 (integration/wiring). Album art, seek, and volume are explicitly Phase 3+ and correctly absent. Power-efficiency constraints are carried into B3/B4.
- **Placeholder scan:** No TBD/TODO. ABI tasks (A6/A7/B4) reference the exact §20.1/§20.2 recipes rather than hand-waving; the one deferred detail (`CompanionLink`'s exact scheduler seam) points the implementer to read `MusicHalf` for the concrete types, which is a real repo pattern, not a placeholder.
- **Type consistency:** `MediaCommand`, `MediaSnapshot`, `MessageType` (bytes `0x01/0x02/0x03/0x10`), the UUID, and the frame layout are identical across PC (A2) and Android (B2). `ICompanionTransport`/`ISmtcPublisher` signatures used in A5's tests match A3/A4.
