# Findings

Research record for Klangbruecke. **Read this before changing the architecture.** Several
widely-repeated claims about this problem are wrong, and two of them nearly sent this project
down a much more expensive road.

---

## 1. The inbox Windows stack does BOTH halves on Windows 10 19045

Verified empirically on the target machine, 2026-08-04:

- **Music** — AudioPlaybackConnector (third-party, abandoned) opened an `AudioPlaybackConnection`,
  which produced the recording endpoint `Line (<phone name> A2DP SNK)`. Audio routed fine.
  It is a *packaged* app, and that turns out to matter — see §8.
- **Calls** — Thy Phone (Store app, `InTheHandLtd.PearYourPhone` 1.0.39.0) routed a live cellular
  call to the PC headset. Mic worked. Caller audio worked.

Both ran **simultaneously without conflict** — A2DP sink and HFP hands-free are different profiles
on different channels. An early theory that they contend for device registration was wrong.

### This contradicts the prevailing wisdom

The Sefirah maintainer, who shipped a working Win11 implementation, states on
[issue #253](https://github.com/shrimqy/Sefirah/issues/253):

> "I'm afraid Windows 10 doesn't support most of the API used for this feature... The alternative
> is upgrading to Windows 11."

**That is not true on this machine.** The likely explanation is that the Win10 failure reports are
misdiagnosed instances of the pairing bug in §3 below.

## 2. There is no LAF gate on `PhoneLineTransportDevice` any more

Older research (and this project's own earlier notes) held that the API was Limited-Access-Feature
gated and required a token generated via the ADeltaX gist / Rafael Rivera technique.

**Microsoft removed it from the LAF list.** Quoted directly in
[MyPhone issue #26](https://github.com/BestOwl/MyPhone/issues/26), 2024-10-11:

> "the PhoneLineTransportDevice API was removed from the LAF list and does not require a token."

So: **do not implement LAF token generation.** Do not port Sefirah's `FeatureTokenGenerator.cs`.
It is obsolete for this API.

The only remaining gate is that `phoneLineTransportManagement` is a **restricted capability**.
That gates *Microsoft Store submission* only. **Sideloading requires no approval.** Hence MSIX
packaging + self-signed cert + sideload.

### The capability gates registration, not discovery

Probed unpackaged on this machine, 2026-08-04, with the phone connected. All of these **worked with
no package identity**:

| Call | Result |
|---|---|
| `PhoneLineTransportDevice.GetDeviceSelector()` | returned the selector |
| `DeviceInformation.FindAllAsync(selector)` | returned the real transport, 1 result |
| `PhoneLineTransportDevice.FromId(<real id>)` | succeeded, `DeviceId` round-tripped |
| `IsRegistered()` | returned `False` cleanly, no throw |

`Package.Current` threw `InvalidOperationException 0x80073D54` in the same run, confirming there was
genuinely no identity.

`RegisterApp()` was **not** tested, and is almost certainly where the capability actually bites. So
do not tell a user "calls are unavailable without MSIX" — enumeration works fine unpackaged, and
saying otherwise sends the next maintainer looking for a packaging problem they do not have.

### Detecting identity — and how to verify the detector

`GetCurrentPackageFullName` (kernel32, no `W` variant — `ExactSpelling = true`) with a zero length
and a null buffer answers the question without allocating:

| Return | Meaning |
|---|---|
| 15700 | `APPMODEL_ERROR_NO_PACKAGE` — no identity |
| 122 | `ERROR_INSUFFICIENT_BUFFER` — there is a package |

Preferred over `Package.Current`, which answers by throwing on every unpackaged run, in a startup
path.

**Measured both directions, 2026-08-04:**

```
unpackaged                                        probe=15700
packaged (Microsoft.WindowsTerminal_8wekyb3d8bbwe) probe=122
```

The unit suite can only ever see the first: the test host is unpackaged. The second is the direction
that matters — a probe that always answered "unpackaged" would silently disable **both** halves in
the shipped MSIX (see §8: identity now guards the music half too) with every test still green.

`Invoke-CommandInDesktopPackage` gets that answer without building an MSIX, by running the probe with
an already-installed package's identity. Identity rides on the process token and trust level gates
capabilities rather than identity, so a borrowed full-trust desktop-bridge package answers for
Klangbruecke's own package too.

`packaging/Test-PackageIdentity.ps1` automates it. **Re-run it after any change to the `DllImport` in
`src/Klangbruecke/Platform/PackageIdentity.cs`** — nothing else covers that direction.

## 3. The stale-IRK pairing bug — check this FIRST when anything fails

Cost roughly an hour to find. Will masquerade as an API problem.

**Symptom:** the app reports "connected", the phone says *"couldn't connect, try again"*, and no
audio route is offered.

**Cause:** the phone is paired both as Classic (`BTHENUM\DEV_<addr>`) and BLE (`BTHLE\DEV_<addr>`).
Removing one side leaves a stale Identity Resolving Key, and Windows then rejects the pairing.

**Diagnosis** — System event log, provider `BTHUSB`:

| Event | Meaning |
|---|---|
| 35 | Device distributing an IRK already used by a paired device — pairing rejected |
| 16 | Mutual authentication failed |
| 24 | Windows rejected connection; no encryption before service-level connection |

```powershell
Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=(Get-Date).AddHours(-2)} |
  Where-Object { $_.ProviderName -match 'BTH|Bluetooth' } |
  Select-Object TimeCreated, ProviderName, Id, LevelDisplayName, Message | Format-List
```

**Fix:** fully remove the device in Windows Settings, then re-pair. Health check — these should all
enumerate under `BTHENUM`:

```powershell
Get-PnpDevice -Class Bluetooth | Where-Object { $_.InstanceId -match 'BTHENUM' }
# expect: Headset Audio Gateway Service, AVRCP Transport x2, Phonebook Access,
#         SIM Access, PAN, Object Push
```

## 4. Never trust an app's "connected" indicator

It lied twice in one session. Verify against the OS instead:

```powershell
# Is the A2DP sink actually live?
Get-PnpDevice -Class AudioEndpoint | Where-Object { $_.FriendlyName -match 'SNK|A2DP' }
```

If that endpoint is absent, **nothing** is holding an `AudioPlaybackConnection` open, and the phone
physically cannot offer the PC as an audio output. That is not a bug — it is the expected state.

## 5. Things deliberately NOT done, and why

| Rejected | Reason |
|---|---|
| BTstack + USB dongle | Unnecessary — inbox stack works. Would also regress range badly (chip-antenna Class 2 vs the machine's antenna setup). |
| Zadig the internal MT7922 to WinUSB | Would work in principle (SCO-over-USB confirmed on this silicon in Linux) but takes the radio away from Windows entirely — kills the Xbox controller, Switch Pro Controller, and every other BT device. |
| `BypassRegistration` registry DWORD | Real value (confirmed in `BTAGService.dll`), but unnecessary — calls work without it. Single-source tip (n=1) whose own author reported no working functionality. |
| LAF token generation | Obsolete — see §2. |
| Upgrading to Windows 11 | Documented regression where the A2DP-sink capture endpoint stops appearing under Recording. Would fix nothing here and risks breaking the music half. |
| 32feet.NET | No audio support at all — RFCOMM/SDP only, no SCO. A2DP request open since 2021. |

## 6. Known open issues

- **Outgoing-call ringback doesn't reach the PC.** The "beep... beep..." while dialling is absent;
  audio works normally once the call connects. Cause is that the SCO link is only established on
  answer, so there is no audio channel during dialling. Fix would be to bring the audio link up at
  call-setup rather than call-active — unverified whether the Windows API exposes that control.
  Cosmetic; not a blocker.

## 7. Machine baseline (development target)

```
OS         Windows 10 Pro 19045 (22H2)
Radio      RZ616 / MediaTek MT7922, USB\VID_0E8D&PID_0616&MI_00
Phone      Google Pixel 9
.NET       8.0.417
Win SDK    10.0.19041.0
Audio      VoiceMeeter + VB-Cable in the chain
```

Relevant registry state (informational — no changes needed):

```
Bluetooth\Audio\A2dp\Sink         Enabled=1
Bluetooth\Audio\Hfp\HandsFree     Enabled=1  ProfileVersion=263 (HFP 1.7)
                                  RfcommServerChannel=2  BrsfSupportedFeatures=183
```

## 8. `AudioPlaybackConnection.TryCreateFromId` kills an unpackaged process

Found 2026-08-04 by the Stage 0 instrumentation, on its first real run against the phone.
**`dotnet run` is not a usable development loop for the music half.**

**Symptom:** the process disappears. No exception, no log line, no status, nothing in the app's own
output — only an `Application Error` / `.NET Runtime` 1026 pair in the Windows Application log:

```
System.AccessViolationException: Attempted to read or write protected memory.
   at ABI.Windows.Media.Audio.IAudioPlaybackConnectionStaticsMethods.TryCreateFromId(WinRT.IObjectReference, System.String)
   at Windows.Media.Audio.AudioPlaybackConnection.TryCreateFromId(System.String)
```

An `AccessViolationException` is a corrupted-state exception: no managed handler runs. Neither
`AppDomain.UnhandledException`, `Application.ThreadException`, nor a `try`/`catch` around the call
can see it, so **it cannot be logged after the fact.** `AudioSinkService.ConnectAsync` therefore
brackets the call with log lines — an `Opening A2DP sink connection to id=...` line with nothing
after it is the fingerprint.

**Verified, not assumed** — reproduced on every attempt, varying one thing at a time:

| Varied | Values tried | Result |
|---|---|---|
| Device id | real live id from `FindAllAsync`; well-formed but nonexistent; arbitrary garbage | AV in all three |
| Apartment | STA (WinForms UI thread, and an explicit STA thread) and MTA (test host) | AV in both |
| SDK projection | `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.56 and 10.0.22621.41 | AV in both |
| Host | the tray app itself, and a plain xunit test host with no app code | AV in both |

**What still works unpackaged:** `AudioPlaybackConnection.GetDeviceSelector()` plus
`DeviceInformation.FindAllAsync` return the phone correctly, as do the phone-line equivalents. The
statics interface is live and activating — only this one method on it faults.

**Leading hypothesis, not verified:** `AudioPlaybackConnection` needs MSIX package identity, and
without it the capability check faults instead of failing cleanly. Consistent with §1, where the
music implementation observed working on this machine was a packaged Store app. Two consequences:

- The next experiment is the **packaged** build, not a code change. Do not go hunting for a bug in
  `AudioSinkService` — the same call crashes a bare test host with no app code in the frame.
- Raising the Windows SDK projection version has been tried and does not help. Do not repeat it.

**The trap this used to set, and the code that closes it.** `TrayContext.ConnectAsync` saves
`PhoneDeviceId` *before* connecting, so once a phone had been picked, every later unpackaged launch
auto-connected at startup, died here, and did it again — an app bricked by one menu click, with a log
reading `starting.` and nothing else, recoverable only by deleting
`%LOCALAPPDATA%\Klangbruecke\settings.json` by hand. (That path is correct as written for both
builds, but only because the manifest now opts out of write virtualization — see §9.)

**`AudioSinkPolicy.CanOpenConnection` is what stops that**, and it is load-bearing rather than
defensive tidiness. Unpackaged it returns false and the connect call is never reached: checked in
`TrayContext.ConnectMusicAsync` so the reason is logged once and the attempt skipped cleanly, and
again as the first statement of `AudioSinkService.ConnectAsync`, immediately above the call, so a
later caller that goes straight to the service cannot reintroduce it. **Do not remove either check to
"let it try" — the failure is process death, not a false return.** Verified over two consecutive
unpackaged launches with a real phone id saved: both survived, both logged the full connect path,
zero `Application Error` events.

Saving `PhoneDeviceId` before connecting is therefore deliberate and safe. It records the user's
answer to "which phone", not what managed to connect, and the packaged build needs it on the next
start.

## 9. The packaged build redirects `%LOCALAPPDATA%` unless you tell it not to

`EntryPoint="Windows.FullTrustApplication"` plus `runFullTrust` makes this a **Desktop Bridge**
package, and those get **AppData write virtualization on by default**.

Probed and confirmed on this machine: `Environment.GetFolderPath(LocalApplicationData)` returns the
**un**redirected path — `C:\Users\<user>\AppData\Local` — while the bytes actually land in:

```
%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Local\Klangbruecke\
```

`FileLog.DefaultDirectory` and `Settings.Directory` both call that API, so the installed build wrote
its log and settings somewhere it could not name. **This is more dangerous than a missing file.**
Unpackaged development runs leave a real log at the documented path whose last lines read
`Music half skipped: no MSIX package identity` — so an operator following the Stage 0 validation
checklist would open exactly that file, find a stale wrong answer, and have no way to tell.

**The fix**, in `packaging/AppxManifest.xml`:

```xml
<Properties>
  ...
  <desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization>
</Properties>
```

with `xmlns:desktop6="http://schemas.microsoft.com/appx/manifest/desktop/windows10/6"`.

### Two traps in doing it

1. **It needs a second restricted capability.** `<rescap:Capability Name="unvirtualizedResources" />`
   — without it `makeappx` fails with `error 80080204: App manifest validation error: ... The
   element specified requires "unvirtualizedResources" capability.` **The XSD does not encode this
   rule**; only the packager enforces it, so schema validation alone will pass a manifest that
   cannot be packed. Restricted capabilities gate Store submission only and this package already
   ships one, so sideloading is unaffected (§2).

2. **It is a child of `<Properties>`, not `<Application>`.** Declared in
   `FoundationManifestSchema.xsd` as part of `CT_Properties`, which is an `xs:all` — so ordering
   within `Properties` is free. Putting it under `<Application>` fails with
   `Element ... is unexpected according to content model of parent element ... Application`.
   Element floor is 1903 (18362); this package's `MinVersion` is 19041, so it always applies.

**Verified, not assumed** — `makeappx pack` run against this project's actual manifest, 2026-08-04:

| Manifest | Result |
|---|---|
| Baseline, before the change | `Package creation succeeded.` exit 0 |
| With `desktop6` element, **without** `unvirtualizedResources` | `error 80080204` exit 1 |
| With both | `Package creation succeeded.` exit 0 |

### The consequence to remember

Packaged and unpackaged runs now **append to the same log file**. That is the point — one path, the
one every document names — but it means a single day's file interleaves runs of both builds. The
startup banner logs `AppContext.BaseDirectory` for exactly this reason: it is the only value
in-process that separates `C:\Program Files\WindowsApps\Klangbruecke_...` from
`src\Klangbruecke\bin\...`. **Read it before drawing a conclusion from any other line.**

Uninstalling no longer removes the log or `settings.json`, since they are no longer package-private.
That is deliberate: an install-fix-reinstall loop that wiped its own evidence at each step would be
useless.
