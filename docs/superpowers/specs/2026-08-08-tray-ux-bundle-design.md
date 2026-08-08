# Tray UX bundle — design

_Written 2026-08-08. Adds a set of small, user-facing tray affordances to the shipped 0.2.2 build.
None of them change the connection lifecycle except **Connect Now**, which reuses an existing,
already-tested mechanism._

## Goal

Add six affordances the app is missing, without betraying the tray-first, minimal ethos:

1. **Connect Now** — a manual, one-shot connect that overrides backoff and a deliberate Disconnect.
2. **Open Logs** — open the log folder in Explorer.
3. **About** — show version and a GitHub link.
4. **Check for Updates** — compare the running version against the latest GitHub release.
5. **Copy Diagnostics** — assemble a paste-ready snapshot to the clipboard.
6. **README troubleshooting** — a docs section (no code).

## Non-goals

- **No harden-the-core work.** The reconnect path is already covered (grace window + reconcile are
  tested through `ConnectionManagerTests`) and the two "bugs" a prior audit raised did not survive
  verification. See the conversation record; not repeated here.
- **No auto-update / auto-download.** Check for Updates only *reports* and offers to open the release
  page. Self-signed MSIX + cert trust makes silent install a separate, larger problem.
- **No call-output picker** (`IPolicyConfig`) — decided WONTFIX (STATUS.md, FINDINGS §15/§16).
- **No always-open window.** Modal `MessageBox` for an explicitly user-invoked action is allowed (it
  is transient); a window you must *keep* open is the anti-goal.

## Architecture

TrayContext is deliberately "a view, and only a view": each click makes exactly one call, and the sole
non-manager action today is Exit. Five of the six items need shell/diagnostic work (open a folder, open
a URL, clipboard, message box, an HTTP call), which would break that contract if inlined.

**Decision: introduce one seam, `IAppShell`, plus pure logic units.** Every new click stays "one call
to a collaborator"; the meaningful logic lives in testable units; the shell verbs are thin untestable
plumbing behind the seam. This matches how the codebase already seams every OS boundary
(`IUiDispatcher`, `IScheduler`, `IPowerNotifier`, `IAudioDeviceFactory`, `ILinkMonitor`). The view's
invariant becomes: **one call per click, to the manager _or_ the shell seam.**

Rejected alternatives: inlining the shell calls (breaks the invariant, leaves wiring untested); a
separate `TrayActions` coordinator (more structure than five small actions warrant).

### New components

| Type | Location | Kind | Responsibility |
|---|---|---|---|
| `IAppShell` / `AppShell` | `Platform/` | seam + impl | `OpenFolder`, `OpenUrl`, `CopyToClipboard`, `ShowInfo`, `Confirm` |
| `AppVersion` | `App/` | helper | `Current` = running assembly version |
| `AboutText` | `App/` | pure | `Build(Version)` → about text + GitHub URL |
| `UpdateCheckResult` | `App/` | record | `UpToDate` / `UpdateAvailable(ver,url)` / `Failed(msg)` |
| `IReleaseFeed` / `GitHubReleaseFeed` | `App/` | seam + impl | `GetLatestReleaseAsync()` → newest release (incl. prereleases) or null |
| `UpdateChecker` | `App/` | logic | orchestrates feed + version compare + error handling |
| `DiagnosticsReport` | `Diagnostics/` | pure | `Build(version, os, state, detail, recentLogLines)` → string |
| `LogTail` | `Diagnostics/` | helper | `ReadRecent(directory, now, count)` → last N lines, never throws |

`ConnectionManager` gains one public method: `RequestConnect()`.

## Component design

### Connect Now → `ConnectionManager.RequestConnect()`

Reuses the `_clickGrant` carve-out that already powers `SelectPhone`:
`ConnectPermitted => !_latch.IsSet && (_settings.AutoReconnect || _clickGrant != ClickGrant.None)`.

`RequestConnect()` is `SelectPhone`'s grant path for the **already-selected** phone:

1. No-op if disposed or no phone selected (`_settings.PhoneDeviceId is null`).
2. Clear the suppression latch — the same clear `SelectPhone` uses, which clears it "whatever the
   reason" (a deliberate Disconnect **or** an auto-reconnect-off suppression).
3. `_clickGrant = ClickGrant.Phone` — grants a connect even with auto-reconnect off.
4. Cancel any open grace window.
5. `_reconciler.RunAsync("connect requested", userAsked: true)` — one connect path in the class.
6. Publish, preserving the grant across the Publish (the `granted = _clickGrant; Publish(); _clickGrant
   = granted;` dance `SelectPhone` documents, for the same reason).

It **does not** change `PhoneDeviceId`, `AutoReconnect`, the watched device, or the calls role. The
one-shot semantics fall out for free: `ReleaseClickGrantIfDelivering` drops the grant once a half is
delivering, so after the next drop with auto-reconnect off the app goes dormant again — matching what
the toggle says.

**Enablement:** the menu item is enabled when a phone is selected (`PhoneDeviceId is not null`),
mirroring Disconnect. Clicking while already connected is a harmless idempotent re-drive.

### Open Logs

`IAppShell.OpenFolder(FileLog.DefaultDirectory)`. Real impl: `Process.Start("explorer.exe", path)`.

### About

`AboutText.Build(AppVersion.Current)` returns a short body (name, version, one-line description). The
tray shows it as `Confirm("About Klangbruecke", body + "\n\nOpen the project page on GitHub?")`; Yes →
`OpenUrl("https://github.com/MYSTRAVIL/klangbruecke")`, No → dismiss. (Confirm rather than ShowInfo so
the GitHub link is reachable from the same dialog.)

### Check for Updates

- `GitHubReleaseFeed.GetLatestReleaseAsync()`: `GET https://api.github.com/repos/MYSTRAVIL/klangbruecke/releases`
  (the **list**, not `/releases/latest` — 0.x builds are prereleases and `/releases/latest` excludes
  them, returning 404). GitHub returns releases newest-first; take the first. **A `User-Agent` header
  is required** by the GitHub API. Returns `ReleaseInfo(Tag, HtmlUrl)` or null (no releases).
- `UpdateChecker.CheckAsync()`: calls the feed; on null/throw → `Failed(message)`; else parse the tag
  (`vX.Y.Z`) to a `Version`, compare to `AppVersion.Current`'s first three components → `UpToDate` or
  `UpdateAvailable(ver, url)`. The pure compare (`newer` / `older` / `equal` / `malformed tag`) is a
  separately tested helper.
- Tray: on `UpToDate`, `ShowInfo("You're up to date", "Klangbruecke <v> is current.")`; on
  `UpdateAvailable`, `Confirm("Update available", "<v> is available. Open the release page?")` →
  `OpenUrl(url)`; on `Failed`, `ShowInfo("Couldn't check for updates", message)`.

`internetClient` is already declared in the manifest (FINDINGS §12), so the call is permitted.

### Copy Diagnostics

`DiagnosticsReport.Build(...)` assembles, in order: a one-line "review before sharing — includes device
names" header; app version; OS version (`Environment.OSVersion`); current `ConnectionState` + detail
(from the manager); then the last ~30 log lines from `LogTail.ReadRecent(FileLog.DefaultDirectory, now,
30)`. `IAppShell.CopyToClipboard(report)`.

`LogTail.ReadRecent` reads the current day's file (`FileLog.FileNameFor(now)`), returns its last N
lines, and — matching the never-throw diagnostics ethos — returns an empty result on a missing or
unreadable file rather than throwing. Device names are included deliberately (personal app, own
machine); the header and clipboard-only delivery let the user scrub before pasting.

### README troubleshooting (docs only)

A "Troubleshooting" section: log location (`%LOCALAPPDATA%\Klangbruecke\logs`), how to reset settings,
verifying the real endpoint with `Get-PnpDevice`, and a pointer to the stale-IRK pairing trap
(FINDINGS §3).

## Menu layout

```
<status line>                (disabled)
──────────
Phone ▸
Output ▸
──────────
Connect Now
Disconnect
──────────
Calls                        (toggle)
Reconnect automatically      (toggle)
──────────
Diagnostics ▸
    Open Logs
    Copy Diagnostics
    ──────────
    Check for Updates…
    About Klangbruecke
──────────
Exit
```

Connect Now / Disconnect (the connection actions) sit directly under the device pickers; the toggles
below them; the four meta items nest under **Diagnostics**; Exit stays last and still-guaranteed.

## Error handling

- The whole `IAppShell` real impl guards each verb; a failed `Process.Start` / clipboard / message box
  is logged (`Log.Error`) and swallowed, never crashing the tray. Menu clicks run on the UI (STA)
  thread, so `Clipboard.SetText` is on the right apartment.
- `Check for Updates` never throws to the UI: all failure (offline, HTTP error, malformed JSON/tag)
  becomes `UpdateCheckResult.Failed`, shown as a message.
- `RequestConnect` follows the manager's existing patterns (disposed guard, single connect path).

## Testing

- **Pure/logic units, direct tests:** `AboutText.Build`; `UpdateChecker` version-compare
  (newer/older/equal/malformed) and `CheckAsync` with a fake `IReleaseFeed` (release / null / throws);
  `DiagnosticsReport.Build` (field order, header, log-line inclusion); `LogTail.ReadRecent` (tail
  count, missing file, unreadable file).
- **`RequestConnect`:** through `ConnectionManagerTests` with the existing `FakeScheduler` /
  `FakeLinkMonitor` harness — grant set and connect permitted with auto-reconnect off; grant released
  once a half delivers; no-op with no phone selected; a deliberate Disconnect cleared by it.
- **`AppShell` real impl:** thin untestable plumbing, exercised in tests via a `FakeAppShell` that
  records calls — consistent with how WASAPI/WinRT wrappers are handled.
- **Suite discipline:** run unfiltered (STATUS.md caution). Bump the version in both
  `Klangbruecke.csproj` and `packaging/AppxManifest.xml` before the next packaged build.

## Files touched (anticipated)

- New: `Platform/IAppShell.cs`, `Platform/AppShell.cs`; `App/AppVersion.cs`, `App/AboutText.cs`,
  `App/UpdateCheckResult.cs`, `App/IReleaseFeed.cs`, `App/GitHubReleaseFeed.cs`, `App/UpdateChecker.cs`;
  `Diagnostics/DiagnosticsReport.cs`, `Diagnostics/LogTail.cs`; plus the mirrored test files and a
  `FakeAppShell` / `FakeReleaseFeed`.
- Changed: `Connection/ConnectionManager.cs` (+`RequestConnect`); `TrayContext.cs` (menu items, wiring,
  `IAppShell` dependency); `Program.cs` (compose `AppShell`, `GitHubReleaseFeed`, `UpdateChecker` and
  inject into `TrayContext`); `README.md` (Troubleshooting).
