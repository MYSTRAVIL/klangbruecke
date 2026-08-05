# Probe: `IMMNotificationClient` as the endpoint-arrival event

**Date:** 2026-08-05
**Machine:** the development target — Windows 10 Pro 19045, .NET 8.0.23, NAudio 2.2.1, x64.
**Why:** Stage 1 wants an endpoint-*arrival* event instead of the one-shot look that misses
`Line (<phone> A2DP SNK)` in 5 of 8 recorded launches
(`docs/superpowers/specs/2026-08-05-stage-1-connection-manager-design.md` §2).
`IMMNotificationClient` is the candidate;
the fallback is a 2 s poll of `AudioRouter.FindSinkCaptureEndpoint`. This probe exists to answer
that with measurements rather than with what the internet says about COM callbacks.

Everything below marked **Measured** came out of a throwaway console probe run on this machine on
2026-08-05. Everything marked **Not measured** is exactly that: the phone stayed connected for the
whole session and no disconnect was forced, so nothing about that path is inferred here and then
written up as if it had been observed.

"Task 13" below means the task that builds `EndpointMonitor` — numbered 13 in the Stage 1
execution brief and 15 in this probe's own brief. Same task.

---

## The four answers

### 1. Which callbacks fire when the A2DP SNK endpoint appears and disappears?

**Not measured. Unresolved.** Do not treat any part of this section as an answer to the question
as asked.

What *was* measured, in a controlled 96-second window with the registration proven live on both
sides of the event:

Each "control" below is a *pair* of default-endpoint flips — set the default to microphone B, then
restore all three roles about 3 s later — and each flip raises 5 `OnDefaultDeviceChanged`
callbacks, so each control accounts for 10.

| Time (probe clock) | What happened | Callbacks about the A2DP endpoint |
|---|---|---|
| +6 s | control 1: default capture endpoint flipped, then restored at +10 s | — (10 `OnDefaultDeviceChanged`) |
| +30 s | control 2: flipped, restored at +34 s | — (10 `OnDefaultDeviceChanged`) |
| +32 s | `Line (MYSTRAPIX9 A2DP SNK)` enumerated: **Active** | |
| +32 s | packaged Klangbruecke 0.1.0.2 launched, logged `A2DP sink connected`, routed audio | **none** |
| +55 s | control 3: flipped, restored at +59 s | — (10 `OnDefaultDeviceChanged`) |
| +57 s | Klangbruecke killed (connection object destroyed with the process) | **none** |
| +75 s | `Line (MYSTRAPIX9 A2DP SNK)` enumerated: **Active** | |
| +79 s | control 4: flipped, restored at +83 s | — (10 `OnDefaultDeviceChanged`) |

**The arithmetic, in full, because this section's whole argument is that every callback is
accounted for and none of it was the A2DP endpoint.** 4 controls × 2 flips × 5 = **40**, which is
exactly the probe's own tally (`Watch window over. 40 callback(s) seen.`) and exactly the 8 bursts
of 5 in the log:

```
02:19:17 -> 5   02:19:20 -> 5     (control 1: flip, restore)
02:19:41 -> 5   02:19:45 -> 5     (control 2)
02:20:05 -> 5   02:20:09 -> 5     (control 3)
02:20:30 -> 5   02:20:33 -> 5     (control 4)
```

Grouping those 40 callback lines by the device name each one carries accounts for all of them:

```
Mikrofon (beyerdynamic LL Adapter)              24 callbacks
VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)  16 callbacks
TOTAL                                           40
```

Those are the two endpoints the controls flip between. **No callback named any other device**, and
the strings `A2DP` and `SNK` do not occur anywhere in that log at all — 0 matches each, case
sensitive. The negative is a census of every callback, not a single pattern that could have been
silently broken.

The reason is not that the notification failed. It is that **the endpoint never changed**: it was
`Active` before the app opened its `AudioPlaybackConnection` and still `Active` after the app died.
So this run measured something else worth knowing, and it is the thing this table is really for:

> **Opening and closing an `AudioPlaybackConnection` does not by itself create or destroy the
> `Line (<phone> A2DP SNK)` capture endpoint.** On this machine the endpoint tracks the phone's
> Bluetooth A2DP link, which was up the whole time and is not the app's to control.

That is a caution for Stage 1 in its own right: an endpoint monitor that waits for an *arrival*
event will, in this state, wait forever for something that is already there. Whatever Task 13
builds must treat "already present at subscribe time" as a first-class case and report it
immediately, not only on the next transition.

**What is still unknown:** which of `OnDeviceAdded` / `OnDeviceRemoved` / `OnDeviceStateChanged`
fires when the Bluetooth link itself comes and goes. Answering it needs the phone to disconnect and
reconnect — or the PC's Bluetooth radio toggled off and on, which was deliberately not done: this
machine's paired state is the project's known-fragile area (`docs/FINDINGS.md` §3) and breaking it
unattended costs more than the answer is worth. **Confirm this on the next session where the phone
is disconnected and reconnected for real**, and record the result here.

### 2. What thread and apartment do they arrive on?

**Measured, and it is the same answer whether registration happens from an MTA or an STA thread.**

Thread ids below are the ones that appear in the saved logs, counted with a case-sensitive match on
the logger's `CALLBACK <client>.` field. (A first pass at this used a case-insensitive `CALLBACK`,
which also matches `RegisterEndpointNotificationCallback returned …` and `40 callback(s) seen` —
main-thread lines — and so wrongly made `tid=4`, the registering thread, look like a callback
thread. The numbers here are from the corrected count.)

| Registering thread | Callback arrives on | Log |
|---|---|---|
| MTA (`tid=4`) | `tid=5`, `6` — **MTA**, `pool=N`, `bg=Y` | `lifetime-mta.log` (70 callbacks), `a2dp-cycle.log` (40), `throwing.log` (10), `reentrant.log` (10) |
| STA, no message pump (`tid=4`) | `tid=5`, `6`, `7` — **MTA**, `pool=N`, `bg=Y` | `lifetime-sta.log` (70 callbacks) |
| STA, pumping the message queue (`tid=4`) | `tid=6`, `7` — **MTA**, `pool=N`, `bg=Y` | `watch-stapump.log` (10 callbacks) |

Every callback line in every log reads `apt=MTA pool=N bg=Y`, without exception. The STA-with-pump
row is excerpted under "Raw output" below along with the other two.

Consequences:

- **Callbacks never arrive on the registering thread.** They arrive on MMDevAPI's own worker
  threads. A WinForms tray app must marshal to the UI thread itself — `IUiDispatcher` is already
  the right tool.
- **No message pump is required.** An STA registrant that does nothing but `Thread.Sleep` still
  received every callback, on an MTA thread. So there is no dependency on the WinForms message loop
  being healthy. (The likely explanation — not measured, and not relied on — is that the CLR's CCW
  is context-agile, so MMDevAPI calls straight in instead of marshalling into the STA.)
- **Delivery is not pinned to one thread** — measured. A single run delivers on more than one
  thread id (`5` and `6` in `lifetime-mta.log`; `5`, `6` and `7` in `lifetime-sta.log`). So a
  handler cannot cache thread affinity or assume it always runs on the same thread.
- **Whether two callbacks can be *in flight at once* was NOT measured, and the evidence leans
  against it.** Distinct thread ids over time do not establish overlap. The only run that holds a
  handler open long enough to test it — `reentrant`, where each handler blocks 152–282 ms in an
  inline lookup — shows strict serialization: the next callback starts 1 ms *after* the previous
  handler returned, every time (`02:17:00.606` enter → `.858` return → `.859` next enter). Two
  milliseconds across all logs carry two different callback thread ids, which at the log's 1 ms
  resolution is equally consistent with fast sequential delivery. **Build the handler thread-safe
  anyway** — that is a precaution against an unmeasured hypothesis, not a measured requirement, and
  it is cheap.
- **Callbacks are duplicated.** One trigger — `SetDefaultEndpoint` called once for each of the
  three roles — produced five `OnDefaultDeviceChanged` calls, `Console` and `Multimedia` twice each.
  Reproduced in every run. **The handler must be idempotent** — "the endpoint arrived" will be
  delivered more than once for one arrival.

### 3. Does the registration survive if the `MMDeviceEnumerator` goes out of scope?

**Measured: yes, twice over — but the *client* must be held, and that is the trap.**

| Case | Result |
|---|---|
| Enumerator local dropped, then full GC + finalizers (`WeakReference.IsAlive` = `False`, so it really was collected) | **10 callbacks still delivered.** Registration survives. |
| `MMDeviceEnumerator.Dispose()` called on the registering instance | **5 callbacks still delivered.** Registration survives. |
| A *different* `MMDeviceEnumerator` instance calls `UnregisterEndpointNotificationCallback(client)` | Returns `S_OK` and **the registration is gone** — 0 callbacks afterwards. |

Observably, then, the enumerator instance is a handle you may throw away, and the registration is
keyed on the client object rather than on the instance that registered it. (That the registration
lives in the audio service is the natural reading of those three results, not a separate
measurement.)

The client is the opposite, and this is the important one:

> **The COM registration does NOT root the managed `IMMNotificationClient`.** Registered and then
> left with only a `WeakReference` to it, the client object was collected by the very next GC
> (`IsAlive=False`), and the next notification killed the process:
> `Fatal error. Internal CLR error. (0x80131506)`, exit code `0xC0000005`. **Reproduced twice,
> identically.**

That is a silent tray-app death — the same class of failure as `docs/FINDINGS.md` §8, with no
managed handler and nothing in the log. Task 13 must keep a strong reference to the client for
exactly as long as it is registered, and the type that owns that field must itself be rooted.

### 4. Does anything throw or leak when `UnregisterEndpointNotificationCallback` runs at shutdown?

**Measured. The happy path is clean; three ways of getting it wrong all throw.**

| Call | Result |
|---|---|
| `Unregister` on the same enumerator, once, while registered | `S_OK`, no throw, **0 callbacks afterwards** — no leak, delivery genuinely stops |
| `Unregister` a second time for the same client | **throws** `COMException 0x80070490` "Element not found" |
| `Unregister` a client that was never registered | **throws** `COMException 0x80070490` |
| `Unregister` on an enumerator that was already `Dispose()`d | **throws** `NullReferenceException` (NAudio nulls its inner COM reference in `Dispose`) |
| Process exit after a clean unregister | exit code 0, no crash, no hang — re-confirmed at 02:36 (`lifetime-exitcheck.log`, `EXITCODE=0`) |

Rows 2 and 3 look contradictory and are not. Row 2 is a client that **is** registered, unregistered
a second time after the first unregister already removed it. Row 3 is a client that was **never**
registered at all. Both end up asking the audio service to remove a registration it does not hold,
which is why both give `0x80070490`. The contrast to keep in mind is with question 3's third row:
unregistering a *currently registered* client succeeds with `S_OK` even from an enumerator instance
that never registered it — the call is keyed on the client, not on the instance.

So shutdown order is fixed: **unregister first, dispose the enumerator second, and unregister
exactly once.** A `Dispose()` that is called twice, or a teardown path that runs after the
enumerator was disposed, will throw where nothing expects an exception.

---

## Two extra measurements Task 13 will want

**An exception escaping the callback does not kill the process.** A client that threw
`InvalidOperationException` out of `OnDefaultDeviceChanged` threw 10 times; the process stayed up,
kept receiving callbacks, and exited 0. `AppDomain.UnhandledException` never fired — the interop
layer swallows it and returns a failure HRESULT to MMDevAPI. Useful (a buggy handler will not take
the tray down) and dangerous in equal measure: **a handler that throws fails completely silently.**
Task 13 should catch and log inside the handler rather than rely on either hook.

**Calling back into the MMDevice API from inside the callback works, but it is slow.** A verbatim
copy of `AudioRouter.FindSinkCaptureEndpoint()` executed inline on the notification thread returned
`'Line (MYSTRAPIX9 A2DP SNK)'` every time, with no deadlock across 12 seconds of repeated
notifications — but each inline lookup took **152–282 ms**, blocking MMDevAPI's worker thread for
that long. Microsoft's documented advice not to block in these callbacks matches the numbers. Do
the lookup off the notification thread.

Also incidental, and worth knowing because a monitor is tempted to enumerate `DeviceState.All`:
**`MMDevice.FriendlyName` throws `COMException 0xE000020B` (`SPAPI_E_NO_SUCH_DEVINST`) on some
`NotPresent` endpoints.** `AudioRouter` is safe today because it filters to `DeviceState.Active`
before touching the name. Anything that widens that filter must wrap the name read.

---

## Decision

**Task 13 should build `EndpointMonitor` on `IMMNotificationClient`, not on the 2 s poll.**

The mechanism is sound where it matters: registration succeeds, delivery is prompt (8 ms from cause
to callback), it survives the enumerator being disposed or collected, unregistration genuinely stops
delivery, and it needs no message pump — so it works the same in the tray app as in a console. The
case against the poll is the wake-up: every 2 seconds for the whole time music is `Linked`, plus a
full `EnumerateAudioEndPoints` each time — measured at 152–282 ms of COM work per enumeration in the
reentrant run — in an app whose reason for existing is to sit in the tray and be forgotten.

To be fair to the fallback: it does *not* inherit most of the pinned rules below. Five of the six
are properties of the callback path (strong reference, MTA delivery, duplicates, no work on the
notification thread, unregister ordering) and simply do not arise for a timer. The poll's cost is
the wake-up, and its benefit is that it cannot miss an arrival it was never told about — which is
precisely the open question. That is why the fallback stays one class away rather than being argued
out of existence here.

The decision is made with one gap open, stated plainly: **it has not been observed that the A2DP
endpoint's own appearance raises a callback**, because the endpoint could not be made to appear or
disappear without the phone. What has been observed is that the notification path is live and
delivers for capture endpoints generally.

Because of that gap, the interface must be built so the fallback costs nothing:

1. **Keep `IAudioEndpointMonitor` exactly as planned.** If the phone-side test later shows no
   callback for the A2DP endpoint, swapping the implementation for the 2 s poll is a one-class
   change, as the brief already specifies.
2. **Check for the endpoint once at subscribe time, before waiting for any event.** This is
   required regardless: it is measured above that the endpoint can already be `Active` when the
   monitor starts, and it also makes the monitor correct even if the arrival callback never comes.
3. **Verify the gap on the next real disconnect/reconnect.** Rebuild the probe by copy-pasting the
   source at the end of this note, run `EndpointProbe watch mta 300` across a genuine phone
   disconnect and reconnect, and record which callback fires here. If none does, take the fallback.

Rules the implementation is pinned to. Five come from measurements above; the concurrency one is
flagged because it does not:

- Hold a strong reference to the `IMMNotificationClient` for the whole registration, or the process
  dies with `0xC0000005`.
- Callbacks arrive on non-UI MTA worker threads and not always the same one; marshal to the UI
  thread through `IUiDispatcher`. Make the handler thread-safe **as a precaution** — that two
  callbacks can overlap is a hypothesis this probe did not confirm, and the one run able to test it
  showed serialization.
- Assume duplicate notifications; make the handler idempotent.
- Do no real work and no MMDevice lookups on the notification thread.
- Catch everything inside the handler and log it; nothing else will.
- At shutdown: unregister exactly once, then dispose the enumerator, in that order.

---

## The probe

A throwaway console app (net8.0-windows10.0.19041.0, NAudio 2.2.1) that lived in the session
scratchpad — a temp directory that will not survive, which is why its **full source is reproduced at
the end of this note**: decision item 3 asks for it to be re-run, and a pointer to a deleted
directory is not a re-runnable probe. Modes: `list`, `watch <mta|sta|sta-pump> <seconds>`,
`lifetime`, `weakclient`, `throwing`, `reentrant`, `setdefault <role> <deviceId>`.

The excerpts immediately below are the parts that produced each answer; the complete file follows
later.

The callback client. Note the name lookup reads a dictionary filled *outside* the callback — the
probe never re-enters COM from a callback except in the one mode that exists to test that:

```csharp
internal sealed class ProbeClient : IMMNotificationClient
{
    private readonly string _name;
    private int _count;

    public ProbeClient(string name) => _name = name;

    public int Count => Volatile.Read(ref _count);

    private void Fire(string callback, string detail)
    {
        Interlocked.Increment(ref _count);
        Log.Line($"CALLBACK {_name}.{callback}: {detail}");
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        Fire("OnDeviceStateChanged", $"newState={newState} {Names.Describe(deviceId)}");

    public void OnDeviceAdded(string pwstrDeviceId) =>
        Fire("OnDeviceAdded", Names.Describe(pwstrDeviceId));

    public void OnDeviceRemoved(string deviceId) =>
        Fire("OnDeviceRemoved", Names.Describe(deviceId));

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) =>
        Fire("OnDefaultDeviceChanged", $"flow={flow} role={role} {Names.Describe(defaultDeviceId)}");

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) =>
        Fire("OnPropertyValueChanged", $"key={key.formatId}/{key.propertyId} {Names.Describe(pwstrDeviceId)}");
}
```

Every log line carries the thread identity, which is the whole of the answer to question 2:

```csharp
Console.WriteLine(
    $"{DateTime.Now:HH:mm:ss.fff} +{Clock.Elapsed.TotalSeconds,6:F2}s " +
    $"[tid={Environment.CurrentManagedThreadId,-3} " +
    $"apt={Thread.CurrentThread.GetApartmentState(),-11} " +
    $"pool={(Thread.CurrentThread.IsThreadPoolThread ? "Y" : "N")} " +
    $"bg={(Thread.CurrentThread.IsBackground ? "Y" : "N")}] {text}");
```

The client-lifetime phase — the one that kills the process:

```csharp
var enumerator = new MMDeviceEnumerator();
WeakReference weak = RegisterWeak(enumerator);

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
Log.Line($"After full GC, the client object IsAlive={weak.IsAlive} (no managed reference is held).");

Trigger("P5 after-GC", _idA);   // <- process dies here

// ...

[MethodImpl(MethodImplOptions.NoInlining)]   // so the local cannot stay rooted on the caller's frame
private static WeakReference RegisterWeak(MMDeviceEnumerator enumerator)
{
    var client = new ProbeClient("P5");
    Log.Line($"Register a client held only by a WeakReference -> 0x{enumerator.RegisterEndpointNotificationCallback(client):X8}");
    return new WeakReference(client);
}
```

### The trigger, and a trap in building one

There is no supported API to add or remove an audio endpoint, so the probe needed a different
observable event. It flips the **default capture endpoint** between two active microphones with the
undocumented `PolicyConfigClient` — capture rather than render so nothing on the machine becomes
audible, and the original defaults for all three roles are recorded first and put back afterwards.

The trap: **`IPolicyConfigVista` (`{568b9108-...}`), which most sample code on the internet uses,
returns `E_NOINTERFACE` on 19045.** The first probe run appeared to show "no callbacks at all"
purely because of this — the trigger had silently done nothing. The IID that works here is
`{f8679f50-850a-41cf-9c72-430f290290c8}`, whose vtable has one extra slot (`ResetDeviceFormat`)
before `SetDefaultEndpoint`:

```csharp
[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
private class PolicyConfigClient { }

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
private interface IPolicyConfig
{
    // GetMixFormat, GetDeviceFormat, ResetDeviceFormat, SetDeviceFormat, GetProcessingPeriod,
    // SetProcessingPeriod, GetShareMode, SetShareMode, GetPropertyValue, SetPropertyValue.
    // Never called; they only push SetDefaultEndpoint to the right vtable slot.
    void Slot0(); void Slot1(); void Slot2(); void Slot3(); void Slot4();
    void Slot5(); void Slot6(); void Slot7(); void Slot8(); void Slot9();

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
}
```

That episode is the reason every result above is stated with its positive control: after the flip,
the probe re-reads the default endpoint and prints `TOOK EFFECT` or `DID NOT TAKE EFFECT`, and the
long observation runs re-fire the control every 25 s so that "no callbacks" can be distinguished
from "the listener was dead".

---

## Raw output

Registration, delivery, and the threads — MTA registrant (`lifetime mta`):

**How these blocks are edited.** The logger (source at the end of this note) emits every line as
`HH:mm:ss.fff +N,NNs [tid=N apt=X pool=Y bg=Y] text`, unconditionally. In the blocks below:

- device-id suffixes like `[{0.0.1.00000000}.{03bb069a-…}]` are trimmed off the end;
- the logger's column padding (it pads `apt=` to 11 characters) is collapsed to single spaces;
- the `pool=` and `bg=` columns are dropped **only** where they are constant for the whole block —
  they read `pool=N bg=N` on main-thread lines and `pool=N bg=Y` on every callback line, without
  exception, and the first block below keeps them in full so the real format is visible;
- `[... x omitted ...]` marks lines removed from the middle of a run;
- `(xN)` marks N consecutive identical lines collapsed to one;
- anything in `( )` at the right-hand end of a line, and any `<-` marker, is my annotation, not
  probe output.

Nothing is reworded, reordered, or re-timed.

```
02:15:39.539 +  1,36s [tid=4   apt=MTA  pool=N bg=N] Register -> 0x00000000
02:15:39.539 +  1,36s [tid=4   apt=MTA  pool=N bg=N] TRIGGER (P1 A): setting default capture endpoint to 'Mikrofon (Steam Streaming Microphone)'
02:15:39.547 +  1,37s [tid=5   apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Console 'Mikrofon (Steam Streaming Microphone)'
02:15:39.547 +  1,37s [tid=5   apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Multimedia 'Mikrofon (Steam Streaming Microphone)'
02:15:39.557 +  1,38s [tid=5   apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Multimedia 'Mikrofon (Steam Streaming Microphone)'
02:15:39.557 +  1,38s [tid=5   apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Console 'Mikrofon (Steam Streaming Microphone)'
02:15:39.574 +  1,40s [tid=5   apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Communications 'Mikrofon (Steam Streaming Microphone)'
02:15:41.085 +  2,91s [tid=4   apt=MTA  pool=N bg=N]   verified default capture/Console is now 'Mikrofon (Steam Streaming Microphone)' -> trigger TOOK EFFECT
[... 7 lines omitted: TRIGGER (P1 B) flips back to 'Mikrofon (beyerdynamic LL Adapter)', 5 further
     callbacks on tid=5 at 02:15:41.089-.119, and its own TOOK EFFECT verification. That second
     flip is where the other 5 of the 10 counted below come from. ...]
02:15:42.626 +  4,45s [tid=4   apt=MTA  pool=N bg=N] RESULT P1: 10 callback(s) while the enumerator was held.
02:15:42.626 +  4,45s [tid=4   apt=MTA  pool=N bg=N] Unregister -> 0x00000000
02:15:44.174 +  6,00s [tid=4   apt=MTA  pool=N bg=N] RESULT P1: 0 callback(s) AFTER unregister (expect 0 if unregister works).
```

Cause to callback is 8 ms: the trigger call at `02:15:39.539`, the first callback at `02:15:39.547`.
The same run's remaining phases:

```
02:15:44.180 +  6,00s [tid=4  apt=MTA] Enumerator reference dropped; full GC + finalizers done. MMDeviceEnumerator IsAlive=False
02:15:47.266 +  9,09s [tid=4  apt=MTA] RESULT P2: 10 callback(s) after the registering enumerator was collected.
02:15:50.361 + 12,18s [tid=4  apt=MTA] RESULT P3: 5 callback(s) after Dispose().
02:15:50.361 + 12,18s [tid=4  apt=MTA] RESULT P3: Unregister on the DISPOSED enumerator THREW NullReferenceException
02:15:51.911 + 13,73s [tid=4  apt=MTA] RESULT P4: 0 callback(s) after the cross-instance unregister
02:15:51.911 + 13,73s [tid=4  apt=MTA] RESULT P4: unregistering on e1 after e2 already unregistered it THREW COMException 0x80070490: Element not found.
02:15:51.911 + 13,73s [tid=4  apt=MTA] Unregister #1 -> 0x00000000
02:15:51.911 + 13,73s [tid=4  apt=MTA] Unregister #2 THREW COMException: Element not found. (0x80070490)
02:15:53.459 + 15,28s [tid=4  apt=MTA] RESULT P6: 0 callback(s) after unregister (expect 0).
```

Same phases with an STA registrant and no message pump (`lifetime sta`) — the registrant is
`tid=4 apt=STA`, every callback is `apt=MTA`:

```
02:15:57.779 +  0,01s [tid=4  apt=STA  pool=N bg=N] Probe start. mode=lifetime apartment-arg=sta actual=STA
02:15:59.129 +  1,35s [tid=5  apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Console ...
02:16:00.673 +  2,89s [tid=6  apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Multimedia ...
02:16:02.221 +  4,44s [tid=4  apt=STA  pool=N bg=N] RESULT P1: 10 callback(s) while the enumerator was held.
02:16:03.777 +  6,00s [tid=4  apt=STA  pool=N bg=N] Enumerator reference dropped; full GC + finalizers done. MMDeviceEnumerator IsAlive=False
02:16:06.899 +  9,12s [tid=4  apt=STA  pool=N bg=N] RESULT P2: 10 callback(s) after the registering enumerator was collected.
02:16:09.985 + 12,21s [tid=4  apt=STA  pool=N bg=N] RESULT P3: 5 callback(s) after Dispose().
02:16:09.986 + 12,21s [tid=4  apt=STA  pool=N bg=N] RESULT P3: Unregister on the DISPOSED enumerator THREW NullReferenceException
02:16:09.986 + 12,21s [tid=4  apt=STA  pool=N bg=N] Unregister on e2 (never registered it) returned 0x00000000
02:16:11.532 + 13,75s [tid=4  apt=STA  pool=N bg=N] RESULT P4: 0 callback(s) after the cross-instance unregister
02:16:11.533 + 13,75s [tid=4  apt=STA  pool=N bg=N] RESULT P4: unregistering on e1 after e2 already unregistered it THREW COMException 0x80070490: Element not found.
02:16:11.533 + 13,75s [tid=4  apt=STA  pool=N bg=N] Unregister #1 -> 0x00000000
02:16:11.533 + 13,75s [tid=4  apt=STA  pool=N bg=N] Unregister #2 THREW COMException: Element not found. (0x80070490)
02:16:11.533 + 13,75s [tid=4  apt=STA  pool=N bg=N] Unregister of a never-registered client THREW COMException: Element not found. (0x80070490)
02:16:13.082 + 15,30s [tid=4  apt=STA  pool=N bg=N] RESULT P6: 0 callback(s) after unregister (expect 0).
```

The STA registrant that *does* pump its message queue — same answer, callbacks still on MTA worker
threads (`watch-stapump.log`; every callback line reads `pool=N bg=Y`):

```
02:16:17.404 +  0,01s [tid=4 apt=STA pool=N bg=N] Probe start. mode=watch apartment-arg=sta-pump actual=STA pid=31472 os=Microsoft Windows NT 10.0.19045.0 clr=8.0.23
02:16:18.487 +  1,08s [tid=4 apt=STA pool=N bg=N] RegisterEndpointNotificationCallback returned 0x00000000
02:16:18.487 +  1,08s [tid=4 apt=STA pool=N bg=N] Pumping the STA message queue.
02:16:23.758 +  6,35s [tid=6 apt=MTA pool=N bg=Y] CALLBACK watch.OnDefaultDeviceChanged: flow=Capture role=Console 'Mikrofon (beyerdynamic LL Adapter)'
[... 8 further callbacks, on tid=6 and tid=7 ...]
02:16:28.340 + 10,94s [tid=7 apt=MTA pool=N bg=Y] CALLBACK watch.OnDefaultDeviceChanged: flow=Capture role=Communications 'Mikrofon (beyerdynamic LL Adapter)'
02:16:38.542 + 21,14s [tid=4 apt=STA pool=N bg=N] Watch window over. 10 callback(s) seen.
02:16:38.542 + 21,14s [tid=4 apt=STA pool=N bg=N] UnregisterEndpointNotificationCallback returned 0x00000000
```

The weakly-held client, run twice, identical both times (`weakclient mta`):

```
02:16:43.359 +  1,37s [tid=4  apt=MTA  pool=N bg=N] Register a client held only by a WeakReference -> 0x00000000
02:16:43.359 +  1,37s [tid=4  apt=MTA  pool=N bg=N] After full GC, the client object IsAlive=False (no managed reference is held).
02:16:43.360 +  1,37s [tid=4  apt=MTA  pool=N bg=N] TRIGGER (P5 after-GC): setting default capture endpoint to 'Mikrofon (Steam Streaming Microphone)'
Fatal error. Internal CLR error. (0x80131506)

RUN weakclient-1 EXITCODE=-1073741819 (0xC0000005)
RUN weakclient-2 EXITCODE=-1073741819 (0xC0000005)
```

A handler that throws (`throwing mta`):

```
02:16:53.336 +  1,37s [tid=5  apt=MTA  pool=N bg=Y] CALLBACK throwing.OnDefaultDeviceChanged: about to throw InvalidOperationException
   (x10)
02:16:54.872 +  2,91s [tid=4  apt=MTA  pool=N bg=N] RESULT: the process is still alive after the handler threw.
02:16:55.728 +  3,76s [tid=4  apt=MTA  pool=N bg=N] Unregister -> 0x00000000
02:16:55.728 +  3,76s [tid=4  apt=MTA  pool=N bg=N] RESULT: 10 exception(s) thrown out of callbacks; process still running.
```

`AudioRouter.FindSinkCaptureEndpoint()` re-entered from inside the callback (`reentrant mta 12`):

```
02:17:00.606 +  1,35s [tid=6  apt=MTA  pool=N bg=Y] CALLBACK reentrant.OnDefaultDeviceChanged flow=Capture role=Console
02:17:00.606 +  1,35s [tid=6  apt=MTA  pool=N bg=Y]   reentrant: calling AudioRouter-style FindSinkCaptureEndpoint() inline...
02:17:00.858 +  1,61s [tid=6  apt=MTA  pool=N bg=Y]   reentrant: inline lookup returned 'Line (MYSTRAPIX9 A2DP SNK)'
02:17:00.859 +  1,61s [tid=6  apt=MTA  pool=N bg=Y] CALLBACK reentrant.OnDefaultDeviceChanged flow=Capture role=Multimedia
02:17:00.859 +  1,61s [tid=6  apt=MTA  pool=N bg=Y]   reentrant: calling AudioRouter-style FindSinkCaptureEndpoint() inline...
02:17:01.023 +  1,77s [tid=6  apt=MTA  pool=N bg=Y]   reentrant: inline lookup returned 'Line (MYSTRAPIX9 A2DP SNK)'
```

The A2DP window. **This one is a constructed timeline, not a single log** — it merges three
sources, each labelled in the left-hand column: `probe` = `a2dp-cycle.log`, `app` =
`%LOCALAPPDATA%\Klangbruecke\logs\klangbruecke-20260805.log`, `list` = a separate `EndpointProbe
list` process. The `pool=`/`bg=` columns are dropped, and each control's 10 callbacks (5 on the
flip, 5 on the restore ~3 s later) are collapsed to the first line of the first burst.

Three lines here are **not** backed by a saved file, and are marked so you do not go looking: the
two `list` lines (`02:19:42.920`, `02:20:25.406`) came from separate short-lived processes whose
output went to the console, and the kill marker (`02:20:08.261`) is the orchestrating shell's own
timestamp. Every other timestamp quoted anywhere in this note appears in one of the probe logs or
in the app log named above.

```
probe  02:19:11.854 +  1,12s [tid=4 apt=MTA] RegisterEndpointNotificationCallback returned 0x00000000
probe  02:19:17.127 +  6,39s [tid=6 apt=MTA] CALLBACK watch.OnDefaultDeviceChanged   (control 1: 5 here + 5 at 02:19:20)
probe  02:19:41.545 + 30,81s [tid=6 apt=MTA] CALLBACK watch.OnDefaultDeviceChanged   (control 2: 5 here + 5 at 02:19:45)
list   02:19:42.920                          Active 'Line (MYSTRAPIX9 A2DP SNK)'
app    02:19:43.646 [INF] Opening A2DP sink connection to id=...\SNK
app    02:19:43.697 [INF] A2DP sink connected.
app    02:19:43.986 [INF] Routing 'Line (MYSTRAPIX9 A2DP SNK)' -> 'VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)'.
probe  02:20:05.952 + 55,22s [tid=6 apt=MTA] CALLBACK watch.OnDefaultDeviceChanged   (control 3: 5 here + 5 at 02:20:09)
                                             <- Klangbruecke killed at 02:20:08.261
list   02:20:25.406                          Active 'Line (MYSTRAPIX9 A2DP SNK)'
probe  02:20:30.373 + 79,64s [tid=6 apt=MTA] CALLBACK watch.OnDefaultDeviceChanged   (control 4: 5 here + 5 at 02:20:33)
probe  02:20:46.963 + 96,23s [tid=4 apt=MTA] Watch window over. 40 callback(s) seen.
```

The negative was taken by censusing all 40 callback lines by device name, as set out under question
1 — not by one pattern match. For the record, the checks were PowerShell `Select-String`
(.NET regex, where `(A2DP|SNK)` really is an alternation, unlike a POSIX BRE `grep` where those
metacharacters would be literal and the check would silently pass against any input):

```
Select-String -Path a2dp-cycle.log -Pattern 'CALLBACK [A-Za-z0-9]+\.' -CaseSensitive   -> 40   (positive control)
Select-String -Path a2dp-cycle.log -Pattern 'A2DP' -CaseSensitive                      ->  0
Select-String -Path a2dp-cycle.log -Pattern 'SNK'  -CaseSensitive                      ->  0
Select-String -Path lifetime-mta.log -Pattern 'CALLBACK.*(Steam|beyerdynamic)'          -> 62   (alternation works)
```

---

## What this probe did to the machine, and what it put back

- Flipped the default **capture** endpoint between two microphones many times, and restored all
  three roles (`Console`, `Multimedia`, `Communications`) after every run, including after the two
  runs that crashed on purpose. Verified restored at the end:
  `Console` and `Multimedia` = `VoiceMeeter Output (VB-Audio VoiceMeeter VAIO)`,
  `Communications` = `Mikrofon (beyerdynamic LL Adapter)` — the values read before the first run.
- Launched and killed the installed package `Klangbruecke_0.1.0.2_x64__vwcm37s2b7kd8` twice.
- Did **not** touch the Bluetooth radio, the pairing, any device's enable/disable state, or the
  default *render* endpoint.

---

## Probe source

The probe itself is throwaway and was never committed as code — but it lived in a session-scoped
temp directory, and the decision above asks for it to be re-run against a real phone disconnect. So
the whole thing is reproduced here. Three files, copy-paste into an empty directory,
`dotnet run -c Release -- <mode>`. Nothing outside NAudio 2.2.1 is required.

This was verified rather than asserted: the two source blocks below were extracted back out of this
markdown file, diffed against the files that produced every measurement above (`Program.cs`,
832 lines — **identical**), then built and run from the extracted copy. That round-trip build
succeeded with 0 warnings and the resulting binary delivered callbacks normally
(`watch mta 16` at 02:42, 10 callbacks on `tid=6`/`tid=7`, controls verified `TOOK EFFECT`,
exit code 0).

The parts worth reading before re-running:

- **`PolicyConfig`** — the default-endpoint flip used as a synthetic notification, and the
  `IPolicyConfigVista` vs `IPolicyConfig` IID discovery. On Windows 10 19045, `{568b9108-…}` (the
  IID in most sample code) returns `E_NOINTERFACE`; `{f8679f50-…}` works and has one extra vtable
  slot. `SetDefaultEndpoint` returns `S_OK` for a normal, non-elevated user.
- **`Trigger`** — never trusts the flip. It re-reads the default afterwards and prints
  `TOOK EFFECT` / `DID NOT TAKE EFFECT`. Every claim in this note rests on that line.
- **`SetupToggleTargets` / `RestoreDefaults`** — record the machine's three capture-role defaults
  before touching anything and put them back after. `setdefault` mode exists so an orchestration
  script can restore them from outside even when a probe run dies mid-way, which two of them do on
  purpose.
- **`PhaseClientOnlyWeaklyHeld`** — the one that kills the process. It runs as its own mode, last,
  because a crash mid-suite skips both the remaining phases and the restore.

### `EndpointProbe.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>EndpointProbe</AssemblyName>
    <RootNamespace>EndpointProbe</RootNamespace>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.2.1" />
  </ItemGroup>

</Project>
```

### `Program.cs`

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace EndpointProbe;

internal static class Log
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Line(string text)
    {
        lock (Gate)
        {
            Console.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff} +{Clock.Elapsed.TotalSeconds,6:F2}s " +
                $"[tid={Environment.CurrentManagedThreadId,-3} " +
                $"apt={Thread.CurrentThread.GetApartmentState(),-11} " +
                $"pool={(Thread.CurrentThread.IsThreadPoolThread ? "Y" : "N")} " +
                $"bg={(Thread.CurrentThread.IsBackground ? "Y" : "N")}] {text}");
            Console.Out.Flush();
        }
    }

    public static void Head(string text)
    {
        lock (Gate)
        {
            Console.WriteLine();
            Console.WriteLine("=== " + text + " " + new string('=', Math.Max(0, 60 - text.Length)));
            Console.Out.Flush();
        }
    }
}

/// <summary>id -> friendly name, filled outside callbacks so no callback ever re-enters COM.</summary>
internal static class Names
{
    private static readonly ConcurrentDictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);

    public static void Refresh()
    {
        using var e = new MMDeviceEnumerator();
        foreach (MMDevice d in e.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
        {
            try { Map[d.ID] = d.FriendlyName; } catch { /* a vanishing device */ }
        }
    }

    public static string Describe(string? id)
    {
        if (id is null) return "(null id)";
        return Map.TryGetValue(id, out string? name) ? $"'{name}' [{id}]" : $"(unknown) [{id}]";
    }
}

internal sealed class ProbeClient : IMMNotificationClient
{
    private readonly string _name;
    private int _count;

    public ProbeClient(string name) => _name = name;

    public int Count => Volatile.Read(ref _count);

    private void Fire(string callback, string detail)
    {
        Interlocked.Increment(ref _count);
        Log.Line($"CALLBACK {_name}.{callback}: {detail}");
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        Fire("OnDeviceStateChanged", $"newState={newState} {Names.Describe(deviceId)}");

    public void OnDeviceAdded(string pwstrDeviceId) =>
        Fire("OnDeviceAdded", Names.Describe(pwstrDeviceId));

    public void OnDeviceRemoved(string deviceId) =>
        Fire("OnDeviceRemoved", Names.Describe(deviceId));

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) =>
        Fire("OnDefaultDeviceChanged", $"flow={flow} role={role} {Names.Describe(defaultDeviceId)}");

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) =>
        Fire("OnPropertyValueChanged", $"key={key.formatId}/{key.propertyId} {Names.Describe(pwstrDeviceId)}");
}

/// <summary>
/// A client that deliberately re-enters the MMDevice API from inside the callback, to find out
/// whether the endpoint can be looked up on the notification thread (what an EndpointMonitor
/// would want to do) or whether that deadlocks / throws.
/// </summary>
internal sealed class ReentrantClient : IMMNotificationClient
{
    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        Log.Line($"CALLBACK reentrant.OnDeviceStateChanged newState={newState} id={deviceId}");
        Lookup();
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
        Log.Line($"CALLBACK reentrant.OnDeviceAdded id={pwstrDeviceId}");
        Lookup();
    }

    public void OnDeviceRemoved(string deviceId)
    {
        Log.Line($"CALLBACK reentrant.OnDeviceRemoved id={deviceId}");
        Lookup();
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        Log.Line($"CALLBACK reentrant.OnDefaultDeviceChanged flow={flow} role={role}");
        Lookup();
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        Log.Line($"CALLBACK reentrant.OnPropertyValueChanged key={key.propertyId}");
    }

    private static void Lookup()
    {
        try
        {
            Log.Line("  reentrant: calling AudioRouter-style FindSinkCaptureEndpoint() inline...");
            using var e = new MMDeviceEnumerator();
            MMDevice? hit = e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains("A2DP", StringComparison.OrdinalIgnoreCase)
                                  || d.FriendlyName.Contains("SNK", StringComparison.OrdinalIgnoreCase));
            Log.Line($"  reentrant: inline lookup returned {(hit is null ? "null" : "'" + hit.FriendlyName + "'")}");
        }
        catch (Exception ex)
        {
            Log.Line($"  reentrant: inline lookup THREW {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>Lets an exception escape the callback, to see what the process does about it.</summary>
internal sealed class ThrowingClient : IMMNotificationClient
{
    private int _thrown;

    public int Thrown => Volatile.Read(ref _thrown);

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Boom("OnDeviceStateChanged");

    public void OnDeviceAdded(string pwstrDeviceId) => Boom("OnDeviceAdded");

    public void OnDeviceRemoved(string deviceId) => Boom("OnDeviceRemoved");

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Boom("OnDefaultDeviceChanged");

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => Boom("OnPropertyValueChanged");

    private void Boom(string callback)
    {
        Interlocked.Increment(ref _thrown);
        Log.Line($"CALLBACK throwing.{callback}: about to throw InvalidOperationException");
        throw new InvalidOperationException("deliberate probe failure inside the callback");
    }
}

internal static class PolicyConfig
{
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient
    {
    }

    [ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        // Vtable placeholders: GetMixFormat, GetDeviceFormat, SetDeviceFormat, GetProcessingPeriod,
        // SetProcessingPeriod, GetShareMode, SetShareMode, GetPropertyValue, SetPropertyValue.
        // Never called; they only push SetDefaultEndpoint to the right slot.
        void Slot0();
        void Slot1();
        void Slot2();
        void Slot3();
        void Slot4();
        void Slot5();
        void Slot6();
        void Slot7();
        void Slot8();

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }

    // Windows 10 19045 answers this IID, not the "Vista" one (measured: QueryInterface for
    // {568B9108-...} returns E_NOINTERFACE). One extra vtable slot: ResetDeviceFormat sits
    // between GetDeviceFormat and SetDeviceFormat.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void Slot0();
        void Slot1();
        void Slot2();
        void Slot3();
        void Slot4();
        void Slot5();
        void Slot6();
        void Slot7();
        void Slot8();
        void Slot9();

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }

    /// <summary>Sets the default endpoint for one role. Returns the HRESULT.</summary>
    public static int SetDefaultEndpoint(string deviceId, Role role)
    {
        object? raw = null;
        try
        {
            raw = new PolicyConfigClient();

            if (raw is IPolicyConfig modern)
            {
                return modern.SetDefaultEndpoint(deviceId, (int)role);
            }

            if (raw is IPolicyConfigVista vista)
            {
                return vista.SetDefaultEndpoint(deviceId, (int)role);
            }

            Log.Line("  PolicyConfigClient supports neither IPolicyConfig nor IPolicyConfigVista.");
            return -1;
        }
        catch (Exception ex)
        {
            Log.Line($"  SetDefaultEndpoint threw {ex.GetType().Name}: {ex.Message}");
            return -1;
        }
        finally
        {
            if (raw is not null)
            {
                Marshal.ReleaseComObject(raw);
            }
        }
    }
}

internal static class Program
{
    private const uint PM_REMOVE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "list";
        string apartment = args.Length > 1 ? args[1] : "mta";

        int exit = 0;
        var thread = new Thread(() => exit = Run(mode, apartment, args), 1024 * 1024);
        thread.SetApartmentState(apartment.StartsWith("sta", StringComparison.OrdinalIgnoreCase)
            ? ApartmentState.STA
            : ApartmentState.MTA);
        thread.Start();
        thread.Join();

        Log.Line($"Main returning {exit}.");
        return exit;
    }

    private static int Run(string mode, string apartment, string[] args)
    {
        Log.Line($"Probe start. mode={mode} apartment-arg={apartment} " +
                 $"actual={Thread.CurrentThread.GetApartmentState()} pid={Environment.ProcessId} " +
                 $"os={Environment.OSVersion.VersionString} clr={Environment.Version}");
        Names.Refresh();

        switch (mode)
        {
            case "list":
                ListEndpoints();
                return 0;
            case "watch":
                return Watch(apartment, args.Length > 2 ? int.Parse(args[2]) : 60);
            case "lifetime":
                return Lifetime();
            case "reentrant":
                return Reentrant(args.Length > 2 ? int.Parse(args[2]) : 30);
            case "throwing":
                return Throwing();
            case "weakclient":
                // Phase 5 alone: it can kill the process, so it must not be able to skip anything.
                return PhaseClientOnlyWeaklyHeld() ? 0 : 3;
            case "setdefault":
                // setdefault <roleIndex> <deviceId> - used by the orchestration script to put the
                // machine back exactly as it was, even after a probe run dies.
                int role = int.Parse(args[2]);
                Log.Line($"SetDefaultEndpoint(role={(Role)role}, {args[3]}) -> " +
                         $"0x{PolicyConfig.SetDefaultEndpoint(args[3], (Role)role):X8}");
                return 0;
            default:
                Log.Line("Unknown mode. Use: list | watch <mta|sta|sta-pump> <seconds> | lifetime | reentrant <seconds> | throwing");
                return 2;
        }
    }

    private static void ListEndpoints()
    {
        using var e = new MMDeviceEnumerator();
        foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            Log.Head($"{flow} endpoints (all states)");
            foreach (MMDevice d in e.EnumerateAudioEndPoints(flow, DeviceState.All))
            {
                string name;
                try
                {
                    name = "'" + d.FriendlyName + "'";
                }
                catch (Exception ex)
                {
                    // Measured: reading FriendlyName on some non-Active endpoints throws
                    // COMException 0xE000020B (SPAPI_E_NO_SUCH_DEVINST).
                    name = $"(FriendlyName threw {ex.GetType().Name} 0x{ex.HResult:X8})";
                }

                Log.Line($"  {d.State,-10} {name} [{d.ID}]");
            }
        }

        foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                string id = e.HasDefaultAudioEndpoint(flow, role)
                    ? e.GetDefaultAudioEndpoint(flow, role).ID
                    : "(none)";
                Log.Line($"default {flow}/{role}: {Names.Describe(id)}");
            }
        }
    }

    // ---------------------------------------------------------------- watch

    private static int Watch(string apartment, int seconds)
    {
        Log.Head($"WATCH for {seconds}s ({apartment})");
        var enumerator = new MMDeviceEnumerator();
        var client = new ProbeClient("watch");
        int hr = enumerator.RegisterEndpointNotificationCallback(client);
        Log.Line($"RegisterEndpointNotificationCallback returned 0x{hr:X8}");

        // A deterministic self-trigger at +5s, restored at +12s, so that every watch run proves the
        // registration was actually live during the window - separate from whatever the machine
        // does on its own (an app opening the A2DP connection, the phone dropping, ...).
        DateTime selfTriggerUntil = DateTime.UtcNow.AddSeconds(seconds - 8);
        var selfTrigger = new Thread(() =>
        {
            Thread.Sleep(5000);
            if (!SetupToggleTargets())
            {
                return;
            }

            // Repeated, not once: a run that watches for an externally caused endpoint change needs
            // a positive control on BOTH sides of it, or "no callbacks" cannot be told apart from
            // "the listener was dead".
            while (DateTime.UtcNow < selfTriggerUntil)
            {
                Trigger("watch self-trigger", _idB);
                Thread.Sleep(2000);
                RestoreDefaults();
                Thread.Sleep(20000);
            }
        })
        { IsBackground = true };
        selfTrigger.SetApartmentState(ApartmentState.MTA);
        selfTrigger.Start();

        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        bool pump = apartment.Equals("sta-pump", StringComparison.OrdinalIgnoreCase);
        Log.Line(pump ? "Pumping the STA message queue." : "NOT pumping any message queue; just sleeping.");

        var lastRefresh = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            if (pump && PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
                continue;
            }

            Thread.Sleep(50);

            // Keep the id -> name map current so newly added endpoints can be named, but never
            // from inside a callback.
            if (DateTime.UtcNow - lastRefresh > TimeSpan.FromSeconds(3))
            {
                lastRefresh = DateTime.UtcNow;
                Names.Refresh();
            }
        }

        Log.Line($"Watch window over. {client.Count} callback(s) seen.");
        int hr2 = enumerator.UnregisterEndpointNotificationCallback(client);
        Log.Line($"UnregisterEndpointNotificationCallback returned 0x{hr2:X8}");
        enumerator.Dispose();
        return 0;
    }

    // ------------------------------------------------------------- lifetime

    private static string? _origConsole;
    private static string? _origMultimedia;
    private static string? _origCommunications;
    private static string _idA = "";
    private static string _idB = "";

    /// <summary>
    /// Picks two active capture endpoints to flip the default between, and remembers the current
    /// defaults so they can be put back. Capture rather than render so nothing audible changes.
    /// </summary>
    private static bool SetupToggleTargets()
    {
        using var e = new MMDeviceEnumerator();
        List<MMDevice> capture = e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            // Never touch the endpoint under study.
            .Where(d => !d.FriendlyName.Contains("A2DP", StringComparison.OrdinalIgnoreCase)
                     && !d.FriendlyName.Contains("SNK", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (capture.Count < 2)
        {
            Log.Line("Need two active capture endpoints to toggle the default between. Aborting.");
            return false;
        }

        _origConsole = e.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            ? e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID : null;
        _origMultimedia = e.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
            ? e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID : null;
        _origCommunications = e.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            ? e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID : null;

        _idA = capture[0].ID;
        _idB = capture[1].ID;

        Log.Line($"Toggle target A: {Names.Describe(_idA)}");
        Log.Line($"Toggle target B: {Names.Describe(_idB)}");
        Log.Line($"Original capture defaults: Console={Names.Describe(_origConsole)} " +
                 $"Multimedia={Names.Describe(_origMultimedia)} Comms={Names.Describe(_origCommunications)}");
        return true;
    }

    private static int Lifetime()
    {
        if (!SetupToggleTargets())
        {
            return 3;
        }

        try
        {
            PhaseSanity();
            PhaseEnumeratorDropped();
            PhaseEnumeratorDisposed();
            PhaseCrossInstanceUnregister();
            // Phase 5 is deliberately NOT here: it kills the process, which would skip both the
            // phase below it and the restore in the finally. It runs as its own "weakclient" mode.
            PhaseUnregisterTwiceAndAfterDispose();
        }
        finally
        {
            RestoreDefaults();
        }

        Log.Head("Process is about to exit normally");
        return 0;
    }

    /// <summary>Flips the default capture endpoint, which is the trigger for every phase below.</summary>
    private static bool Trigger(string label, string toId)
    {
        Log.Line($"TRIGGER ({label}): setting default capture endpoint to {Names.Describe(toId)}");
        foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            int hr = PolicyConfig.SetDefaultEndpoint(toId, role);
            if (hr != 0)
            {
                Log.Line($"  SetDefaultEndpoint({role}) returned 0x{hr:X8}");
            }
        }

        Thread.Sleep(1500);

        using var e = new MMDeviceEnumerator();
        string now = e.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            ? e.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID : "(none)";
        bool ok = string.Equals(now, toId, StringComparison.OrdinalIgnoreCase);
        Log.Line($"  verified default capture/Console is now {Names.Describe(now)} -> trigger {(ok ? "TOOK EFFECT" : "DID NOT TAKE EFFECT")}");
        return ok;
    }

    private static void RestoreDefaults()
    {
        Log.Head("Restoring the original default capture endpoints");
        if (_origConsole is not null) Log.Line($"  Console -> 0x{PolicyConfig.SetDefaultEndpoint(_origConsole, Role.Console):X8}");
        if (_origMultimedia is not null) Log.Line($"  Multimedia -> 0x{PolicyConfig.SetDefaultEndpoint(_origMultimedia, Role.Multimedia):X8}");
        if (_origCommunications is not null) Log.Line($"  Communications -> 0x{PolicyConfig.SetDefaultEndpoint(_origCommunications, Role.Communications):X8}");

        Thread.Sleep(800);
        using var e = new MMDeviceEnumerator();
        foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            string id = e.HasDefaultAudioEndpoint(DataFlow.Capture, role)
                ? e.GetDefaultAudioEndpoint(DataFlow.Capture, role).ID : "(none)";
            Log.Line($"  default capture/{role} is now {Names.Describe(id)}");
        }
    }

    private static void PhaseSanity()
    {
        Log.Head("PHASE 1: does registration deliver at all, and on which thread");
        var enumerator = new MMDeviceEnumerator();
        var client = new ProbeClient("P1");
        Log.Line($"Register -> 0x{enumerator.RegisterEndpointNotificationCallback(client):X8}");

        int before = client.Count;
        Trigger("P1 A", _idA);
        Trigger("P1 B", _idB);
        Log.Line($"RESULT P1: {client.Count - before} callback(s) while the enumerator was held.");

        Log.Line($"Unregister -> 0x{enumerator.UnregisterEndpointNotificationCallback(client):X8}");
        int after = client.Count;
        Trigger("P1 after-unregister", _idA);
        Log.Line($"RESULT P1: {client.Count - after} callback(s) AFTER unregister (expect 0 if unregister works).");
        enumerator.Dispose();
    }

    private static void PhaseEnumeratorDropped()
    {
        Log.Head("PHASE 2: registration survives the enumerator going out of scope + GC?");
        var client = new ProbeClient("P2");
        WeakReference weakEnumerator = RegisterAndDrop(client);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Log.Line($"Enumerator reference dropped; full GC + finalizers done. " +
                 $"MMDeviceEnumerator IsAlive={weakEnumerator.IsAlive} " +
                 "(False => the wrapper really was collected and its COM reference released).");

        int before = client.Count;
        Trigger("P2 after-GC", _idB);
        Trigger("P2 after-GC", _idA);
        Log.Line($"RESULT P2: {client.Count - before} callback(s) after the registering enumerator was collected.");
    }

    // Separate method so the enumerator local cannot stay rooted on the caller's frame.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RegisterAndDrop(ProbeClient client)
    {
        var enumerator = new MMDeviceEnumerator();
        Log.Line($"Register (enumerator local to this frame) -> 0x{enumerator.RegisterEndpointNotificationCallback(client):X8}");
        return new WeakReference(enumerator);
    }

    private static void PhaseEnumeratorDisposed()
    {
        Log.Head("PHASE 3: registration survives MMDeviceEnumerator.Dispose()?");
        var enumerator = new MMDeviceEnumerator();
        var client = new ProbeClient("P3");
        Log.Line($"Register -> 0x{enumerator.RegisterEndpointNotificationCallback(client):X8}");

        int before = client.Count;
        Trigger("P3 pre-dispose", _idB);
        Log.Line($"  {client.Count - before} callback(s) before Dispose.");

        enumerator.Dispose();
        Log.Line("Enumerator disposed.");

        int mid = client.Count;
        Trigger("P3 post-dispose", _idA);
        Log.Line($"RESULT P3: {client.Count - mid} callback(s) after Dispose().");

        try
        {
            int hr = enumerator.UnregisterEndpointNotificationCallback(client);
            Log.Line($"RESULT P3: Unregister on the DISPOSED enumerator returned 0x{hr:X8} (no throw).");
        }
        catch (Exception ex)
        {
            Log.Line($"RESULT P3: Unregister on the DISPOSED enumerator THREW {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void PhaseCrossInstanceUnregister()
    {
        Log.Head("PHASE 4: can a DIFFERENT enumerator instance unregister the callback?");
        var e1 = new MMDeviceEnumerator();
        var client = new ProbeClient("P4");
        Log.Line($"Register on e1 -> 0x{e1.RegisterEndpointNotificationCallback(client):X8}");

        var e2 = new MMDeviceEnumerator();
        int hr;
        try
        {
            hr = e2.UnregisterEndpointNotificationCallback(client);
            Log.Line($"Unregister on e2 (never registered it) returned 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Log.Line($"Unregister on e2 THREW {ex.GetType().Name}: {ex.Message}");
        }

        int before = client.Count;
        Trigger("P4", _idB);
        Log.Line($"RESULT P4: {client.Count - before} callback(s) after the cross-instance unregister " +
                 "(non-zero => registration is per-enumerator-instance, and e2's unregister was a no-op).");

        try
        {
            Log.Line($"Cleanup: unregister the same client on e1 -> 0x{e1.UnregisterEndpointNotificationCallback(client):X8}");
        }
        catch (Exception ex)
        {
            Log.Line($"RESULT P4: unregistering on e1 after e2 already unregistered it THREW " +
                     $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
        }

        e1.Dispose();
        e2.Dispose();
    }

    private static bool PhaseClientOnlyWeaklyHeld()
    {
        Log.Head("PHASE 5: does the COM registration keep the managed client alive across a GC?");
        if (!SetupToggleTargets())
        {
            return false;
        }

        var enumerator = new MMDeviceEnumerator();
        WeakReference weak = RegisterWeak(enumerator);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Log.Line($"After full GC, the client object IsAlive={weak.IsAlive} (no managed reference is held).");

        Trigger("P5 after-GC", _idA);
        var revived = weak.Target as ProbeClient;
        Log.Line($"RESULT P5: client alive={weak.IsAlive}, callbacks seen by it = " +
                 $"{(revived is null ? "(collected)" : revived.Count.ToString())}");

        // Cannot unregister a collected client; if it is alive, clean up properly.
        if (revived is not null)
        {
            try
            {
                Log.Line($"Cleanup unregister -> 0x{enumerator.UnregisterEndpointNotificationCallback(revived):X8}");
            }
            catch (Exception ex)
            {
                Log.Line($"Cleanup unregister THREW {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            }
        }

        enumerator.Dispose();
        RestoreDefaults();
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RegisterWeak(MMDeviceEnumerator enumerator)
    {
        var client = new ProbeClient("P5");
        Log.Line($"Register a client held only by a WeakReference -> 0x{enumerator.RegisterEndpointNotificationCallback(client):X8}");
        return new WeakReference(client);
    }

    private static void PhaseUnregisterTwiceAndAfterDispose()
    {
        Log.Head("PHASE 6: shutdown behaviour of UnregisterEndpointNotificationCallback");
        var enumerator = new MMDeviceEnumerator();
        var client = new ProbeClient("P6");
        Log.Line($"Register -> 0x{enumerator.RegisterEndpointNotificationCallback(client):X8}");

        try
        {
            Log.Line($"Unregister #1 -> 0x{enumerator.UnregisterEndpointNotificationCallback(client):X8}");
        }
        catch (Exception ex)
        {
            Log.Line($"Unregister #1 THREW {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            Log.Line($"Unregister #2 (double unregister) -> 0x{enumerator.UnregisterEndpointNotificationCallback(client):X8}");
        }
        catch (Exception ex)
        {
            Log.Line($"Unregister #2 THREW {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var never = new ProbeClient("never-registered");
            Log.Line($"Unregister a client that was never registered -> 0x{enumerator.UnregisterEndpointNotificationCallback(never):X8}");
        }
        catch (Exception ex)
        {
            Log.Line($"Unregister of a never-registered client THREW {ex.GetType().Name}: {ex.Message}");
        }

        int before = client.Count;
        Trigger("P6 after-unregister", _idB);
        Log.Line($"RESULT P6: {client.Count - before} callback(s) after unregister (expect 0).");
        enumerator.Dispose();
    }

    // ------------------------------------------------------------- throwing

    /// <summary>
    /// What happens to the process when a handler throws on the notification thread? An
    /// EndpointMonitor handler that lets an exception escape must not take the tray app down.
    /// Deliberately the last thing any run does, and it restores the defaults first, so a
    /// process death here cannot leave the machine's default capture endpoint changed.
    /// </summary>
    private static int Throwing()
    {
        Log.Head("THROWING: an exception escaping the callback");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Line($"AppDomain.UnhandledException saw {(e.ExceptionObject as Exception)?.GetType().Name}, " +
                     $"IsTerminating={e.IsTerminating}");

        if (!SetupToggleTargets())
        {
            return 3;
        }

        var enumerator = new MMDeviceEnumerator();
        var client = new ThrowingClient();
        Log.Line($"Register -> 0x{enumerator.RegisterEndpointNotificationCallback(client):X8}");

        try
        {
            Trigger("throwing", _idB);
            Log.Line("RESULT: the process is still alive after the handler threw.");
        }
        finally
        {
            RestoreDefaults();
            try
            {
                Log.Line($"Unregister -> 0x{enumerator.UnregisterEndpointNotificationCallback(client):X8}");
            }
            catch (Exception ex)
            {
                Log.Line($"Unregister THREW {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            }

            enumerator.Dispose();
        }

        Log.Line($"RESULT: {client.Thrown} exception(s) thrown out of callbacks; process still running.");
        return 0;
    }

    // ------------------------------------------------------------ reentrant

    private static int Reentrant(int seconds)
    {
        Log.Head($"REENTRANT lookup inside the callback, {seconds}s");
        Log.Line("A watchdog will hard-exit the process if a callback deadlocks.");

        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds + 25));
            Log.Line("WATCHDOG: probe did not finish in time - the callback most likely deadlocked. Killing.");
            Environment.Exit(99);
        })
        { IsBackground = true };
        watchdog.Start();

        var enumerator = new MMDeviceEnumerator();
        var client = new ReentrantClient();
        Log.Line($"Register -> 0x{enumerator.RegisterEndpointNotificationCallback(client):X8}");

        // Self-trigger with a default-device flip, then idle so an externally caused endpoint change
        // (app launch/exit) can also be caught.
        if (SetupToggleTargets())
        {
            Trigger("reentrant", _idB);
            RestoreDefaults();
        }

        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        Log.Line($"Unregister -> 0x{enumerator.UnregisterEndpointNotificationCallback(client):X8}");
        enumerator.Dispose();
        return 0;
    }
}
```

### `run-suite.ps1`

The orchestration script. The restore after every run is unconditional and by explicit device id,
so a run that dies cannot leave the machine's default capture endpoint changed.

```powershell
$ErrorActionPreference = 'Continue'
$s = "C:\Users\MYSTRA~1\AppData\Local\Temp\claude\c--Users-MYSTRAVIL-Documents-Programming-Projects-klangbruecke\7bd81eec-f68e-4ad8-a0c1-fc1c489caba3\scratchpad"
$exe = "$s\EndpointProbe\bin\Release\net8.0-windows10.0.19041.0\EndpointProbe.exe"

# The defaults this machine had before any probe ran.
$vm = "{0.0.1.00000000}.{5c619667-1059-4de0-9107-8766297156ff}"   # VoiceMeeter Output
$bd = "{0.0.1.00000000}.{135882a4-8a93-4b59-bfcd-1a35f4559b8c}"   # Mikrofon (beyerdynamic LL Adapter)

function Restore {
    & $exe setdefault mta 0 $vm | Out-Null
    & $exe setdefault mta 1 $vm | Out-Null
    & $exe setdefault mta 2 $bd | Out-Null
}

function Run([string]$name, [string[]]$probeArgs) {
    "==================== RUN $name : $($probeArgs -join ' ') ===================="
    & $exe @probeArgs *>&1 | Out-File -FilePath "$s\$name.log" -Encoding utf8
    $code = $LASTEXITCODE
    "RUN $name EXITCODE=$code (0x{0:X8})" -f $code
    Restore
}

Run "lifetime-mta"  @("lifetime", "mta")
Run "lifetime-sta"  @("lifetime", "sta")
Run "watch-stapump" @("watch", "sta-pump", "20")
Run "weakclient-1"  @("weakclient", "mta")
Run "weakclient-2"  @("weakclient", "mta")
Run "throwing"      @("throwing", "mta")
Run "reentrant"     @("reentrant", "mta", "12")

"==================== FINAL DEFAULTS ===================="
& $exe list | Select-String "default Capture"
```
