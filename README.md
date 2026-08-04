# Klangbruecke

One Windows tray app that bridges phone audio to the PC:

- **Music / notifications** — the phone streams to the PC over Bluetooth A2DP; the PC renders it to any output device you pick.
- **Calls** — cellular calls on the phone route to the PC's headset (speakers + mic).

No Phone Link. No dongle. Runs on the machine's built-in Bluetooth radio.

## Status

Scaffold. The approach is **proven working on the target machine** — see [docs/FINDINGS.md](docs/FINDINGS.md).
Both halves were verified 2026-08-04 using two separate third-party apps; this project replaces
them with a single tray app, which is the one thing neither existing app provides.

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
