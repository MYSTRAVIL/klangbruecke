# Klangbruecke — status

_Written 2026-08-07. Supersedes the old `STAGE-1-STATUS.md`: Stage 1 is merged and validated, so this
is the living status rather than a stage report._

## Where things stand

| | |
|---|---|
| Branch | `main`, clean, **pushed to `origin/main`** (github.com/MYSTRAVIL/klangbruecke). |
| Stage 1 (reconnect) | **Merged and validated on hardware.** Both halves work; the connection lifecycle recovers unattended. |
| Stage 2 (seam extraction) | **Merged (`796839d`) and pushed.** Grace window + reconcile split into their own seams; behaviour unchanged. Not yet re-validated on hardware — it is a pure refactor, but the reconnect paths deserve one live pass. |
| Installed build | **0.2.1.0** (MSIX, sideloaded, self-signed), running in the tray |
| Tests | **4258, all green, zero warnings** |

## Shipped 2026-08-07 (this session)

- **Tray icon** — a state-reflecting brand glyph (blue = Connected, amber = Connecting/Discovering/
  Degraded/RetryBackoff, grey = Idle/Suppressed), replacing the generic `SystemIcons.Application`; the
  `.exe` gets a real icon too. Pure `TrayIconPolicy` (tested) + generator `packaging/Generate-Icons.ps1`.
  Commit `4997107`.
- **Releases** — `packaging/Publish-Release.ps1`: builds+signs, pushes the commit, and cuts a manual
  `gh` release with the `.msix`, the public `.cer`, and trust instructions. The `.cer` ships because a
  self-signed MSIX has **no "install anyway" prompt** — the cert must be trusted first. Commit `d9f95e8`.
  **No release has been cut yet.**
- **§15 call auto-route** — investigated → **WONTFIX on Win10**; recorded in `FINDINGS §15.1`. Commit
  `a23accc`. See below.
- **Robustness (the Stage-1 review's quick-win list, now cleared):**
  - Both halves are gated on package identity in the manager — injected, so it stays testable — so an
    unpackaged `dotnet run` reports **Idle** instead of retrying both halves at the 60 s ceiling
    forever. Commit `a7983fc`.
  - `WasapiDeviceFactory`'s leaked `MMDevice`s (adapters + enumeration) are disposed. Commit `64f7be1`.
- Version bumped `0.2.0.0 → 0.2.1.0` in **both** `Klangbruecke.csproj` and `packaging/AppxManifest.xml`.
  Commit `3620ead`.

## Key findings this session

- **§15 (FINDINGS §15.1): headset-style call auto-route is not achievable on Win10.** The lever exists —
  `PhoneCall.ChangeAudioDevice(LocalDevice)` — but it is `CallsPhoneContract` **v6**, a Windows 11
  contract. Measured absent at runtime on this Win10 19045 machine (only v5 is present); no Win10 client
  build ships v6. v6 *is* on Win11, but Win11 blocks call registration for sideloaded apps — so no
  configuration in reach has both the routing API and working calls. Reopen only if the platform moves.
- **Win11 calls are blocked, and parked.** On a friend's Win11 25H2 machine, `RequestAccessAsync` returns
  `DeniedBySystem` for sideloaded third-party apps (documented: MyPhone #26). Store-signing is the leading
  lead. Two handoff docs from that machine live in `C:\Users\MYSTRAVIL\Downloads\klangbruecke-handoff-2026-08-07*.md`
  (not in the repo). Left to the friend's investigation for now.

## What's next

The quick-win well is dry — the Stage-1 deferred-minors ledger was triaged and nothing safe-and-small
remains. The review's #1 Stage-2 priority — **refactoring `ConnectionManager`** — shipped this session
(the grace window and reconcile are now the `GraceWindow`/`Reconciler` seams reaching the hub through
`IConnectionCoordinator`; see `docs/superpowers/specs/2026-08-07-connection-manager-seam-extraction-design.md`
and the plan beside it). One substantive item remains, with its prerequisite:

1. **Tray call-output picker** — a requested feature. Routing HFP call audio to a chosen PC output means
   changing the **system-wide default communications device** via the undocumented `IPolicyConfig` — a
   global side effect on every app's comms audio. **Decide that footprint before building.**

And re-validate the Stage-2 refactor on hardware once — it is behaviour-preserving and fully green, but
the reconnect-after-reboot and phone-initiated-reconnect paths are the historically fragile ones.

Not blockers: `ConnectAsync` returns False (§12, likely benign, untestable until it matters); the
outgoing-call ringback does not reach the PC (§6, cosmetic).

## Two cautions before you touch it

- **Run the suite unfiltered** (or `--logger "console;verbosity=detailed"`). A rare failure has been seen
  historically and its name was lost to a `grep` pipe both times; unfiltered, a flake names itself.
- **Bump the version in both `Klangbruecke.csproj` and `packaging/AppxManifest.xml`** or `Add-AppxPackage`
  will not upgrade. Currently `0.2.1.0`. `Add-AppxPackage` runs from **Windows PowerShell**, not pwsh.

## Where the record lives

- `docs/FINDINGS.md` — the empirical record; read it before changing approach. §15.1 is this session's.
- `.superpowers/sdd/2026-08-05-stage-1-connection-manager/` — the Stage 1 ledger and per-task reports
  (gitignored, on disk).
- `docs/HANDOFF.md` — the Stage 0 handoff.
