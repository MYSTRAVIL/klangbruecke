# Connection lifecycle design

Date: 2026-08-04
Status: approved, not yet implemented

## Problem

The scaffold has never been run against a phone. It compiles, and the music path is wired end to
end, but no MSIX has been installed and no connection has ever been attempted with this code. The
approach is proven — `docs/FINDINGS.md` records both halves working on this machine — but it was
proven with two third-party apps, not with this one.

Separately, the scaffold has no connection lifecycle at all:

- `Settings.AutoReconnect` is written by the tray menu and read by no code. It is a dead setting.
- The only connect attempt is a fire-and-forget call in the `TrayContext` constructor
  (`TrayContext.cs:46-49`). If the phone is out of range at launch, it fails once and never retries.
- `AudioSinkService.OnStateChanged` (`AudioSinkService.cs:77-80`) is the sole signal that a
  connection dropped. It formats a tooltip string and nothing else — it does not stop the router,
  clear state, or retry.
- `IsConnected` tests `_connection is not null`, so it stays `true` after the phone has gone.
- There is no `DeviceWatcher`, no timer, no retry, and no sleep/resume handling.

`CLAUDE.md` names reconnect-after-reboot and phone-initiated reconnect as the predecessor app's
defining bug. That is the one part of this app with no implementation.

There is also no logging anywhere. The tray tooltip is the entire diagnostic surface and every
status change overwrites it. A failed first run would produce no evidence.

## Goals

1. Find out whether the connect path works at all, with enough instrumentation that a failure is
   diagnosable on the first attempt rather than the second.
2. Make the connection survive the transitions that killed the predecessor: reboot, walking out of
   range and back, PC sleep/resume, phone-initiated disconnect, Bluetooth off and on.
3. Never claim "connected" when the OS disagrees.

## Non-goals

- Fixing the outgoing-call ringback gap (`FINDINGS.md` §6). Cosmetic, and the fix is unverified.
- Anything in `FINDINGS.md` §5. Those are settled decisions, not open questions.
- Microsoft Store submission. Sideloading needs no approval; the restricted capability only gates
  the Store.

## Architecture

`TrayContext` currently orchestrates everything and holds all state implicitly. It becomes a view:
it renders the menu and tooltip and forwards user intent. All connection logic moves into a new
single owner.

```
ConnectionManager  (state machine + reconcile timer)
├── AudioSinkService      A2DP  — exists; gains DeviceWatcher + real state reporting
├── CallTransportService  HFP   — exists; gains device correlation
├── AudioRouter           WASAPI bridge — exists; gains format logging + PlaybackStopped
└── Log                   new; rolling file under %LOCALAPPDATA%\Klangbruecke\logs\
```

`AudioRouter` does **not** gain a resampler. That item was reverted during execution — WASAPI's
shared mode already converts, and `MediaFoundationResampler` in this topology destroys half the
audio. See item 3 under Stage 0 below for the full reasoning.

The `Log` path is literal for both the packaged and the unpackaged build, but only because
`packaging/AppxManifest.xml` disables Desktop Bridge write virtualization. Without that opt-out the
installed build writes to `%LOCALAPPDATA%\Packages\<PFN>\LocalCache\Local\` instead, while still
reporting the path above. See `FINDINGS.md` §9.

The services stop deciding anything. Each reports facts upward via events and exposes
`ConnectAsync` / `Disconnect`; `ConnectionManager` decides what to do about them. That boundary is
what lets the state machine be tested without a phone in the room.

Each service sits behind an interface (`IAudioSinkService`, `ICallTransportService`,
`IAudioRouter`) so `ConnectionManager` can be driven by fakes in tests.

### Grounding `IsConnected` in observable facts

Both services currently report connectivity from whether they hold a non-null object. That survives
the phone leaving. It gets re-grounded:

- **Music** — the A2DP SNK capture endpoint is present and active. Per `FINDINGS.md` §4, if that
  endpoint is absent then nothing is holding an `AudioPlaybackConnection` open and the phone
  physically cannot offer the PC as an output.
- **Calls** — `PhoneLineTransportDevice.IsRegistered()`.

## State machine

| From | Event | To |
|---|---|---|
| `Idle` | phone selected in settings | `Discovering` |
| `Discovering` | device appears (`DeviceWatcher`) | `Connecting` |
| `Connecting` | all enabled halves up | `Connected` |
| `Connecting` | one half up, one failed | `Degraded` |
| `Connecting` | all halves failed | `RetryBackoff` |
| `RetryBackoff` | backoff elapsed | `Connecting` |
| `Connected` / `Degraded` | close, and link gone after grace window | `Discovering` |
| `Connected` / `Degraded` | close, but link alive after grace window | `Suppressed` |
| `Connected` / `Degraded` | tray Disconnect clicked | `Suppressed` |
| `Degraded` | reconcile tick | retry failed half in place |
| `Suppressed` | link drops **and** returns | `Discovering` |
| any | phone deselected | `Idle` |

| State | Meaning |
|---|---|
| `Idle` | No phone selected in settings. |
| `Discovering` | Phone selected but not present. A `DeviceWatcher` is running; this costs nothing. |
| `Connecting` | Opening `AudioPlaybackConnection`, and registering the call transport if enabled. |
| `Connected` | Every enabled half is up and confirmed against the OS. |
| `Degraded` | One half up, the other failed. |
| `Suppressed` | Deliberate disconnect. Dormant on purpose. |
| `RetryBackoff` | Connect failed; waiting before the next attempt. |

`Degraded` is a first-class state because the two halves fail independently — A2DP sink and HFP
hands-free are different profiles on different channels (`FINDINGS.md` §1). Music up with calls
unregistered is a real and useful condition, and the tray must say so rather than reporting
"Connected". `FINDINGS.md` §4 is explicit that a lying indicator cost an hour of debugging.

**Disabled is not failed.** A half that is switched off (`Settings.EnableCalls` false) or
structurally unavailable (no package identity, so the restricted capability cannot apply) is not
attempted at all, and its absence does not produce `Degraded`. Music alone, with calls disabled,
is `Connected`. Otherwise running unpackaged during development would pin the app in `Degraded`
permanently and retry a call registration that cannot succeed.

**`Degraded` retries in place.** The failed half is re-attempted on each reconcile tick, subject to
the same backoff schedule, while the working half keeps running untouched. A2DP sink and HFP
hands-free are independent profiles on independent channels (`FINDINGS.md` §1), so recovering one
must never tear down the other. `Degraded` is a working state, not an error state — it is how the
app spends its time when, say, the call transport has not come back after resume but music has.

### Deliberate disconnect vs. walking away

`AudioPlaybackConnection.StateChanged` reports *that* the connection closed and never *why*.
"Disconnected on the phone" and "walked out of range" are indistinguishable at that layer, and they
need opposite responses.

The separating signal is `BluetoothDevice.ConnectionStatus`. On connection close, do not act
immediately — wait a **3 second grace window**, then read it:

- Still `Connected` → the ACL link is alive, only the audio profile went away → **deliberate** →
  `Suppressed`.
- `Disconnected` → the whole link is gone → **out of range** → `Discovering`.

The grace window exists because leaving range tears down the audio connection a beat before the ACL
link drops. Without it every range exit is misread as deliberate and the app goes dormant —
silently, and precisely when it would not be noticed.

### Leaving `Suppressed`

`Suppressed` is entered by a tray Disconnect click or a phone-initiated disconnect. It is left when
the Bluetooth link genuinely drops and returns — `ConnectionStatus` transitioning
`Connected → Disconnected → Connected`. Deliberate intent expires when the phone leaves the room.

**Suppression is in-memory only.** It does not persist across app restart or reboot, which follows
from the same rule: a reboot is the link dropping and returning. Disconnecting before bed leaves
the app connected again in the morning.

### Retry and resume

- **Backoff** — 2s, 4s, 8s, 16s, 30s, then every 60s. Reset on success.
- **Resume** — `SystemEvents.PowerModeChanged` with `PowerModes.Resume` does not reconcile
  immediately. The Bluetooth stack is not back yet and an instant attempt only burns the first
  backoff step. Wait 5s, then force a reconcile.

### Reconcile loop

Every 30 seconds, compare desired state against observed reality: is the connection object alive,
is the A2DP SNK capture endpoint actually present, is the router running, is the transport
registered. Correct any drift.

This is the backstop that makes the design robust rather than merely event-driven. WinRT device
events are unreliable across sleep/resume, and under a purely edge-triggered design a single
dropped event leaves the app idle believing it is connected, with nothing to correct it — the
predecessor's dormant-forever bug, reproduced. Level-triggered reconciliation turns that into a
30-second annoyance.

Rejected alternatives: pure event-driven (leaner, no idle wakeups, but every event source becomes
load-bearing and the failure mode is silent permanent death); pure polling (simplest and
self-healing, but 30s of latency after unlocking the phone, and cutting the interval to fix that
means enumerating WinRT devices every second or two all day).

## Staging

The stages exist because designing reconnect hardening on top of a connect path that has never once
succeeded is building on sand.

### Stage 0 — instrument, then validate

Smallest diff that makes a first-run failure diagnosable.

1. **`Log`** — hand-rolled rolling file at `%LOCALAPPDATA%\Klangbruecke\logs\`, one file per day,
   7-day retention. No NuGet dependency. Logs every state transition, every WinRT call and its
   result, and every device enumeration. That path is correct as written for the installed build as
   well as `dotnet run`, because the manifest opts out of Desktop Bridge write virtualization —
   which also means both builds append to the *same* file, so read the startup
   `Base directory:` line before attributing anything in it. See `FINDINGS.md` §9.
2. **UI thread marshalling** — capture the `SynchronizationContext` in the `TrayContext`
   constructor and post through it. Today `SetStatus` writes `_icon.Text` directly while being
   invoked from the WinRT threadpool (`AudioPlaybackConnection.StateChanged`) and from NAudio's
   `RecordingStopped`. This surfaces as an intermittent `InvalidOperationException`, not a clean
   failure.
3. **Format logging — NOT a resampler.** This item originally called for inserting a
   `MediaFoundationResampler` when the capture format does not match the output's mix format, on
   the premise that `WasapiOut.Init` throws on a shared-mode mismatch and the failure presents as
   silence. **That premise is false and the resampler is actively harmful.** Verified against
   decompiled NAudio 2.2.1:

   - `WasapiOut.Init`'s entire `IsFormatSupported` / `ResamplerDmoStream` / `GetFallbackFormat`
     block is inside `if (shareMode == AudioClientShareMode.Exclusive)`. `AudioRouter` uses
     `Shared`, so none of it runs.
   - In shared mode `Init` passes `AudioClientStreamFlags.SrcDefaultQuality | AutoConvertPcm` to
     `AudioClient.Initialize`. **WASAPI's own sample-rate converter already handles the mismatch.**
   - `MediaFoundationTransform` allocates `sourceBuffer = new byte[AverageBytesPerSecond]` and
     always requests that full second, ignoring the caller's `count`. Against a
     `BufferedWaveProvider` capped at 500 ms, `ReadFully` zero-fills the shortfall while
     `DiscardOnBufferOverflow` destroys the overflow — a permanent 1 Hz cycle of 500 ms audio and
     500 ms silence, with half the source discarded. Both sizes are fixed at construction; no
     steady state resolves it.

   What this item delivers instead: log the capture and render format pair unconditionally at
   `Start`, and subscribe to `WasapiOut.PlaybackStopped` — nothing did, so play-thread failures
   were invisible while the tray still read "Routing X -> Y". The format pair is the first thing
   anyone needs when diagnosing Stage 0's validation run. The live pair on this machine is
   44100 Hz / 32-bit / 2 ch capture against 48000 Hz render endpoints.

   If explicit resampling is ever genuinely needed in this topology,
   `WdlResamplingSampleProvider` (NAudio.Core, no new package) reads caller-sized chunks and is
   the real-time-safe choice. `MediaFoundationResampler` is a file-transcoding component and
   would require `BufferDuration` above ~2 s, which is fatal latency for the calls half.
4. **Transport correlation** — `TrayContext.cs:175` takes `transports.FirstOrDefault()` and never
   correlates the transport to the phone the user selected. With one phone paired this works by
   accident. Correlate on the Bluetooth address embedded in the device ID.
5. **Unpackaged fallback** — detect package identity at startup. Without it, skip the calls half
   and log the reason; music is unaffected. This buys `dotnet run` as the inner loop instead of a
   full MSIX install cycle for every music-side change.

Then package, install, and run the two checks below.

**Open question Stage 0 answers:** whether `AudioPlaybackConnection` works without package
identity. It is not a restricted capability, so it probably does, but WinRT is inconsistent here.
If it turns out to require identity, the fast dev loop disappears and every iteration costs an
install — worth knowing before Stage 1 rather than during it.

### Stage 1 — connection lifecycle

Build `ConnectionManager` and the state machine described above, informed by what Stage 0 actually
observed. Move orchestration out of `TrayContext`. Make `Settings.AutoReconnect` live.

### Stage 2 — reconnect matrix

Verify the transitions that define the project.

## Testing

The two halves are independent and are tested independently, per `CLAUDE.md`.

**Music** — connect, then confirm the endpoint exists and the phone offers the PC:

```powershell
Get-PnpDevice -Class AudioEndpoint | Where-Object { $_.FriendlyName -match 'SNK|A2DP' }
```

Expect `Line (Pixel 9 A2DP SNK)`. Then confirm the PC appears in the phone's output picker and
audio reaches the selected output device.

**Calls** — place a real cellular call. Verify audio in **both** directions; the mic half fails
silently otherwise.

**Never trust the tray indicator.** Verify against `Get-PnpDevice` (`FINDINGS.md` §4).

**When something does not connect**, check the pairing before suspecting the code. The stale-IRK
bug (`FINDINGS.md` §3) presents exactly like an API failure. Look at `BTHUSB` events 35 / 16 / 24
in the System log first.

### Unit tests

The state machine is the only part with logic worth unit-testing, and the only part that must not
need a phone. Table-driven tests over (state, event) → (next state, action), driving
`ConnectionManager` through fakes for the three service interfaces.

Everything else — WinRT, WASAPI — is verified by hand against the OS. There is no test project
today; Stage 0 adds an xunit project, because Stage 0 is itself written test-first and has nowhere
else to put its tests.

### Stage 2 matrix

Each row verified by hand, with the log as evidence:

| Scenario | Expected |
|---|---|
| Reboot PC, phone in range | Auto-connects without interaction |
| App restart while connected | Reconnects |
| Walk out of range, return | `Discovering`, then reconnects on return |
| PC sleep, resume | Reconnects within ~35s of resume |
| Disconnect on the phone | `Suppressed`; no reconnect while phone stays in range |
| Suppressed, then leave and return | Auto-connect resumes |
| Tray Disconnect click | `Suppressed`; same re-arm rule |
| Bluetooth off on phone, back on | Reconnects |
| Phone off entirely, back on | Reconnects |
| Connect with phone absent at launch | `Discovering`, backs off, connects when phone appears |

## Error handling

- **Connect failures** are expected, not exceptional — the phone is often simply absent. They log
  at info and feed the backoff; they do not surface a dialog.
- **`Settings` load/save** already swallows IO and JSON exceptions deliberately. Keep that, but log
  the swallowed exception instead of discarding it.
- **`AudioRouter` failures** are logged with the capture and render format pair, which is the
  diagnosis when audio is wrong. A shared-mode format mismatch does not fail — WASAPI converts it
  — so the pair is logged unconditionally rather than only on error. Play-thread failures arrive
  via `PlaybackStopped` and clear `IsRunning`; without that subscription they are silent and the
  tray keeps claiming it is routing.
- **Missing capability or package identity** logs a clear single line naming what is disabled and
  why, rather than failing opaquely.

## Files

New:

```
src/Klangbruecke/Connection/ConnectionManager.cs   (Stage 1)
src/Klangbruecke/Connection/ConnectionState.cs     (Stage 1)
src/Klangbruecke/Diagnostics/Log.cs                (Stage 0)
src/Klangbruecke/Diagnostics/FileLog.cs            (Stage 0)
src/Klangbruecke/Audio/AudioFormatBridge.cs        (Stage 0)
src/Klangbruecke/Bluetooth/BluetoothDeviceId.cs    (Stage 0)
src/Klangbruecke/Platform/PackageIdentity.cs       (Stage 0)
src/Klangbruecke/Platform/CallsPolicy.cs           (Stage 0)
src/Klangbruecke/UiDispatcher.cs                   (Stage 0)
tests/Klangbruecke.Tests/                          (Stage 0)
```

Modified: `TrayContext.cs` (orchestration out, view only), `AudioSinkService.cs` (DeviceWatcher,
real state), `CallTransportService.cs` (correlation), `AudioRouter.cs` (format logging and
`PlaybackStopped` — **not** a resampler; see item 3 above), `Settings.cs` (log swallowed
exceptions).

`TrayContext.cs` is 228 lines and is currently the only orchestrator; moving connection logic out
is what keeps it reviewable, not incidental cleanup.

---

## What Stage 0 learned that Stage 1 must honour

Stage 0 shipped (153 tests, branch `stage-0-instrumentation`). These are the constraints it
discovered empirically. They are not in the git history in this form, and several contradict what
this document originally assumed.

### Hard facts, verified on this machine

- **`AudioPlaybackConnection.TryCreateFromId` terminates an unpackaged process** with an
  `AccessViolationException` inside the CsWinRT ABI shim — a corrupted-state exception no managed
  handler can catch or log. `docs/FINDINGS.md` §8. `AudioSinkPolicy` gates it in two places; **do
  not remove either "to let it try"**. There is no unpackaged dev loop for the music half.
- **Discovery works unpackaged**: `GetDeviceSelector()`, `FindAllAsync`, `PhoneLineTransportDevice.FromId`
  and `IsRegistered()` all succeed without identity. Only registration is expected to need it.
- **Correlation works on real hardware.** Both selectors return `BTHENUM` interfaces carrying the
  same address; the app's log shows `AddressMatch`, not the single-candidate fallback. The
  phone-line selector filters on `DeviceInstanceId` containing the HFP-AG service UUID, which
  structurally guarantees the address is present.
- Both devices also share a `System.Devices.ContainerId`, which links them to the MMDEVAPI
  endpoints too — a stronger correlation key than the address, **but it is regenerated on
  unpair/re-pair**, so never persist it as the phone's identity.
- `DeviceInformation.Pairing.IsPaired` reads `false` unless `System.Devices.Aep.IsPaired` was
  requested in `extraProps`. Any bare `FindAllAsync` result gives a false negative.
- The MSIX disables Desktop Bridge write virtualization, which needs the `unvirtualizedResources`
  restricted capability — **not encoded in the XSD**, so schema validation gives a false pass
  (`FINDINGS.md` §9). Consequence: packaged and unpackaged runs share one log file, and uninstall
  no longer removes logs or settings.

### The largest risk Stage 1 inherits

`AudioRouter` constructs `WasapiCapture`/`WasapiOut` inline with no seam, so **none of its
behaviour is testable**. Five properties there are load-bearing and defended by comments alone —
reverting any one leaves all 153 tests green:

1. `_session` published before `StartRecording`/`Play`
2. `EndSession()` called before `Report()` in both stopped handlers
3. `_session = null` before the unsubscribes in `Stop()`
4. the stale-session check inside the posted teardown lambda
5. the `ReferenceEquals(sender, …)` guards

**Two real defects were found there by live hardware probes after surviving three review rounds
each.** Treat "reviewed repeatedly" as weak evidence for anything behind that seam. Introducing the
`IAudioSinkService`/`ICallTransportService`/`IAudioRouter` interfaces this document already
specifies is what makes it guardable — that is the point of them, not tidiness.

Related: `NAudio`'s `WasapiOut` raises `PlaybackStopped` **on the play thread** when no
`SynchronizationContext` was captured, and `PlayThread` assigns `Stopped` only on its one clean
fall-through. So a handler that calls `Stop()`/`Dispose()` self-joins and deadlocks, and every
abnormal exit — including `audioClient.Stop()` throwing when the phone leaves range — lands in that
window. Teardown is posted through `IUiDispatcher` for this reason. **Any Stage 1 auto-reconnect
that calls `Start()` from a threadpool thread would have hit this**; production avoided it only by
field-initializer ordering.

### Smaller carry-forwards

- **Nothing restarts the route after teardown.** Audio stays down until the user re-picks. This is
  the retry gap `ConnectionManager` exists to close.
- `AudioRouter.Start()` returns `true` for a source that cannot capture — the capture thread dies
  asynchronously. Retry logic that trusts the return value will loop.
- `FileLog.Write` does synchronous file I/O under a lock. Harmless today because nothing logs from
  the audio hot path. **If the reconcile loop or a capture callback ever logs, it needs a queue and
  a single writer thread first**, or it becomes an audio dropout.
- `Application.ThreadException`'s add accessor **overwrites** rather than combines. A second
  subscriber silently drops the first.
- `StatusPresenter.Last` is written on the UI thread inside the post. Even `volatile` does not stop
  a cross-thread reader observing it a few instructions before the tooltip write.
- `Settings.EnableCalls` toggled off skips `_calls.Disconnect()`, so an existing registration
  survives the setting change until the next connect.
- "Route calls to PC" shows checked while calls do nothing unpackaged. Wants a UI answer, not
  another status line.
- `TrayContext` subscribes the three `Status` handlers before `_status` is assigned; safe only by
  current call ordering.

### Process note

Stage 0's plan embedded complete code for each task. Eight defects were found, **all eight in the
plan text, none in the implementations** — the plan got one self-review while the code got a
dedicated adversarial reviewer per task. Stage 1's plan should specify interfaces, test cases and
constraints, and let implementers write the bodies.
