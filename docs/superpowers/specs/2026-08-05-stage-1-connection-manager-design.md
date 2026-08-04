# Stage 1 — ConnectionManager design

Date: 2026-08-05
Status: approved, not yet implemented
Supersedes the "Stage 1 — connection lifecycle" section of
`docs/superpowers/specs/2026-08-04-connection-lifecycle-design.md`. That document's state table is
revised here; everything else in it — the grace window, the backoff schedule, the reconcile loop,
the rejected alternatives, the Stage 2 matrix — still stands and is not repeated.

## Problem

Stage 0 shipped and both halves work (`docs/FINDINGS.md` §10, §12). There is still no connection
lifecycle: no `DeviceWatcher`, no retry, no backoff, no sleep/resume handling, and
`Settings.AutoReconnect` is written by the tray menu and read by nothing.

The design was written before Stage 0 ran. Re-reading the app's own log
(`%LOCALAPPDATA%\Klangbruecke\logs\klangbruecke-20260804.log`) against it turned up three facts that
change it. All three are measurements, not inferences.

### 1. The calls half reports failure on every run, including the runs where calls worked

```
21:02:56.958 [INF] RegisterApp returned; IsRegistered=True.
21:02:56.961 [INF] PhoneLineTransportDevice.ConnectAsync returned False.
21:02:56.964 [INF] Call transport connect failed.
```

That is build `0.1.0.2` — the run in which the hands-free role was claimed and the phone offered the
PC as a call audio device. `CallTransportService.ConnectAsync` treats
`PhoneLineTransportDevice.ConnectAsync() == false` as failure and returns `false` without ever
setting `IsConnected`, so **`ICallTransportService`'s success signal is `false` in the known-good
configuration.**

A state machine fed that signal would sit in `Degraded` permanently and re-run
`RequestAccessAsync` / `RegisterApp` on every reconcile tick, forever. Grounding the calls half in
`IsRegistered()` — which the original design already prescribes — is therefore **required**, not
tidying. `ConnectAsync`'s bool is demoted to a logged fact.

This retires open item #2 in `docs/HANDOFF.md` without answering it: the app stops depending on the
return value, so why it is `false` no longer blocks anything.

### 2. The A2DP capture endpoint is absent right after connect in 5 of 8 recorded runs

| Launch | connect → endpoint check | endpoint |
|---|---|---|
| 20:04:30 | +65 ms | found |
| 20:14:55 | +253 ms | **absent** |
| 20:15:59 | +250 ms | **absent** |
| 20:17:11 | +69 ms | found |
| 20:41:13 | +247 ms | **absent** |
| 20:45:26 | +252 ms | **absent** |
| 20:59:19 | +243 ms | **absent** |
| 21:02:56 | +195 ms | found |

The absent rows are the *slower* ones because `FindSinkCaptureEndpoint` uses `FirstOrDefault`, which
short-circuits on a hit and scans every endpoint on a miss. The gap is enumeration cost, not a wait.

`TrayContext.StartRouting()` runs synchronously after `AudioSinkService.ConnectAsync` returns, so in
five of eight launches **music silently never routed for the whole session.** This ships today. It
is invisible because it is indistinguishable from "the phone is not streaming yet".

The structural consequence: the endpoint's arrival is not synchronous with the connection opening,
and nothing in the app today reports it. Any design that starts the route once, immediately after
connecting, is wrong.

### 3. A call does not close the A2DP connection

```
21:03:35.361 [ERR] Capture stopped.
    System.Runtime.InteropServices.COMException (0x88890004)   <- AUDCLNT_E_DEVICE_INVALIDATED
21:03:35.404 [WRN] Tearing the route down: the capture half stopped.
```

No `A2DP sink state:` line follows. The `AudioPlaybackConnection` stayed `Opened` while the phone
took the radio for SCO. So `FINDINGS.md` §13 — the flagship reconnect gap, "one phone call silently
costs you the music bridge" — needs **the route restarted, not the phone reconnected.**

Recorded honestly: the log ends 30 seconds into that call, so what is confirmed is that the
connection survives the capture teardown, not what happens when the call ends.

### Two constraints that follow

- `CallTransportService.Disconnect()` **unregisters the hands-free role.** Any teardown path that
  reaches it flaps the phone's call-audio-device option. Nothing on the music half may call it.
- During a call, SCO holds the radio, so A2DP cannot be re-established regardless of retry schedule.
  Finding #3 means it does not need to be.

## Goals

1. Close the reconnect gap: the app recovers, unattended, from a call ending, a range exit and
   return, sleep/resume, reboot, and a phone-initiated disconnect.
2. Fix finding #2 — the route starts when the endpoint actually appears, however late that is.
3. Never claim "connected" when the OS disagrees, and never report a half as failed when it is
   working (finding #1).
4. Put `AudioRouter`'s five load-bearing properties under test. Today reverting any one leaves all
   153 tests green.

## Non-goals

- The Stage 2 hardware matrix. Stage 1 ends at a packaged smoke check.
- Tray selection of the call audio device. That means writing the system-wide default
  communications device via `IPolicyConfig`, an undocumented COM interface, and it is an open
  question for the user (`HANDOFF.md` item 4).
- LAF token generation. Standing instruction; the user's call.
- The outgoing-call bandwidth investigation (`FINDINGS.md` §11).
- Explaining why `PhoneLineTransportDevice.ConnectAsync` returns `False`.

## Decisions

Taken during brainstorming on 2026-08-05, with the reasoning that produced them.

| Decision | Chosen | Why |
|---|---|---|
| Router seam depth | Outer `IAudioRouter` **and** an inner capture/render factory | The outer interface only makes `ConnectionManager` testable. The five load-bearing properties need a seam *inside* `AudioRouter`. Stage 1 turns `Start`/`Stop` from click-driven into event-driven, so those properties go from exercised a few times a day to dozens. |
| Endpoint arrival signal | `IMMNotificationClient` via `MMDeviceEnumerator` | One mechanism fixes both finding #2 (startup race) and finding #3 (post-call recovery), near-instantly. A poll would work but leaves latency on the flagship scenario. |
| Threading | Single-threaded on the UI thread via `IUiDispatcher` | Makes the `WasapiOut` play-thread deadlock structurally impossible rather than accidentally avoided by field-initializer ordering. No locks anywhere. |
| `Suppressed` re-arm | Poll `ConnectionStatus` in the 30 s reconcile | No long-lived `BluetoothDevice` held open, no subscription to unwind across sleep/resume. The miss-window is explicit and tested (see the latch table). |
| Machine shape | Decompose into three small machines; derive the reported state | "Connection up, route down" has no home in the original single table — it lands in `Connected` while no audio flows, the exact lying indicator `FINDINGS.md` §4 warns about. Findings #2 and #3 are both that condition. |
| `AutoReconnect` off | No app-initiated connect; a click-initiated connect still runs to completion | Without the carve-out, finding #2 leaves these users with silent no-audio in most launches. |
| Tooltip | State first, last message as detail | The state is always accurate; the message is context. |

## Architecture

```
TrayContext (view: menu, tooltip, exit — no connection logic)
└── ConnectionManager           state, intent, reconcile timer; UI thread only
    ├── LinkMachine             NoPhone | Absent | Present
    ├── SuppressionLatch        deliberate-disconnect memory
    ├── MusicHalf               Off | Connecting | Linked | Up | Backoff(n)
    ├── CallsHalf               Off | Registering | Up | Backoff(n)
    └── ConnectionStateProjection   pure (link, latch, music, calls) -> reported state

    behind interfaces:
      IAudioSinkService      IAudioRouter        ILinkMonitor
      ICallTransportService  IAudioEndpointMonitor
      IScheduler             IPowerNotifier      IUiDispatcher (exists)

AudioRouter (implementation of IAudioRouter)
    └── IAudioDeviceFactory -> ICaptureSource / IRenderSink
        (wraps WasapiCapture / WasapiOut; the seam that makes the five properties testable)
```

`TrayContext`'s dependency set shrinks to `ConnectionManager`, `StatusPresenter` and `NotifyIcon`.
Neither `MMDevice` nor `DeviceInformation` appears in it any more.

### Interfaces

Declarations only. Implementers write the bodies — see "Process" at the end.

```csharp
// --- Bluetooth ---------------------------------------------------------------

public enum AudioSinkConnectionState { Closed, Opened }

public interface IAudioSinkService : IDisposable
{
    string? ConnectedDeviceId { get; }

    /// <summary>The WinRT connection object is open. NOT a claim about the capture endpoint.</summary>
    bool IsConnected { get; }

    event EventHandler<StatusMessage>? Status;
    event EventHandler<AudioSinkConnectionState>? StateChanged;

    Task<bool> ConnectAsync(string deviceId);
    void Disconnect();
}

public readonly record struct CallTransportResult(
    bool Registered,
    bool? TransportConnected,
    string Reason);

public interface ICallTransportService : IDisposable
{
    /// <summary>Live PhoneLineTransportDevice.IsRegistered(). The calls half's only health signal.</summary>
    bool IsRegistered { get; }

    event EventHandler<StatusMessage>? Status;

    Task<CallTransportResult> ConnectAsync(string transportDeviceId);

    /// <summary>Unregisters the hands-free role. Deliberate intent changes only.</summary>
    void Disconnect();
}

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

// --- Audio -------------------------------------------------------------------

public readonly record struct AudioOutputDevice(string Id, string Name);

public interface IAudioRouter : IDisposable
{
    bool IsRunning { get; }

    event EventHandler<StatusMessage>? Status;
    event EventHandler? Stopped;

    /// <summary>Resolves both endpoints itself. False if either is unavailable.</summary>
    bool Start(string? preferredOutputDeviceId);

    void Stop();
}

public interface IAudioEndpointMonitor : IDisposable
{
    bool SinkCaptureEndpointPresent { get; }
    event EventHandler? EndpointsChanged;
    void Start();
}

// --- Inside AudioRouter ------------------------------------------------------

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
    ICaptureSource? CreateSinkCapture();
    IRenderSink? CreateRender(string? preferredOutputDeviceId);
    IReadOnlyList<AudioOutputDevice> ListOutputs();
}

// --- Platform ----------------------------------------------------------------

public interface IScheduler
{
    DateTimeOffset Now { get; }
    IDisposable Schedule(TimeSpan delay, Action action);
    IDisposable SchedulePeriodic(TimeSpan period, Action action);
}

public interface IPowerNotifier : IDisposable
{
    event EventHandler? Resumed;
    void Start();
}
```

`IAudioRouter.Start` takes a device id string rather than two `MMDevice`s. Endpoint resolution moves
inside the router, so no `ConnectionManager` fake has to impersonate a COM object.

`IAudioEndpointMonitor` is the sole owner of "is the endpoint present". `IAudioDeviceFactory` only
creates; `CreateSinkCapture()` returning null is how the router learns it cannot.

**Deliberate deviation from the previous design.** It said to ground the music half's `IsConnected`
in the capture endpoint. That is split here instead: `IAudioSinkService.IsConnected` reports only
that the WinRT connection object is open, and endpoint presence is `IAudioEndpointMonitor`'s to
report. Finding #2 is why — the two facts are separated by an unbounded interval, and folding them
into one property makes the interval unrepresentable. The original intent is preserved at the level
that matters: music is `Up` only when the connection is open, the endpoint is present, **and** the
router is running.

### ConnectionManager surface

```csharp
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

public readonly record struct PhoneDevice(string Id, string Name);
```

Every tray interaction maps to exactly one of those methods. The startup connect moves out of the
`TrayContext` constructor into `Start()`.

## State model

### Link machine

| From | Event | To |
|---|---|---|
| `NoPhone` | phone selected | `Absent` |
| any | phone deselected | `NoPhone` |
| `Absent` | `DeviceAppeared`, or reconcile reads `Connected` | `Present` |
| `Present` | `DeviceRemoved`, or reconcile reads `Disconnected` | `Absent` |

`BluetoothLinkStatus.Unknown` is treated as `Disconnected`. Guessing the other way produces silent
permanent dormancy, which is the predecessor's defining bug.

### Suppression latch

Separate from the link machine, in memory only, never persisted. It carries a reason, because the two
ways to become dormant re-arm differently.

| Latch | Event | After |
|---|---|---|
| clear | tray Disconnect clicked | set `Deliberate`, `sawAbsent = false` |
| clear | connection closed and link still `Present` after the 3 s grace window | set `Deliberate`, `sawAbsent = false` |
| clear | a half drops while `AutoReconnect` is off | set `AutoReconnectOff` |
| set `Deliberate` | link → `Absent` | set, `sawAbsent = true` |
| set `Deliberate`, `sawAbsent` | link → `Present` | **clear** |
| set `AutoReconnectOff` | link → `Absent` → `Present` | unchanged — the user asked for no auto-reconnect |
| set `AutoReconnectOff` | `AutoReconnect` switched on, or a phone picked from the tray | clear |
| any | phone deselected or reselected | clear |

Storing `sawAbsent` explicitly is what makes the poll's miss-window a testable property rather than
an accident: a drop-and-return entirely inside one 30 s tick leaves the latch set, and the app stays
dormant until the next observed absence. That is a deliberate, documented limitation of the polling
choice.

`Deliberate` expires when the phone leaves the room; `AutoReconnectOff` does not, because it is a
setting rather than a moment. Both report `Suppressed`, with different detail text.

### Connect permitted

A guard used by both halves' tables, evaluated once per transition attempt. It is true when
`AutoReconnect` is on, **or** the attempt descends from a tray click that has not yet reached `Up`.
That is the whole of the `AutoReconnect`-off carve-out: the manager may finish what the user started,
including waiting for the endpoint and retrying the route, but may not start anything itself.

### Music half

Enabled iff a phone is selected **and** `AudioSinkPolicy.CanOpenConnection(PackageIdentity.IsPackaged)`.
Disabled is not failed — unpackaged, the music half is `Off` and does not contribute `Degraded`.

| From | Event | To | Action |
|---|---|---|---|
| `Off` | enabled, link `Present`, not suppressed, connect permitted | `Connecting` | `sink.ConnectAsync` |
| `Connecting` | returned true | `Linked` | — |
| `Connecting` | returned false or threw | `Backoff(0)` | schedule retry |
| `Linked` | endpoint present **and** route backoff elapsed | `Up` if `router.Start(outputId)` returned true, else stay `Linked` and advance route backoff | `router.Start(outputId)` |
| `Up` | `router.Stopped` | `Linked` | advance route backoff — **no Bluetooth reconnect** |
| `Up` | reconcile: endpoint absent | `Linked` | `router.Stop()` |
| `Up` | reconcile: `router.IsRunning` false | `Linked` | advance route backoff |
| `Up` / `Linked` | connection closed | grace window (below) | `router.Stop()` |
| `Backoff(n)` | delay elapsed, link `Present`, connect permitted | `Connecting` | `sink.ConnectAsync` |
| any | link → `Absent`, suppressed, or phone deselected | `Off` | `router.Stop()`, `sink.Disconnect()` |

`Linked` never times out. "Connection open, endpoint absent" is the normal state whenever the phone
is not streaming, and it is also what a call looks like from finding #3. Treating it as an error
would produce a false failure every time the user pauses their music.

**Route backoff** is separate from the connect backoff: same 2/4/8/16/30/60 s schedule, reset once
`router.IsRunning` has been continuously true for 10 s. It exists because
`AudioRouter.Start()` returns `true` for a source that cannot capture — the capture thread dies
asynchronously (`HANDOFF.md`, smaller carry-forwards). Without it, a dead endpoint that still
enumerates produces a `Linked → Up → Stopped → Linked` loop at event speed.

### Calls half

Enabled iff `CallsPolicy.Decide(settings.EnableCalls, PackageIdentity.IsPackaged) == Enabled`
**and** a phone is selected.

| From | Event | To | Action |
|---|---|---|---|
| `Off` | enabled, link `Present`, not suppressed, connect permitted | `Registering` | enumerate, `TransportMatcher.Match`, `calls.ConnectAsync` |
| `Registering` | `result.Registered` | `Up` | log `TransportConnected`; **not** a failure when false |
| `Registering` | `!result.Registered`, threw, or no match | `Backoff(0)` | schedule retry |
| `Up` | reconcile: `calls.IsRegistered` false | `Backoff(0)` | schedule retry |
| `Backoff(n)` | delay elapsed, link `Present`, connect permitted | `Registering` | retry |
| any | phone deselected, `EnableCalls` off, or dispose | `Off` | `calls.Disconnect()` — the only unregister path |

**The calls half is not torn down when the link goes `Absent`.** Registration is not link-scoped: it
is what makes the phone offer the PC when it returns. Unregistering on every range exit is exactly
the flapping the constraint above forbids. The link check dominates the projection anyway, so the
reported state while absent is `Discovering` regardless.

### Projection to the reported state

Pure function of `(phoneSelected, latch, link, music, calls)`. Evaluated in order; first match wins.

| Condition | Reported |
|---|---|
| no phone selected | `Idle` |
| suppression latch set, either reason | `Suppressed` |
| link `Absent` | `Discovering` |
| any enabled half in `Connecting` / `Registering` | `Connecting` |
| every enabled half `Up`, music `Linked` counting as up | `Connected` |
| at least one enabled half `Up`, at least one in `Backoff` | `Degraded` |
| every enabled half in `Backoff` | `RetryBackoff` |

Music `Linked` reports `Connected` with detail `waiting for phone audio`. The Bluetooth connection
genuinely is open and the phone genuinely can select the PC; the only thing missing is the phone
pressing play. Reporting that as `Degraded` would cry wolf on the most common idle condition.

If no half is enabled — unpackaged with calls off — the reported state is `Idle` with detail naming
why, not `Connected`.

`Detail` is derived alongside the state, not inherited from whatever spoke last: each row above owns
a short phrase (`waiting for phone audio`, `retrying in 8s`, `calls unregistered`,
`auto-reconnect is off`). `StatusPresenter` composes `Klangbruecke: <State> — <Detail>` and applies
its existing 96-character cap to the composed string.

## Threading

`ConnectionManager` is single-threaded by contract: every method and every internal transition runs
on the UI thread. Each inbound event source posts through `IUiDispatcher` before touching state:

| Source | Arrives on | Marshalled |
|---|---|---|
| `DeviceWatcher` Added/Removed | WinRT threadpool | yes |
| `AudioPlaybackConnection.StateChanged` | WinRT threadpool | yes |
| `IMMNotificationClient` callbacks | COM/MTA thread | yes |
| NAudio `RecordingStopped` / `PlaybackStopped` | NAudio worker or play thread | yes (already, via `AudioRouter`) |
| `SystemEvents.PowerModeChanged` | `SystemEvents` thread | yes |
| `IScheduler` ticks | production impl delivers on the UI thread | n/a |
| Tray clicks | UI thread | n/a |

No locks. This is what makes the `WasapiOut` self-join deadlock structurally impossible:
`IAudioRouter.Start` can only be reached from the thread that constructed the marshalling control.
Production avoids it today only by field-initializer ordering (`HANDOFF.md`, "Riskiest code").

`Application.ThreadException`'s add accessor **assigns rather than combines** — if anything in
Stage 1 needs it, extend `Program.Main`'s existing lambda; do not add a second subscriber.

## Reconcile loop

Every 30 s, five level-triggered reads, then re-derive and correct drift:

1. `link.ReadLinkStatusAsync()` → link machine and the suppression latch
2. `sink.IsConnected` false while music is `Linked` / `Up` → connection-closed path
3. `endpoints.SinkCaptureEndpointPresent` → `Linked` ↔ `Up` correction
4. `router.IsRunning` false while music `Up` → `Linked`, advance route backoff
5. `calls.IsRegistered` false while calls `Up` → `Backoff(0)`

**Logs only when something changed.** A tick that finds no drift writes nothing. At 30 s intervals an
unconditional line is 2,880 entries a day, which both buries the events worth reading and starts to
matter given `FileLog.Write` does synchronous file I/O under a lock.

## Grace window, backoff, resume

**Grace window (3 s).** On the connection reporting `Closed`, do not act immediately. Schedule 3 s,
then `ReadLinkStatusAsync()`:

- `Connected` → the ACL link is alive, only the audio profile went → **deliberate** → set the
  suppression latch, halves to `Off`.
- `Disconnected` or `Unknown` → **out of range** → link to `Absent`, music to `Backoff(0)`.

**Backoff.** 2, 4, 8, 16, 30, then 60 s. Independent per half, plus the separate route backoff
described above. Reset when that half reaches `Up`.

**Resume.** `IPowerNotifier.Resumed` → `IScheduler.Schedule(5 s)` → forced reconcile. The Bluetooth
stack is not back at the moment the event fires, and an immediate attempt only burns the first
backoff step.

**`AutoReconnect` off** removes the manager's permission to *initiate* a connect: `Backoff(n) →
Connecting`, `Absent → Present → Connecting`, and the resume-forced reconcile all fail the
"connect permitted" guard. A click-initiated connect still runs to completion, including `Linked →
Up` when the endpoint arrives and the route backoff — that is finishing what the user started, and
without it finding #2 leaves these users with silent no-audio in most launches. A half that reaches
`Up` and later drops goes to `Off` and sets the suppression latch with reason `AutoReconnectOff`,
reported as `Suppressed` with detail `auto-reconnect is off`.

## Testing

No test sleeps, allocates a real timer, or needs a phone. `FakeScheduler.Advance(TimeSpan)` drives
all four timings.

Test doubles live in `tests/Klangbruecke.Tests/Fakes/` as ordinary `public`/`internal` classes, not
`file`-scoped — the existing suite's `DeferringUiDispatcher` and `DroppingUiDispatcher` are
`file`-scoped and therefore unreachable from other test files, and Stage 1 needs them across
several.

### The five load-bearing AudioRouter properties

This is the coverage the inner seam exists for. Each test goes red if its property is reverted.

| Property | Test |
|---|---|
| `_session` published before `StartRecording` / `Play` | fake capture raises `RecordingStopped` synchronously from inside `StartRecording`; assert the router tears down rather than discarding the event as stale |
| `EndSession()` before `Report()` in both stopped handlers | a `Status` subscriber asserts `IsRunning == false` at the moment it is invoked, for the capture and playback paths separately |
| `_session = null` before the unsubscribes in `Stop()` | fake's `Dispose` re-raises `Stopped`; assert `Stop` is not re-entered and teardown runs once |
| stale-session check inside the posted teardown lambda | deferring dispatcher; `Stop()` then `Start()` between the post and the drain; assert the new route survives |
| `ReferenceEquals(sender, …)` guards | an old fake raises `Stopped` after a later `Start` replaced it; assert nothing is torn down |

Plus: `Start` logs the capture/render format pair unconditionally on both the matched and differing
paths; `Start`'s failure path names both formats even when the `MixFormat` read is what threw.

### State machine

- **Projection** — exhaustive table over the cross-product of (phoneSelected, latch, link, music,
  calls). It is pure and the space is small enough to enumerate rather than sample.
- **Link machine** — table-driven, including `Unknown` treated as `Disconnected`.
- **Suppression latch** — table-driven over both reasons, including: `Deliberate` clears on a link
  drop-and-return, `AutoReconnectOff` does not; and the documented miss, where a drop-and-return
  entirely inside one reconcile tick leaves the latch set.
- **Music half** — every row of its table. Named cases that must exist:
  - `Up` + `router.Stopped` goes to `Linked` and does **not** call `sink.ConnectAsync` (finding #3)
  - `Linked` + endpoint appears starts the route (finding #2)
  - `Linked` does not time out after any elapsed time
  - route backoff advances on repeated immediate `Stopped`, and resets after 10 s of running
  - link `Absent` stops the router and disconnects the sink
- **Calls half** — every row. Named cases that must exist:
  - `result.Registered == true` with `TransportConnected == false` reaches `Up` (finding #1)
  - link `Absent` does **not** call `calls.Disconnect()`
  - `EnableCalls` off does call it
  - reconcile seeing `IsRegistered == false` drops `Up` to `Backoff(0)`
- **Grace window** — both branches, driven by `FakeScheduler`, plus `Unknown` taking the
  out-of-range branch.
- **Backoff** — the 2/4/8/16/30/60 sequence, per-half independence, reset on success.
- **Resume** — no reconcile before 5 s, forced reconcile at 5 s.
- **`AutoReconnect` off** — no manager-initiated connect from backoff, link return, or resume; a
  click-initiated connect still reaches `Up` via the endpoint event.
- **Reconcile** — one drift test per check, plus: a tick with no drift writes no log line.

### Not unit-tested

`ILinkMonitor`, `IAudioEndpointMonitor`, `IAudioDeviceFactory` and the WinRT/WASAPI implementations
behind them. They are the seam, not the thing under test; they are verified by hand against the OS.
`AudioSinkPolicy` still gates `TryCreateFromId` in two places and neither gate is removed.

## Files

New:

```
src/Klangbruecke/Connection/ConnectionManager.cs
src/Klangbruecke/Connection/ConnectionState.cs          reported enum + projection
src/Klangbruecke/Connection/LinkMachine.cs
src/Klangbruecke/Connection/SuppressionLatch.cs
src/Klangbruecke/Connection/MusicHalf.cs
src/Klangbruecke/Connection/CallsHalf.cs
src/Klangbruecke/Connection/BackoffSchedule.cs
src/Klangbruecke/Bluetooth/IAudioSinkService.cs
src/Klangbruecke/Bluetooth/ICallTransportService.cs
src/Klangbruecke/Bluetooth/ILinkMonitor.cs
src/Klangbruecke/Bluetooth/LinkMonitor.cs               DeviceWatcher + ConnectionStatus
src/Klangbruecke/Audio/IAudioRouter.cs
src/Klangbruecke/Audio/IAudioEndpointMonitor.cs
src/Klangbruecke/Audio/EndpointMonitor.cs               IMMNotificationClient
src/Klangbruecke/Audio/IAudioDeviceFactory.cs           + ICaptureSource / IRenderSink
src/Klangbruecke/Audio/WasapiDeviceFactory.cs           adapters over WasapiCapture / WasapiOut
src/Klangbruecke/Platform/IScheduler.cs
src/Klangbruecke/Platform/UiScheduler.cs
src/Klangbruecke/Platform/IPowerNotifier.cs
src/Klangbruecke/Platform/PowerNotifier.cs
tests/Klangbruecke.Tests/Fakes/                         reusable doubles incl. FakeScheduler
tests/Klangbruecke.Tests/Connection/
tests/Klangbruecke.Tests/Audio/AudioRouterTests.cs
```

Modified: `TrayContext.cs` (view only), `AudioSinkService.cs` (implements the interface, `StateChanged`
event, `IsConnected` scoped to the connection object), `CallTransportService.cs` (`IsRegistered`,
`CallTransportResult`), `AudioRouter.cs` (device factory seam, `Stopped` event, id-based `Start`),
`StatusPresenter.cs` (state-first composition), `Program.cs` (wiring).

`Settings` is unchanged in shape; `AutoReconnect` is simply read for the first time.

## Validation

Stage 1 ends with a packaged build and a smoke check, not the full matrix.

- Bump the version in **both** `Klangbruecke.csproj` and `packaging/AppxManifest.xml`, or the install
  will not upgrade.
- `./packaging/Build-Msix.ps1`, then `Add-AppxPackage` from **Windows PowerShell** — the `Appx`
  module does not load in PowerShell 7.
- Read the startup `Base directory:` line before drawing any conclusion from the log; packaged and
  unpackaged runs share one file (`FINDINGS.md` §9).
- Smoke check: launch with the phone in range and confirm the route starts even when the endpoint
  lags, i.e. the finding #2 case now recovers instead of failing silently.
- Then place a call, end it, and confirm music returns without touching the tray. That is
  `FINDINGS.md` §13 closed.

The full Stage 2 matrix is unchanged and stays in the previous design document.

## Risks and open questions

1. **`IMMNotificationClient` threading and lifetime.** Callbacks arrive on a COM/MTA thread and the
   `MMDeviceEnumerator` must stay alive for the registration to hold. Not identity-gated, so this is
   cheap to probe with `dotnet run` before committing to it — do that first.
2. **`BluetoothDevice.FromBluetoothAddressAsync` unpackaged is unverified.** `FINDINGS.md` §2
   verified discovery unpackaged for the A2DP and phone-line selectors, not this call.
   `BluetoothDeviceId.TryExtractAddress` returns a 12-char hex string; the API wants a `ulong`.
3. **Music-half validation still costs a full MSIX cycle.** `TryCreateFromId` kills an unpackaged
   process (`FINDINGS.md` §8) and `AudioSinkPolicy` gates it in two places — neither gate is removed.
4. **Finding #3 is confirmed only for the first 30 s of a call.** What happens at call *end* is the
   thing Stage 1 is built to handle and has not been observed. If the connection does close after
   all, the music half falls back to `Backoff` and reconnects — the design degrades correctly, but
   more slowly than designed.

## Correction to FINDINGS.md §4

§4 states: "If that endpoint is absent, **nothing** is holding an `AudioPlaybackConnection` open."
The log contradicts this in five runs — the connection reported `Opened`, `A2DP sink connected` was
logged, and the endpoint was absent 250 ms later.

The endpoint's presence is **sufficient** proof of a live connection, not **necessary**. The
operational advice in §4 — verify against `Get-PnpDevice` rather than trusting a UI indicator — is
unaffected, but the absence of the endpoint no longer proves the absence of a connection. To be
folded into `FINDINGS.md` during Stage 1.

## Process

Per `HANDOFF.md`: Stage 0's plan embedded complete code, and all eight defects found were in the
plan text rather than in the implementations — the plan got one self-review while each task's code
got a dedicated adversarial reviewer.

**Stage 1's implementation plan specifies interface signatures, test names, the assertion each test
makes, and the invariant it defends. It does not contain method bodies.**
