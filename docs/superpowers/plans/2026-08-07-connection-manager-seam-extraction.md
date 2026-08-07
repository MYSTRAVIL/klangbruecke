# ConnectionManager Seam Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the grace window and the reconcile out of `ConnectionManager` into two `internal` peer seams, `GraceWindow` and `Reconciler`, without changing any behaviour.

**Architecture:** `ConnectionManager` stays the hub owning the four state machines, the endpoint probe, the click-grant/permission logic, and the projection. The two extracted seams own only their timer + supersession token + orchestration, hold the shared machine instances directly, and reach the hub's private operations through a narrow `internal IConnectionCoordinator` that the manager implements with explicit interface implementations.

**Tech Stack:** C# / .NET 8 (`net8.0-windows10.0.19041.0`), xUnit tests (`dotnet test`), no new dependencies.

**Spec:** `docs/superpowers/specs/2026-08-07-connection-manager-seam-extraction-design.md`

## Global Constraints

Every task's requirements implicitly include these:

- **Strictly behaviour-preserving.** No behavioural test changes. The full suite (4258 tests) stays green after every task. Move method bodies **verbatim including their doc-comments**, applying only the mechanical substitutions each task specifies.
- **Zero warnings, always.** `dotnet build Klangbruecke.sln` must produce zero warnings after every task — this includes XML-doc `CS1574` (broken `<see cref>`) warnings introduced by moving members. Re-point or reword dangling crefs in the same task that moves their target.
- **Never add `ConfigureAwait(false)`** to anything in the connection subsystem. Every `await` must resume on the thread the turn started on. This is load-bearing — the "captured context" tests exist to catch it.
- **Single-threaded by contract, no locks.** Every input is already marshalled onto the UI thread by the manager. The seams subscribe to nothing.
- Target `net8.0-windows10.0.19041.0`. Do **not** raise the floor.
- ASCII `Klangbruecke` everywhere. No umlaut anywhere.
- The seams and the interface are `internal` (no `InternalsVisibleTo` exists; no test constructs them). If isolated seam tests are ever wanted, add `[assembly: InternalsVisibleTo("Klangbruecke.Tests")]` then — out of scope here.
- Build: `dotnet build Klangbruecke.sln`. Test: `dotnet test Klangbruecke.sln` (run **unfiltered** — STATUS.md's caution: an unfiltered run lets a rare flake name itself).
- **Line numbers in this plan are indicative** (captured before Task 1) and drift as tasks edit the file. Always locate a member by its **name**, not by the cited line.

---

### Task 1: Introduce `IConnectionCoordinator` and implement it on the manager

Pure addition — no extraction yet. Confirms the interface surface exactly covers what the two seams will reach back for, and that the explicit forwarders compile against the existing private members, before any code moves.

**Files:**
- Create: `src/Klangbruecke/Connection/IConnectionCoordinator.cs`
- Modify: `src/Klangbruecke/Connection/ConnectionManager.cs` (class declaration + add explicit interface implementations)

**Interfaces:**
- Produces: `internal interface IConnectionCoordinator` with members `bool IsDisposed { get; }`, `bool ConnectPermitted { get; }`, `void RefreshEndpointLevel()`, `void EnforceConnectPermission()`, `void Publish()`, `void SuppressDeliberately(string status)`, `void Report(string message)`. Tasks 2 and 3 consume this.

- [ ] **Step 1: Create the interface file**

`src/Klangbruecke/Connection/IConnectionCoordinator.cs`:

```csharp
namespace Klangbruecke.Connection;

/// <summary>
/// The manager-private operations the two timing seams - <see cref="GraceWindow"/> and
/// <see cref="Reconciler"/> - reach back for. Everything else they need is a shared machine
/// (<see cref="LinkMachine"/>, <see cref="SuppressionLatch"/>, <see cref="MusicHalf"/>,
/// <see cref="CallsHalf"/>) or a shared service, held directly.
///
/// Narrow on purpose: it is the whole answer to "what is a seam allowed to ask the hub for", and a
/// member added here is a coupling added on purpose rather than by reaching through a concrete
/// manager. <see cref="ConnectionManager"/> is the only implementor.
/// </summary>
internal interface IConnectionCoordinator
{
    /// <summary>The hub is being torn down; a seam must stop acting the moment it sees this.</summary>
    bool IsDisposed { get; }

    /// <summary>May a connect be initiated right now? Reads the latch, the setting and the click grant.</summary>
    bool ConnectPermitted { get; }

    /// <summary>Kick an off-thread refresh of the cached capture-endpoint level.</summary>
    void RefreshEndpointLevel();

    /// <summary>Stand down a half counting down to an attempt it is no longer allowed to make.</summary>
    void EnforceConnectPermission();

    /// <summary>Recompute the reported state and announce it if it moved.</summary>
    void Publish();

    /// <summary>Latch a deliberate suppression and tear both halves down. Used by the grace window's Connected branch.</summary>
    void SuppressDeliberately(string status);

    /// <summary>Raise the manager's own status announcement (always Info).</summary>
    void Report(string message);
}
```

- [ ] **Step 2: Make the manager implement it**

In `ConnectionManager.cs`, change the class declaration:

```csharp
public sealed class ConnectionManager : IDisposable, IConnectionCoordinator
```

- [ ] **Step 3: Add the explicit interface implementations**

Add a new region near the bottom of the class (e.g. after `Post`), forwarding to the existing private members. Explicit implementation keeps every private member private:

```csharp
    // --- the coordinator the two timing seams reach back through ---------------------------------

    bool IConnectionCoordinator.IsDisposed => _disposed;
    bool IConnectionCoordinator.ConnectPermitted => ConnectPermitted;
    void IConnectionCoordinator.RefreshEndpointLevel() => RefreshEndpointLevel();
    void IConnectionCoordinator.EnforceConnectPermission() => EnforceConnectPermission();
    void IConnectionCoordinator.Publish() => Publish();
    void IConnectionCoordinator.SuppressDeliberately(string status) => SuppressDeliberately(status);
    void IConnectionCoordinator.Report(string message) => Report(message);
```

- [ ] **Step 4: Build and confirm zero warnings**

Run: `dotnet build Klangbruecke.sln`
Expected: build succeeds, **zero warnings**. (No extraction yet, so nothing dangles.)

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS, all 4258 tests green.

- [ ] **Step 6: Commit**

```bash
git add src/Klangbruecke/Connection/IConnectionCoordinator.cs src/Klangbruecke/Connection/ConnectionManager.cs
git commit -m "Introduce IConnectionCoordinator, the seams' hub back-channel"
```

---

### Task 2: Extract `GraceWindow`

Move the 3 s grace window — the generation shape — into its own seam. Smaller and more self-contained than the reconcile, so it goes first.

**Files:**
- Create: `src/Klangbruecke/Connection/GraceWindow.cs`
- Modify: `src/Klangbruecke/Connection/ConnectionManager.cs` (remove the moved members; add the field + construction; rewire the three call sites; fix dangling crefs)

**Interfaces:**
- Consumes: `IConnectionCoordinator` (Task 1); the shared seams `LinkMachine`, `SuppressionLatch`, `MusicHalf`; the services `IScheduler`, `ILinkMonitor`.
- Produces: `internal sealed class GraceWindow` with constructor `GraceWindow(IScheduler scheduler, ILinkMonitor linkMonitor, LinkMachine linkMachine, SuppressionLatch latch, MusicHalf music, IConnectionCoordinator coordinator)` and public surface `void OnConnectionClosed()`, `void Cancel()`, `void Dispose()`. Task 3's `Reconciler` holds a reference to it and calls `OnConnectionClosed()`.

- [ ] **Step 1: Create `GraceWindow.cs` — fields, constructor, and the small methods**

Create `src/Klangbruecke/Connection/GraceWindow.cs` with the shell below. The class docstring carries the single-threaded / no-`ConfigureAwait` preamble the other seams carry.

```csharp
using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke.Connection;

/// <summary>
/// The 3 s window that decides, when the audio connection closes, whether the phone dropped the
/// profile deliberately (the ACL link is still up - suppress) or left the room (the link is gone -
/// range exit). Nothing is decided until it elapses, which is what keeps a one-second dropout from
/// flapping the tray.
///
/// <b>The generation shape of supersession.</b> A window is superseded by discrete events - another
/// window being armed, or the phone selection changing - so a counter is enough; it needs no time to
/// compare against, unlike the <see cref="Reconciler"/>'s stall timestamp. The generation is captured
/// when the question is asked (the window is armed) and read by the answer (the elapsed callback):
/// what crosses the await is a stale answer, not a data race.
///
/// <b>Single-threaded, subscribes to nothing, and never add <c>ConfigureAwait(false)</c>.</b> Every
/// input is a method call the manager has already marshalled onto the UI thread, and the one await -
/// the link status read in <see cref="OnElapsedAsync"/> - must resume on that thread, because its
/// answer drives the suppression latch. A <c>ConfigureAwait(false)</c> here would drive it from a
/// radio's worker thread. The manager holds no lock and this is why.
/// </summary>
internal sealed class GraceWindow
{
    /// <summary>
    /// How long to wait before believing a closed audio connection.
    ///
    /// The connection closing is the same event for two opposite causes: the phone dropped the audio
    /// profile deliberately (the ACL link stays up, and reconnecting would fight the user), or the
    /// phone left the room. Three seconds is long enough for the radio to settle and short enough
    /// that a real range exit is not left looking connected - and, more to the point, it is what
    /// keeps a one-second dropout from flapping the tray, because nothing is decided until it
    /// elapses.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly IScheduler _scheduler;
    private readonly ILinkMonitor _linkMonitor;
    private readonly LinkMachine _linkMachine;
    private readonly SuppressionLatch _latch;
    private readonly MusicHalf _music;
    private readonly IConnectionCoordinator _coordinator;

    private IDisposable? _timer;

    /// <summary>
    /// Bumped by every window that is armed, and read - never written - by the window that finally
    /// answers. What crosses the await is a stale answer, not a data race.
    /// </summary>
    private int _generation;

    public GraceWindow(
        IScheduler scheduler,
        ILinkMonitor linkMonitor,
        LinkMachine linkMachine,
        SuppressionLatch latch,
        MusicHalf music,
        IConnectionCoordinator coordinator)
    {
        _scheduler = scheduler;
        _linkMonitor = linkMonitor;
        _linkMachine = linkMachine;
        _latch = latch;
        _music = music;
        _coordinator = coordinator;
    }

    /// <summary>
    /// Voids an outstanding window, because the question it is going to answer is about a phone the
    /// user has just changed their mind about.
    ///
    /// Both matter. Bumping the generation alone would leave the armed timer standing, and
    /// <see cref="OnConnectionClosed"/> declines to arm a window while one is armed - so the next
    /// Closed would get no window at all. Disposing alone would leave a window that has already fired
    /// and is waiting on its read free to come back and decide.
    /// </summary>
    public void Cancel()
    {
        _timer?.Dispose();
        _timer = null;
        _generation++;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private bool Superseded(int generation) => _coordinator.IsDisposed || _generation != generation;
}
```

- [ ] **Step 2: Move `OnConnectionClosed` into `GraceWindow`**

Move the body of `ConnectionManager.OnConnectionClosed` (currently `ConnectionManager.cs:702-726`) into `GraceWindow`, **verbatim including its doc-comment**, applying substitution **Table G** below. Result:

```csharp
    /// <summary>
    /// The audio connection reported Closed - or the reconcile found it gone without one.
    ///
    /// Nothing is decided here. The half drops its route, because there is nothing to route, and
    /// keeps everything else: which of the two causes this was is a question only a link status read
    /// can answer, and asking it immediately gets the wrong answer for a radio that has not settled.
    /// </summary>
    public void OnConnectionClosed()
    {
        _music.OnConnectionClosed();

        if (_timer is null)
        {
            // One window at a time ... (keep the existing comment verbatim)
            int generation = ++_generation;

            _timer = _scheduler.Schedule(Window, () =>
            {
                _timer = null;
                _ = OnElapsedAsync(generation);
            });
        }

        _coordinator.Publish();
    }
```

**Table G (apply to all moved grace code):**

| In the manager | In `GraceWindow` |
|---|---|
| `_graceTimer` | `_timer` |
| `_graceGeneration` | `_generation` |
| `GraceWindow` (the const) | `Window` |
| `OnGraceWindowElapsedAsync` | `OnElapsedAsync` |
| `_disposed` | `_coordinator.IsDisposed` |
| `Publish()` | `_coordinator.Publish()` |
| `SuppressDeliberately(x)` | `_coordinator.SuppressDeliberately(x)` |
| `Report(x)` | `_coordinator.Report(x)` |
| `EnforceConnectPermission()` | `_coordinator.EnforceConnectPermission()` |
| `_music`, `_linkMachine`, `_latch`, `_linkMonitor`, `_scheduler`, `Log` | unchanged |

- [ ] **Step 3: Move `OnGraceWindowElapsedAsync` into `GraceWindow` as `OnElapsedAsync`**

Move the body of `ConnectionManager.OnGraceWindowElapsedAsync` (currently `ConnectionManager.cs:728-775`) into `GraceWindow`, **verbatim including its doc-comment**, applying **Table G**. It becomes:

```csharp
    private async Task OnElapsedAsync(int generation)
    {
        if (_coordinator.IsDisposed)
        {
            return;
        }

        BluetoothLinkStatus status = await _linkMonitor.ReadLinkStatusAsync();

        if (Superseded(generation))
        {
            // ... (keep the existing comment verbatim)
            return;
        }

        if (status == BluetoothLinkStatus.Connected)
        {
            Log.Info("The audio connection closed with the Bluetooth link still up: treating it as deliberate.");
            _coordinator.SuppressDeliberately("The phone dropped the audio connection.");
        }
        else
        {
            Log.Info("The audio connection closed and the phone is not reachable: treating it as a range exit.");

            _linkMachine.OnDeviceRemoved();
            _latch.OnLinkState(_linkMachine.State);

            _music.OnLinkAbsent();
            _coordinator.Report("The phone is out of range.");
        }

        _coordinator.EnforceConnectPermission();
        _coordinator.Publish();
    }
```

Keep every existing inline comment from the original method verbatim.

- [ ] **Step 4: Delete the moved members from the manager and wire the field**

In `ConnectionManager.cs`:
- Delete the fields `_graceTimer` (`:122`) and `_graceGeneration` + its doc-comment (`:125-130`).
- Delete the `GraceWindow` const + its doc-comment (`:55-65`).
- Delete the methods `OnConnectionClosed` (`:702-726`), `OnGraceWindowElapsedAsync` (`:728-775`), `Superseded(int)` + its doc-comment (`:984-996`), and `CancelGraceWindow` + its doc-comment (`:1107-1125`).
- Add the field: `private readonly GraceWindow _graceWindow;`
- In the constructor, after `_calls = new CallsHalf(...)` (`:238`) and before `Refresh()` (`:240`), add:

```csharp
        _graceWindow = new GraceWindow(_scheduler, _linkMonitor, _linkMachine, _latch, _music, this);
```

- [ ] **Step 5: Rewire the manager's three call sites**

- `OnSinkStateChanged` (`:639`): `OnConnectionClosed();` → `_graceWindow.OnConnectionClosed();`
- `SelectPhone` (`:374`): `CancelGraceWindow();` → `_graceWindow.Cancel();`
- `DeselectPhone` (`:429`): `CancelGraceWindow();` → `_graceWindow.Cancel();`
- `ReconcileAsync` check-2 non-userAsked branch (`:898`): `OnConnectionClosed();` → `_graceWindow.OnConnectionClosed();`
- `Dispose` (`:576-577`): replace `_graceTimer?.Dispose(); _graceTimer = null;` with `_graceWindow.Dispose();`

- [ ] **Step 6: Fix dangling `<see cref>` links in comments that stayed**

Some doc-comments that remain in the manager reference members that just moved. Find them:

Run: `grep -nE 'cref="(OnConnectionClosed|OnGraceWindowElapsedAsync|CancelGraceWindow|GraceWindow)"' src/Klangbruecke/Connection/ConnectionManager.cs`

For each hit in a comment that is **staying**, re-point it to the new location (`<see cref="GraceWindow.Cancel"/>`, `<see cref="GraceWindow.OnConnectionClosed"/>`) or reword to prose. Comments inside the *moved* methods left with the methods and need no fix. (Known example: the `Superseded(DateTimeOffset)` doc-comment at `:984-996` moves in Task 3; do not worry about it here.)

- [ ] **Step 7: Build and confirm zero warnings**

Run: `dotnet build Klangbruecke.sln`
Expected: build succeeds, **zero warnings** (no `CS1574`).

- [ ] **Step 8: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS, all 4258 tests green. In particular `A_grace_window_resumes_on_the_context_that_started_it` and every test in the `--- the grace window ---` section still pass — they drive through the manager and reach the seam unchanged.

- [ ] **Step 9: Commit**

```bash
git add src/Klangbruecke/Connection/GraceWindow.cs src/Klangbruecke/Connection/ConnectionManager.cs
git commit -m "Extract the grace window into its own seam"
```

---

### Task 3: Extract `Reconciler`

Move the 30 s reconcile — the timestamp shape, the stall bookkeeping, and the drift report — into its own seam.

**Files:**
- Create: `src/Klangbruecke/Connection/Reconciler.cs`
- Modify: `src/Klangbruecke/Connection/ConnectionManager.cs` (remove the moved members; add the field + construction; rewire the four call sites; fix dangling crefs)

**Interfaces:**
- Consumes: `IConnectionCoordinator` (Task 1); `GraceWindow` (Task 2); the shared seams `LinkMachine`, `SuppressionLatch`, `MusicHalf`, `CallsHalf`; the services `IScheduler`, `ILinkMonitor`, `IAudioSinkService`.
- Produces: `internal sealed class Reconciler` with constructor `Reconciler(IScheduler scheduler, ILinkMonitor linkMonitor, IAudioSinkService sink, LinkMachine linkMachine, SuppressionLatch latch, MusicHalf music, CallsHalf calls, GraceWindow graceWindow, IConnectionCoordinator coordinator)` and public surface `void Start()`, `Task RunAsync(string trigger, bool userAsked = false)`, `void Dispose()`.

- [ ] **Step 1: Create `Reconciler.cs` — fields, constructor, `Start`, `Dispose`, and the supersession helpers**

Create `src/Klangbruecke/Connection/Reconciler.cs` with the shell below. Move the `ReconcilePeriod` (`:67-73`) and `ReconcileStall` (`:81-93`) consts **with their doc-comments** from the manager (names unchanged, so their cross-reference stays valid). Move `Superseded(DateTimeOffset)` (`:972-982`) and `StillOurs` (`:998-1013`) **with their doc-comments**, applying **Table R**.

```csharp
using Klangbruecke.Audio;
using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke.Connection;

/// <summary>
/// The 30 s level-triggered drift correction: the spec's five checks in order, and the stall/
/// supersession bookkeeping that keeps a wedged pass from stopping the only backstop the app has.
///
/// <b>The timestamp shape of supersession.</b> Unlike the <see cref="GraceWindow"/>'s generation, a
/// pass is marked by <em>when</em> it started, because the reconcile also has to decide when a pass
/// has stalled - a bool set by a read that never returns would silently stop the backstop forever,
/// which is the predecessor app's defining bug rebuilt out of a mutex. A time can both identify the
/// current pass and say when it has been running too long to defer to.
///
/// <b>Single-threaded, subscribes to nothing, and never add <c>ConfigureAwait(false)</c>.</b> Every
/// await in a pass must resume on the thread the turn started on; that is what makes four machines
/// correct with no lock between them. See the "captured context" map in <c>ConnectionManagerTests</c>.
/// </summary>
internal sealed class Reconciler
{
    // Move ReconcilePeriod and ReconcileStall here, verbatim with their doc-comments.

    private readonly IScheduler _scheduler;
    private readonly ILinkMonitor _linkMonitor;
    private readonly IAudioSinkService _sink;
    private readonly LinkMachine _linkMachine;
    private readonly SuppressionLatch _latch;
    private readonly MusicHalf _music;
    private readonly CallsHalf _calls;
    private readonly GraceWindow _graceWindow;
    private readonly IConnectionCoordinator _coordinator;

    private IDisposable? _timer;

    // Move the _reconcilingSince field here, verbatim with its doc-comment.

    public Reconciler(
        IScheduler scheduler,
        ILinkMonitor linkMonitor,
        IAudioSinkService sink,
        LinkMachine linkMachine,
        SuppressionLatch latch,
        MusicHalf music,
        CallsHalf calls,
        GraceWindow graceWindow,
        IConnectionCoordinator coordinator)
    {
        _scheduler = scheduler;
        _linkMonitor = linkMonitor;
        _sink = sink;
        _linkMachine = linkMachine;
        _latch = latch;
        _music = music;
        _calls = calls;
        _graceWindow = graceWindow;
        _coordinator = coordinator;
    }

    /// <summary>Schedules the periodic tick. Call once, from the manager's Start.</summary>
    public void Start()
    {
        _timer = _scheduler.SchedulePeriodic(ReconcilePeriod, () => _ = RunAsync("tick"));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    // Move Superseded(DateTimeOffset) and StillOurs here, verbatim with their doc-comments (Table R).
}
```

**Table R (apply to all moved reconcile code):**

| In the manager | In `Reconciler` |
|---|---|
| `_reconcileTimer` | `_timer` |
| `_disposed` | `_coordinator.IsDisposed` |
| `ConnectPermitted` | `_coordinator.ConnectPermitted` |
| `RefreshEndpointLevel()` | `_coordinator.RefreshEndpointLevel()` |
| `EnforceConnectPermission()` | `_coordinator.EnforceConnectPermission()` |
| `Publish()` (inside `ReportDrift`) | `_coordinator.Publish()` |
| `_graceWindow.OnConnectionClosed()` (check-2, already rewired in Task 2) | unchanged |
| `_reconcilingSince`, `_scheduler`, `_linkMonitor`, `_sink`, `_linkMachine`, `_latch`, `_music`, `_calls`, `Log` | unchanged |
| `Superseded`, `StillOurs`, `TakeDrift`, `ReportDrift`, `Drift`, `ReconcilePeriod`, `ReconcileStall` | unchanged (move with the class) |

- [ ] **Step 2: Move `RunAsync` (the pass) into `Reconciler`**

Move the body of `ConnectionManager.ReconcileAsync` (currently `ConnectionManager.cs:783-970`, the whole method including the `try`/`finally`) into `Reconciler` as `RunAsync`, **verbatim including every doc- and inline comment**, applying **Table R**. The signature becomes `public async Task RunAsync(string trigger, bool userAsked = false)`. Note the check-2 non-userAsked branch already reads `_graceWindow.OnConnectionClosed()` (Task 2 rewired it while it still lived in the manager), and `_graceWindow` is now a field on this class — no further change there.

- [ ] **Step 3: Move `Drift`, `TakeDrift`, and `ReportDrift` into `Reconciler`**

Move the `Drift` record + its doc-comment (`:1015-1023`), `TakeDrift` (`:1025`), and `ReportDrift` + its doc-comment (`:1027-1047`) into `Reconciler`, **verbatim**, applying **Table R** (the `Publish()` at the end of `ReportDrift` becomes `_coordinator.Publish()`).

- [ ] **Step 4: Delete the moved members from the manager and wire the field**

In `ConnectionManager.cs`:
- Delete the fields `_reconcileTimer` (`:121`) and `_reconcilingSince` + its doc-comment (`:135-152`).
- Delete the consts `ReconcilePeriod` (`:67-73`) and `ReconcileStall` (`:81-93`).
- Delete the methods `ReconcileAsync` (`:783-970`), `Superseded(DateTimeOffset)` (`:972-982`), `StillOurs` (`:998-1013`), `TakeDrift` (`:1025`), `ReportDrift` (`:1027-1047`), and the `Drift` record (`:1015-1023`).
- Add the field: `private readonly Reconciler _reconciler;`
- In the constructor, immediately after the `_graceWindow = new GraceWindow(...)` line from Task 2, add:

```csharp
        _reconciler = new Reconciler(_scheduler, _linkMonitor, _sink, _linkMachine, _latch, _music, _calls, _graceWindow, this);
```

- [ ] **Step 5: Rewire the manager's four call sites**

- `Start` (`:329`): `_reconcileTimer = _scheduler.SchedulePeriodic(ReconcilePeriod, () => _ = ReconcileAsync("tick"));` → `_reconciler.Start();`
- `SelectPhone` (`:393`): `_ = ReconcileAsync("phone selected", userAsked: true);` → `_ = _reconciler.RunAsync("phone selected", userAsked: true);`
- `SetAutoReconnect` (`:528`): `_ = ReconcileAsync("auto-reconnect on");` → `_ = _reconciler.RunAsync("auto-reconnect on");`
- `OnResumed` (`:661`): `_ = ReconcileAsync("resume");` → `_ = _reconciler.RunAsync("resume");`
- `Dispose` (`:574-575`): replace `_reconcileTimer?.Dispose(); _reconcileTimer = null;` with `_reconciler.Dispose();`

Leave `_resumeTimer` and its disposal (`:578-579`) exactly as they are — the resume settle stays a manager concern that merely calls `_reconciler.RunAsync("resume")`.

- [ ] **Step 6: Fix dangling `<see cref>` links in comments that stayed**

Run: `grep -nE 'cref="(ReconcileAsync|ReconcileStall|ReconcilePeriod|ReportDrift|TakeDrift|Drift|_reconcilingSince|StillOurs|Superseded)"' src/Klangbruecke/Connection/ConnectionManager.cs`

For each hit in a **staying** comment, re-point it (`<see cref="Reconciler.RunAsync"/>`, `<see cref="Reconciler"/>`) or reword to prose. Known example: `DetailChanged`'s summary (`:270`) references `<see cref="ReportDrift"/>` — re-point to `<see cref="Reconciler"/>` or reword ("the same arithmetic the reconcile refuses").

- [ ] **Step 7: Build and confirm zero warnings**

Run: `dotnet build Klangbruecke.sln`
Expected: build succeeds, **zero warnings** (no `CS1574`).

- [ ] **Step 8: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS, all 4258 tests green. In particular tests 1, 6, 7, 8 in the captured-context section (`A_reconcile_resumes...`, `A_reconcile_that_connects_a_half...`, `A_reconcile_retrying_the_music_half...`, `A_reconcile_retrying_the_calls_half...`) and every test in `--- the reconcile ---` and `--- the poll as the music half's backstop ---` still pass.

- [ ] **Step 9: Commit**

```bash
git add src/Klangbruecke/Connection/Reconciler.cs src/Klangbruecke/Connection/ConnectionManager.cs
git commit -m "Extract the reconcile into its own seam"
```

---

### Task 4: Update the narrative — manager docstring and the captured-context test map

The code is done and green; this task makes the prose honest. No code changes, no behaviour change.

**Files:**
- Modify: `src/Klangbruecke/Connection/ConnectionManager.cs` (class-level docstring)
- Modify: `tests/Klangbruecke.Tests/Connection/ConnectionManagerTests.cs` (the captured-context map comment)

- [ ] **Step 1: Update the manager's class docstring**

In `ConnectionManager.cs:9-52`, the class summary still claims the manager owns "the three timings that make an unattended recovery possible - the 3 s grace window, the 30 s reconcile, and the 5 s settle after a resume." Update it to say the manager now **delegates** the grace window to `<see cref="GraceWindow"/>` and the reconcile to `<see cref="Reconciler"/>`, and owns the resume settle. Keep the rest of the docstring (the single-threaded contract, the `ConfigureAwait` prohibition, the "assembles rather than decides" paragraph) — those still hold and now also describe the two new seams. Adjust the sentence "Eleven of the fourteen awaits in these three classes..." to "...in these five classes..." if that clause remains here (it may read better moved to the test map).

- [ ] **Step 2: Rewrite the captured-context map**

In `ConnectionManagerTests.cs`, the section headed `--- the captured context: one test per await a foreign thread can complete ---` (`:1448-1510`) names await sites by their old locations. Update the map so each site names its new home:

| Old location in the comment | New location |
|---|---|
| `ConnectionManager.ReconcileAsync   _linkMonitor.ReadLinkStatusAsync()` | `Reconciler.RunAsync   _linkMonitor.ReadLinkStatusAsync()` |
| `ConnectionManager.OnGraceWindowElapsedAsync   _linkMonitor.ReadLinkStatusAsync()` | `GraceWindow.OnElapsedAsync   _linkMonitor.ReadLinkStatusAsync()` |
| `StillOurs   await step` | `Reconciler.StillOurs   await step` |
| `ReconcileAsync   await StillOurs(...)` (×4) | `Reconciler.RunAsync   await StillOurs(...)` |

Also update the count sentence "There are fourteen awaits across the three classes" → "across the five classes" (`ConnectionManager`, `Reconciler`, `GraceWindow`, `MusicHalf`, `CallsHalf`), and the "three no test can cover" list: `ReconcileAsync`'s fourth `StillOurs` → `Reconciler.RunAsync`'s fourth; `ConnectHalvesAsync`'s second await and `RegisterCallsAsync`'s await stay in `ConnectionManager`. The test **bodies and names do not change** — they drive through the manager, and the awaits are still reached the same way.

- [ ] **Step 3: Build and confirm zero warnings**

Run: `dotnet build Klangbruecke.sln`
Expected: build succeeds, zero warnings.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test Klangbruecke.sln`
Expected: PASS, all 4258 tests green.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Connection/ConnectionManager.cs tests/Klangbruecke.Tests/Connection/ConnectionManagerTests.cs
git commit -m "Point the class docstring and the captured-context map at the new seams"
```

---

## Post-plan verification

After Task 4, confirm the whole thing landed:

- [ ] `dotnet test Klangbruecke.sln` — 4258 green, unfiltered.
- [ ] `dotnet build Klangbruecke.sln` — zero warnings.
- [ ] `ConnectionManager.cs` is materially smaller (~400 lines lighter); the grace-window and reconcile mechanisms live only in `GraceWindow.cs` and `Reconciler.cs`; each supersession shape (generation / timestamp / re-read) has exactly one home.
- [ ] Grep confirms no dangling references: `grep -rnE 'ReconcileAsync|OnGraceWindowElapsedAsync|CancelGraceWindow|_graceGeneration|_reconcilingSince|_reconcileTimer|_graceTimer' src/Klangbruecke/Connection/ConnectionManager.cs` returns nothing.
