# Probe: `IMMNotificationClient` as the endpoint-arrival event

**Date:** 2026-08-05
**Machine:** the development target — Windows 10 Pro 19045, .NET 8.0.23, NAudio 2.2.1, x64.
**Why:** Stage 1 wants an endpoint-*arrival* event instead of the one-shot look that misses
`Line (<phone> A2DP SNK)` in 5 of 8 recorded launches. `IMMNotificationClient` is the candidate;
the fallback is a 2 s poll of `AudioRouter.FindSinkCaptureEndpoint`. This probe exists to answer
that with measurements rather than with what the internet says about COM callbacks.

Everything below marked **Measured** came out of a throwaway console probe run on this machine on
2026-08-05. Everything marked **Not measured** is exactly that — no phone-side disconnect was
available, and nothing here is inferred and then written up as if it had been observed.

---

## The four answers

### 1. Which callbacks fire when the A2DP SNK endpoint appears and disappears?

**Not measured. Unresolved.** Do not treat any part of this section as an answer to the question
as asked.

What *was* measured, in a controlled 96-second window with the registration proven live on both
sides of the event:

| Time (probe clock) | What happened | Callbacks about the A2DP endpoint |
|---|---|---|
| +6 s | control: default capture endpoint flipped | — (5 `OnDefaultDeviceChanged`) |
| +30 s | control: flipped again | — (5 `OnDefaultDeviceChanged`) |
| +32 s | `Line (MYSTRAPIX9 A2DP SNK)` enumerated: **Active** | |
| +32 s | packaged Klangbruecke 0.1.0.2 launched, logged `A2DP sink connected`, routed audio | **none** |
| +55 s | control: flipped again | — (5 `OnDefaultDeviceChanged`) |
| +57 s | Klangbruecke killed (connection object destroyed with the process) | **none** |
| +75 s | `Line (MYSTRAPIX9 A2DP SNK)` enumerated: **Active** | |
| +79 s | control: flipped again | — (5 `OnDefaultDeviceChanged`) |

40 callbacks arrived in the window, all of them from the deliberate controls. **Zero** mentioned
the A2DP endpoint.

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

| Registering thread | Callback arrives on |
|---|---|
| MTA (`tid=4`) | `tid=5/6/7/8`, **MTA**, not a thread-pool thread, `IsBackground=true` |
| STA, no message pump (`tid=4`) | `tid=5/6/7`, **MTA**, not a thread-pool thread, `IsBackground=true` |
| STA, pumping the message queue (`tid=4`) | `tid=6/7`, **MTA**, not a thread-pool thread, `IsBackground=true` |

Consequences, all of them measured rather than assumed:

- **Callbacks never arrive on the registering thread.** They arrive on MMDevAPI's own worker
  threads. A WinForms tray app must marshal to the UI thread itself — `IUiDispatcher` is already
  the right tool.
- **No message pump is required.** An STA registrant that does nothing but `Thread.Sleep` still
  received every callback, on an MTA thread. So there is no dependency on the WinForms message loop
  being healthy. (The likely explanation — not measured, and not relied on — is that the CLR's CCW
  is context-agile, so MMDevAPI calls straight in instead of marshalling into the STA.)
- **Delivery is not serialised onto one thread.** Within a single run, callbacks came in on
  `tid=5`, `6`, `7` and `8`. Two callbacks can be in a handler concurrently. The handler must be
  thread-safe; it cannot rely on a single-threaded notification thread.
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
| Process exit after a clean unregister | exit code 0, no crash, no hang |

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

The mechanism is sound where it matters: registration succeeds, delivery is prompt (single-digit
milliseconds from cause to callback), it survives the enumerator being disposed or collected,
unregistration genuinely stops delivery, and it needs no message pump — so it works the same in the
tray app as in a console. A 2 s poll would add a wake-up every 2 seconds for the whole time music is
`Linked`, in an app whose reason for existing is to sit in the tray, and would still need every
piece of thread-safety work the event path needs.

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
3. **Verify the gap on the next real disconnect/reconnect.** Run the probe's `watch` mode across a
   genuine phone disconnect and reconnect and record which callback fires here. If none does, take
   the fallback.

Rules the implementation is pinned to, all from the measurements above:

- Hold a strong reference to the `IMMNotificationClient` for the whole registration, or the process
  dies with `0xC0000005`.
- Assume callbacks arrive concurrently on several non-UI MTA threads; marshal to the UI thread
  through `IUiDispatcher`.
- Assume duplicate notifications; make the handler idempotent.
- Do no real work and no MMDevice lookups on the notification thread.
- Catch everything inside the handler and log it; nothing else will.
- At shutdown: unregister exactly once, then dispose the enumerator, in that order.

---

## The probe

A throwaway console app (net8.0-windows10.0.19041.0, NAudio 2.2.1), in the session scratchpad and
not committed. Modes: `list`, `watch <mta|sta|sta-pump> <seconds>`, `lifetime`, `weakclient`,
`throwing`, `reentrant`. The excerpts below are verbatim; what is omitted around them is logging
plumbing and command-line dispatch.

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

The device-id suffix `[{0.0.1.00000000}.{03bb069a-...}]` is trimmed off the end of the callback
lines for width; nothing else is edited.

```
02:15:39.539 +  1,36s [tid=4   apt=MTA  pool=N bg=N] Register -> 0x00000000
02:15:39.539 +  1,36s [tid=4   apt=MTA  pool=N bg=N] TRIGGER (P1 A): setting default capture endpoint to 'Mikrofon (Steam Streaming Microphone)'
02:15:39.547 +  1,37s [tid=5   apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Console 'Mikrofon (Steam Streaming Microphone)'
02:15:39.547 +  1,37s [tid=5   apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Multimedia 'Mikrofon (Steam Streaming Microphone)'
02:15:39.557 +  1,38s [tid=5   apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Multimedia 'Mikrofon (Steam Streaming Microphone)'
02:15:39.557 +  1,38s [tid=5   apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Console 'Mikrofon (Steam Streaming Microphone)'
02:15:39.574 +  1,40s [tid=5   apt=MTA  pool=N bg=Y] CALLBACK P1.OnDefaultDeviceChanged: flow=Capture role=Communications 'Mikrofon (Steam Streaming Microphone)'
02:15:41.085 +  2,91s [tid=4   apt=MTA  pool=N bg=N]   verified default capture/Console is now 'Mikrofon (Steam Streaming Microphone)' -> trigger TOOK EFFECT
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

The A2DP window, with the app's own log interleaved (`watch mta 95` + packaged launch/kill):

```
probe  02:19:11.854 +  1,12s [tid=4 apt=MTA] RegisterEndpointNotificationCallback returned 0x00000000
probe  02:19:17.127 +  6,39s [tid=6 apt=MTA] CALLBACK watch.OnDefaultDeviceChanged  (control 1, x5)
probe  02:19:41.545 + 30,81s [tid=6 apt=MTA] CALLBACK watch.OnDefaultDeviceChanged  (control 2, x5)
list   02:19:42.920                          Active 'Line (MYSTRAPIX9 A2DP SNK)'   (separate process)
app    02:19:43.646 [INF] Opening A2DP sink connection to id=...\SNK
app    02:19:43.697 [INF] A2DP sink connected.
app    02:19:43.986 [INF] Routing 'Line (MYSTRAPIX9 A2DP SNK)' -> 'VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)'.
probe  02:20:05.952 + 55,22s [tid=6 apt=MTA] CALLBACK watch.OnDefaultDeviceChanged  (control 3, x5)
                                             <- app killed at 02:20:08
list   02:20:25.406                          Active 'Line (MYSTRAPIX9 A2DP SNK)'   (separate process)
probe  02:20:30.373 + 79,64s [tid=6 apt=MTA] CALLBACK watch.OnDefaultDeviceChanged  (control 4, x5)
probe  02:20:46.963 + 96,23s [tid=4 apt=MTA] Watch window over. 40 callback(s) seen.

grep 'CALLBACK.*(A2DP|SNK)' -> 0 matches
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
