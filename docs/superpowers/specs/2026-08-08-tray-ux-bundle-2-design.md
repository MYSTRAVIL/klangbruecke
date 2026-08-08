# Tray UX bundle 2 — design

_Written 2026-08-08. Three tray UX improvements on top of the shipped bundle 1 (0.2.3.0). Follows the
codebase pattern of a pure, tested *policy* plus a thin, untested *seam*, like `TrayIconPolicy`/`IAppShell`._

## Goal

1. **Left-click opens the tray menu** (today only right-click does).
2. **Event sounds** — a chime on Connected / Disconnected / Degraded, behind a toggle.
3. **Auto-pick phone** — remember a set of phones and auto-connect whichever is present (first-present-wins).

## Non-goals

- **No ranking / preferred-switching** for auto-pick — first-present-wins, and an already-connected
  ("incumbent") phone is never switched away from even if another remembered phone is also on.
- **No AVRCP now-playing, no phone battery, no music-codec readout** — all measured absent on this Win10
  A2DP-sink stack (`FINDINGS §17`). Do not attempt them.
- **No call narrowband/wideband indicator** — parked behind a live-call probe (`FINDINGS §17`, §14).
- **No always-open window.**

## A. Left-click opens the menu

`TrayContext` shows the `ContextMenuStrip` today via the `_menu.Opening` handler (`OnMenuOpening`), which
cancels the first open, rebuilds the menu async, sets `_menuRebuilt`, and calls `_menu.Show(Cursor.Position)`
(which re-raises Opening, let through by the `_menuRebuilt` flag).

- Extract the rebuild-and-show body into a private `ShowContextMenuAsync()` used by both paths.
- Subscribe `_icon.MouseUp`; when `e.Button == MouseButtons.Left`, call `ShowContextMenuAsync()`. (WinForms
  auto-shows the context menu only on right-click; left-click must invoke it manually.)
- Preserve the `_menuRebuilt` reentrancy guard so `Show()`'s Opening event does not re-rebuild.
- No unit test (WinForms view). Gate: builds, suite green, and a manual smoke (left-click shows the menu).

## B. Event sounds

- **`SoundEvent`** enum in a suitable place (e.g. `src/Klangbruecke/Diagnostics/` or a new `Feedback/`):
  `Connected`, `Disconnected`, `Degraded`.
- **`SoundPolicy.For(ConnectionState previous, ConnectionState next) : SoundEvent?`** — pure, tested. The
  transition table (only these fire; everything else is `null`):
  - `Connected` — when `next == Connected` and `previous != Connected`.
  - `Degraded` — when `next == Degraded` and `previous == Connected` (a half dropped from a full bridge;
    NOT on `Connecting/Discovering → Degraded`, which is a partial initial connect).
  - `Disconnected` — when `previous ∈ {Connected, Degraded}` and `next ∉ {Connected, Degraded}` (the bridge
    was lost). This includes a deliberate Disconnect (`→ Suppressed`); that is acceptable feedback.
  - Silent on all Discovering/Connecting/RetryBackoff churn so it is not chatty.
- **`ISoundPlayer`** seam: `void Play(SoundEvent e)`. Real impl `SoundPlayer` plays the matching embedded
  WAV to the default output via `System.Media.SoundPlayer`, each call guarded so a playback failure cannot
  crash the tray (never-throw, like `AppShell`). A `FakeSoundPlayer` records calls for tests.
- **Synthesized chimes** — a committed generator `packaging/Generate-Sounds.ps1` writes three 16-bit PCM
  mono WAVs (44100 Hz) into `src/Klangbruecke/Assets/`, embedded as resources exactly like the tray `.ico`s
  (loaded from the assembly manifest). Tones (short, ~200–300 ms, modest amplitude with a tiny fade to
  avoid clicks):
  - `connect.wav` — two ascending tones (~660 Hz → ~880 Hz).
  - `disconnect.wav` — two descending tones (~880 Hz → ~660 Hz).
  - `degraded.wav` — one neutral tone (~440 Hz).
- **Toggle** — `Settings.EventSounds` (bool, **default true**; a new field defaults true on load). A tray
  **"Sounds"** checkable menu item toggles it via `ConnectionManager.SetEventSounds(bool)` (which saves
  Settings, consistent with the other toggles that route writes through the manager). `TrayContext` reads
  `Settings.EventSounds` for the tick and to gate playback.
- **Wiring** — `TrayContext` observes `StateChanged`; it tracks the previous state it saw, computes
  `SoundPolicy.For(previous, next)`, and if non-null and `Settings.EventSounds`, calls `_soundPlayer.Play`.
  Composed in `Program.cs` (`new SoundPlayer()` injected into `TrayContext`).

## C. Auto-pick phone (first-present-wins)

### Data model
- **`Settings.RememberedPhoneIds : List<string>`** (new; default empty) is the auto-pick candidate set.
- **`Settings.PhoneDeviceId : string?`** stays as the *currently-active/watched* phone (the one
  `LinkMonitor` watches, for fast reconnect).
- **Migration** — in `Settings.Load`, if `RememberedPhoneIds` is empty and `PhoneDeviceId` is non-null, seed
  `RememberedPhoneIds` with that one id. Preserves existing behaviour for a user upgrading.

### `PhonePicker.Pick(string? activeId, IReadOnlyList<string> remembered, Func<string,bool> isPresent) : string?`
Pure, tested. In order:
1. If `activeId` is non-null, is in `remembered`, and `isPresent(activeId)` → return `activeId` (keep the
   incumbent; never thrash a working connection).
2. Else the first id in `remembered` where `isPresent(id)` → return it.
3. Else if `activeId` is non-null and in `remembered` → return `activeId` (keep watching it so its
   fast-reconnect edge still fires when it returns).
4. Else `remembered` first element, or `null` if empty.

### `ConnectionManager`
- Public API changes (the phone submenu now manages a *set*, not a single selection):
  - `SetPhoneRemembered(string id, bool remembered)` — add/remove from `RememberedPhoneIds`, save, resolve.
  - `ClearRememberedPhones()` — the "None" action; clears the set, saves, stops watching, resolves.
  - Keep `RequestConnect()` (bundle 1): it now runs the resolver (grant + connect the picked phone).
  - `SelectPhone`/`DeselectPhone` are removed or repurposed into the above; update all call sites and their
    `ConnectionManagerTests`.
- **The resolver** (private): reads each remembered phone's presence via the existing static
  `LinkMonitor.ReadLinkStatusAsync(id)` (present ≙ `BluetoothLinkStatus.Connected`, i.e. the ACL link is
  up — reliable, unlike the audio endpoint per §4), calls `PhonePicker.Pick`, and if the pick differs from
  the current active phone, re-points watch + connect through the existing single-connect path (the same
  latch-clear + `ClickGrant` + reconcile the old `SelectPhone` used). Runs at: `Start`, when the active
  phone goes absent (removed edge / grace-window range exit), and on the 30 s reconcile tick.
- **Latency**: the usual phone returning is still ~5 s (watcher edge + fast-reconnect probe on the watched
  id). Switching to a *different* remembered phone is noticed on the next reconcile tick (~30 s) — matches
  the existing backstop cadence and is acceptable.

### Tray UI (flagged change)
- The Phone submenu becomes a **checkable list**: each paired phone item is `Checked` when its id is in
  `RememberedPhoneIds`; clicking toggles membership via `SetPhoneRemembered`. **None** calls
  `ClearRememberedPhones` (auto-connect off). The currently-connected phone is indicated in its label
  (e.g. append " (connected)"), read from the manager's state, not from a live ABI call on the menu path.

### Test infrastructure
- `FakeLinkMonitor` must answer `ReadLinkStatusAsync` per device id (today it returns a single `Status`
  regardless). Extend it with an id→status map (falling back to the existing single `Status` for
  unmapped ids) so resolver/picker tests can stage "phone A present, phone B absent".

## Architecture summary

New tested-pure units: `SoundPolicy`, `PhonePicker`. New seam: `ISoundPlayer` (+ `SoundPlayer` impl +
`FakeSoundPlayer`). Changed: `Settings` (+`RememberedPhoneIds`, +`EventSounds`, migration),
`ConnectionManager` (resolver + remembered-set API + `SetEventSounds`), `TrayContext` (left-click, Sounds
toggle, checkable phone submenu, sound wiring, previous-state tracking), `Program.cs` (compose
`SoundPlayer`), `FakeLinkMonitor` (per-id status). New: `packaging/Generate-Sounds.ps1` + three embedded
WAV assets.

## Execution & verification notes (this bundle is built unattended)

- This spec and its plan are executed **subagent-driven in a fresh, headless session while the user is
  away**. The plan must carry every decision (all resolved above) so no mid-build clarification is needed.
- **Hardware smoke is deferred to the user** and must NOT block the build: left-click opening the menu, the
  chimes actually being audible, and auto-pick with two real phones all need the machine + hardware. The
  unattended run should get to *builds + full suite green + whole-branch review clean*, push the branch,
  and **stop without merging to `main`** — leaving the merge and the hardware smoke to the user.
- Bump the version in both `Klangbruecke.csproj` and `packaging/AppxManifest.xml` only as part of a
  packaged build the user runs; the code+test work does not require it.
- Run the suite unfiltered (STATUS.md caution).
