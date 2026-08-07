# Stage 2 — ConnectionManager seam extraction design

Date: 2026-08-07
Status: approved, not yet implemented

Extracts the grace window and the reconcile out of `ConnectionManager` into two peer seams. This is
the Stage-1 review's **#1 Stage-2 priority**, recorded at
`.superpowers/sdd/2026-08-05-stage-1-connection-manager/task-16-report.md` and `progress.md`. It is a
**strictly behaviour-preserving** refactor: the 4258-test suite is the net, and every existing
behavioural test stays green unchanged.

## Problem

`ConnectionManager.cs` is ~1290 lines, and four review rounds each added a supersession guard to the
same two methods — `ReconcileAsync` and `OnGraceWindowElapsedAsync`. The reviewer's words:

> four rounds have each added a guard to the same two methods, and that is the pattern that precedes
> a type wanting to exist … the strongest signal so far that the grace window and the reconcile want
> to be a type of their own.

The recurring bug class across the whole connection subsystem is **one thing**: a stale answer
crossing an `await`. Every async seam guards it with a token captured-before-await, compared-after —
`MusicHalf._generation`, `CallsHalf._generation`, and in the manager the same idea spelled three
different ways. Today that guard is a *discipline* enforced by comments and mutation tests, not by the
type system. The concentration of it in two ever-growing methods is what this refactor relieves.

### The three shapes are not duplication, and must not be unified

The reviewer's own caution, honoured here: the supersession idiom appears in **three shapes that
answer three genuinely different questions**, and "each says which — so the file is not yet
incoherent":

- a **timestamp** (`_reconcilingSince`) — "stop waiting on a wedged pass". Needs a *time* to compare
  against, because it also has to decide when a pass has stalled (`ReconcileStall`).
- a **generation** (`_graceGeneration`) — "a newer question was asked". Superseded by discrete events
  (another window armed; the phone selection changed), so a counter is enough.
- a **re-read** (`FinishTurn`, `ConnectHalvesAsync`) — "the answer can be re-derived". Needs no token
  at all: permission is read per half at the call site and the tail recomputes from scratch.

A naive "one supersession type" would collapse three questions into one and *lose* meaning. The goal
is therefore **extraction into coherent, single-responsibility types**, not unification. Each shape
ends up with exactly one home.

### What this refactor is explicitly not

- **Not** a change to make the guard a *compile-time* obligation. `STATUS.md` reframed the review's
  recommendation as "compile-time obligation instead of a grep"; that is a larger, more invasive
  ambition, and it only partly lands — a type can force "check supersession after this await" but
  **cannot** enforce the other half of the invariant the tests protect: no `ConfigureAwait(false)`
  (the "captured context" map). That discipline+test survives either way, so the compile-time version
  removes one grep, not the test map. Rejected for this pass; see *Rejected alternatives*.
- **Not** an extraction of the connect-permission concern (`_clickGrant`, `ConnectPermitted`,
  `EnforceConnectPermission`). It is a distinct third mechanism the reviewer did not name. YAGNI —
  it stays in the hub.
- **Not** an extraction of the event-driven connect turns (`ConnectHalvesAsync`,
  `RegisterCallsAsync`, `FinishTurn`). These are the re-read shape, a different mechanism from the
  grace window and the reconcile, and stay in the hub.

## Decisions taken

1. **Ambition: clean extraction** (the reviewer's literal target), not the compile-time `PassStep`
   ambition and not "grace window only, first".
2. **Seam ↔ hub cut: a narrow coordinator interface + shared machine references.** The seams hold the
   same machine instances the manager holds, plus a small `internal IConnectionCoordinator` for the
   manager-private operations. Rejected: a single fat hub interface (a god-interface plus a wall of
   forwarders that hides that the seams share real machines) and passing the concrete
   `ConnectionManager` (circular coupling, unbounded back-surface — defeats the "narrow, explicit
   coupling" purpose).
3. **Strictly behaviour-preserving.** No behavioural test changes; the suite is green at every step.

## Architecture

The manager stays the **hub**. It keeps owning the four machines (`_linkMachine`, `_latch`, `_music`,
`_calls`), the endpoint probe (`_endpointProbe`, `EndpointPresenceCache`, `ProbeEndpointLevel`,
`ApplyEndpointPresence`, `RefreshEndpointLevel`), the click-grant/permission logic, the projection
(`Refresh`/`Publish`), the synchronous inbound event handlers, the public command surface, and the
event-driven connect turns. Two mechanisms move out into peer seams that own only their *timer +
supersession token + orchestration*.

```
ConnectionManager (hub)  ── implements IConnectionCoordinator
   ├─ owns: linkMachine, latch, music, calls, endpoint probe, clickGrant, projection
   ├─ owns: inbound handlers, public commands, ConnectHalves/RegisterCalls turns (re-read shape)
   ├─ GraceWindow   ──┐  each holds shared machine refs + IConnectionCoordinator
   └─ Reconciler    ──┘  Reconciler also holds a ref to GraceWindow (to arm it in check-2)
```

The result: the **generation** shape lives only in `GraceWindow`, the **timestamp** shape lives only
in `Reconciler`, the **re-read** shape stays only in the hub. A future fifth guard is added to the one
class that owns its concern, not piled onto a 1290-line file.

This is faithful to the manager's existing docstring — "it assembles rather than decides" — because
the seams *already are* composed independent machines; the two new seams join that composition.

### GraceWindow (new file: `src/Klangbruecke/Connection/GraceWindow.cs`)

The 3 s window that decides, when the audio connection closes, whether the phone dropped the profile
deliberately (link still up → suppress) or left the room (link gone → range exit).

**Moves in from the manager:** `_graceTimer`, `_graceGeneration`, the `GraceWindow` 3 s constant,
`OnConnectionClosed()` (the arming), `OnGraceWindowElapsedAsync` → `OnElapsedAsync`, `Superseded(int)`,
`CancelGraceWindow` → `Cancel()`.

**Holds:** `IScheduler`, `ILinkMonitor`, `LinkMachine`, `SuppressionLatch`, `MusicHalf`,
`IConnectionCoordinator`.

**Public surface:** `OnConnectionClosed()`, `Cancel()`, `Dispose()`.

**Reaches back through the coordinator for:** `SuppressDeliberately(status)` (Connected branch),
`Report(msg)` (range-exit branch), `EnforceConnectPermission()`, `Publish()`, `IsDisposed`.

**Hub rewiring:** `OnSinkStateChanged`'s `OnConnectionClosed()` → `_graceWindow.OnConnectionClosed()`;
`SelectPhone`/`DeselectPhone`'s `CancelGraceWindow()` → `_graceWindow.Cancel()`.

### Reconciler (new file: `src/Klangbruecke/Connection/Reconciler.cs`)

The 30 s level-triggered drift correction — the spec's five checks in order — plus its stall/
supersession bookkeeping.

**Moves in from the manager:** `_reconcileTimer`, `_reconcilingSince`, the `ReconcilePeriod` (30 s)
and `ReconcileStall` (25 s) constants, `ReconcileAsync` → `RunAsync(trigger, userAsked)`,
`Superseded(DateTimeOffset)`, `StillOurs`, and the `Drift` record + `TakeDrift`/`ReportDrift` (they
are the pass's own bookkeeping).

**Holds:** `IScheduler`, `ILinkMonitor`, `IAudioSinkService`, `LinkMachine`, `SuppressionLatch`,
`MusicHalf`, `CallsHalf`, `GraceWindow`, `IConnectionCoordinator`.

**Public surface:** `Start()` (schedules the periodic tick), `RunAsync(trigger, userAsked = false)`,
`Dispose()`.

**Reaches back through the coordinator for:** `ConnectPermitted`, `RefreshEndpointLevel()`,
`EnforceConnectPermission()`, `Publish()`, `IsDisposed`. Reaches `GraceWindow.OnConnectionClosed()`
**directly** for check-2's non-userAsked branch (the reconcile can arm the grace window).

**Hub rewiring:** the `Start()` periodic schedule → `_reconciler.Start()`; every `ReconcileAsync(...)`
call site in `SelectPhone`, `SetAutoReconnect`, and `OnResumed` → `_reconciler.RunAsync(...)`.

### IConnectionCoordinator (new, `internal`)

```csharp
internal interface IConnectionCoordinator
{
    bool IsDisposed { get; }
    bool ConnectPermitted { get; }
    void RefreshEndpointLevel();
    void EnforceConnectPermission();
    void Publish();
    void SuppressDeliberately(string status);
    void Report(string message);
}
```

`ConnectionManager` implements it with **explicit interface implementation** forwarding to the
existing private members. This adds seven one-line forwarders and **does not widen** the manager's
public/private surface: `_clickGrant`, `ConnectPermitted`, `EnforceConnectPermission`,
`SuppressDeliberately`, the endpoint probe, and `Publish`/`Refresh`/projection all stay private, and
`RequestDisconnect`/`SetAutoReconnect` keep calling them directly.

`Dispose` gains `_graceWindow.Dispose()` + `_reconciler.Dispose()` in place of the two timer
disposals. `_resumeTimer` stays in the manager — it is a manager inbound-event concern that merely
*calls* `_reconciler.RunAsync("resume")`.

## Test and documentation impact

- **No behavioural test changes.** All ~30 tests drive the manager's public surface
  (`Scheduler.Advance`, `Link.DeferRead`/`CompleteRead`, `SetEndpointPresent`); the awaits move
  classes but stay reachable through it, and announcements still flow from the manager's `Publish`.
- **The "captured context" map** (`tests/Klangbruecke.Tests/Connection/ConnectionManagerTests.cs`,
  the section headed *"one test per await a foreign thread can complete"*) must be **rewritten** to
  name the new sites (`Reconciler.RunAsync`, `GraceWindow.OnElapsedAsync`, `Reconciler.StillOurs`) and
  to recount "fourteen awaits across the three classes" → across **five** classes. The test *bodies*
  do not change; the honest await→test map is doc-updated. The three named-uncoverable awaits move
  with their turns (`Reconciler.RunAsync`'s fourth `StillOurs`; `ConnectHalvesAsync`'s second and
  `RegisterCallsAsync`'s — both still in the hub).
- **The `ConfigureAwait(false)` prohibition and the single-threaded-by-contract docstring** carry into
  both new files, which now hold the awaits. The manager's class docstring updates from "owns … the
  3 s grace window, the 30 s reconcile" to "delegates the grace window and the reconcile to their
  seams". `GraceWindow` and `Reconciler` each get the "subscribes to nothing; single-threaded; never
  add `ConfigureAwait(false)`" preamble the other seams carry.
- **New isolated seam tests: not required.** Existing coverage-through-the-manager is the net; the
  interface makes isolated tests *possible* later but adding them now is out of scope (YAGNI).
- **Verification:** `dotnet test` **unfiltered** (`STATUS.md`'s caution — an unfiltered run lets a
  flake name itself), zero warnings, all 4258 green. The captured-context tests are the mutation net
  that proves the no-`ConfigureAwait` invariant survived the move.

## Deliberate non-changes

- The reconcile's check-2 non-userAsked path currently `Publish()`es twice — once inside
  `OnConnectionClosed`, once via `ReportDrift`. It is idempotent (`Publish` raises events only on
  change), and it is **preserved exactly**, not "fixed". Behaviour preservation means the double
  publish moves verbatim.
- The exact call ordering inside `SelectPhone` (the `Cancel()` before `ApplySettingsToHalves`, the
  grant put back around `Publish`) is preserved verbatim; only the method *names* it calls change.

## Suggested sequencing (for the implementation plan)

Behaviour-preserving throughout, suite green at every step:

1. Add `IConnectionCoordinator` + the manager's explicit implementation. No extraction yet — pure
   addition. Suite green.
2. Extract `GraceWindow` (the smaller, more self-contained mechanism: one timer, one generation, one
   link read, two outcomes). Rewire the hub. Run the suite unfiltered.
3. Extract `Reconciler`. Rewire the hub. Run the suite unfiltered.
4. Rewrite the captured-context map and the three docstrings. Run the suite unfiltered.

## Rejected alternatives

- **Compile-time `PassStep<T>` type** (`STATUS.md`'s "compile-time obligation"). A generic
  "awaited step in a pass" whose result is a discriminated `Fresh<T> | Superseded` the caller cannot
  unwrap without branching. It would turn the *generation* shape from a discipline into a type — but
  only that one of the three shapes fits the mould, it does not remove the `ConfigureAwait` test map
  (the other half of the invariant), and it introduces novel type-machinery into the app's most
  dangerous file. Deferred; revisit only if the clean extraction surfaces the type naturally.
- **One fat `IConnectionHub` interface** (~20 members wrapping every machine call). Fully mockable,
  but a god-interface plus a wall of one-line pass-throughs, and it hides that the seams share real
  machines. Rejected in favour of shared machine references + a narrow coordinator.
- **Passing the concrete `ConnectionManager` to the seams.** No new interface, but circular concrete
  coupling and an unbounded back-surface — the seam can reach anything, which is exactly the question
  the extraction exists to answer. Rejected.
- **Merging the grace window and the reconcile into one type.** They are related (the reconcile arms
  the window; both read the link; both guard supersession) but answer different questions (generation
  vs timestamp) with different lifecycles (one-shot vs periodic). One class would be a smaller manager
  with the same two-mechanisms-in-one-file problem. Two single-responsibility classes instead.
