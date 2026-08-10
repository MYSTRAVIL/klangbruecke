# Klangbruecke — status

_Written 2026-08-07. Updated 2026-08-10: tray UX bundle 2 + active-initiate Connect Now shipped as
`0.2.4.0` (released `v0.2.4`). Living status, not a stage report._

## Where things stand

| | |
|---|---|
| Branch | `main`, clean, **pushed to `origin/main`** (github.com/MYSTRAVIL/klangbruecke). |
| Stage 1 (reconnect) | **Merged and validated on hardware.** Both halves recover unattended. |
| Stage 2 (seam extraction) | **Merged (`796839d`).** Grace window + reconcile as their own seams. |
| Fast reconnect probe | **Merged (`d17da8c`).** A 5 s probe bounds phone-initiated reconnect to ~5 s. |
| Tray UX bundle 1 | **Merged, released `v0.2.3`.** Connect Now, Open Logs, About, Check for Updates, Copy Diagnostics, README troubleshooting, behind `IAppShell`. |
| Tray UX bundle 2 | **Merged (`273840a`), released `v0.2.4`.** Left-click opens the menu; event sounds (soft low chimes, toggle); auto-pick among remembered phones (first-present-wins); **Connect Now now actively pulls a cold phone** (FINDINGS §18). Subagent-driven build + whole-branch review; hardware-smoked (left-click, chimes, Connect Now). |
| Installed build | **0.2.4.0** (MSIX, sideloaded, self-signed), running in the tray |
| Releases | `v0.2.2`, `v0.2.3`, `v0.2.4` — all prereleases (0.x), `.msix` + `.cer` attached |
| Tests | **4320, all green, zero warnings** |

## Shipped 2026-08-10 (tray UX bundle 2)

Spec + plan: `docs/superpowers/specs/2026-08-08-tray-ux-bundle-2-design.md` and the plan beside it.

- **Connect Now actively initiates.** It reaches out via `AudioPlaybackConnection.OpenAsync` to pull a
  paired, in-range but currently-disconnected phone up (the same thing Windows' own Connect does) — not
  just when the phone is already present. Automatic reconnect stays passive/presence-gated; only the
  deliberate click reaches out. Hardware-validated; see FINDINGS §18.
- **Left-click opens the tray menu** — invokes the same private `NotifyIcon.ShowContextMenu` (with its
  `SetForegroundWindow`) that right-click uses; a direct `_menu.Show()` opened unfocused and closed on the
  click.
- **Event sounds** — pure `SoundPolicy` (state-transition → chime) + `ISoundPlayer` seam; synthesized low
  chimes (G3→C4 / C4→G3 / E3, generator `packaging/Generate-Sounds.ps1`, embedded); **Sounds** toggle,
  default on.
- **Auto-pick phone** — pure `PhonePicker` (first-present-wins, never thrashes a present incumbent) + an
  async resolver over the existing single-phone machinery; checkable phone submenu; settings migrate a
  prior single selection into the remembered set. (2-phone hardware test still pending — see below.)

## Shipped 2026-08-08 (tray UX bundle 1)

Connect Now (one-shot override), Open Logs, About, Check for Updates (`/releases` list), Copy Diagnostics
(spans the day boundary as of the follow-ups), README troubleshooting; `IAppShell` seam + `App/` and
`Diagnostics/` units. Released `v0.2.3`.

## Shipped 2026-08-07

State-reflecting tray icon; `Publish-Release.ps1` (fixed to use the full SHA as `--target`); §15 call
auto-route → WONTFIX; package-identity gating; `WasapiDeviceFactory` leak fixed.

## Key findings

- **§18: the PC can initiate a Bluetooth connection.** `AudioPlaybackConnection.OpenAsync` pulls a cold,
  paired, in-range phone up from the PC — validated on hardware. Connect Now uses it; unreachable is a
  bounded backoff. (If the *pairing* is stale, even Windows' Connect fails — §3.)
- **§15/§16: call control is Windows', not ours**, and the in-call keypad's DTMF is out of reach on Win10.
- **§15.1: headset-style call auto-route not achievable on Win10** (`PhoneCall.ChangeAudioDevice` is v6).
- **Win11 calls blocked/parked** — `DeniedBySystem` for sideloaded apps; Store-signing is the lead.
- **§17: AVRCP now-playing, phone battery, and the A2DP music codec are not exposed to a Win10 app.** But
  Windows' own device tile *does* show the phone battery (observed 20%), so a battery indicator via a
  different source may be salvageable — revisit if wanted.

## What's next

Two shippable bundles are done and released; the tray call-output picker stays **WONTFIX** (global
`IPolicyConfig` footprint). Open threads, none urgent:

1. **Auto-pick with two phones is untested on hardware** — built and unit-tested (first-present-wins,
   incumbent kept), but the user had only one phone at smoke time. Verify with two: it should connect
   whichever is present and not drop a working one when the other appears (switch on the ~30 s tick).
2. **Call narrowband/wideband indicator** — parked behind a live-call probe (§17/§14); only worth it if
   the SCO endpoint proves readable during a call.
3. **Battery indicator** — possibly salvageable via whatever source Windows' tile uses (§17 caveat).
4. Parked: **option B reconnect latency** (`ConnectionStatusChanged`, edge-driven) — only if the ~5 s
   probe proves too slow in daily use.

Not blockers: `ConnectAsync` returns False (§12, benign); outgoing-call ringback not on the PC (§6).

## Two cautions before you touch it

- **Run the suite unfiltered.** A rare historical flake lost its name to a `grep` pipe twice; unfiltered,
  it names itself.
- **Bump the version in both `Klangbruecke.csproj` and `packaging/AppxManifest.xml`** before the next
  packaged build, or `Add-AppxPackage` will not upgrade. Currently `0.2.4.0` (released as `v0.2.4`) — bump
  to `0.2.5.0` for the next build. `Add-AppxPackage` runs from **Windows PowerShell**, not pwsh; to
  reinstall the *same* version during dev, uninstall first (`Get-AppxPackage Klangbruecke | Remove-AppxPackage`).

## Where the record lives

- `docs/FINDINGS.md` — the empirical record; §18 is the latest.
- `docs/superpowers/specs/` + `docs/superpowers/plans/` — bundle 1 and bundle 2 specs and plans.
- `docs/HANDOFF.md` — the Stage 0 handoff.
