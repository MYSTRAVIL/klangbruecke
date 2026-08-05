# Stage 1 ConnectionManager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Klangbruecke a connection lifecycle that recovers unattended from a call ending, a range exit and return, sleep/resume, reboot, and a phone-initiated disconnect.

**Architecture:** A `ConnectionManager` owning three small state machines — a link machine, a suppression latch, and one controller per half — with the seven reported state names derived as a pure projection. Every service sits behind an interface so the machines are driven by fakes in tests, and `AudioRouter` gains a second seam *inside* it so its five load-bearing properties become testable. Everything runs single-threaded on the UI thread via the existing `IUiDispatcher`.

**Tech Stack:** .NET 8 (`net8.0-windows10.0.19041.0`), WinForms tray host, NAudio 2.2.1 (WASAPI), WinRT (`AudioPlaybackConnection`, `PhoneLineTransportDevice`, `DeviceWatcher`, `BluetoothDevice`), xunit 2.9.2. No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-08-05-stage-1-connection-manager-design.md`. Read it before Task 2.

---

## Deliberate deviation from the writing-plans default

This plan gives **exact interface declarations** and **exact test cases** — each test's name, its
inputs, and the assertion it makes — but **not method bodies**. Implementers write the bodies.

That is not an oversight. From `docs/HANDOFF.md`: Stage 0's plan embedded complete code, and all
eight defects found in Stage 0 were **in the plan text, none in the implementations** — the plan got
one self-review while each task's code got a dedicated adversarial reviewer. Writing bodies into this
document would move the bugs back into the document.

A step that says "implement X so that the tests in step 1 pass" is complete when the interface and
the test cases above it are unambiguous. If they are not, that is a plan bug — report it rather than
guessing.

## Global Constraints

- Target `net8.0-windows10.0.19041.0`. **Do not raise `TargetPlatformMinVersion`** — 19041 is the floor for both WinRT APIs and the dev machine is 19045.
- **No new NuGet packages.** NAudio 2.2.1 and the Windows SDK projection are the entire dependency set.
- ASCII `Klangbruecke` everywhere — folder, namespace, assembly, package identity, display name. No umlaut anywhere.
- `Nullable` is enabled and warnings are errors in review. New code is null-annotated.
- **Do not remove either `AudioSinkPolicy.CanOpenConnection` gate** (`TrayContext` and `AudioSinkService.ConnectAsync`). Unpackaged, `AudioPlaybackConnection.TryCreateFromId` kills the process with an uncatchable `AccessViolationException` (`FINDINGS.md` §8). "Letting it try" is process death, not a false return.
- **Do not implement LAF token generation.** Standing instruction, the user's call (`CLAUDE.md`, `HANDOFF.md`).
- **No test may sleep, allocate a real timer, or require a phone.** `FakeScheduler.Advance` drives all timings.
- Test doubles go in `tests/Klangbruecke.Tests/Fakes/` as `public` or `internal` classes — **never `file`-scoped**. The existing `DeferringUiDispatcher` and `DroppingUiDispatcher` are `file`-scoped and therefore unreachable from other test files; Stage 1 needs its doubles across several.
- `Application.ThreadException`'s add accessor **assigns rather than combines**. If anything needs it, extend `Program.Main`'s existing lambda; never add a second subscriber.
- Log only when something changed. A reconcile tick that finds no drift writes nothing.
- Build: `dotnet build Klangbruecke.sln`. Test: `dotnet test Klangbruecke.sln`.
- Commit per task. Do not push.

---

## File structure

**New — pure units, no external dependencies:**

| File | Responsibility |
|---|---|
| `src/Klangbruecke/Platform/IScheduler.cs` | time and delayed work, as a seam |
| `src/Klangbruecke/Platform/UiScheduler.cs` | production `IScheduler` on the UI thread |
| `src/Klangbruecke/Platform/IPowerNotifier.cs` | resume notification, as a seam |
| `src/Klangbruecke/Platform/PowerNotifier.cs` | wraps `SystemEvents.PowerModeChanged` |
| `src/Klangbruecke/Connection/BackoffSchedule.cs` | the 2/4/8/16/30/60 s sequence |
| `src/Klangbruecke/Connection/LinkMachine.cs` | `NoPhone` / `Absent` / `Present` |
| `src/Klangbruecke/Connection/SuppressionLatch.cs` | deliberate-disconnect memory, two reasons |
| `src/Klangbruecke/Connection/ConnectionState.cs` | reported enum, snapshot, projection, detail text |

**New — seams over WinRT/WASAPI:**

| File | Responsibility |
|---|---|
| `src/Klangbruecke/Bluetooth/IAudioSinkService.cs` | A2DP connection contract + `PhoneDevice` |
| `src/Klangbruecke/Bluetooth/ICallTransportService.cs` | HFP registration contract + `CallTransportResult` |
| `src/Klangbruecke/Bluetooth/ILinkMonitor.cs` | `DeviceWatcher` + `ConnectionStatus` contract |
| `src/Klangbruecke/Bluetooth/LinkMonitor.cs` | its WinRT implementation |
| `src/Klangbruecke/Audio/IAudioRouter.cs` | router contract |
| `src/Klangbruecke/Audio/IAudioEndpointMonitor.cs` | endpoint presence contract |
| `src/Klangbruecke/Audio/EndpointMonitor.cs` | `IMMNotificationClient` implementation |
| `src/Klangbruecke/Audio/IAudioDeviceFactory.cs` | `ICaptureSource`, `IRenderSink`, factory, `AudioOutputDevice` |
| `src/Klangbruecke/Audio/WasapiDeviceFactory.cs` | adapters over `WasapiCapture` / `WasapiOut` |

**New — the machine:**

| File | Responsibility |
|---|---|
| `src/Klangbruecke/Connection/MusicHalf.cs` | `Off`/`Connecting`/`Linked`/`Up`/`Backoff` |
| `src/Klangbruecke/Connection/CallsHalf.cs` | `Off`/`Registering`/`Up`/`Backoff` |
| `src/Klangbruecke/Connection/ConnectionManager.cs` | intent, wiring, grace window, reconcile, resume |

**Modified:** `AudioRouter.cs`, `AudioSinkService.cs`, `CallTransportService.cs`, `StatusPresenter.cs`, `TrayContext.cs`, `Program.cs`.

**Tests:** `tests/Klangbruecke.Tests/Fakes/`, `tests/Klangbruecke.Tests/Connection/`, `tests/Klangbruecke.Tests/Audio/AudioRouterTests.cs`, `tests/Klangbruecke.Tests/Platform/SchedulerTests.cs`.

## Task dependency graph

```
Wave A (fully parallel):  1 (probe)   2 (IScheduler)   3 (BackoffSchedule)
                          4 (LinkMachine)   7 (router seam)   9 (IAudioSinkService)
                          10 (ICallTransportService)   12 (IPowerNotifier)

Wave B:  5  needs 4          (LinkState)
         8  needs 7          (same files, router property tests)
         11 needs 4          (BluetoothLinkStatus lives in ILinkMonitor.cs)
         13 needs 1          (probe decides IMMNotificationClient vs poll)

Wave C:  6  needs 4, 5       (projection over LinkState + SuppressionReason)

Wave D:  14 needs 2, 3, 6, 7, 9, 13      (MusicHalf)
         15 needs 2, 3, 6, 10            (CallsHalf)

Wave D': 19 needs 11          (debounce the link poll -- added mid-execution)

Wave E:  16 needs 4, 5, 6, 11, 12, 14, 15, 19
Wave F:  17 needs 16
Wave G:  18 needs 17
```

Tasks 7, 9, 10 and 17 all touch `TrayContext.cs`. Run 7, 9 and 10 in either order but not
concurrently in the same worktree, or expect a merge conflict in that one file.

---

### Task 1: Probe `IMMNotificationClient` before building on it

**Files:**
- Create: `docs/probes/2026-08-05-endpoint-notification.md`
- Scratch (not committed): a throwaway console probe under the session scratchpad

**Interfaces:**
- Consumes: nothing
- Produces: a documented yes/no that Task 15 depends on

This is risk #1 in the spec. It is not identity-gated, so `dotnet run` answers it in twenty minutes,
and the whole endpoint-driven design rests on it. Do this before writing `EndpointMonitor`.

- [ ] **Step 1: Write a throwaway probe**

A minimal console program (scratchpad, not the repo) that registers an `IMMNotificationClient` via
`MMDeviceEnumerator.RegisterEndpointNotificationCallback` and prints every callback with
`Environment.CurrentManagedThreadId` and `Thread.CurrentThread.GetApartmentState()`.

- [ ] **Step 2: Run it and toggle endpoints**

With the phone paired, connect and disconnect A2DP (or enable/disable any capture endpoint in Sound
settings) and record what arrives.

Answer these four questions explicitly:
1. Which callbacks fire when the A2DP SNK endpoint appears and disappears — `OnDeviceAdded`, `OnDeviceRemoved`, `OnDeviceStateChanged`, or several?
2. What thread and apartment do they arrive on?
3. Does the registration survive if the `MMDeviceEnumerator` used to register goes out of scope, or must it be held?
4. Does anything throw or leak when `UnregisterEndpointNotificationCallback` runs during shutdown?

- [ ] **Step 3: Write the findings note**

`docs/probes/2026-08-05-endpoint-notification.md` recording the four answers, the exact code that
produced them, and the date. State measurements as measurements.

- [ ] **Step 4: Decide**

If the callbacks work: proceed as planned, and Task 15 pins the marshalling and lifetime rules the
probe established.

If they do not: `EndpointMonitor` falls back to polling `FindSinkCaptureEndpoint` every 2 s **only
while music is `Linked`** (idle otherwise), behind the same `IAudioEndpointMonitor` interface. No
other task changes. Record the fallback decision in the note.

- [ ] **Step 5: Commit**

```bash
git add docs/probes/2026-08-05-endpoint-notification.md
git commit -m "Probe IMMNotificationClient before building the endpoint monitor on it"
```

---

### Task 2: `IScheduler` and `FakeScheduler`

**Files:**
- Create: `src/Klangbruecke/Platform/IScheduler.cs`, `src/Klangbruecke/Platform/UiScheduler.cs`
- Create: `tests/Klangbruecke.Tests/Fakes/FakeScheduler.cs`, `tests/Klangbruecke.Tests/Platform/SchedulerTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:

```csharp
namespace Klangbruecke.Platform;

public interface IScheduler
{
    DateTimeOffset Now { get; }

    /// <summary>Runs the action once after the delay. Disposing the handle cancels it.</summary>
    IDisposable Schedule(TimeSpan delay, Action action);

    /// <summary>Runs the action every period until the handle is disposed.</summary>
    IDisposable SchedulePeriodic(TimeSpan period, Action action);
}

/// <summary>Delivers every callback on the thread that scheduled it. UI thread only.</summary>
public sealed class UiScheduler : IScheduler, IDisposable
{
    public UiScheduler();
    public DateTimeOffset Now { get; }
    public IDisposable Schedule(TimeSpan delay, Action action);
    public IDisposable SchedulePeriodic(TimeSpan period, Action action);
    public void Dispose();
}
```

```csharp
namespace Klangbruecke.Tests.Fakes;

public sealed class FakeScheduler : IScheduler
{
    public FakeScheduler(DateTimeOffset start);
    public DateTimeOffset Now { get; }
    public int PendingCount { get; }
    public IDisposable Schedule(TimeSpan delay, Action action);
    public IDisposable SchedulePeriodic(TimeSpan period, Action action);

    /// <summary>Advances the clock, firing every due callback in due order.</summary>
    public void Advance(TimeSpan by);
}
```

`UiScheduler` is built on `System.Windows.Forms.Timer`, which raises `Tick` on the UI thread, so no
marshalling is needed inside it. Do not use `System.Threading.Timer` — its callbacks arrive on the
threadpool and would defeat the single-threading guarantee the whole design rests on.

- [ ] **Step 1: Write the failing tests**

`tests/Klangbruecke.Tests/Platform/SchedulerTests.cs`, all against `FakeScheduler`:

| Test | Asserts |
|---|---|
| `Advance_before_delay_does_not_run_the_action` | advancing 1 s on a 2 s schedule leaves the action uncalled |
| `Advance_past_delay_runs_the_action_once` | advancing 3 s on a 2 s schedule calls it exactly once |
| `Advance_far_past_delay_still_runs_a_one_shot_once` | advancing 60 s on a 2 s schedule calls it once, not repeatedly |
| `Disposing_the_handle_cancels_a_pending_action` | dispose then advance past the delay; never called |
| `Periodic_fires_once_per_period` | 10 s period, advance 35 s, called 3 times |
| `Disposing_a_periodic_handle_stops_it` | advance 15 s, dispose, advance 100 s; called once |
| `Now_advances_by_exactly_the_amount_advanced` | start + 7 s after `Advance(7s)` |
| `Callbacks_fire_in_due_order_not_registration_order` | register 5 s then 1 s; advance 10 s; the 1 s action ran first |
| `An_action_scheduled_from_inside_a_callback_runs_on_a_later_advance` | re-entrant scheduling does not deadlock or run early |
| `PendingCount_drops_to_zero_after_a_one_shot_fires` | bookkeeping does not leak handles |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~SchedulerTests"`
Expected: FAIL — `FakeScheduler` / `IScheduler` do not exist.

- [ ] **Step 3: Implement `IScheduler`, `FakeScheduler`, `UiScheduler`**

Bodies are yours. Constraints: `FakeScheduler.Advance` must fire due callbacks in due order and must
tolerate a callback that schedules more work; `UiScheduler` must use `System.Windows.Forms.Timer`.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~SchedulerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Platform/IScheduler.cs src/Klangbruecke/Platform/UiScheduler.cs tests/Klangbruecke.Tests/Fakes/FakeScheduler.cs tests/Klangbruecke.Tests/Platform/SchedulerTests.cs
git commit -m "Add the IScheduler seam so Stage 1's four timings are testable without sleeping"
```

---

### Task 3: `BackoffSchedule`

**Files:**
- Create: `src/Klangbruecke/Connection/BackoffSchedule.cs`
- Create: `tests/Klangbruecke.Tests/Connection/BackoffScheduleTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:

```csharp
namespace Klangbruecke.Connection;

/// <summary>2, 4, 8, 16, 30, then 60 seconds forever. Reset on success.</summary>
public sealed class BackoffSchedule
{
    public int Attempt { get; }
    public TimeSpan CurrentDelay { get; }
    public void Advance();
    public void Reset();
}
```

- [ ] **Step 1: Write the failing tests**

| Test | Asserts |
|---|---|
| `First_delay_is_two_seconds` | `CurrentDelay == 2s` on a fresh instance |
| `Delays_follow_the_specified_sequence` | advancing four times yields 2, 4, 8, 16, 30 s |
| `Sixth_and_later_delays_are_sixty_seconds` | after five advances, `CurrentDelay == 60s`, and still 60 s after fifty |
| `Reset_returns_to_the_first_delay` | advance three times, `Reset()`, `CurrentDelay == 2s` |
| `Reset_returns_attempt_to_zero` | `Attempt == 0` after `Reset()` |
| `Attempt_counts_advances` | `Attempt == 3` after three advances |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~BackoffScheduleTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement `BackoffSchedule`**

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~BackoffScheduleTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Connection/BackoffSchedule.cs tests/Klangbruecke.Tests/Connection/BackoffScheduleTests.cs
git commit -m "Add BackoffSchedule: 2/4/8/16/30/60s, reset on success"
```

---

### Task 4: `LinkMachine`

**Files:**
- Create: `src/Klangbruecke/Connection/LinkMachine.cs`
- Create: `tests/Klangbruecke.Tests/Connection/LinkMachineTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `BluetoothLinkStatus`, **created in this task** as a new file
  `src/Klangbruecke/Bluetooth/ILinkMonitor.cs` containing only the enum for now. Task 11 adds the
  `ILinkMonitor` interface to the same file. Do not put the enum anywhere else — Task 11 expects it
  there.

```csharp
namespace Klangbruecke.Bluetooth;

public enum BluetoothLinkStatus { Unknown, Disconnected, Connected }
```

Also produces:

```csharp
namespace Klangbruecke.Connection;

public enum LinkState { NoPhone, Absent, Present }

public sealed class LinkMachine
{
    public LinkState State { get; }

    /// <summary>Each returns true if the state changed, so callers can log only on change.</summary>
    public bool OnPhoneSelected();
    public bool OnPhoneDeselected();
    public bool OnDeviceAppeared();
    public bool OnDeviceRemoved();
    public bool OnLinkStatusRead(BluetoothLinkStatus status);
}
```

- [ ] **Step 1: Write the failing tests**

| Test | Asserts |
|---|---|
| `Starts_in_NoPhone` | fresh instance is `NoPhone` |
| `Selecting_a_phone_moves_to_Absent` | not straight to `Present` — presence must be observed |
| `Deselecting_from_any_state_returns_to_NoPhone` | theory over all three states |
| `Device_appearing_moves_Absent_to_Present` | |
| `Device_removed_moves_Present_to_Absent` | |
| `Link_status_Connected_moves_Absent_to_Present` | the reconcile path, not just the watcher |
| `Link_status_Disconnected_moves_Present_to_Absent` | |
| `Link_status_Unknown_is_treated_as_Disconnected` | **spec rule** — guessing the other way produces silent permanent dormancy |
| `Link_status_read_while_NoPhone_changes_nothing` | stays `NoPhone`, returns false |
| `Device_appearing_while_NoPhone_changes_nothing` | a stale watcher event cannot resurrect a deselected phone |
| `Transitions_that_change_nothing_return_false` | theory: re-applying the current state returns false every time |
| `Transitions_that_change_something_return_true` | theory across each real transition |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~LinkMachineTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `LinkMachine`**

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~LinkMachineTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Connection/LinkMachine.cs src/Klangbruecke/Bluetooth/ILinkMonitor.cs tests/Klangbruecke.Tests/Connection/LinkMachineTests.cs
git commit -m "Add LinkMachine; Unknown link status counts as disconnected"
```

---

### Task 5: `SuppressionLatch`

**Files:**
- Create: `src/Klangbruecke/Connection/SuppressionLatch.cs`
- Create: `tests/Klangbruecke.Tests/Connection/SuppressionLatchTests.cs`

**Interfaces:**
- Consumes: `LinkState` (Task 4)
- Produces:

```csharp
namespace Klangbruecke.Connection;

public enum SuppressionReason { None, Deliberate, AutoReconnectOff }

/// <summary>
/// In-memory only, never persisted: a reboot is the link dropping and returning.
/// Deliberate suppression expires when the phone leaves the room; AutoReconnectOff does not,
/// because it is a setting rather than a moment.
/// </summary>
public sealed class SuppressionLatch
{
    public SuppressionReason Reason { get; }
    public bool IsSet { get; }

    public void SuppressDeliberate();
    public void SuppressAutoReconnectOff();

    /// <summary>Feeds the link machine's state in. Drives the sawAbsent memory and the re-arm.</summary>
    public void OnLinkState(LinkState state);

    public void OnAutoReconnectEnabled();
    public void OnPhoneSelectionChanged();
    public void Clear();
}
```

- [ ] **Step 1: Write the failing tests**

| Test | Asserts |
|---|---|
| `Starts_clear` | `Reason == None`, `IsSet == false` |
| `Deliberate_suppression_sets_the_reason` | |
| `Deliberate_clears_after_the_link_drops_and_returns` | `SuppressDeliberate()`, `OnLinkState(Absent)`, `OnLinkState(Present)` → clear |
| `Deliberate_does_not_clear_on_Present_alone` | suppress then `OnLinkState(Present)` without an intervening `Absent` → still set. **This is the whole point of `sawAbsent`** |
| `Deliberate_survives_repeated_Present_reports` | ten `OnLinkState(Present)` calls do not clear it |
| `A_drop_and_return_inside_one_reconcile_tick_leaves_it_set` | the documented limitation of polling: if `Absent` is never observed, the latch stays set |
| `AutoReconnectOff_does_not_clear_on_link_drop_and_return` | the difference from `Deliberate` |
| `AutoReconnectOff_clears_when_auto_reconnect_is_switched_on` | |
| `AutoReconnectOff_clears_when_a_phone_is_picked` | `OnPhoneSelectionChanged()` |
| `Deliberate_clears_when_a_phone_is_picked` | |
| `Clear_resets_the_sawAbsent_memory` | `Clear()`, `SuppressDeliberate()`, `OnLinkState(Present)` → still set |
| `Suppressing_again_resets_the_sawAbsent_memory` | `SuppressDeliberate()`, `OnLinkState(Absent)`, `SuppressDeliberate()`, `OnLinkState(Present)` → still set |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~SuppressionLatchTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `SuppressionLatch`**

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~SuppressionLatchTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Connection/SuppressionLatch.cs tests/Klangbruecke.Tests/Connection/SuppressionLatchTests.cs
git commit -m "Add SuppressionLatch with two reasons that re-arm differently"
```

---

### Task 6: `ConnectionState` and the projection

**Files:**
- Create: `src/Klangbruecke/Connection/ConnectionState.cs`
- Create: `tests/Klangbruecke.Tests/Connection/ConnectionStateProjectionTests.cs`

**Interfaces:**
- Consumes: `LinkState`, `SuppressionReason` (Tasks 4, 5)
- Produces:

```csharp
namespace Klangbruecke.Connection;

public enum ConnectionState { Idle, Discovering, Connecting, Connected, Degraded, Suppressed, RetryBackoff }

public enum MusicState { Off, Connecting, Linked, Up, Backoff }
public enum CallsState { Off, Registering, Up, Backoff }

public readonly record struct ConnectionSnapshot(
    bool PhoneSelected,
    SuppressionReason Suppression,
    LinkState Link,
    bool MusicEnabled,
    MusicState Music,
    bool CallsEnabled,
    CallsState Calls,
    TimeSpan? NextRetryIn);

public static class ConnectionStateProjection
{
    public static ConnectionState Project(ConnectionSnapshot snapshot);

    /// <summary>The short phrase the tray shows after the state name. Never null, never empty.</summary>
    public static string DetailFor(ConnectionSnapshot snapshot);
}
```

Evaluation order — **first match wins**:

1. `!PhoneSelected` → `Idle`
2. `Suppression != None` → `Suppressed`
3. `Link == Absent` → `Discovering`
4. any enabled half in `Connecting` / `Registering` → `Connecting`
5. at least one half enabled, and every enabled half up (music `Linked` **or** `Up` counts) → `Connected`
6. at least one enabled half up, and at least one enabled half **not** up → `Degraded`
7. no enabled half up, and at least one enabled half in `Backoff` → `RetryBackoff`
8. otherwise — no half enabled, or every enabled half still `Off` → `Idle`

Rules 5-8 are wider than the design document's table, which was **not exhaustive**: an *enabled*
half sitting in `Off` matched none of its rows, so a phone selected with the link `Present` and
neither half yet initiated — the ordinary startup instant — fell off the end. Rule 5 also needs the
"at least one half enabled" conjunct because "every enabled half is up" is vacuously true over an
empty set, which would report `Connected` on an unpackaged run with calls switched off. Widening
the existing rules is deliberate rather than adding a narrow rule plus a fallback: two branches
that agree on every input are mutually un-killable by any mutation, which is exactly what the
standing "everything gets an assertion" rule exists to catch.

- [ ] **Step 1: Write the failing tests**

| Test | Asserts |
|---|---|
| `No_phone_selected_is_Idle` | regardless of every other field |
| `Suppressed_beats_link_and_half_state` | `Deliberate` and `AutoReconnectOff` both project `Suppressed` |
| `Link_absent_is_Discovering` | even when the calls half is still `Up` — registration is not link-scoped |
| `Both_halves_up_is_Connected` | |
| `Music_Linked_counts_as_up` | music `Linked` + calls `Up` → `Connected`, **not** `Degraded`. The most common idle condition must not cry wolf |
| `Music_up_and_calls_backoff_is_Degraded` | |
| `Calls_up_and_music_backoff_is_Degraded` | |
| `Both_halves_backoff_is_RetryBackoff` | |
| `Either_half_connecting_is_Connecting` | theory over music `Connecting` and calls `Registering` |
| `Music_alone_with_calls_disabled_is_Connected` | **disabled is not failed** — calls off must not pin the app in `Degraded` |
| `Calls_alone_with_music_disabled_is_Connected` | the unpackaged development case |
| `No_half_enabled_is_Idle` | unpackaged with calls off; never `Connected` |
| `Disabled_half_in_Backoff_is_ignored` | a stale `Backoff` on a disabled half does not produce `Degraded` |
| `Project_is_total` | `MemberData` over the full cross-product of every enum value and both bools; assert no throw and a defined result for every combination |
| `Detail_is_never_null_or_empty` | same cross-product |
| `Detail_for_music_Linked_names_waiting_for_phone_audio` | pinned string, because the tray is the only place a user learns this |
| `Detail_for_backoff_names_the_retry_interval` | uses `NextRetryIn` |
| `Detail_for_AutoReconnectOff_differs_from_Deliberate` | both project `Suppressed`; the detail is what tells them apart |
| `Detail_for_no_half_enabled_names_the_reason` | not a bare "Idle" |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~ConnectionStateProjectionTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `ConnectionState.cs`**

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~ConnectionStateProjectionTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Connection/ConnectionState.cs tests/Klangbruecke.Tests/Connection/ConnectionStateProjectionTests.cs
git commit -m "Derive the seven reported states as a pure projection over the three machines"
```

---

### Task 7: The `AudioRouter` device factory seam

**Files:**
- Create: `src/Klangbruecke/Audio/IAudioDeviceFactory.cs`, `src/Klangbruecke/Audio/WasapiDeviceFactory.cs`, `src/Klangbruecke/Audio/IAudioRouter.cs`
- Modify: `src/Klangbruecke/Audio/AudioRouter.cs`, `src/Klangbruecke/TrayContext.cs` (construction and the Output menu only)

**Interfaces:**
- Consumes: nothing
- Produces:

```csharp
namespace Klangbruecke.Audio;

public readonly record struct AudioOutputDevice(string Id, string Name);

public interface ICaptureSource : IDisposable
{
    WaveFormat WaveFormat { get; }
    string FriendlyName { get; }
    event EventHandler<WaveInEventArgs>? DataAvailable;
    event EventHandler<StoppedEventArgs>? RecordingStopped;
    void StartRecording();
    void StopRecording();
}

public interface IRenderSink : IDisposable
{
    WaveFormat MixFormat { get; }
    string FriendlyName { get; }
    event EventHandler<StoppedEventArgs>? PlaybackStopped;
    void Init(IWaveProvider source);
    void Play();
}

public interface IAudioDeviceFactory
{
    /// <summary>Null when no A2DP SNK capture endpoint is present.</summary>
    ICaptureSource? CreateSinkCapture();

    /// <summary>Falls back to the default multimedia render endpoint. Null when there is none.</summary>
    IRenderSink? CreateRender(string? preferredOutputDeviceId);

    IReadOnlyList<AudioOutputDevice> ListOutputs();
}

public interface IAudioRouter : IDisposable
{
    bool IsRunning { get; }
    event EventHandler<StatusMessage>? Status;

    /// <summary>Raised after the route has been torn down, on the dispatcher thread.</summary>
    event EventHandler? Stopped;

    bool Start(string? preferredOutputDeviceId);
    void Stop();
}
```

`AudioRouter`'s constructor becomes `AudioRouter(IUiDispatcher ui, IAudioDeviceFactory devices)` and
it implements `IAudioRouter`. The static `FindSinkCaptureEndpoint`, `GetOutputDevices` and
`GetOutputDeviceOrDefault` move into `WasapiDeviceFactory`; `MMDevice` must not appear in
`AudioRouter`'s own body or in `TrayContext` after this task.

**This is a refactor, not a behaviour change.** Every one of the five load-bearing properties
documented in `AudioRouter.cs`'s comments must survive verbatim in meaning:

1. `_session` published before `StartRecording`/`Play`
2. `EndSession()` before `Report()` in both stopped handlers
3. `_session = null` before the unsubscribes in `Stop()`
4. the stale-session check inside the posted teardown lambda
5. the `ReferenceEquals(sender, …)` guards

The new `Stopped` event is raised from inside the posted teardown lambda, **after** `Stop()` — so
subscribers see `IsRunning == false` and cannot re-enter teardown. Do not raise it from the stopped
handlers directly; that would put a `ConnectionManager` callback on the NAudio play thread, which is
the deadlock this whole design avoids.

- [ ] **Step 1: Build and run the existing suite to record the baseline**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS. **Write down the exact test count** — it is 153 plus whatever Wave A tasks have
already landed, so do not hard-code 153. Step 5 compares against this number.

- [ ] **Step 2: Extract the interfaces and adapters**

Create `IAudioDeviceFactory.cs` and `WasapiDeviceFactory.cs` with `WasapiCaptureSource` and
`WasapiRenderSink` adapters wrapping `WasapiCapture` and `WasapiOut`. The adapters are thin: they
forward events and calls and own disposal. `WasapiOut` is constructed with
`AudioClientShareMode.Shared`, `useEventSync: true`, `latency: 50` exactly as today.

- [ ] **Step 3: Rewrite `AudioRouter` against the factory**

`Start(string? preferredOutputDeviceId)` asks the factory for both ends, reports
`"No A2DP sink endpoint - nothing is holding a connection open."` when the capture is null and
`"No usable output device."` when the render is null, and otherwise proceeds exactly as today —
including the unconditional format-pair log line and the failure path that names both formats even
when the `MixFormat` read is what threw.

- [ ] **Step 4: Point `TrayContext` at the new surface**

Construct `AudioRouter` with a `WasapiDeviceFactory`; build the Output menu from
`IAudioDeviceFactory.ListOutputs()`; drop `StartRouting`'s endpoint lookup in favour of
`_router.Start(_settings.OutputDeviceId)`. No other `TrayContext` change in this task.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS, with the same count recorded in Step 1. A refactor that loses a test has changed
behaviour.

- [ ] **Step 6: Commit**

```bash
git add src/Klangbruecke/Audio/ src/Klangbruecke/TrayContext.cs
git commit -m "Put a device factory seam inside AudioRouter so its behaviour becomes testable"
```

---

### Task 8: Test the five load-bearing `AudioRouter` properties

**Files:**
- Create: `tests/Klangbruecke.Tests/Fakes/FakeAudioDeviceFactory.cs` (with `FakeCaptureSource`, `FakeRenderSink`)
- Create: `tests/Klangbruecke.Tests/Audio/AudioRouterTests.cs`

**Interfaces:**
- Consumes: Task 7's `IAudioDeviceFactory`, `ICaptureSource`, `IRenderSink`, `IAudioRouter`
- Produces: nothing consumed downstream

This is the task the whole inner seam exists for. From `HANDOFF.md`: two real defects were found here
by live hardware probes **after each survived three review rounds**, and reverting any of the five
properties today leaves all 153 tests green. Every test below must go red if its property is reverted
— verify that by actually reverting it, not by reasoning about it.

The fakes need to be able to: raise `RecordingStopped` / `PlaybackStopped` on demand and from inside
`StartRecording`; re-raise `Stopped` from inside `Dispose`; and record call order.

- [ ] **Step 1: Write the failing tests**

| Test | Asserts | Property |
|---|---|---|
| `Capture_stopping_during_StartRecording_is_not_discarded_as_stale` | fake raises `RecordingStopped` synchronously from inside `StartRecording`; the router tears down instead of ignoring it | 1 |
| `Playback_stopping_during_Play_is_not_discarded_as_stale` | same for `Play` | 1 |
| `IsRunning_is_false_when_the_capture_stopped_status_is_raised` | a `Status` subscriber reads `IsRunning` at the instant it is invoked | 2 |
| `IsRunning_is_false_when_the_playback_stopped_status_is_raised` | same for the playback handler | 2 |
| `Stop_is_not_re_entered_when_disposal_re_raises_stopped` | fake's `Dispose` raises `Stopped`; `Stop()` runs its body once | 3 |
| `A_queued_teardown_is_a_no_op_after_Stop_already_ran` | deferring dispatcher; `Stop()`, then drain; no second teardown | 4 |
| `A_teardown_posted_before_a_restart_does_not_kill_the_new_route` | deferring dispatcher; raise stopped, `Stop()`, `Start()`, then drain; the new route is still running | 4 |
| `A_stopped_event_from_a_replaced_capture_is_ignored` | keep the first fake, `Start()` again, raise stopped on the old one; nothing tears down | 5 |
| `A_stopped_event_from_a_replaced_output_is_ignored` | same for the render side | 5 |
| `Both_halves_reporting_stopped_tears_down_once` | capture and playback both raise; one teardown | 3, 4 |
| `Start_logs_the_capture_and_render_format_pair_when_they_match` | `RecordingLog`; the line exists on the happy path, not only on failure | — |
| `Start_logs_the_format_pair_when_they_differ` | and says shared mode is converting | — |
| `Start_failure_names_both_formats_when_the_MixFormat_read_threw` | fake render throws on `MixFormat`; the error line still names the capture format | — |
| `Start_returns_false_and_reports_when_there_is_no_capture_endpoint` | factory returns null capture | — |
| `Start_returns_false_and_reports_when_there_is_no_render_endpoint` | factory returns null render | — |
| `Stopped_event_fires_after_IsRunning_is_false` | the new event's contract; a subscriber that calls `Start()` must not race teardown | — |
| `Stopped_event_does_not_fire_on_a_deliberate_Stop` | tray Disconnect is not a route failure | — |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~AudioRouterTests"`
Expected: FAIL — fakes do not exist.

- [ ] **Step 3: Write the fakes and make the tests pass**

If a test fails because `AudioRouter` is wrong rather than because the test is wrong, **fix
`AudioRouter`** and say so in the commit message. That is the point of the exercise.

- [ ] **Step 4: Verify each test actually defends its property**

For each of the five, revert the property in a scratch edit, confirm the named test goes red, then
restore. A test that stays green is not defending anything — rewrite it.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add tests/Klangbruecke.Tests/Fakes/ tests/Klangbruecke.Tests/Audio/AudioRouterTests.cs src/Klangbruecke/Audio/AudioRouter.cs
git commit -m "Cover AudioRouter's five load-bearing properties, each verified to fail on revert"
```

---

### Task 9: `IAudioSinkService`

**Files:**
- Create: `src/Klangbruecke/Bluetooth/IAudioSinkService.cs`
- Modify: `src/Klangbruecke/Bluetooth/AudioSinkService.cs`, `src/Klangbruecke/TrayContext.cs`

**Interfaces:**
- Consumes: nothing
- Produces:

```csharp
namespace Klangbruecke.Bluetooth;

public readonly record struct PhoneDevice(string Id, string Name);

public enum AudioSinkConnectionState { Closed, Opened }

public interface IAudioSinkService : IDisposable
{
    string? ConnectedDeviceId { get; }

    /// <summary>
    /// The WinRT connection object is open. Deliberately NOT a claim about the capture endpoint:
    /// the two are separated by an unbounded interval (spec finding #2), and folding them into one
    /// property makes that interval unrepresentable.
    /// </summary>
    bool IsConnected { get; }

    event EventHandler<StatusMessage>? Status;
    event EventHandler<AudioSinkConnectionState>? StateChanged;

    Task<IReadOnlyList<PhoneDevice>> FindDevicesAsync();
    Task<bool> ConnectAsync(string deviceId);
    void Disconnect();
}
```

Changes to `AudioSinkService`: implement the interface; convert the static `FindDevicesAsync` to an
instance method returning `PhoneDevice` records (keeping the per-device log lines verbatim — they are
the input to transport correlation and must stay greppable); translate
`AudioPlaybackConnection.StateChanged` into the `StateChanged` event **in addition to** the existing
`Report`, not instead of it.

**Do not remove or weaken the `AudioSinkPolicy.CanOpenConnection` guard at the top of
`ConnectAsync`.** Its comment already anticipates this exact task.

- [ ] **Step 1: Write the failing tests**

`tests/Klangbruecke.Tests/Bluetooth/AudioSinkServiceContractTests.cs`. These cannot open a real
connection, so they cover the surface that does not need one:

| Test | Asserts |
|---|---|
| `Disconnect_on_a_fresh_service_does_not_throw` | |
| `IsConnected_is_false_before_connecting` | |
| `ConnectedDeviceId_is_null_before_connecting` | |
| `ConnectAsync_returns_false_unpackaged_without_reaching_TryCreateFromId` | the test host is unpackaged; assert `false` **and** that the process survived, and that a warning naming `AudioSinkPolicy` was logged via `RecordingLog` |
| `Dispose_is_idempotent` | two `Dispose()` calls, no throw |
| `AudioSinkConnectionState_maps_both_WinRT_values` | `Closed` and `Opened` exist and are distinct |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~AudioSinkServiceContractTests"`
Expected: FAIL.

- [ ] **Step 3: Implement the interface and update `AudioSinkService`**

- [ ] **Step 4: Update `TrayContext` to the instance `FindDevicesAsync`**

The phone menu builds from `PhoneDevice` records; `DeviceInformation` must not appear in
`TrayContext` after this task.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Klangbruecke/Bluetooth/IAudioSinkService.cs src/Klangbruecke/Bluetooth/AudioSinkService.cs src/Klangbruecke/TrayContext.cs tests/Klangbruecke.Tests/Bluetooth/AudioSinkServiceContractTests.cs
git commit -m "Put AudioSinkService behind IAudioSinkService; scope IsConnected to the connection object"
```

---

### Task 10: `ICallTransportService`, grounded in `IsRegistered()`

**Files:**
- Create: `src/Klangbruecke/Bluetooth/ICallTransportService.cs`
- Modify: `src/Klangbruecke/Bluetooth/CallTransportService.cs`, `src/Klangbruecke/TrayContext.cs`

**Interfaces:**
- Consumes: `TransportCandidate` (exists in `TransportMatcher.cs`)
- Produces:

```csharp
namespace Klangbruecke.Bluetooth;

/// <summary>
/// Registered is the health signal. TransportConnected carries PhoneLineTransportDevice.ConnectAsync's
/// bool, which returns False on this machine even when registration succeeded and calls demonstrably
/// work (spec finding #1, FINDINGS.md §12). It is logged, never treated as failure.
/// </summary>
public readonly record struct CallTransportResult(bool Registered, bool? TransportConnected, string Reason);

public interface ICallTransportService : IDisposable
{
    /// <summary>Live PhoneLineTransportDevice.IsRegistered(). False when no transport is held.</summary>
    bool IsRegistered { get; }

    event EventHandler<StatusMessage>? Status;

    Task<IReadOnlyList<TransportCandidate>> FindTransportsAsync();
    Task<CallTransportResult> ConnectAsync(string transportDeviceId);

    /// <summary>Unregisters the hands-free role. Deliberate intent changes only.</summary>
    void Disconnect();
}
```

**This task fixes the defect that would otherwise make the state machine unusable.** Today
`ConnectAsync` returns `false` whenever `PhoneLineTransportDevice.ConnectAsync()` returns `false`,
which is *every run on this machine* — including the ones where the role was claimed and calls
worked. Replace the bool return with `CallTransportResult`:

- `Registered` = the post-`RegisterApp` `IsRegistered()` re-check, which is already there.
- `TransportConnected` = whatever `PhoneLineTransportDevice.ConnectAsync()` returned, or null if the
  call was not reached.
- `Reason` = a non-empty sentence for the log.

`Registered == true` with `TransportConnected == false` is **success**. Downgrade the current
"Call transport refused to connect. Check the pairing first..." status from a failure claim to an
informational line, because it is currently shown on every successful run and sends readers to
`FINDINGS.md` §3 for a problem they do not have.

`IsRegistered` reads the live `PhoneLineTransportDevice.IsRegistered()` when a device is held, and
`false` otherwise. Do not cache it — the reconcile loop's whole job is to catch it going false.

- [ ] **Step 1: Write the failing tests**

`tests/Klangbruecke.Tests/Bluetooth/CallTransportResultTests.cs` plus contract tests. Registration
cannot be exercised unpackaged, so cover the shape and the decision rule:

| Test | Asserts |
|---|---|
| `Registered_with_TransportConnected_false_is_success` | the record's own semantics, pinned so a later reader cannot re-invert it |
| `Reason_is_never_null_or_empty` | theory over constructed results |
| `IsRegistered_is_false_before_connecting` | |
| `Disconnect_on_a_fresh_service_does_not_throw` | |
| `Dispose_is_idempotent` | |
| `FindTransportsAsync_returns_candidates_unpackaged` | discovery works without identity (`FINDINGS.md` §2). Skip cleanly with a clear message if no phone is paired on the build machine, rather than failing |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~CallTransport"`
Expected: FAIL.

- [ ] **Step 3: Implement the interface and rework `CallTransportService`**

- [ ] **Step 4: Update `TrayContext.ConnectCallsAsync` to read `result.Registered`**

The `"Call transport connect {succeeded|failed}."` line now follows `Registered`, and a separate
line records `TransportConnected`.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Klangbruecke/Bluetooth/ICallTransportService.cs src/Klangbruecke/Bluetooth/CallTransportService.cs src/Klangbruecke/TrayContext.cs tests/Klangbruecke.Tests/Bluetooth/
git commit -m "Ground the calls half in IsRegistered(); ConnectAsync's False is a logged fact, not a failure"
```

---

### Task 11: `ILinkMonitor` and `LinkMonitor`

**Files:**
- Modify: `src/Klangbruecke/Bluetooth/ILinkMonitor.cs` (created in Task 4)
- Create: `src/Klangbruecke/Bluetooth/LinkMonitor.cs`

**Interfaces:**
- Consumes: `BluetoothDeviceId.TryExtractAddress` (exists)
- Produces:

```csharp
namespace Klangbruecke.Bluetooth;

public enum BluetoothLinkStatus { Unknown, Disconnected, Connected }

public interface ILinkMonitor : IDisposable
{
    bool DevicePresent { get; }

    event EventHandler? DeviceAppeared;
    event EventHandler? DeviceRemoved;

    void Watch(string phoneDeviceId);
    void StopWatching();
    Task<BluetoothLinkStatus> ReadLinkStatusAsync();
}
```

`LinkMonitor` runs a `DeviceWatcher` over `AudioPlaybackConnection.GetDeviceSelector()`, filtered to
the watched device id, and reads `ConnectionStatus` via
`BluetoothDevice.FromBluetoothAddressAsync(address)`.

Constraints:

- `BluetoothDeviceId.TryExtractAddress` returns a 12-character uppercase hex string; the API wants a
  `ulong`. Parse with `NumberStyles.HexNumber` and `CultureInfo.InvariantCulture`.
- Return `Unknown` — never throw — when the address cannot be extracted, `FromBluetoothAddressAsync`
  returns null, or anything throws. `LinkMachine` treats `Unknown` as `Disconnected`, which is the
  safe direction.
- **Do not request `System.Devices.Aep.IsPaired` and expect it from a bare `FindAllAsync`** — it
  reads `false` unless asked for in `extraProps` (`HANDOFF.md`). This monitor does not need pairing
  state; do not add it.
- Dispose the `BluetoothDevice` after each read. Holding one is the design this project deliberately
  did not choose.
- Events are raised on the WinRT threadpool. `LinkMonitor` does **not** marshal — `ConnectionManager`
  posts. Say so in a comment so nobody adds a second marshalling layer.

- [ ] **Step 1: Write the failing tests**

`tests/Klangbruecke.Tests/Bluetooth/LinkMonitorTests.cs` — thin, because this is a seam over WinRT:

| Test | Asserts |
|---|---|
| `DevicePresent_is_false_before_Watch` | |
| `StopWatching_before_Watch_does_not_throw` | |
| `Dispose_is_idempotent` | |
| `ReadLinkStatusAsync_returns_Unknown_when_no_device_is_watched` | never throws |
| `ReadLinkStatusAsync_returns_Unknown_for_an_id_with_no_extractable_address` | pass `"garbage"`; assert `Unknown`, not a throw |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~LinkMonitorTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `LinkMonitor`**

- [ ] **Step 4: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS.

- [ ] **Step 5: Verify `ReadLinkStatusAsync` against the real phone, unpackaged**

Risk #2 in the spec: `BluetoothDevice.FromBluetoothAddressAsync` unpackaged is unverified. A tiny
`dotnet run` harness or a temporary xunit fact that prints the result with the phone connected and
then disconnected. Record what it returned in the commit message. If it does not work unpackaged,
say so and fall back to `DeviceWatcher` presence alone for the link machine, noting that the
deliberate-vs-range-exit distinction degrades to "always treat as range exit".

- [ ] **Step 6: Commit**

```bash
git add src/Klangbruecke/Bluetooth/ILinkMonitor.cs src/Klangbruecke/Bluetooth/LinkMonitor.cs tests/Klangbruecke.Tests/Bluetooth/LinkMonitorTests.cs
git commit -m "Add LinkMonitor: DeviceWatcher presence plus ConnectionStatus reads"
```

---

### Task 12: `IPowerNotifier` and `PowerNotifier`

**Files:**
- Create: `src/Klangbruecke/Platform/IPowerNotifier.cs`, `src/Klangbruecke/Platform/PowerNotifier.cs`
- Create: `tests/Klangbruecke.Tests/Fakes/FakePowerNotifier.cs`

**Interfaces:**
- Consumes: nothing
- Produces:

```csharp
namespace Klangbruecke.Platform;

public interface IPowerNotifier : IDisposable
{
    event EventHandler? Resumed;
    void Start();
}
```

```csharp
namespace Klangbruecke.Tests.Fakes;

public sealed class FakePowerNotifier : IPowerNotifier
{
    public event EventHandler? Resumed;
    public bool Started { get; }
    public void Start();
    public void RaiseResumed();
    public void Dispose();
}
```

`PowerNotifier` wraps `SystemEvents.PowerModeChanged` and raises `Resumed` only for
`PowerModes.Resume`. `SystemEvents` raises on its own thread and **holds a static subscription** —
`Dispose` must unsubscribe or the object leaks for the process lifetime.

- [ ] **Step 1: Write the failing tests**

`tests/Klangbruecke.Tests/Platform/PowerNotifierTests.cs`:

| Test | Asserts |
|---|---|
| `Dispose_without_Start_does_not_throw` | |
| `Dispose_is_idempotent` | |
| `Dispose_unsubscribes_from_SystemEvents` | construct, `Start()`, `Dispose()`, then `Start()`/`Dispose()` again on a second instance without a leak warning; assert no exception and that the first instance's handler is gone |
| `FakePowerNotifier_raises_Resumed_on_demand` | the double works |
| `FakePowerNotifier_records_Start` | |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~PowerNotifierTests"`
Expected: FAIL.

- [ ] **Step 3: Implement both**

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~PowerNotifierTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Platform/IPowerNotifier.cs src/Klangbruecke/Platform/PowerNotifier.cs tests/Klangbruecke.Tests/Fakes/FakePowerNotifier.cs tests/Klangbruecke.Tests/Platform/PowerNotifierTests.cs
git commit -m "Add the IPowerNotifier seam over SystemEvents.PowerModeChanged"
```

---

### Task 13: `IAudioEndpointMonitor` and `EndpointMonitor`

**Files:**
- Create: `src/Klangbruecke/Audio/IAudioEndpointMonitor.cs`, `src/Klangbruecke/Audio/EndpointMonitor.cs`
- Create: `tests/Klangbruecke.Tests/Fakes/FakeEndpointMonitor.cs`

**Interfaces:**
- Consumes: Task 1's findings note
- Produces:

```csharp
namespace Klangbruecke.Audio;

public interface IAudioEndpointMonitor : IDisposable
{
    /// <summary>The A2DP SNK capture endpoint exists and is active.</summary>
    bool SinkCaptureEndpointPresent { get; }

    /// <summary>Raised on whatever thread the OS uses. ConnectionManager marshals; this does not.</summary>
    event EventHandler? EndpointsChanged;

    void Start();
}
```

```csharp
namespace Klangbruecke.Tests.Fakes;

public sealed class FakeEndpointMonitor : IAudioEndpointMonitor
{
    public bool SinkCaptureEndpointPresent { get; set; }
    public event EventHandler? EndpointsChanged;
    public bool Started { get; }
    public void Start();

    /// <summary>Sets presence and raises the event in one call.</summary>
    public void SetPresent(bool present);

    public void Dispose();
}
```

Implement `EndpointMonitor` exactly as Task 1's note concluded — `IMMNotificationClient` if the probe
succeeded, the 2-second poll fallback if it did not. Either way the interface is unchanged, which is
why this task does not block on the answer.

This is the fix for spec finding #2 (the endpoint is absent right after connect in 5 of 8 launches)
and finding #3 (a call invalidates the endpoint without closing the connection).

- [ ] **Step 1: Write the failing tests**

`tests/Klangbruecke.Tests/Audio/EndpointMonitorTests.cs`:

| Test | Asserts |
|---|---|
| `Dispose_without_Start_does_not_throw` | |
| `Dispose_is_idempotent` | |
| `SinkCaptureEndpointPresent_does_not_throw_before_Start` | returns a value rather than faulting |
| `Start_is_idempotent` | two `Start()` calls do not double-register |
| `FakeEndpointMonitor_raises_on_SetPresent` | the double works |
| `FakeEndpointMonitor_reports_the_value_set` | |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~EndpointMonitorTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `EndpointMonitor` and the fake**

- [ ] **Step 4: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Audio/IAudioEndpointMonitor.cs src/Klangbruecke/Audio/EndpointMonitor.cs tests/Klangbruecke.Tests/Fakes/FakeEndpointMonitor.cs tests/Klangbruecke.Tests/Audio/EndpointMonitorTests.cs
git commit -m "Add the endpoint monitor: the signal the route has never had"
```

---

### Task 14: `MusicHalf`

**Files:**
- Create: `src/Klangbruecke/Connection/MusicHalf.cs`
- Create: `tests/Klangbruecke.Tests/Fakes/FakeAudioSinkService.cs`, `tests/Klangbruecke.Tests/Fakes/FakeAudioRouter.cs`
- Create: `tests/Klangbruecke.Tests/Connection/MusicHalfTests.cs`

**Interfaces:**
- Consumes: `IAudioSinkService` (9), `IAudioRouter` (7), `IAudioEndpointMonitor` (13), `IScheduler` (2), `BackoffSchedule` (3), `MusicState` (6)
- Produces:

```csharp
namespace Klangbruecke.Connection;

public sealed class MusicHalf
{
    public MusicHalf(
        IAudioSinkService sink,
        IAudioRouter router,
        IAudioEndpointMonitor endpoints,
        IScheduler scheduler);

    public MusicState State { get; }
    public bool Enabled { get; }
    public TimeSpan? NextRetryIn { get; }

    /// <summary>Raised after any state change, on the calling thread.</summary>
    public event EventHandler? Changed;

    public void Configure(bool enabled, string? phoneDeviceId, string? outputDeviceId);

    public Task OnLinkPresentAsync(bool connectPermitted);
    public void OnLinkAbsent();
    public void OnSuppressed();
    public void OnConnectionClosed();
    public void OnEndpointsChanged();
    public void OnRouteStopped();
    public Task ReconcileAsync(bool connectPermitted);
}
```

Behaviour, from the spec's music table:

- `Off` + enabled + link present + not suppressed + connect permitted → `Connecting`, calls `sink.ConnectAsync`.
- `Connecting` + true → `Linked`. `Connecting` + false/throw → `Backoff`, schedules the connect retry.
- `Linked` + endpoint present + route backoff elapsed → `router.Start(outputDeviceId)`; `Up` if it returned true, otherwise stay `Linked` and advance the **route** backoff.
- `Up` + `OnRouteStopped` → `Linked`, advance the route backoff. **Never calls `sink.ConnectAsync`.**
- `Up` + reconcile finds the endpoint absent → `router.Stop()`, `Linked`.
- `Up` + reconcile finds `router.IsRunning` false → `Linked`, advance the route backoff.
- `Up`/`Linked` + `OnConnectionClosed` → `router.Stop()`; the manager owns the grace window and then calls `OnLinkAbsent` or the backoff path.
- link absent / suppressed / phone deselected → `Off`, `router.Stop()`, `sink.Disconnect()`.
- `Linked` **never times out.**
- Two independent backoffs: the connect backoff and the route backoff, both 2/4/8/16/30/60 s. The route backoff resets once `router.IsRunning` has been continuously true for 10 s — measured with `IScheduler.Now`, because `AudioRouter.Start()` returns `true` for a source that cannot capture.

- [ ] **Step 1: Write the failing tests**

| Test | Asserts |
|---|---|
| `Disabled_stays_Off_when_the_link_arrives` | no `ConnectAsync` call |
| `Link_present_starts_a_connect` | `Off` → `Connecting`, `sink.ConnectAsync` called once with the configured id |
| `Successful_connect_moves_to_Linked_not_Up` | **finding #2** — the endpoint has not been seen yet |
| `Failed_connect_moves_to_Backoff_and_schedules_a_retry` | |
| `Backoff_retries_on_the_2_4_8_sequence` | `FakeScheduler.Advance`; three retries at the right moments |
| `Backoff_resets_after_a_successful_connect` | next failure waits 2 s again |
| `Endpoint_appearing_while_Linked_starts_the_route` | **finding #2 fix** — `router.Start` called with the configured output id |
| `Endpoint_appearing_while_Linked_moves_to_Up` | |
| `Route_start_failure_keeps_Linked_and_advances_the_route_backoff` | does not spin |
| `Route_stopped_while_Up_returns_to_Linked` | **finding #3** |
| `Route_stopped_while_Up_does_not_reconnect_bluetooth` | assert `sink.ConnectAsync` was **not** called again — the single most important test in this task |
| `Route_restarts_when_the_endpoint_returns_after_a_call` | stop the route, drop the endpoint, restore it; `router.Start` called again, `Up` |
| `Repeated_immediate_route_stops_advance_the_route_backoff` | five stop/start cycles do not produce five immediate restarts |
| `Route_backoff_resets_after_ten_seconds_of_running` | advance 10 s with `IsRunning` true; the next stop retries at 2 s |
| `Linked_does_not_time_out` | advance one hour; still `Linked`, no state change, no reconnect |
| `Link_absent_stops_the_router_and_disconnects_the_sink` | both called, state `Off` |
| `Suppressed_stops_the_router_and_disconnects_the_sink` | |
| `Reconcile_moves_Up_to_Linked_when_the_endpoint_vanished` | drift correction |
| `Reconcile_moves_Up_to_Linked_when_the_router_stopped_silently` | `IsRunning` false without an event |
| `Reconcile_reconnects_from_Backoff_when_permitted` | |
| `Reconcile_does_not_connect_when_connect_is_not_permitted` | the `AutoReconnect`-off guard |
| `Connect_not_permitted_leaves_Off_on_link_present` | |
| `Connect_not_permitted_still_starts_the_route_from_Linked` | the carve-out: finishing what the user started |
| `Changed_fires_once_per_state_change` | not per event |
| `Changed_does_not_fire_when_nothing_changed` | |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~MusicHalfTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `MusicHalf` and the two fakes**

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~MusicHalfTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Connection/MusicHalf.cs tests/Klangbruecke.Tests/Fakes/ tests/Klangbruecke.Tests/Connection/MusicHalfTests.cs
git commit -m "Add MusicHalf: a call ends with the route restarted, not the phone reconnected"
```

---

### Task 15: `CallsHalf`

**Files:**
- Create: `src/Klangbruecke/Connection/CallsHalf.cs`
- Create: `tests/Klangbruecke.Tests/Fakes/FakeCallTransportService.cs`
- Create: `tests/Klangbruecke.Tests/Connection/CallsHalfTests.cs`

**Interfaces:**
- Consumes: `ICallTransportService` (10), `IScheduler` (2), `BackoffSchedule` (3), `CallsState` (6), `TransportMatcher` (exists)
- Produces:

```csharp
namespace Klangbruecke.Connection;

public sealed class CallsHalf
{
    public CallsHalf(ICallTransportService calls, IScheduler scheduler);

    public CallsState State { get; }
    public bool Enabled { get; }
    public TimeSpan? NextRetryIn { get; }

    public event EventHandler? Changed;

    public void Configure(bool enabled, string? phoneDeviceId);

    public Task OnLinkPresentAsync(bool connectPermitted);

    /// <summary>Unregisters. Deliberate intent only.</summary>
    public void OnPhoneDeselected();

    /// <summary>Unregisters. Deliberate intent only.</summary>
    public void OnDisabled();

    public Task ReconcileAsync(bool connectPermitted);
}
```

**There is deliberately no `OnLinkAbsent`.** Registration is not link-scoped — it is what makes the
phone offer the PC when it returns, and unregistering on every range exit would flap the phone's
call-audio-device option. The absence of the method is what makes that rule structural rather than a
rule someone has to remember. Do not add it.

`OnLinkPresentAsync` enumerates via `FindTransportsAsync`, correlates with `TransportMatcher.Match`
against the configured phone id, and calls `ConnectAsync` on the match. `Ambiguous` and
`NoCandidates` both go to `Backoff` — `TransportMatcher` deliberately connects nothing when it cannot
tell two phones apart.

- [ ] **Step 1: Write the failing tests**

| Test | Asserts |
|---|---|
| `Disabled_stays_Off_when_the_link_arrives` | no enumeration, no `ConnectAsync` |
| `Link_present_enumerates_and_registers` | `Off` → `Registering` → `Up` |
| `Registered_true_with_TransportConnected_false_reaches_Up` | **spec finding #1** — the single most important test in this task |
| `Registered_false_moves_to_Backoff` | |
| `A_throw_from_ConnectAsync_moves_to_Backoff` | not an escape |
| `No_matching_transport_moves_to_Backoff_without_connecting` | `NoCandidates` |
| `Ambiguous_match_moves_to_Backoff_without_connecting` | assert `ConnectAsync` was never called |
| `Backoff_retries_on_the_2_4_8_sequence` | |
| `Backoff_resets_after_reaching_Up` | |
| `Link_absent_is_not_an_input` | there is no method to call — assert by reflection that `CallsHalf` exposes no `OnLinkAbsent`, so a later edit adding one fails this test |
| `Phone_deselected_unregisters` | `calls.Disconnect()` called once |
| `Disabling_calls_unregisters` | `calls.Disconnect()` called once — closes the `HANDOFF.md` carry-forward where toggling `EnableCalls` off left the registration live |
| `Reconcile_drops_Up_to_Backoff_when_IsRegistered_goes_false` | drift correction |
| `Reconcile_does_nothing_while_Up_and_still_registered` | no re-registration, no log |
| `Reconcile_does_not_register_when_connect_is_not_permitted` | the `AutoReconnect`-off guard |
| `Reregistering_after_backoff_does_not_call_Disconnect_first` | unregistering would flap the phone's option |
| `Changed_fires_once_per_state_change` | |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~CallsHalfTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `CallsHalf` and the fake**

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~CallsHalfTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Connection/CallsHalf.cs tests/Klangbruecke.Tests/Fakes/FakeCallTransportService.cs tests/Klangbruecke.Tests/Connection/CallsHalfTests.cs
git commit -m "Add CallsHalf: registration is not link-scoped and is never unregistered by the music half"
```

---

### Task 16: `ConnectionManager`

**Files:**
- Create: `src/Klangbruecke/Connection/ConnectionManager.cs`
- Create: `tests/Klangbruecke.Tests/Fakes/FakeLinkMonitor.cs`
- Create: `tests/Klangbruecke.Tests/Connection/ConnectionManagerTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–15
- Produces:

```csharp
namespace Klangbruecke.Connection;

public sealed class ConnectionManager : IDisposable
{
    public ConnectionManager(
        Settings settings,
        IAudioSinkService sink,
        ICallTransportService calls,
        IAudioRouter router,
        IAudioEndpointMonitor endpoints,
        ILinkMonitor link,
        IScheduler scheduler,
        IPowerNotifier power,
        IUiDispatcher ui);

    public ConnectionState State { get; }
    public string Detail { get; }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<StatusMessage>? Status;

    /// <summary>Subscribes to every source and begins watching. Call once, from the UI thread.</summary>
    public void Start();

    public Task<IReadOnlyList<PhoneDevice>> FindPhonesAsync();
    public IReadOnlyList<AudioOutputDevice> ListOutputDevices();

    public void SelectPhone(string deviceId);
    public void DeselectPhone();
    public void SelectOutput(string? deviceId);
    public void SetCallsEnabled(bool enabled);
    public void SetAutoReconnect(bool enabled);
    public void RequestDisconnect();

    public void Dispose();
}
```

Constraints:

- **Single-threaded by contract.** Every inbound event from `ILinkMonitor`, `IAudioSinkService.StateChanged`, `IAudioEndpointMonitor`, `IAudioRouter.Stopped` and `IPowerNotifier` is posted through `IUiDispatcher` before any state is touched. No locks anywhere in this file. This is what makes the `WasapiOut` play-thread deadlock structurally impossible.
- **Grace window:** on the sink reporting `Closed`, schedule 3 s, then `ReadLinkStatusAsync()`. `Connected` → `SuppressDeliberate()` and both halves `Off`. `Disconnected` or `Unknown` → link `Absent`, music to backoff.
- **Reconcile:** `SchedulePeriodic(30 s)` running the five checks in the spec's order. **Writes no log line when nothing changed.**
- **Resume:** `Resumed` → `Schedule(5 s)` → forced reconcile. Never reconcile immediately; the Bluetooth stack is not back and an instant attempt only burns the first backoff step.
- **Connect permitted** = `settings.AutoReconnect` is on, **or** the attempt descends from a `SelectPhone` call whose halves have not yet reached `Up`.
- Persist `PhoneDeviceId`, `OutputDeviceId`, `EnableCalls` and `AutoReconnect` through `Settings.Save()` on the corresponding setter. Saving `PhoneDeviceId` before connecting is deliberate and safe (`FINDINGS.md` §8) — it records the user's answer to "which phone".
- `ListOutputDevices` delegates to the router's factory so `TrayContext` never sees `MMDevice`.

- [ ] **Step 1: Write the failing tests**

All with `ImmediateUiDispatcher`, `FakeScheduler` and the fakes from Tasks 8, 12–15.

| Test | Asserts |
|---|---|
| `Start_with_no_saved_phone_is_Idle` | |
| `Start_with_a_saved_phone_begins_watching` | `link.Watch` called with the saved id, state `Discovering` |
| `Device_appearing_connects_both_halves` | ends `Connected` |
| `Selecting_a_phone_saves_it_before_connecting` | `Settings.PhoneDeviceId` written first |
| `Connection_closed_with_the_link_still_up_suppresses_after_the_grace_window` | advance 3 s; state `Suppressed`, reason `Deliberate` |
| `Connection_closed_with_the_link_gone_goes_to_Discovering` | advance 3 s with `Disconnected` |
| `Connection_closed_with_an_unknown_link_goes_to_Discovering` | `Unknown` takes the out-of-range branch |
| `No_action_is_taken_before_the_grace_window_elapses` | advance 2.9 s; nothing has changed yet |
| `Suppressed_re_arms_after_the_link_drops_and_returns` | |
| `Suppressed_does_not_re_arm_while_the_phone_stays_in_range` | |
| `Tray_disconnect_suppresses` | `RequestDisconnect()` → `Suppressed` |
| `Reconcile_runs_every_thirty_seconds` | advance 95 s; three reconciles |
| `Reconcile_writes_no_log_line_when_nothing_changed` | `RecordingLog` has no new entries after three quiet ticks |
| `Reconcile_logs_when_something_changed` | one entry, not three |
| `Resume_does_not_reconcile_immediately` | raise `Resumed`, advance 4 s; no reconcile |
| `Resume_reconciles_after_five_seconds` | advance 5 s; one reconcile |
| `Auto_reconnect_off_does_not_connect_when_the_device_appears` | |
| `Auto_reconnect_off_does_not_retry_from_backoff` | advance 120 s; no further `ConnectAsync` |
| `Auto_reconnect_off_still_completes_a_click_initiated_connect` | `SelectPhone` then the endpoint arrives → `Up` |
| `Auto_reconnect_off_after_a_drop_reports_Suppressed_with_its_own_detail` | distinct from the deliberate detail |
| `Turning_auto_reconnect_back_on_clears_the_latch` | |
| `Deselecting_the_phone_unregisters_calls_and_stops_the_router` | |
| `Disabling_calls_unregisters` | |
| `Disabling_calls_leaves_music_running` | the halves are independent |
| `Selecting_an_output_restarts_the_route_without_touching_bluetooth` | `router.Start` called again, `sink.ConnectAsync` not called |
| `StateChanged_fires_once_per_reported_state_change` | not per internal transition |
| `Every_inbound_event_is_posted_through_the_dispatcher` | a counting `IUiDispatcher`; raise each of the five event sources; assert one post each |
| `Dispose_stops_watching_and_disposes_every_owned_seam` | |
| `Dispose_is_idempotent` | |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~ConnectionManagerTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `ConnectionManager`**

- [ ] **Step 4: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Connection/ConnectionManager.cs tests/Klangbruecke.Tests/Fakes/FakeLinkMonitor.cs tests/Klangbruecke.Tests/Connection/ConnectionManagerTests.cs
git commit -m "Add ConnectionManager: grace window, per-half backoff, reconcile, resume"
```

---

### Task 17: `TrayContext` becomes a view

**Files:**
- Modify: `src/Klangbruecke/TrayContext.cs`, `src/Klangbruecke/StatusPresenter.cs`, `src/Klangbruecke/Program.cs`
- Modify: `tests/Klangbruecke.Tests/StatusPresenterTests.cs`

**Interfaces:**
- Consumes: `ConnectionManager` (16)
- Produces: nothing consumed downstream

`TrayContext`'s only dependencies become `ConnectionManager`, `StatusPresenter` and `NotifyIcon`. All
of `ConnectAsync`, `ConnectMusicAsync`, `ConnectCallsAsync`, `StartRouting` and `Disconnect` move out;
each menu handler calls exactly one `ConnectionManager` method.

`StatusPresenter` gains a state-first composition: `Klangbruecke: <State> — <Detail>`, with its
existing 96-character cap applied to the **composed** string. That cap and the "log the full message
before posting" behaviour are pinned by 212 lines of existing tests — extend them, do not replace
them.

Two smaller carry-forwards close here:

- The menu's `"Route calls to PC"` item is **disabled with `(needs MSIX)` appended** when
  `PackageIdentity.IsPackaged` is false, instead of showing checked while doing nothing.
- The three `Status` handlers are subscribed after `_status` is assigned, not before. Today that is
  safe only by call ordering.

- [ ] **Step 1: Write the failing tests**

Extend `StatusPresenterTests.cs`:

| Test | Asserts |
|---|---|
| `Composes_state_then_detail` | `"Klangbruecke: Connected — waiting for phone audio"` |
| `Caps_the_composed_state_and_detail_at_96_characters` | a long detail truncates to exactly 96 including the prefix |
| `Logs_the_full_untruncated_state_and_detail` | the log keeps what the tooltip drops |
| `Existing_message_only_behaviour_still_works` | the old `Show(string, LogLevel)` path is unchanged |

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~StatusPresenterTests"`
Expected: FAIL on the new tests, PASS on the existing ones.

- [ ] **Step 3: Rewrite `TrayContext` as a view and wire `Program.Main`**

`Program.Main` constructs the concrete seams — `WasapiDeviceFactory`, `AudioRouter`, `AudioSinkService`,
`CallTransportService`, `EndpointMonitor`, `LinkMonitor`, `UiScheduler`, `PowerNotifier`,
`ControlUiDispatcher` — and hands them to `ConnectionManager`, then to `TrayContext`. Order matters:
the dispatcher must be constructed on the UI thread before anything that posts through it.

Do not add a second `Application.ThreadException` subscriber; extend the existing lambda if needed.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS.

- [ ] **Step 5: Build the app**

Run: `dotnet build Klangbruecke.sln -c Release`
Expected: no warnings introduced.

- [ ] **Step 6: Commit**

```bash
git add src/Klangbruecke/TrayContext.cs src/Klangbruecke/StatusPresenter.cs src/Klangbruecke/Program.cs tests/Klangbruecke.Tests/StatusPresenterTests.cs
git commit -m "Reduce TrayContext to a view; the tooltip now leads with the connection state"
```

---

### Task 18: Package, install, smoke test

**Files:**
- Modify: `src/Klangbruecke/Klangbruecke.csproj`, `packaging/AppxManifest.xml`, `docs/FINDINGS.md`

**Interfaces:**
- Consumes: everything
- Produces: the evidence Stage 2 starts from

- [ ] **Step 1: Bump the version in both files**

`0.1.0.2` → `0.2.0.0` in **both** `Klangbruecke.csproj` (`<Version>`) and
`packaging/AppxManifest.xml` (`Identity Version`). Two files, no enforcement — miss one and the
install will not upgrade, and the startup banner will report a version the package never carried.

- [ ] **Step 2: Build and install**

```powershell
./packaging/Build-Msix.ps1
```

Then, from **Windows PowerShell** (not pwsh — the `Appx` module does not load in PowerShell 7):

```powershell
Add-AppxPackage -Path <the built .msix>
```

- [ ] **Step 3: Verify which build is running**

Read the last `Base directory:` line in `%LOCALAPPDATA%\Klangbruecke\logs\` **before** believing
anything else in the file. Packaged and unpackaged runs share one log (`FINDINGS.md` §9), and the
banner is the only in-process value that separates them. Confirm it names
`C:\Program Files\WindowsApps\Klangbruecke_0.2.0.0_...`.

- [ ] **Step 4: Smoke test — the endpoint race**

Launch with the phone in range. Confirm the route starts **even when the endpoint lags**, i.e. the
log shows the connection opening, then the endpoint arriving, then `Routing ... -> ...`. This is the
finding #2 case, which failed silently in five of eight recorded launches before this work.

Verify against the OS, never the tray:

```powershell
Get-PnpDevice -Class AudioEndpoint | Where-Object { $_.FriendlyName -match 'SNK|A2DP' }
```

- [ ] **Step 5: Smoke test — the reconnect gap**

Place a real call. Confirm audio both directions (the mic half fails silently otherwise). End the
call. **Confirm music returns without touching the tray.** That is `FINDINGS.md` §13 closed, and it
is the single most valuable thing Stage 1 adds.

Record what happened at call end — specifically whether the A2DP connection closed or stayed open.
Spec risk #4: this has never been observed, and it is the one assumption the design rests on that no
measurement covers.

- [ ] **Step 6: Update `FINDINGS.md`**

Two edits, both already argued in the spec:

1. **Correct §4.** "If that endpoint is absent, nothing is holding an `AudioPlaybackConnection` open"
   is too strong — five recorded runs had the connection reporting `Opened` with no endpoint. Endpoint
   presence is *sufficient* proof of a live connection, not *necessary*. The operational advice —
   verify against `Get-PnpDevice` rather than trusting a UI indicator — is unaffected.
2. **Add a new section** recording what Step 5 observed at call end, and resolving §13 if music
   returned unattended.

If a check fails, **stop and report** rather than patching around it. Every real defect in this
project has been in code that compiled and had never been executed; a failed smoke test is that
class of finding, not a nuisance.

- [ ] **Step 7: Commit**

```bash
git add src/Klangbruecke/Klangbruecke.csproj packaging/AppxManifest.xml docs/FINDINGS.md
git commit -m "Stage 1 validated on hardware: version 0.2.0.0, FINDINGS updated"
```

---

### Task 19: Debounce the link poll

**Runs after Task 11, and must land before Task 16.** Numbered last only to keep the earlier
numbering stable.

**Files:**
- Modify: `src/Klangbruecke/Connection/LinkMachine.cs`
- Modify: `tests/Klangbruecke.Tests/Connection/LinkMachineTests.cs`

**Interfaces:**
- Consumes: `LinkState`, `BluetoothLinkStatus` (Task 4)
- Produces: no signature change — `OnLinkStatusRead(BluetoothLinkStatus)` keeps its shape

**Why.** `ILinkMonitor.ReadLinkStatusAsync` returns `Unknown` whenever a read fails — an
unparseable address, a null WinRT result, a throw — and `LinkMachine` correctly treats `Unknown` as
`Disconnected`, because guessing the other way produces silent permanent dormancy. But a single
transient failure then looks exactly like the phone leaving the room, and two reviews found real
consequences:

1. The music half's `Absent` row calls `router.Stop()` and `sink.Disconnect()`, so one WinRT hiccup
   tears down a working A2DP route mid-song.
2. `SuppressionLatch` re-arms on `Present → Absent → Present`, so a failed poll followed by a good
   one silently undoes a deliberate tray Disconnect roughly 60 seconds after the user asked for it.

**The fix.** A **poll** must report non-`Connected` **twice consecutively** before `Present` becomes
`Absent`. Watcher edges are unaffected — `OnDeviceRemoved` is a definite signal and still transitions
immediately. Nothing else changes.

This costs nothing on a real range exit: the music half tears down on its own evidence — the
connection closes and the endpoint vanishes — so the debounce delays only the state *label*, never
the teardown.

- [ ] **Step 1: Write the failing tests**

| Test | Asserts |
|---|---|
| `A_single_non_connected_poll_does_not_leave_Present` | one `Disconnected` read; still `Present`, returns false |
| `Two_consecutive_non_connected_polls_move_Present_to_Absent` | second read transitions and returns true |
| `A_Connected_poll_resets_the_debounce` | `Disconnected`, `Connected`, `Disconnected` leaves `Present` |
| `Unknown_and_Disconnected_both_count_toward_the_debounce` | theory over the two, and mixed pairs, since both mean "not connected" |
| `Device_removed_still_moves_Present_to_Absent_immediately` | a watcher edge is definite and is not debounced |
| `The_debounce_is_reset_on_entering_Present` | `Disconnected`, `DeviceRemoved`, `DeviceAppeared`, `Disconnected` leaves `Present` |
| `Polls_while_Absent_do_not_accumulate_toward_anything` | repeated `Disconnected` while `Absent` stay `Absent`, all returning false |

`Link_status_Disconnected_moves_Present_to_Absent` and `Link_status_Unknown_is_treated_as_Disconnected`
from Task 4 must be updated to perform two reads. That is the intended behaviour change, not
breakage — but update them deliberately and say so in the commit message.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Klangbruecke.sln --filter "FullyQualifiedName~LinkMachineTests"`
Expected: the new tests FAIL; the two updated ones FAIL until the debounce exists.

- [ ] **Step 3: Implement the debounce**

- [ ] **Step 4: Re-run the mutation checks from Task 4**

Task 4 ran six mutations and killed all six. Re-run them against the amended machine and confirm
they still fail a named test — a debounce counter is exactly the kind of addition that can make a
previously-sharp test pass for a new reason. Then mutate the debounce itself: a threshold of 1 must
redden `A_single_non_connected_poll_does_not_leave_Present`, and a counter that never resets must
redden `A_Connected_poll_resets_the_debounce`.

- [ ] **Step 5: Run the full suite and commit**

```bash
git add src/Klangbruecke/Connection/LinkMachine.cs tests/Klangbruecke.Tests/Connection/LinkMachineTests.cs
git commit -m "Debounce the link poll: one failed read must not look like a range exit"
```

---

## What Stage 1 deliberately does not do

Carried into Stage 2 or left as the user's call, all argued in the spec:

- The full Stage 2 reconnect matrix (ten scenarios, by hand, log as evidence).
- Tray selection of the call audio device — it means writing the system-wide default communications
  device via `IPolicyConfig`, an undocumented COM interface. Ask before building.
- LAF token generation. Standing instruction.
- The outgoing-call bandwidth investigation (`FINDINGS.md` §11). The trap there is that a cellular
  call reads ~4 kHz whatever Bluetooth negotiated — only a **wideband** call from a second phone is
  conclusive.
- Explaining why `PhoneLineTransportDevice.ConnectAsync` returns `False`. Task 10 makes the app stop
  depending on it.
