# Klangbruecke

Windows tray app that bridges phone audio to the PC over the built-in Bluetooth radio — no Phone
Link, no dongle.

- **Music / notifications** — the phone streams to the PC over A2DP; the PC renders it to any output you pick.
- **Calls** — cellular calls route to the PC's headset (speakers + mic) via the HFP hands-free role.

## Status

Both halves and unattended reconnect work, validated on hardware with the packaged build.

- **Music** (Stage 0, 2026-08-04) — connects the A2DP sink, correlates the transport to the phone by
  Bluetooth address, routes to a chosen output.
- **Calls** (Stage 0, 2026-08-04) — claims the HFP hands-free role; a real call routes to the PC with
  audio in both directions.
- **Reconnect** (Stage 1, 2026-08-07) — a `ConnectionManager` state machine recovers unattended from
  a call ending, a range exit and return, sleep/resume, reboot, and a phone-initiated disconnect. It
  also restarts the route when the A2DP capture endpoint appears late — a case the connect path alone
  silently missed. This was the predecessor app's defining bug; it is fixed. FINDINGS §13, §14.

Not yet done: the full Stage 2 reconnect matrix (hand-verified scenarios) and tray selection of the
call audio device.

**Requires the packaged build.** `dotnet run` is not a dev loop for the music half —
`AudioPlaybackConnection.TryCreateFromId` kills an unpackaged process with an uncatchable access
violation (FINDINGS §8).

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

## Install a release

Prebuilt, signed packages are on the
[Releases](https://github.com/MYSTRAVIL/klangbruecke/releases) page — each carries the `.msix` and the
public `.cer` it was signed with. The certificate is self-signed, so Windows refuses the package until
that cert is trusted; unlike a downloaded `.exe`, an MSIX has no "install anyway" prompt, so this step
is required (once per machine):

```powershell
# elevated: trust the signing certificate
Import-Certificate -FilePath .\Klangbruecke.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople

# then from Windows PowerShell (not pwsh — the Appx module does not load there):
Add-AppxPackage -Path .\Klangbruecke-<version>.msix
```

Klangbruecke starts on sign-in and lives in the tray.

## Build, package, install

MSIX packaging is load-bearing: package identity keeps `TryCreateFromId` from killing the process
(§8) and carries the `phoneLineTransportManagement` capability the calls half needs. Sideloading
needs no Microsoft approval.

```powershell
dotnet build src/Klangbruecke/Klangbruecke.csproj -c Release
./packaging/New-DevCert.ps1      # once: create + trust a self-signed dev cert
./packaging/Build-Msix.ps1       # build, package, sign
```

Install the produced `.msix` with sideloading enabled
(Settings → Update & Security → For developers → Sideload apps).

Cut a GitHub release (build, sign, push the commit, upload the `.msix` + `.cer` + install notes) with
`./packaging/Publish-Release.ps1`. The tray/app icons are generated from the brand mark by
`./packaging/Generate-Icons.ps1`.

## Troubleshooting

- **See what's happening.** Right-click the tray icon → **Diagnostics → Open Logs**, or open
  `%LOCALAPPDATA%\Klangbruecke\logs` directly. **Copy Diagnostics** puts a paste-ready snapshot
  (version, OS, state, recent log lines) on the clipboard — review it before sharing.
- **It won't connect.** Check the pairing before suspecting the app. Klangbruecke shows "connected"
  for its own view of the connection; verify the real endpoint with PowerShell:
  `Get-PnpDevice -Class AudioEndpoint | Where-Object FriendlyName -like '*A2DP*'`. A stale pairing
  (the IRK trap) presents exactly like an app failure — look at `BTHUSB` events 35 / 16 / 24 in the
  System log first. See `docs/FINDINGS.md` §3.
- **Force a reconnect.** Diagnostics won't help if the app is deliberately dormant — use
  **Connect Now** to override a Disconnect or a switched-off auto-reconnect for one attempt.
- **Reset configuration.** Delete `%LOCALAPPDATA%\Klangbruecke\settings.json` and restart; the app
  starts from defaults (no phone selected).
- **Check for a newer build.** Diagnostics → **Check for Updates**, or see the
  [Releases page](https://github.com/MYSTRAVIL/klangbruecke/releases).

## Architecture

```
        Bluetooth (built-in radio, Windows stack)
                        |
    +-------------------+--------------------+
  A2DP sink                            HFP hands-free
  AudioPlaybackConnection              PhoneLineTransportDevice
    |                                        |
  "Line (<phone> A2DP SNK)"            call audio in/out (Windows owns the path)
  capture endpoint
    |
  WASAPI capture -> BufferedWaveProvider -> WASAPI render -> selected output

  ConnectionManager owns the lifecycle: a DeviceWatcher, an endpoint monitor,
  and a 30s reconcile loop drive connect / route / retry / reconnect.
```

## Layout

```
src/Klangbruecke/
  Program.cs                         entry point, single-instance guard
  TrayContext.cs                     tray icon + menu (view only)
  Connection/ConnectionManager.cs    reconnect state machine
  Connection/                        LinkMachine, SuppressionLatch, MusicHalf,
                                     CallsHalf, ConnectionState, BackoffSchedule
  Bluetooth/AudioSinkService.cs      A2DP sink lifecycle
  Bluetooth/CallTransportService.cs  HFP call transport
  Bluetooth/LinkMonitor.cs           DeviceWatcher + ConnectionStatus
  Audio/AudioRouter.cs               WASAPI capture -> render bridge
  Audio/EndpointMonitor.cs           IMMNotificationClient: A2DP endpoint arrival
  Platform/                          scheduler, power notifier, package identity
tests/Klangbruecke.Tests/            4,200+ unit tests
packaging/                           AppxManifest, dev cert, MSIX build scripts
docs/FINDINGS.md                     research record; read before changing approach
```

## Licence

Personal project, unlicensed.
