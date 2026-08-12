# Handoff — parked research items (call-quality, battery, phone-media-control)
_Written 2026-08-10. Three open items after the tray UX bundles (0.2.4 shipped). None blocks anything.
Item 4 was researched to a NO-GO; items 2 and 3 each need one machine probe before any build._

Read `docs/FINDINGS.md` first (esp. §8, §11, §14, §17, §18, §19) — it records what is empirically true on
this Win10 19045 machine versus what the internet claims. The scratchpad probe pattern these items reuse
is `scratchpad/featureprobe` (an unpackaged net8.0-windows10.0.19041.0 console; do NOT call
`AudioPlaybackConnection.TryCreateFromId` from it — §8, uncatchable AV).

---

## Item 2 — Call narrowband vs wideband indicator

**Goal:** surface whether an active cellular call's Bluetooth SCO link is **narrowband (CVSD, 8 kHz)** —
the "outgoing calls sound bad" case (§11) — or **wideband (mSBC, 16 kHz)**. Directly diagnoses §11.

**Approach:** during a *live call*, read the communications/HFP capture endpoint's mix format via WASAPI
(`MMDeviceEnumerator`); `SampleRate == 8000` ⇒ narrowband, `16000` ⇒ wideband. Surface it in the tray
tooltip and/or Copy Diagnostics.

**Blocker / the one thing to verify first:** FINDINGS §14 warns the HFP endpoints "never go Active in the
enumerator during a call". So the endpoint may not be readable *at all* during a call — the whole feature
hinges on this.

**Next step (needs a real cellular call + the machine):** while on a call, enumerate Capture endpoints in
*every* `DeviceState` (Active, Disabled, Unplugged, NotPresent), and log the FriendlyName + `MixFormat` of
any HFP / "Hands-Free" / communications endpoint. If an 8 k/16 k endpoint is readable → build the
indicator. If nothing readable enumerates → dead; record it in FINDINGS and close. `packaging/Watch-HfpAudio.ps1`
exists but per §14 reads endpoints that never go Active during a call — treat it as a starting point, not a
solution.

**Confidence it's buildable:** low-medium (the §14 warning is the likely killer).

---

## Item 3 — Phone battery indicator

**Goal:** show the phone's battery % in the tray tooltip.

**Status:** §17 recorded battery as NOT exposed (the phone's `DeviceInformation.Properties` had no battery;
`BluetoothDevice` exposed none, queried with `System.Devices.BatteryLevel` and the `{104EA319-…} 2` key on
the device node). **But** — observed 2026-08-10: Windows' own Bluetooth device tile shows the phone battery
(20%). So the data reaches Windows via a source the first probe missed. This item is about finding that
source.

**Next step (probe, phone connected):** extend `scratchpad/featureprobe` Probe B to look on *other* nodes:
- The **Association Endpoint** (`DeviceInformationKind.AssociationEndpoint`) and its **container**
  (`AssociationEndpointContainer`) for the phone, requesting `System.Devices.BatteryLevel` and the
  `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` property — battery often lives on the AEP/container, not the
  device node.
- The device **container** via `System.Devices.ContainerId` → the PnP container's battery property.
- Cross-check with `Get-PnpDeviceProperty` on the phone's container for any battery property that is
  populated.
If a node exposes it → surface in the tooltip (and Copy Diagnostics). **Risk:** may be populated only on
Win11 and empty on 19045 despite the tile showing it (the tile may use a private path).

**Confidence it's buildable:** medium (the tile proves Windows has the value; the question is whether a
WinRT property carries it on 19045).

---

## Item 4 — Now-playing + control the phone's music from the PC (pause/resume/skip)

**RESEARCHED 2026-08-10 → NO-GO.** Full record in FINDINGS §19. Summary:
- Windows implements the AVRCP **Target** role, not **Controller** — it receives transport commands (as an
  accessory) and does not send them to a phone. **No WinRT/Win32 AVRCP Controller API exists.**
- GSMTC (`GlobalSystemMediaTransportControlsSessionManager`) returns 0 sessions for a connected A2DP-source
  phone BY DESIGN — it only surfaces local PC-app media (confirmed empirically, 0 sessions even with music
  playing).
- No third-party Windows app does this (AudioPlaybackConnector by ysc3839 and the other A2DP-sink projects
  are connection-management only). Absence across every comparable project ≈ a platform wall.
- Only Phone Link controls phone media, via a proprietary app-layer protocol over USB/Wi-Fi, not BT AVRCP.

**The one untested sliver (long shot, ~75% expected to fail):** synthesize the system media keys
(`VK_MEDIA_PLAY_PAUSE` 0xB3 / `VK_MEDIA_NEXT_TRACK` 0xB0 / `VK_MEDIA_PREV_TRACK` 0xB1) via `SendInput` and
see whether Windows forwards them to the phone over its internal AVRCP link. Needs a live test: music
playing on the phone → inject a key → does the phone pause/skip? If yes, *control* (not metadata) may be
partially achievable; if no (likely — there is no GSMTC session for the keys to target), it's fully closed.
Metadata / now-playing is a hard NO regardless.

**Recommendation:** closed as WONTFIX unless the media-key test surprises. Do not spend time on an AVRCP
Controller API — there isn't one.

---

## Already decided (not in scope here)
- **Auto-pick with two phones** — built + unit-tested (first-present-wins, incumbent kept); just needs a
  two-phone hardware smoke (STATUS "What's next" #1).
- **Tray call-output picker** — WONTFIX (undocumented `IPolicyConfig`, global comms-audio footprint).
