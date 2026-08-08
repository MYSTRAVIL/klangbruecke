# Klangbruecke — status

_Written 2026-08-07. Updated 2026-08-08: the tray UX bundle shipped as `0.2.3.0`. Living status, not a
stage report._

## Where things stand

| | |
|---|---|
| Branch | `main`, clean, **pushed to `origin/main`** (github.com/MYSTRAVIL/klangbruecke). |
| Stage 1 (reconnect) | **Merged and validated on hardware.** Both halves work; the connection lifecycle recovers unattended. |
| Stage 2 (seam extraction) | **Merged (`796839d`) and pushed.** Grace window + reconcile split into their own seams; behaviour unchanged. |
| Fast reconnect probe | **Merged (`d17da8c`) and pushed.** A 5 s probe in the reconcile bounds phone-initiated reconnect to ~5 s. Reviewed (Opus) and validated on hardware. |
| Tray UX bundle | **Merged (`c9ff72e`) and pushed.** Connect Now, Open Logs, About, Check for Updates, Copy Diagnostics, README troubleshooting — behind one `IAppShell` seam. Subagent-driven, whole-branch review clean. Installed as `0.2.3.0` and validated on hardware (both halves reconnected after the upgrade). |
| Installed build | **0.2.3.0** (MSIX, sideloaded, self-signed), running in the tray |
| Tests | **4290, all green, zero warnings** |

## Shipped 2026-08-08 (tray UX bundle)

Six user-facing affordances, none betraying the tray-first ethos. Spec + plan in
`docs/superpowers/specs/2026-08-08-tray-ux-bundle-design.md` and the plan beside it.

- **Connect Now** — a manual one-shot reconnect that overrides a deliberate Disconnect or auto-reconnect
  off, reusing `ConnectionManager`'s existing `ClickGrant` carve-out (auto-reconnect setting untouched).
- **Diagnostics submenu** — **Open Logs** (opens `%LOCALAPPDATA%\Klangbruecke\logs`), **Copy Diagnostics**
  (version + OS + state + last 30 log lines to the clipboard), **Check for Updates** (GitHub `/releases`
  list, prerelease-aware), **About** (version + GitHub link).
- **Menu reorder** — Connect Now / Disconnect above the Calls / Reconnect-automatically toggles.
- **README Troubleshooting** section.
- New testable units: `App/` (AboutText, AppVersion, UpdateChecker + `GitHubReleaseFeed`),
  `Diagnostics/` (LogTail, DiagnosticsReport); one shell seam `Platform/IAppShell` + `AppShell`.

## Shipped 2026-08-07 (previous session)

- **Tray icon** — a state-reflecting brand glyph, replacing the generic `SystemIcons.Application`. Commit `4997107`.
- **Releases** — `packaging/Publish-Release.ps1`: builds+signs, pushes, and cuts a manual `gh` release with
  the `.msix`, the public `.cer`, and trust instructions. First release cut 2026-08-08:
  [`v0.2.2`](https://github.com/MYSTRAVIL/klangbruecke/releases/tag/v0.2.2). The script's abbreviated-SHA
  `--target` bug (`422 target_commitish is invalid`) is fixed (full SHA).
- **§15 call auto-route** — investigated → **WONTFIX on Win10**; recorded in `FINDINGS §15.1`.
- Package-identity gating so an unpackaged run reports Idle (`a7983fc`); `WasapiDeviceFactory` leak fixed
  (`64f7be1`).

## Key findings

- **§15/§16: call control is Windows', not ours.** The incoming-call popup and in-call mute/hangup/keypad
  window are the Windows shell's, provided because the app registers the phone-line transport — not our
  code. The keypad's DTMF does not work and there is no app-side lever on Win10 (the `PhoneCall` surface is
  the `CallsPhoneContract` v6 ceiling from §15.1). WONTFIX. See `FINDINGS §16`.
- **§15.1: headset-style call auto-route is not achievable on Win10.** `PhoneCall.ChangeAudioDevice` is v6,
  absent on this 19045 machine; present on Win11, but Win11 blocks sideloaded call registration. No
  configuration in reach has both. Reopen only if the platform moves.
- **Win11 calls are blocked, and parked.** `RequestAccessAsync` → `DeniedBySystem` for sideloaded apps
  (MyPhone #26). Store-signing is the leading lead. Left to the friend's investigation.

## What's next

The tray UX bundle shipped, and the tray call-output picker is **WONTFIX** (decided 2026-08-08): routing
HFP call audio to a chosen PC output means changing the **system-wide default communications device** via
the undocumented `IPolicyConfig` — a global side effect on every app's comms audio. Reopen only if a
non-global routing lever appears.

The bundle's whole-branch-review follow-ups are **done** (2026-08-08, `0.2.4.0` in dev, commit `dbdbc22`):
`LogTail.ReadRecent` now spans the day boundary (Copy Diagnostics no longer loses the prior evening across
midnight); `DiagnosticsReport.Build` gained null guards for parity with `AboutText`; and the deferred
edge-case tests were added (suite now 4290). **Not yet released — the last release and installed build are
`0.2.3.0`.**

Also parked: **option B for reconnect latency** — a `BluetoothDevice.ConnectionStatusChanged` subscription
would make reconnect edge-driven but reintroduces a long-lived WinRT object `LinkMonitor` avoids. Only
worth it if the ~5 s probe proves not fast enough in daily use.

Not blockers: `ConnectAsync` returns False (§12, likely benign); the outgoing-call ringback does not reach
the PC (§6, cosmetic).

## Two cautions before you touch it

- **Run the suite unfiltered** (or `--logger "console;verbosity=detailed"`). A rare flake was seen
  historically and its name was lost to a `grep` pipe both times; unfiltered, a flake names itself.
- **Bump the version in both `Klangbruecke.csproj` and `packaging/AppxManifest.xml`** or `Add-AppxPackage`
  will not upgrade. Currently `0.2.4.0` in dev; the last release and installed build are `0.2.3.0`.
  `Add-AppxPackage` runs from **Windows PowerShell**, not pwsh.

## Where the record lives

- `docs/FINDINGS.md` — the empirical record; read it before changing approach. §16 is the latest.
- `docs/superpowers/specs/` + `docs/superpowers/plans/` — the tray UX bundle's spec and plan (2026-08-08).
- `docs/HANDOFF.md` — the Stage 0 handoff.
