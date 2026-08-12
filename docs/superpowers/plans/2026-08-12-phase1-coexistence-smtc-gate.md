# Phase 1 — RFCOMM Coexistence + SMTC Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the two load-bearing assumptions of the phone-media-remote design *before* any feature code exists: (1) an RFCOMM channel to the phone coexists with A2DP + HFP audio without disrupting either, and (2) Klangbruecke can publish a System Media Transport Controls (SMTC) session that ModernFlyouts and the native overlay render and that hardware media keys target.

**Architecture:** Three throwaway **spikes** — a PC-side SMTC-publish probe, an Android RFCOMM echo server, and a PC RFCOMM client — exercised together on real hardware. The outcome is recorded as a go/no-go in `docs/FINDINGS.md` §20. This plan builds only what's needed to answer the gate; the real feature is deferred to post-gate plans (see Coverage note).

**Tech Stack:** .NET 8 (`net8.0-windows10.0.19041.0`), WinRT SMTC interop (`ISystemMediaTransportControlsInterop`), `Windows.Devices.Bluetooth.Rfcomm` + `StreamSocket`; Android (Kotlin, minimal, JDK 17 + Android SDK cmdline-tools, `BluetoothServerSocket`).

## Global Constraints

- Target framework `net8.0-windows10.0.19041.0`; **do not raise the floor** (19041 is the floor; dev machine is 19045).
- ASCII **`Klangbruecke`** everywhere — no umlaut in any name, string, or identifier.
- **Shared RFCOMM SDP service UUID**, defined once and used verbatim on both ends: **`6f5e4d3c-2b1a-4c8d-9e7f-0a1b2c3d4e5f`** (PC client selector via `RfcommServiceId.FromUuid` + Android `listenUsingRfcommWithServiceRecord`). Service name string: `KlangbrueckeGate`. Both ends must match exactly or discovery silently returns nothing.
- **Spikes are throwaway.** They live in the session scratchpad (`C:\Users\MYSTRA~1\AppData\Local\Temp\claude\...\scratchpad`), **not** the repo tree. They are exploratory validation — **not** TDD, not test-first. The *only* permanent artifacts from this plan are the FINDINGS §20 writeup and this plan file.
- **Never trust a UI "connected" indicator** — verify Bluetooth/audio state with `Get-PnpDevice` (per CLAUDE.md).
- **Empirical-on-this-machine** discipline: record what was observed on 19045 vs what docs claim, in FINDINGS §20.
- The tray app must **never crash / never show a window**; spikes are separate processes and do not touch the shipping app.

---

## Coverage note (why this plan stops at the gate)

The spec's build order is explicit: *"Nothing else proceeds until both [probes] pass."* The coexistence gate can veto the entire RFCOMM transport. Writing detailed plans for the PC `Companion/` module and the Android companion app before that veto is resolved would be exactly the wasted work the gate exists to prevent. On a **PASS**, Phases 2–4 (PC module, Android companion, art+seek, polish) each get their own plan. On a **FAIL**, we revisit the transport decision (Wi-Fi was ruled out, so this means regrouping, not an automatic pivot).

---

## Task 0: Install the Android build toolchain

**Files:** none in the repo — this is environment setup. Folded in as a task because Task 2 cannot build without it.

**Interfaces:**
- Produces: a working `JAVA_HOME` (JDK 17), `ANDROID_HOME` with `platform-tools`, `platforms;android-34`, `build-tools;34.0.0`, and `sdkmanager` on PATH — everything Task 2's `./gradlew assembleDebug` needs.

- [ ] **Step 1: Install JDK 17 via winget**

Run: `winget install --id Microsoft.OpenJDK.17 --accept-source-agreements --accept-package-agreements`
Expected: exit 0; a JDK 17 under `C:\Program Files\Microsoft\jdk-17*`.

- [ ] **Step 2: Verify JDK 17 is resolvable**

Run: `& "C:\Program Files\Microsoft\jdk-17.0.13.11-hotspot\bin\java.exe" -version` (adjust the folder to the installed version)
Expected: `openjdk version "17.x"`. Capture the exact path for `JAVA_HOME`.

- [ ] **Step 3: Download Android command-line tools**

Run (PowerShell):
```powershell
$sdk = "$env:LOCALAPPDATA\Android\Sdk"
New-Item -ItemType Directory -Force "$sdk\cmdline-tools" | Out-Null
$zip = "$env:TEMP\android-cmdline-tools.zip"
Invoke-WebRequest -Uri "https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip" -OutFile $zip
Expand-Archive $zip "$sdk\cmdline-tools" -Force
# sdkmanager expects cmdline-tools/latest/bin/... — rename the unzipped 'cmdline-tools' to 'latest'
Rename-Item "$sdk\cmdline-tools\cmdline-tools" "$sdk\cmdline-tools\latest"
```
Expected: `$env:LOCALAPPDATA\Android\Sdk\cmdline-tools\latest\bin\sdkmanager.bat` exists.

- [ ] **Step 4: Set env vars for the session and install SDK packages**

Run (PowerShell):
```powershell
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:JAVA_HOME = "C:\Program Files\Microsoft\jdk-17.0.13.11-hotspot"  # adjust to installed version
$sdkm = "$env:ANDROID_HOME\cmdline-tools\latest\bin\sdkmanager.bat"
& $sdkm --licenses  # accept all (pipe 'y' if non-interactive: 'y'*20 | & $sdkm --licenses)
& $sdkm "platform-tools" "platforms;android-34" "build-tools;34.0.0"
```
Expected: packages install under `$env:ANDROID_HOME`; exit 0.

- [ ] **Step 5: Verify the toolchain**

Run: `& "$env:ANDROID_HOME\cmdline-tools\latest\bin\sdkmanager.bat" --list_installed`
Expected: `platform-tools`, `platforms;android-34`, `build-tools;34.0.0` all listed. Record the exact `JAVA_HOME` and `ANDROID_HOME` values in the plan-run notes so Task 2 reuses them. **Persist them** to the user environment so a fresh shell inherits them:
```powershell
[Environment]::SetEnvironmentVariable("ANDROID_HOME", "$env:LOCALAPPDATA\Android\Sdk", "User")
[Environment]::SetEnvironmentVariable("JAVA_HOME", "C:\Program Files\Microsoft\jdk-17.0.13.11-hotspot", "User")
```

---

## Task 1: SMTC-publish spike (PC-only — unblocked, run first)

**Files:**
- Create: `<scratchpad>/smtc-probe/SmtcProbe.csproj`
- Create: `<scratchpad>/smtc-probe/Program.cs`
- Create: `<scratchpad>/smtc-probe/cover.jpg` (any ~400px JPEG for the thumbnail)

**Interfaces:**
- Produces: empirical confirmation (yes/no) that an SMTC session published from a .NET desktop process on 19045 renders in ModernFlyouts + the native media overlay, that hardware media keys fire `ButtonPressed`, and that ModernFlyouts seeking fires `PlaybackPositionChangeRequested`. Also: whether packaged identity is required (test unpackaged first).

- [ ] **Step 1: Create the probe project**

`SmtcProbe.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>
```
(WinForms gives a trivial HWND via a hidden `Form`; `WinExe` avoids a console window.)

- [ ] **Step 2: Implement the SMTC publisher**

`Program.cs` — get SMTC for the hidden form's HWND via the interop, set metadata + timeline, log events:
```csharp
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WinRT; // for interop cast
using Windows.Media;
using Windows.Storage.Streams;

[ComImport, Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISystemMediaTransportControlsInterop
{
    [return: MarshalAs(UnmanagedType.IInspectable)]
    object GetForWindow(IntPtr hwnd, [In] ref Guid riid);
}

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var form = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false };
        form.Load += (_, _) => form.Hide();
        form.Shown += (_, _) =>
        {
            var interopGuid = typeof(ISystemMediaTransportControlsInterop).GUID;
            var factory = (ISystemMediaTransportControlsInterop)
                WinRT.MarshalInspectable<object>.FromAbi(
                    /* activation factory */ IntPtr.Zero); // see note below
            // Simpler path: use the CsWinRT-generated interop helper if available; otherwise
            // P/Invoke RoGetActivationFactory for "Windows.Media.SystemMediaTransportControls".
            var smtcGuid = typeof(SystemMediaTransportControls).GUID;
            var smtc = (SystemMediaTransportControls)factory.GetForWindow(form.Handle, ref smtcGuid);

            smtc.IsPlayEnabled = true; smtc.IsPauseEnabled = true;
            smtc.IsNextEnabled = true; smtc.IsPreviousEnabled = true;
            smtc.PlaybackStatus = MediaPlaybackStatus.Playing;

            var upd = smtc.DisplayUpdater;
            upd.Type = MediaPlaybackType.Music;
            upd.MusicProperties.Title = "Gate Probe Track";
            upd.MusicProperties.Artist = "Klangbruecke";
            upd.Thumbnail = RandomAccessStreamReference.CreateFromFile(
                Windows.Storage.StorageFile.GetFileFromPathAsync(
                    Path.GetFullPath("cover.jpg")).GetAwaiter().GetResult());
            upd.Update();

            var tl = new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(200),
                Position = TimeSpan.FromSeconds(30),
                MinSeekTime = TimeSpan.Zero, MaxSeekTime = TimeSpan.FromSeconds(200)
            };
            smtc.UpdateTimelineProperties(tl);

            smtc.ButtonPressed += (_, e) => Console.Beep(800, 120); // + log e.Button
            smtc.PlaybackPositionChangeRequested += (_, e) => Console.Beep(1200, 120); // + log e.RequestedPlaybackPosition
        };
        Application.Run(form);
    }
}
```
**Note on the interop cast:** the exact CsWinRT incantation to obtain `ISystemMediaTransportControlsInterop` from the `SystemMediaTransportControls` activation factory is the one fiddly bit. The implementer should use the documented CsWinRT pattern (`SystemMediaTransportControls.As<ISystemMediaTransportControlsInterop>()` on the factory obtained via `RoGetActivationFactory`) and adjust until `GetForWindow` returns a live SMTC. This *is* the thing the spike exists to nail down — treat a non-obvious interop cast as expected work, not a blocker.

- [ ] **Step 3: Run unpackaged and observe**

Run: `dotnet run` from the probe dir, then:
1. Press the keyboard's **play/pause** media key → expect a beep (ButtonPressed fired).
2. Open the media flyout (ModernFlyouts should pop on the media-key press) → expect "Gate Probe Track / Klangbruecke" + cover + a seek bar at ~0:30/3:20.
3. Drag the ModernFlyouts seek bar → expect a higher-pitched beep (PlaybackPositionChangeRequested fired).

Record: did it render? did keys fire? did seek fire? **If unpackaged does NOT surface in ModernFlyouts/GSMTC**, note it and retest under package identity (the shipping app is packaged, so packaged-only is acceptable — but we must know).

- [ ] **Step 4: Record the result**

Write the observations into a scratchpad note for the Task 4 FINDINGS writeup. No commit (spike is throwaway).

---

## Task 2: Android RFCOMM echo server spike (needs Task 0 + the phone)

**Files:**
- Create a minimal Gradle project under `<scratchpad>/rfcomm-echo-android/` (single module, one `MainActivity`).

**Interfaces:**
- Produces: an installed debug APK that advertises SDP UUID `6f5e4d3c-2b1a-4c8d-9e7f-0a1b2c3d4e5f` (name `KlangbrueckeGate`) via `listenUsingRfcommWithServiceRecord`, accepts one client, and echoes every byte it receives. Consumed by Task 3 (the PC client connects to it) and Task 4 (the coexistence run).

- [ ] **Step 1: Scaffold a minimal Gradle project**

Create `settings.gradle.kts`, `build.gradle.kts` (app), `gradle/wrapper/*` (wrapper for Gradle 8.7), `app/src/main/AndroidManifest.xml`, `app/src/main/java/.../MainActivity.kt`. `compileSdk=34`, `minSdk=26`, `targetSdk=34`, `applicationId="klangbruecke.gate.echo"`. Manifest permissions: `BLUETOOTH_CONNECT` (Android 12+), and legacy `BLUETOOTH`/`BLUETOOTH_ADMIN` with `maxSdkVersion=30`.

- [ ] **Step 2: Implement the echo server**

`MainActivity.kt` (essentials):
```kotlin
val UUID_GATE = java.util.UUID.fromString("6f5e4d3c-2b1a-4c8d-9e7f-0a1b2c3d4e5f")
// after BLUETOOTH_CONNECT granted:
val adapter = (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager).adapter
Thread {
    val server = adapter.listenUsingRfcommWithServiceRecord("KlangbrueckeGate", UUID_GATE)
    val sock = server.accept()          // blocks until the PC connects
    server.close()
    val input = sock.inputStream; val output = sock.outputStream
    val buf = ByteArray(1024)
    while (true) {
        val n = input.read(buf); if (n < 0) break
        output.write(buf, 0, n); output.flush()   // echo
    }
}.start()
```
Show connection status in a `TextView` on the UI thread. Request `BLUETOOTH_CONNECT` at runtime before starting the thread.

- [ ] **Step 3: Build the APK**

Run (from the project dir, env from Task 0):
`./gradlew assembleDebug`
Expected: `app/build/outputs/apk/debug/app-debug.apk`.

- [ ] **Step 4: Install and launch on the phone**

Run: `adb install -r app\build\outputs\apk\debug\app-debug.apk` then launch the app; grant the Bluetooth permission. The phone must already be BT-bonded to this PC (it is, for audio).
Expected: app shows "waiting for connection".

---

## Task 3: PC RFCOMM client spike (buildable now; runs with Task 2)

**Files:**
- Create: `<scratchpad>/rfcomm-client/RfcommClient.csproj`, `Program.cs`.

**Interfaces:**
- Consumes: the phone's advertised RFCOMM service (UUID from Global Constraints).
- Produces: confirmation the PC can discover + connect the bonded phone's RFCOMM service and round-trip bytes, with a rough latency number. Consumed by Task 4.

- [ ] **Step 1: Create the client project**

`RfcommClient.csproj`: `net8.0-windows10.0.19041.0`, `OutputType=Exe`, `Nullable=enable`. (Console is fine here; WinRT works from a console on this TFM.)

- [ ] **Step 2: Implement discovery + connect + echo round-trip**

`Program.cs` (essentials):
```csharp
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

var uuid = Guid.Parse("6f5e4d3c-2b1a-4c8d-9e7f-0a1b2c3d4e5f");
var selector = RfcommDeviceService.GetDeviceSelector(RfcommServiceId.FromUuid(uuid));
var devices = await DeviceInformation.FindAllAsync(selector);
Console.WriteLine($"Found {devices.Count} service(s).");
var svc = await RfcommDeviceService.FromIdAsync(devices[0].Id);

using var socket = new StreamSocket();
await socket.ConnectAsync(svc.ConnectionHostName, svc.ConnectionServiceName);

var writer = new DataWriter(socket.OutputStream);
var reader = new DataReader(socket.InputStream);
var payload = System.Text.Encoding.ASCII.GetBytes("PING-0123456789");
for (int i = 0; i < 20; i++)
{
    writer.WriteBytes(payload); await writer.StoreAsync();
    await reader.LoadAsync((uint)payload.Length);
    var echoed = new byte[payload.Length]; reader.ReadBytes(echoed);
    // assert echoed == payload; print round-trip time
}
Console.WriteLine("Round-trip OK");
```
If `FindAllAsync` returns 0, the phone app (Task 2) isn't advertising or the devices aren't bonded — verify bonding with `Get-PnpDevice` before suspecting code.

- [ ] **Step 3: Dry-run build (no phone needed)**

Run: `dotnet build`
Expected: compiles clean. (Actual connection needs Task 2 running on the phone — that happens in Task 4.)

---

## Task 4: Coexistence run + FINDINGS §20 (the actual gate — hardware + user)

**Files:**
- Modify: `docs/FINDINGS.md` (add §20).

**Interfaces:**
- Consumes: Task 1 result, Task 2 APK on the phone, Task 3 client.
- Produces: the go/no-go decision recorded empirically. This is the gate.

- [ ] **Step 1: Bring up both audio halves**

Connect the phone; route music to the PC (A2DP). Confirm with:
`Get-PnpDevice -Class AudioEndpoint | Where-Object FriendlyName -match 'A2DP'`
Expected: `Line (<phone> A2DP SNK)` present. Start music playing, audible on the PC.

- [ ] **Step 2: Bring up a live call (HFP)**

Place a real cellular call. Confirm audio **both directions** (the mic half fails silently otherwise). Keep the call up.

- [ ] **Step 3: Run the RFCOMM echo under load, during audio**

With music playing AND the call live: launch Task 2 on the phone, run Task 3 on the PC (`dotnet run`). Let it round-trip continuously for ~60s.
Observe and note precisely:
1. Any **audio dropout / stutter / glitch** in the music?
2. Does the **call mic** still work (ask the other party)? Any degradation?
3. Does RFCOMM **round-trip cleanly** the whole time (no stalls/errors)?

- [ ] **Step 4: Record §20 and decide**

Add `docs/FINDINGS.md` §20 "RFCOMM coexistence with A2DP + HFP (empirical, 19045)" with the observations and the verdict:
- **PASS** (audio uninterrupted + RFCOMM round-trips) → the transport is viable; proceed to write Phase 2+ plans.
- **FAIL** (audio disturbed) → STOP. Record exactly how it failed. Do not pivot to Wi-Fi (ruled out) without a new decision.

- [ ] **Step 5: Commit the finding**

```bash
git add docs/FINDINGS.md
git commit -m "FINDINGS 20: RFCOMM/audio coexistence gate result (<PASS|FAIL>)"
```

---

## Self-Review

- **Spec coverage:** This plan covers the spec's "coexistence gate (hard go/no-go, built first)" and "SMTC probe" in full. Feature sections (Android app, PC `Companion/` module, protocol, art+seek, polish) are **intentionally deferred** to post-gate plans per the spec's own ordering — not gaps.
- **Placeholder scan:** No TBD/TODO. The one genuinely open detail — the exact CsWinRT interop cast in Task 1 Step 2 — is called out as the spike's purpose, with the documented approach named, which is appropriate for a validation spike rather than a hidden placeholder.
- **Type/UUID consistency:** UUID `6f5e4d3c-2b1a-4c8d-9e7f-0a1b2c3d4e5f` and service name `KlangbrueckeGate` are identical across Task 2 (`listenUsingRfcommWithServiceRecord`) and Task 3 (`RfcommServiceId.FromUuid`). Same `net8.0-windows10.0.19041.0` TFM across both PC spikes.
