# Klangbruecke

One Windows tray app that bridges phone audio to the PC:

- **Music / notifications** — the phone streams to the PC over Bluetooth A2DP; the PC renders it to any output device you pick.
- **Calls** — cellular calls on the phone route to the PC's headset (speakers + mic).

No Phone Link. No dongle. Runs on the machine's built-in Bluetooth radio.

## Status

**Working.** Both halves run from this one app, verified 2026-08-04 on the target machine with the
packaged build: music routes over A2DP, and a real cellular call routes to the PC with audio in
both directions. That is the thing neither existing app provided — see
[docs/FINDINGS.md](docs/FINDINGS.md) §10.

What is done: the connect path, transport-to-phone correlation, and a rolling log at
`%LOCALAPPDATA%\Klangbruecke\logs\` that is the app's only diagnostic surface and the reason the
first packaged run was debuggable at all.

What is not done:

- **Reconnect.** There is no state machine yet — no `DeviceWatcher`, no retry, no sleep/resume
  handling. The app connects when told to and stays connected until something stops it. Reconnect
  after reboot and phone-initiated reconnect are the predecessor app's defining bug and remain
  unaddressed. Designed in
  [the connection lifecycle spec](docs/superpowers/specs/2026-08-04-connection-lifecycle-design.md).
- **Outgoing call audio quality.** Intelligible but degraded relative to holding the phone
  directly. Ruled out: VoiceMeeter, the microphone, and the cellular network. The Bluetooth SCO
  link is the remaining suspect, likely a narrowband codec forced by a 2021 radio driver.
  FINDINGS §11.

**Requires the packaged build.** `dotnet run` is not a development loop for the music half:
`AudioPlaybackConnection.TryCreateFromId` terminates an unpackaged process with an access
violation no managed handler can catch. FINDINGS §8.

## Why this exists

| Need | Existing option | Why it isn't enough |
|---|---|---|
| Music | AudioPlaybackConnector | Abandoned 2020, reconnect bug, music only |
| Calls | Thy Phone (Store, $3.69) | Works, but is **not a tray app** — must stay open |
| Both | Phone Link | Ruled out |

## Requirements

- Windows 10 build 19041+ (developed on 19045 / 22H2)
- .NET 8 SDK
- Windows SDK 10.0.19041.0 (for `makeappx` / `signtool`)
- A Bluetooth radio the Windows stack owns (no Zadig / WinUSB rebinding)

## Build

```powershell
dotnet build src/Klangbruecke/Klangbruecke.csproj -c Release
```

## Package and install

MSIX packaging is required — not cosmetic. The `phoneLineTransportManagement` restricted
capability only works with package identity. Sideloading needs no Microsoft approval.

```powershell
./packaging/New-DevCert.ps1      # once: create + trust a self-signed dev cert
./packaging/Build-Msix.ps1       # build, package, sign
```

Then install the produced `.msix` and enable sideloading in Windows Settings
(Settings → Update & Security → For developers → Sideload apps).

## Architecture

```
        Bluetooth (built-in radio, Windows stack)
                        |
    +-------------------+--------------------+
    |                                        |
  A2DP sink                            HFP hands-free
  AudioPlaybackConnection              PhoneLineTransportDevice
    |                                        |
  "Line (<phone> A2DP SNK)"            call audio in/out
  capture endpoint                           |
    |                                        |
  WASAPI capture --> BufferedWaveProvider --> WASAPI render
                                             |
                                      selected output device
```

## Layout

```
src/Klangbruecke/
  Program.cs                      entry point, single-instance guard
  TrayContext.cs                  tray icon, menu, wiring
  Bluetooth/AudioSinkService.cs   A2DP sink lifecycle
  Bluetooth/CallTransportService.cs  HFP call transport lifecycle
  Audio/AudioRouter.cs            WASAPI capture -> render bridge
  Config/Settings.cs              persisted settings
packaging/
  AppxManifest.xml                package identity + capabilities
  New-DevCert.ps1                 self-signed cert for sideloading
  Build-Msix.ps1                  build + package + sign
docs/FINDINGS.md                  research record; read before changing approach
```

## Licence

Personal project, unlicensed.
