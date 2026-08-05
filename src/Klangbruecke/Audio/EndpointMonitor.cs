using Klangbruecke.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Klangbruecke.Audio;

/// <summary>
/// Which MMDevAPI notification arrived, as one value per <see cref="IMMNotificationClient"/> method.
///
/// Exists so the decision below - which callbacks can change what endpoints <i>exist</i> - is a value a
/// test can pass in, rather than five copies of a filter buried in five COM adapters that no test host
/// can raise. Keep it one-for-one with the interface; <c>EndpointMonitorTests</c> asserts the count so
/// a sixth callback in a future NAudio cannot be wired up silently.
/// </summary>
public enum EndpointNotification
{
    DeviceAdded,
    DeviceRemoved,
    DeviceStateChanged,
    DefaultDeviceChanged,
    PropertyValueChanged,
}

/// <summary>
/// Registering an <see cref="IMMNotificationClient"/> with the audio service, and giving it back.
///
/// The seam that keeps MMDevAPI out of <see cref="EndpointMonitor"/>, for the same reason
/// <see cref="IAudioDeviceFactory"/> keeps WASAPI out of <see cref="AudioRouter"/>: the registration
/// lives in the audio service, keyed on the client object, with no managed way to ask how many are
/// outstanding. Behind this interface, "Start registered once" and "Dispose unregistered exactly once,
/// before the enumerator went away" are counts on a double. In front of it they are unobservable, and
/// both of them are load-bearing - see the measurements on the implementation.
///
/// <b><see cref="IDisposable.Dispose"/> must be called after the unregister, never before.</b> Measured:
/// unregistering on an <c>MMDeviceEnumerator</c> that has already been disposed throws
/// <see cref="NullReferenceException"/>, because NAudio nulls its inner COM reference in
/// <c>Dispose</c>.
/// </summary>
public interface IEndpointNotificationRegistrar : IDisposable
{
    void Register(IMMNotificationClient client);

    /// <summary>
    /// Must be handed the same object <see cref="Register"/> was given, exactly once. Measured: a
    /// second unregister for the same client, and an unregister for a client that was never
    /// registered, both throw <c>COMException 0x80070490</c> "Element not found".
    /// </summary>
    void Unregister(IMMNotificationClient client);
}

/// <summary>
/// The A2DP sink capture endpoint's arrival and departure, from MMDevAPI's notification callbacks.
///
/// See <see cref="IAudioEndpointMonitor"/> for what this is for and why an edge-only design would not
/// do. This class is the <c>IMMNotificationClient</c> half of it; every claim below was measured on
/// this machine and is written up in <c>docs/probes/2026-08-05-endpoint-notification.md</c>.
///
/// <b>Nothing here marshals.</b> Callbacks arrive on MMDevAPI's own MTA worker threads - never the
/// registering thread, and not always the same one twice - and <see cref="EndpointsChanged"/> is
/// re-raised on that same thread. <c>ConnectionManager</c> posts every inbound event through
/// <c>IUiDispatcher</c> before touching state, which is what makes it single-threaded by contract.
/// Marshalling here as well would put a second hop in front of every notification and buy nothing -
/// the same instruction <c>LinkMonitor</c> and <c>PowerNotifier</c> arrive at by their own routes.
///
/// <b>Nothing here looks an endpoint up on the notification thread.</b> Measured: a verbatim copy of
/// the endpoint lookup, run inline from inside a callback, returned the right answer every time and
/// took <b>152-282 ms</b>, blocking one of MMDevAPI's worker threads for that long. The handler says
/// "something changed"; the consumer reads <see cref="SinkCaptureEndpointPresent"/> after it has
/// marshalled.
///
/// <b>Nothing here throws out of a callback.</b> Measured, and this is the dangerous half: an exception
/// escaping a callback does <i>not</i> kill the process - the interop layer swallows it and returns a
/// failure HRESULT - and <c>AppDomain.UnhandledException</c> never fires. So a handler that throws fails
/// completely silently, and catching plus logging here is the only way it is ever seen.
///
/// The single most dangerous thing in this file is <see cref="_client"/>. Read its comment before
/// touching it.
/// </summary>
public sealed class EndpointMonitor : IAudioEndpointMonitor
{
    private readonly IEndpointNotificationRegistrar _registrar;
    private readonly Func<bool> _probe;

    /// <summary>
    /// <b>The strong reference the process depends on.</b>
    ///
    /// Measured, twice, identically: the COM registration does <b>not</b> root the managed
    /// <see cref="IMMNotificationClient"/>. Registered and then left with only a
    /// <see cref="WeakReference"/> to it, the client was collected by the very next GC and the next
    /// notification killed the process - <c>Fatal error. Internal CLR error. (0x80131506)</c>, exit
    /// code <c>0xC0000005</c>. There is no managed handler for an access violation: no log line, no
    /// <c>AppDomain.UnhandledException</c>, nothing. It is the third uncatchable-crash trap in this
    /// project.
    ///
    /// So this field is not a convenience, and the type that owns it must itself be rooted for as long
    /// as the registration stands - <c>ConnectionManager</c> holds the monitor, and <c>TrayContext</c>
    /// holds that.
    ///
    /// It is deliberately <b>not</b> cleared in <see cref="Dispose"/>. Delivery genuinely stops at the
    /// unregister (measured: 0 callbacks afterwards), but no run ever held a callback open <i>across</i>
    /// an unregister, so whether one can already be in flight at that moment is unmeasured. Clearing the
    /// field would make the client collectable inside exactly that window, and the cost of being wrong
    /// is the crash above. One reference, released when the monitor itself is, is a cheap way not to
    /// find out.
    /// </summary>
    private EndpointNotificationClient? _client;

    // volatile, not locked, and for the same reason LinkMonitor's are: written by the thread that calls
    // Start/Dispose - the UI thread - and read on MMDevAPI's worker threads, which are neither. The
    // design forbids locks anywhere in this stage; volatile is what makes a write on one visible to the
    // other without one, and a torn read of a bool is impossible.
    //
    // Whether two callbacks can be *in flight at once* was not measured, and the one probe run able to
    // test it showed strict serialization. Building the handler thread-safe anyway is a precaution
    // against an unconfirmed hypothesis, not a measured requirement - and it is cheap here, because the
    // handler mutates nothing.
    private volatile bool _started;
    private volatile bool _disposed;

    /// <summary>The production monitor: MMDevAPI notifications, and the factory's own endpoint lookup.</summary>
    public EndpointMonitor()
        : this(new MmDeviceNotificationRegistrar(), WasapiDeviceFactory.IsSinkCaptureEndpointPresent)
    {
    }

    /// <param name="registrar">Where the notification client is registered.</param>
    /// <param name="probe">
    /// Answers <see cref="SinkCaptureEndpointPresent"/>. Production passes
    /// <see cref="WasapiDeviceFactory.IsSinkCaptureEndpointPresent"/> rather than a fourth copy of the
    /// "friendly name contains A2DP or SNK" rule - <see cref="AudioRouter"/> and the factory already
    /// agree on what "the endpoint" means, and a third definition that drifts is how this app would
    /// start lying again. It may throw; this class absorbs that.
    /// </param>
    public EndpointMonitor(IEndpointNotificationRegistrar registrar, Func<bool> probe)
    {
        _registrar = registrar;
        _probe = probe;
    }

    public event EventHandler? EndpointsChanged;

    /// <summary>
    /// A live lookup on every read, deliberately not cached.
    ///
    /// A cache would have to be refreshed from the notification handler, which is forbidden to do the
    /// lookup, so it could only ever be refreshed on a schedule - and a stale "absent" is precisely the
    /// state the app was stuck in for whole sessions. The cost is real (152-282 ms of COM per read,
    /// measured); read it on an edge or on the reconcile tick, never in a loop and never from a
    /// notification callback.
    ///
    /// Never throws, and answers <c>false</c> rather than a stale value once disposed.
    /// </summary>
    public bool SinkCaptureEndpointPresent
    {
        get
        {
            // False rather than the last live answer. Teardown races - the reconcile tick and the
            // tray's exit path are different threads - so a read can land after Dispose, and answering
            // "present" there sends a consumer off to build a route out of an endpoint nothing is
            // listening for any more.
            if (_disposed)
            {
                return false;
            }

            try
            {
                return _probe();
            }
            catch (Exception ex)
            {
                // Reachable: reading MMDevice.FriendlyName throws COMException 0xE000020B on some
                // endpoints, and the whole enumeration can fail while the audio service restarts. This
                // is read on the reconcile tick, and an escaping exception there kills the loop that is
                // the app's only backstop - the predecessor's defining bug, restored.
                Log.Warn($"Looking for the A2DP sink capture endpoint failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Register for notifications, then look once.
    ///
    /// The order is the whole point and it is not interchangeable. Looking first and registering second
    /// leaves a hole: an endpoint that arrives between the two is seen by neither - the look ran before
    /// it existed, and the registration was not yet in place to hear it arrive. Registering first makes
    /// the worst case a duplicate notification, which every consumer has to tolerate anyway because
    /// MMDevAPI duplicates by itself.
    ///
    /// Subscribe to <see cref="EndpointsChanged"/> before calling this - the already-present case is
    /// reported by raising it from in here.
    /// </summary>
    public void Start()
    {
        // Refused rather than tolerated, for the reason LinkMonitor.Watch and PowerNotifier.Start refuse
        // - and it bites hardest here. Dispose's idempotence guard returns early on a second call, so a
        // registration taken after the first Dispose would never be unregistered; the monitor would then
        // be dropped, the client with it, and the next notification into a collected client is the
        // 0xC0000005 above.
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
        {
            return;
        }

        _started = true;

        var client = new EndpointNotificationClient(this);

        // Assigned before the registration, not after. Between the two calls the audio service can
        // already be delivering, and the field is the only thing keeping the object alive.
        _client = client;

        _registrar.Register(client);

        bool present = SinkCaptureEndpointPresent;

        // The line that would have made finding #2 diagnosable from the log the first time round. A log
        // that records only what the router concluded cannot tell "the endpoint was not there yet" from
        // "it was there and nothing looked".
        Log.Info($"Watching for the A2DP sink capture endpoint. Present at subscribe time: {present}.");

        if (present)
        {
            // The measured case, not a defensive one: the endpoint tracks the phone's Bluetooth link
            // and was already Active before the app opened its connection. A monitor that waited for an
            // arrival edge here would wait forever for something that had already happened.
            Raise("the endpoint was already present at subscribe time");
        }
    }

    /// <summary>
    /// One MMDevAPI notification, filtered and forwarded.
    ///
    /// Public, and the seam this class is tested through - the real callbacks are raised by the audio
    /// service and a suite that runs in two seconds cannot arrange for an endpoint to appear. Same
    /// precedent as <c>LinkMonitor.OnCandidateAdded</c> and <c>PowerNotifier.OnPowerModeChanged</c>:
    /// call it only as a test, or from <see cref="EndpointNotificationClient"/>.
    ///
    /// Holds no state, so a duplicate costs nothing here and nothing is de-duplicated - see
    /// <see cref="IAudioEndpointMonitor"/>.
    /// </summary>
    /// <param name="deviceId">
    /// The endpoint id MMDevAPI named, logged and not otherwise used. Not filtered on: the endpoint this
    /// app wants may not exist yet, so there is no id to compare against, and that is the entire point.
    /// </param>
    public void OnEndpointNotification(EndpointNotification kind, string? deviceId)
    {
        // A callback can already be in flight when Dispose runs on the UI thread; no check-then-act here
        // could prevent that and this does not pretend to. What it does prevent is a late notification
        // driving a consumer that has already torn its route down.
        if (_disposed)
        {
            return;
        }

        if (!SignalsEndpointExistence(kind))
        {
            return;
        }

        // Work on the notification thread, and the only work done here. Weighed rather than assumed:
        // FileLog is thread-safe and never throws, and this fires only for the three device-lifecycle
        // callbacks - devices do not come and go often, unlike OnPropertyValueChanged, which is filtered
        // out above. It buys the one thing the probe could not measure without a phone: *which* callback
        // the A2DP endpoint's own arrival raises. Task 18's smoke test reads these lines to close that,
        // and this may be dropped to Debug afterwards.
        Log.Info($"Audio endpoint notification: {kind} id={deviceId ?? "(none)"}");

        Raise($"an audio endpoint notification ({kind})");
    }

    /// <summary>
    /// Can this callback change which endpoints <i>exist</i>?
    ///
    /// Pure and public so the decision can be asserted for every member of the enum. The three admitted
    /// are the ones MMDevAPI defines in terms of a device coming into or going out of existence. The two
    /// rejected cannot create or destroy one and are the loud ones: the probe's own controls produced
    /// <b>40</b> <c>OnDefaultDeviceChanged</c> callbacks from four default-endpoint flips, none about
    /// the A2DP endpoint, and forwarding those would make the consumer re-enumerate WASAPI at 152-282 ms
    /// a time every time somebody changed their default microphone.
    ///
    /// <b>This is not an answer to the probe's open question.</b> Which callback the A2DP endpoint's
    /// arrival actually raises needs a real phone disconnect and reconnect and is unmeasured; this
    /// admits every callback that can plausibly carry it rather than guessing one. If Task 18's smoke
    /// test finds it lands elsewhere, this method is the one place to change.
    /// </summary>
    public static bool SignalsEndpointExistence(EndpointNotification kind) =>
        kind is EndpointNotification.DeviceAdded
             or EndpointNotification.DeviceRemoved
             or EndpointNotification.DeviceStateChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_client is not null)
        {
            // Exactly once, and only for a client that was actually registered. Measured: unregistering
            // a client that was never registered, or unregistering the same one twice, both throw
            // COMException 0x80070490 - and this runs on the path to TrayContext.Dispose, where a throw
            // escapes Main and Windows answers with a WER dialog.
            //
            // _client is deliberately left set; see its comment.
            _registrar.Unregister(_client);
        }

        // Second, never first. Measured: unregistering on an already-disposed MMDeviceEnumerator throws
        // NullReferenceException, because NAudio nulls its inner COM reference in Dispose.
        _registrar.Dispose();
    }

    /// <summary>
    /// Raise <see cref="EndpointsChanged"/> without letting a subscriber's failure escape.
    ///
    /// Both callers need this and for different reasons. From a notification an escaping exception is
    /// swallowed by the interop layer and vanishes without a trace - measured. From <see cref="Start"/>
    /// it is on the UI thread during startup, where nothing swallows it and Windows answers with a WER
    /// dialog - a window, in an app whose whole premise is not to have one.
    /// </summary>
    private void Raise(string because)
    {
        try
        {
            // `this` as the sender: subscribers identify the source they registered with, and a consumer
            // holding more than one seam would otherwise have no way to tell which one spoke.
            EndpointsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn($"A handler for the audio endpoint change raised because {because} threw: {ex.Message}");
        }
    }
}

/// <summary>
/// The five <see cref="IMMNotificationClient"/> methods, as five one-line adapters onto
/// <see cref="EndpointMonitor.OnEndpointNotification"/>.
///
/// Separate from <see cref="EndpointMonitor"/> rather than implemented on it, because the strong
/// reference the process depends on has to be a field somebody owns - see
/// <c>EndpointMonitor._client</c>. Public rather than nested and private for the reason
/// <c>LinkMonitor.OnCandidateAdded</c> is public: five adapters is five chances to paste the wrong enum
/// member, and every argument these methods take is a plain enum or struct the projection renders as
/// managed constants, so a test can call all five without going anywhere near COM.
///
/// It does nothing itself. Everything - the filter, the logging, the disposal check, the catch - lives
/// in the monitor, so none of it can be reachable only from a callback no test host can raise.
/// </summary>
public sealed class EndpointNotificationClient : IMMNotificationClient
{
    private readonly EndpointMonitor _monitor;

    public EndpointNotificationClient(EndpointMonitor monitor) => _monitor = monitor;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        _monitor.OnEndpointNotification(EndpointNotification.DeviceStateChanged, deviceId);

    public void OnDeviceAdded(string pwstrDeviceId) =>
        _monitor.OnEndpointNotification(EndpointNotification.DeviceAdded, pwstrDeviceId);

    public void OnDeviceRemoved(string deviceId) =>
        _monitor.OnEndpointNotification(EndpointNotification.DeviceRemoved, deviceId);

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) =>
        _monitor.OnEndpointNotification(EndpointNotification.DefaultDeviceChanged, defaultDeviceId);

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) =>
        _monitor.OnEndpointNotification(EndpointNotification.PropertyValueChanged, pwstrDeviceId);
}

/// <summary>
/// The real registration, against MMDevAPI. Untested by design, for the reason
/// <see cref="WasapiDeviceFactory"/> is: it is a thin wrapper over COM whose behaviour worth pinning
/// lives on the other side of <see cref="IEndpointNotificationRegistrar"/>.
///
/// Everything about the ordering here is measured, in
/// <c>docs/probes/2026-08-05-endpoint-notification.md</c> question 4:
///
/// <list type="bullet">
/// <item>The registration survives the enumerator being collected or disposed - it lives in the audio
/// service and is keyed on the <i>client</i>, not on the instance that registered it. So the enumerator
/// is a handle, not the subscription.</item>
/// <item>Unregistering twice, or unregistering a client that was never registered, throws
/// <c>COMException 0x80070490</c>. Unregistering on an enumerator that was already disposed throws
/// <see cref="NullReferenceException"/>.</item>
/// <item>A clean unregister genuinely stops delivery - 0 callbacks afterwards - and the process exits
/// 0 with no hang.</item>
/// </list>
/// </summary>
public sealed class MmDeviceNotificationRegistrar : IEndpointNotificationRegistrar
{
    private MMDeviceEnumerator? _enumerator;

    /// <summary>
    /// The enumerator is built here rather than in the constructor, for the reason
    /// <c>PowerNotifier.Start</c> does not subscribe in its: constructing a monitor should not activate
    /// a COM server.
    /// </summary>
    public void Register(IMMNotificationClient client)
    {
        _enumerator ??= new MMDeviceEnumerator();

        int hr = _enumerator.RegisterEndpointNotificationCallback(client);

        if (hr != 0)
        {
            // Warned rather than thrown. A failed registration is not fatal - the consumer still has its
            // level read and its reconcile tick - but it is invisible otherwise, and it would present as
            // exactly the bug this whole class exists to fix: audio that never routes, with nothing in
            // the log to say why.
            Log.Warn($"Registering for audio endpoint notifications failed with 0x{hr:X8}. "
                     + "The A2DP sink endpoint's arrival will not be noticed; the route will only "
                     + "recover on the next reconcile.");
        }
    }

    public void Unregister(IMMNotificationClient client)
    {
        if (_enumerator is null)
        {
            return;
        }

        // Quietly, because this is on the path to TrayContext.Dispose where a throw becomes a WER
        // dialog, and because the two failures COM has for this - "element not found" and a disposed
        // enumerator - are both cases where the registration is already gone.
        Teardown.Quietly(
            () => _enumerator.UnregisterEndpointNotificationCallback(client),
            "unregister the audio endpoint notification client");
    }

    public void Dispose()
    {
        MMDeviceEnumerator? enumerator = _enumerator;
        _enumerator = null;

        // Nulled first so a second Dispose, or an Unregister after one, finds nothing rather than a
        // disposed enumerator whose inner COM reference NAudio has already set to null.
        Teardown.Quietly(() => enumerator?.Dispose(), "dispose the MMDevice enumerator");
    }
}
