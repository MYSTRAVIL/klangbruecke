# Findings

Research record for Klangbruecke. **Read this before changing the architecture.** Several
widely-repeated claims about this problem are wrong, and two of them nearly sent this project
down a much more expensive road.

---

## 1. The inbox Windows stack does BOTH halves on Windows 10 19045

Verified empirically on the target machine, 2026-08-04:

- **Music** — AudioPlaybackConnector (third-party, abandoned) opened an `AudioPlaybackConnection`,
  which produced the recording endpoint `Line (<phone name> A2DP SNK)`. Audio routed fine.
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
