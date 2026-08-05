using Klangbruecke.Audio;
using Klangbruecke.Diagnostics;
using Klangbruecke.Tests.Diagnostics;
using Klangbruecke.Tests.Fakes;
using NAudio.CoreAudioApi;
using Xunit;

namespace Klangbruecke.Tests.Audio;

/// <summary>
/// <see cref="EndpointMonitor"/> without an audio service and without a phone.
///
/// This class is the fix for the spec's finding #2 - the app looked for
/// <c>Line (&lt;phone&gt; A2DP SNK)</c> once, immediately after the connection reported Opened, found
/// nothing in 5 of 8 recorded launches and never routed audio for the rest of the session - and for
/// finding #3, where a call invalidates the endpoint without closing the connection. Both are one
/// missing signal, so what has to be pinned is that the signal exists on both paths: the endpoint
/// being <b>already there</b> when the monitor starts, and the endpoint <b>changing</b> afterwards.
///
/// Nothing here touches COM. Two seams make that possible and they are the same trick Task 11 used on
/// <c>LinkMonitor</c>:
///
/// <list type="bullet">
/// <item><see cref="IEndpointNotificationRegistrar"/> stands in for
/// <c>MMDeviceEnumerator.RegisterEndpointNotificationCallback</c>, so "did Start register?", "did it
/// register twice?" and "did Dispose unregister exactly once, before disposing the enumerator?" become
/// counts on a hand-rolled double instead of unobservable COM state.</item>
/// <item><see cref="EndpointMonitor.OnEndpointNotification"/> is what the five
/// <c>IMMNotificationClient</c> methods delegate to, so the whole edge-triggered half is reachable
/// without MMDevAPI raising anything.</item>
/// </list>
///
/// The measurements these tests are written against are in
/// <c>docs/probes/2026-08-05-endpoint-notification.md</c>. The one that decides the shape of this file
/// is question 3: the COM registration does <b>not</b> root the managed client, and a client left only
/// weakly held was collected and took the process down with <c>0xC0000005</c>, reproduced twice. There
/// is no managed handler for that, so
/// <see cref="The_notification_client_is_strongly_rooted_while_it_is_registered"/> is the most
/// important test here.
/// </summary>
public sealed class EndpointMonitorTests : IDisposable
{
    private readonly ILog _original = Log.Current;
    private readonly RecordingLog _log = new();

    public EndpointMonitorTests() => Log.Current = _log;

    public void Dispose() => Log.Current = _original;

    // --- the brief's six -------------------------------------------------------------------------

    [Fact]
    public void Dispose_without_Start_does_not_throw()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        var monitor = new EndpointMonitor(registrar, () => false);

        // TrayContext unwinds blind: it disposes whatever it built without knowing how far startup
        // got. Same reasoning as PowerNotifier's namesake.
        Assert.Null(Record.Exception(monitor.Dispose));

        // And it must not have unregistered anything. Measured (probe question 4): unregistering a
        // client that was never registered throws COMException 0x80070490 "Element not found" - so a
        // Dispose that unregisters unconditionally would throw on every never-started monitor, on the
        // one path in this app where a throw becomes a WER dialog.
        Assert.Equal(0, registrar.UnregisterCount);
    }

    // Load-bearing, unlike its namesakes on LinkMonitor and PowerNotifier - both of which are labelled
    // contract markers because a second pass through their Dispose is observably a no-op either way.
    // Here it is not: the probe measured that a second UnregisterEndpointNotificationCallback for the
    // same client throws COMException 0x80070490. Delete the `if (_disposed) return;` guard and the
    // count assertion below reddens.
    [Fact]
    public void Dispose_is_idempotent()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        var monitor = new EndpointMonitor(registrar, () => false);
        monitor.Start();
        monitor.Dispose();

        Assert.Null(Record.Exception(monitor.Dispose));

        Assert.Equal(1, registrar.UnregisterCount);
        Assert.Equal(1, registrar.DisposeCount);
    }

    [Fact]
    public void SinkCaptureEndpointPresent_does_not_throw_before_Start()
    {
        using var monitor = new EndpointMonitor(new FakeEndpointNotificationRegistrar(), () => true);

        // A value rather than a fault. ConnectionManager reads this on its reconcile tick and is not
        // required to know whether the monitor has been started yet; the level is a question about the
        // machine, not about this object's lifecycle.
        Assert.True(monitor.SinkCaptureEndpointPresent);
    }

    [Fact]
    public void Start_is_idempotent()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        int probes = 0;
        using var monitor = new EndpointMonitor(
            registrar,
            () =>
            {
                probes++;
                return false;
            });

        monitor.Start();
        monitor.Start();

        // Two registrations of the same client would deliver every notification twice, and - the part
        // that actually breaks - a single Dispose could only give one of them back. The probe measured
        // that unregistering the leftover a second time throws, so there is no tidy way out of it
        // afterwards.
        Assert.Equal(1, registrar.RegisterCount);

        // And the subscribe-time check happens once, not once per call: it is a full COM enumeration,
        // measured at 152-282 ms.
        Assert.Equal(1, probes);
    }

    [Fact]
    public void FakeEndpointMonitor_raises_on_SetPresent()
    {
        var monitor = new FakeEndpointMonitor();
        int changes = 0;
        monitor.EndpointsChanged += (_, _) => changes++;

        monitor.SetPresent(true);
        monitor.SetPresent(false);

        // Twice, and in both directions. The double has to be able to express the endpoint arriving
        // late (finding #2) and the endpoint being invalidated by a call while the connection stays
        // open (finding #3) - the two things ConnectionManager's tests exist to drive.
        Assert.Equal(2, changes);
    }

    [Fact]
    public void FakeEndpointMonitor_reports_the_value_set()
    {
        var monitor = new FakeEndpointMonitor();

        Assert.False(monitor.SinkCaptureEndpointPresent);

        monitor.SetPresent(true);
        Assert.True(monitor.SinkCaptureEndpointPresent);

        monitor.SetPresent(false);
        Assert.False(monitor.SinkCaptureEndpointPresent);
    }

    // --- beyond the brief: the subscribe-time check ------------------------------------------------
    //
    // The whole reason this class exists. Measured in the probe's A2DP window: the capture endpoint was
    // already Active before the app opened its AudioPlaybackConnection and still Active after the app
    // was killed - it tracks the phone's Bluetooth link, which is not the app's to control. A monitor
    // that only reacts to edges would, in that state, wait forever for an arrival that already
    // happened, which is the bug it was built to remove.

    [Fact]
    public void Start_raises_EndpointsChanged_when_the_endpoint_is_already_present()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        using var monitor = new EndpointMonitor(registrar, () => true);
        int changes = 0;
        object? sender = null;
        monitor.EndpointsChanged += (s, _) =>
        {
            changes++;
            sender = s;
        };

        monitor.Start();

        Assert.Equal(1, changes);

        // The monitor, not whatever raised underneath. Subscribers identify the source they registered
        // with; same rule as PowerNotifier.
        Assert.Same(monitor, sender);
    }

    [Fact]
    public void Start_does_not_raise_EndpointsChanged_when_the_endpoint_is_absent()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        using var monitor = new EndpointMonitor(registrar, () => false);
        int changes = 0;
        monitor.EndpointsChanged += (_, _) => changes++;

        monitor.Start();

        // Nothing to report yet. Raising unconditionally would be harmless for a consumer that re-reads
        // the level, but it would also mean the event carries no information at all - and every
        // consumer of it would then have to be written as though it did not exist.
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Start_registers_before_it_checks_for_the_endpoint()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        using var monitor = new EndpointMonitor(
            registrar,
            () =>
            {
                registrar.Operations.Add("probe");
                return false;
            });

        monitor.Start();

        // This ordering is the reason the subscribe-time check closes the hole instead of just
        // narrowing it. Check first and register second, and an endpoint that arrives between the two
        // is seen by neither: the check ran before it existed and the registration was not yet in place
        // to hear it arrive. Register first and the worst case is a duplicate, which the consumer must
        // tolerate anyway because MMDevAPI duplicates notifications by itself.
        Assert.Equal(new[] { "register", "probe" }, registrar.Operations);
    }

    [Fact]
    public void Start_logs_whether_the_endpoint_was_already_there()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        using var monitor = new EndpointMonitor(registrar, () => true);

        monitor.Start();

        // The one line that would have made finding #2 diagnosable from the log the first time. A log
        // that records only what the router concluded cannot tell "the endpoint was not there yet" from
        // "it was there and nothing looked".
        (LogLevel Level, string Message, Exception? Exception) entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Contains("True", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_after_Dispose_throws_rather_than_registering_a_client_nothing_will_unregister()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        var monitor = new EndpointMonitor(registrar, () => false);
        monitor.Dispose();

        // Refused, for the reason LinkMonitor.Watch and PowerNotifier.Start refuse - and it bites
        // hardest here. Dispose's idempotence guard returns early on a second call, so a registration
        // taken after the first Dispose would never be unregistered; the monitor would then be dropped,
        // the client with it, and the next notification into a collected client is the 0xC0000005 the
        // probe reproduced twice. There is no managed handler for that.
        Assert.Throws<ObjectDisposedException>(monitor.Start);
        Assert.Equal(0, registrar.RegisterCount);
    }

    // --- beyond the brief: the strong reference ----------------------------------------------------

    [Fact]
    public void The_notification_client_is_strongly_rooted_while_it_is_registered()
    {
        // Probe question 3, and the single most dangerous line in this class. Registered and then left
        // with only a WeakReference to it, the client was collected by the very next GC and the next
        // notification killed the process with `Fatal error. Internal CLR error. (0x80131506)`, exit
        // code 0xC0000005 - reproduced twice, identically. The COM registration does not root the
        // managed object.
        //
        // The double deliberately holds the client only weakly, which is what makes this test able to
        // fail: turn EndpointMonitor's _client field into a local and nothing else in the process
        // references the object.
        var registrar = new FakeEndpointNotificationRegistrar();
        using var monitor = new EndpointMonitor(registrar, () => false);

        monitor.Start();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.NotNull(registrar.Client);
        Assert.True(
            registrar.Client!.IsAlive,
            "The IMMNotificationClient was collected while it was still registered. The next "
            + "notification would kill the process with 0xC0000005 and nothing managed can catch it.");
    }

    [Fact]
    public void Dispose_does_not_release_the_notification_client()
    {
        // Not measured, and stated as insurance rather than as a finding: the probe measured that
        // unregistering stops delivery, but it never held a callback open across an unregister, so the
        // in-flight window is unmeasured. Clearing the field would make the client collectable inside
        // exactly that window, and the failure mode is process death rather than a wrong answer. One
        // reference, held until the monitor itself is collected, is a cheap way not to find out.
        var registrar = new FakeEndpointNotificationRegistrar();
        var monitor = new EndpointMonitor(registrar, () => false);
        monitor.Start();
        monitor.Dispose();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(registrar.Client!.IsAlive);

        // Keeps the monitor - and therefore its field - rooted across the collection above, so this
        // test measures Dispose's behaviour rather than the JIT's opinion of when `monitor` died.
        GC.KeepAlive(monitor);
    }

    [Fact]
    public void Dispose_unregisters_the_client_it_registered_and_only_then_disposes_the_registrar()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        var monitor = new EndpointMonitor(registrar, () => false);
        monitor.Start();

        monitor.Dispose();

        // Shutdown order is fixed by measurement, not taste: unregister on a MMDeviceEnumerator that
        // has already been Dispose()d throws NullReferenceException, because NAudio nulls its inner COM
        // reference in Dispose. The other way round is clean.
        Assert.Equal(new[] { "register", "unregister", "dispose" }, registrar.Operations);

        // And it hands back the same object it was given. Measured: the registration is keyed on the
        // client rather than on the enumerator instance, so unregistering some other client - or a
        // freshly built one - reports success for nothing and leaves the real registration in place.
        Assert.True(registrar.UnregisteredTheClientItRegistered);
    }

    // --- beyond the brief: the notification handler ------------------------------------------------

    [Theory]
    [InlineData(EndpointNotification.DeviceAdded)]
    [InlineData(EndpointNotification.DeviceRemoved)]
    [InlineData(EndpointNotification.DeviceStateChanged)]
    public void A_notification_about_a_device_existing_raises_EndpointsChanged(EndpointNotification kind)
    {
        using EndpointMonitor monitor = Started(out _);
        int changes = 0;
        object? sender = null;
        monitor.EndpointsChanged += (s, _) =>
        {
            changes++;
            sender = s;
        };

        monitor.OnEndpointNotification(kind, "{0.0.1.00000000}.{deadbeef}");

        Assert.Equal(1, changes);
        Assert.Same(monitor, sender);
    }

    [Theory]
    [InlineData(EndpointNotification.DefaultDeviceChanged)]
    [InlineData(EndpointNotification.PropertyValueChanged)]
    public void A_notification_about_something_other_than_existence_is_dropped(EndpointNotification kind)
    {
        using EndpointMonitor monitor = Started(out _);
        int changes = 0;
        monitor.EndpointsChanged += (_, _) => changes++;

        monitor.OnEndpointNotification(kind, "{0.0.1.00000000}.{deadbeef}");

        // Neither can create or destroy an endpoint, and both are loud. The probe's controls produced
        // 40 OnDefaultDeviceChanged callbacks from four default-endpoint flips - five per flip, none of
        // them about the A2DP endpoint - and forwarding those would make ConnectionManager re-enumerate
        // WASAPI, at 152-282 ms a time, every time somebody changes their default microphone.
        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_notification_does_not_look_up_the_endpoint()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        int probes = 0;
        using var monitor = new EndpointMonitor(
            registrar,
            () =>
            {
                probes++;
                return false;
            });
        monitor.Start();

        int afterStart = probes;

        monitor.OnEndpointNotification(EndpointNotification.DeviceAdded, "id");
        monitor.OnEndpointNotification(EndpointNotification.DeviceStateChanged, "id");

        // The pinned rule: no MMDevice lookups on the notification thread. Measured - a verbatim copy of
        // the endpoint lookup run inline from a callback returned the right answer every time but took
        // 152-282 ms, blocking one of MMDevAPI's own worker threads for that long. The consumer does the
        // lookup, after it has marshalled.
        Assert.Equal(afterStart, probes);
    }

    [Fact]
    public void Duplicate_notifications_are_forwarded_rather_than_swallowed()
    {
        using EndpointMonitor monitor = Started(out _);
        int changes = 0;
        monitor.EndpointsChanged += (_, _) => changes++;

        monitor.OnEndpointNotification(EndpointNotification.DeviceAdded, "id");
        monitor.OnEndpointNotification(EndpointNotification.DeviceAdded, "id");

        // Measured: MMDevAPI duplicates. One SetDefaultEndpoint per role produced five callbacks, with
        // two of the three roles reported twice, in every run.
        //
        // De-duplicating here would need to know what changed, and knowing that needs the endpoint
        // lookup this handler is forbidden to do. So the handler stays stateless and says so, and
        // idempotence is the consumer's - ConnectionManager reads the level after it marshals, and
        // reading it twice is the same as reading it once. Swallowing the second notification on a
        // guess is how a real second arrival gets lost, which is the failure this class exists to
        // remove.
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_notification_arriving_after_Dispose_is_ignored()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        var monitor = new EndpointMonitor(registrar, () => false);
        monitor.Start();
        int changes = 0;
        monitor.EndpointsChanged += (_, _) => changes++;

        monitor.Dispose();
        monitor.OnEndpointNotification(EndpointNotification.DeviceAdded, "id");

        // Callbacks arrive on MMDevAPI's worker threads, so one can already be in flight when Dispose
        // runs on the UI thread - no check-then-act here could prevent that, and this does not pretend
        // to. What it does prevent is a late notification driving a consumer that has already torn
        // its route down.
        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_handler_that_throws_does_not_escape_the_notification()
    {
        using EndpointMonitor monitor = Started(out _);
        monitor.EndpointsChanged += (_, _) => throw new InvalidOperationException("boom");

        // Measured, and this is the dangerous half of the measurement: an exception escaping an
        // IMMNotificationClient callback does NOT kill the process - the interop layer swallows it and
        // returns a failure HRESULT - and AppDomain.UnhandledException never fires. So a handler that
        // throws fails completely silently. Catching and logging here is the only way it is ever seen.
        Assert.Null(Record.Exception(
            () => monitor.OnEndpointNotification(EndpointNotification.DeviceAdded, "id")));

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public void A_handler_that_throws_at_Start_does_not_escape_Start()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        using var monitor = new EndpointMonitor(registrar, () => true);
        monitor.EndpointsChanged += (_, _) => throw new InvalidOperationException("boom");

        // The same rule on the other raise. This one runs on the UI thread during startup, where an
        // escaping exception is not swallowed by anything: TrayContext builds this inside the path that
        // ends at Application.Run, and a throw there is a WER dialog - a window, in an app whose whole
        // premise is not to have one.
        Assert.Null(Record.Exception(monitor.Start));

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public void A_notification_raises_on_the_thread_it_arrived_on()
    {
        using EndpointMonitor monitor = Started(out _);
        int handlerThread = 0;
        monitor.EndpointsChanged += (_, _) => handlerThread = Environment.CurrentManagedThreadId;

        int raisingThread = 0;
        var thread = new Thread(() =>
        {
            raisingThread = Environment.CurrentManagedThreadId;
            monitor.OnEndpointNotification(EndpointNotification.DeviceAdded, "id");
        });
        thread.Start();
        thread.Join();

        // The no-marshalling contract, asserted rather than commented. Callbacks arrive on MMDevAPI's
        // own MTA worker threads - measured, never the registering thread, and not always the same one -
        // and ConnectionManager posts every inbound event through IUiDispatcher before touching state.
        // A second marshalling layer here would add a hop and buy nothing; LinkMonitor and PowerNotifier
        // reach the same instruction by their own routes.
        Assert.Equal(raisingThread, handlerThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, handlerThread);
    }

    // --- beyond the brief: the level ---------------------------------------------------------------

    [Fact]
    public void SinkCaptureEndpointPresent_reads_the_endpoint_on_every_read()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        bool present = false;
        using var monitor = new EndpointMonitor(registrar, () => present);

        Assert.False(monitor.SinkCaptureEndpointPresent);

        present = true;

        // Live, not cached. A cached level would have to be refreshed from the notification handler,
        // which is forbidden to do the lookup - so the cache could only ever be refreshed on a
        // schedule, and a stale "absent" is precisely the state the app was stuck in for whole
        // sessions.
        Assert.True(monitor.SinkCaptureEndpointPresent);
    }

    [Fact]
    public void SinkCaptureEndpointPresent_is_false_when_the_lookup_throws()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        using var monitor = new EndpointMonitor(
            registrar,
            () => throw new InvalidOperationException("the enumerator fell over"));

        // Reachable in practice: reading MMDevice.FriendlyName throws COMException 0xE000020B on some
        // endpoints, and the whole enumeration can fail while the audio service restarts. This is read
        // on the reconcile tick, so a throw here would kill the loop that is the app's only backstop -
        // the predecessor's defining bug, restored.
        Assert.False(monitor.SinkCaptureEndpointPresent);

        Assert.Contains(
            _log.Entries,
            e => e.Level == LogLevel.Warn && e.Message.Contains("the enumerator fell over", StringComparison.Ordinal));
    }

    [Fact]
    public void SinkCaptureEndpointPresent_is_false_after_Dispose()
    {
        var registrar = new FakeEndpointNotificationRegistrar();
        var monitor = new EndpointMonitor(registrar, () => true);
        monitor.Start();
        monitor.Dispose();

        // False rather than a throw, and false rather than the last live answer. Teardown races: the
        // reconcile tick and the tray's exit path are different threads, so a read can land after
        // Dispose. Answering "present" there would send a consumer off to build a route out of an
        // endpoint nobody is listening for any more.
        Assert.False(monitor.SinkCaptureEndpointPresent);
    }

    // --- beyond the brief: the filter and the COM adapters ----------------------------------------

    [Fact]
    public void EndpointNotification_covers_every_MMDevAPI_callback()
    {
        // The enum mirrors IMMNotificationClient one-for-one, and the filter below is a decision about
        // every member of it. A sixth callback appearing in a future NAudio would otherwise be wired up
        // silently, defaulting to whichever side of the filter the `is` pattern happened to put it on.
        // Same assertion, same reasoning, as LinkMonitorTests on BluetoothConnectionStatus.
        Assert.Equal(5, Enum.GetValues<EndpointNotification>().Length);
    }

    [Theory]
    [InlineData(EndpointNotification.DeviceAdded, true)]
    [InlineData(EndpointNotification.DeviceRemoved, true)]
    [InlineData(EndpointNotification.DeviceStateChanged, true)]
    [InlineData(EndpointNotification.DefaultDeviceChanged, false)]
    [InlineData(EndpointNotification.PropertyValueChanged, false)]
    public void SignalsEndpointExistence_admits_only_the_three_that_can_change_what_exists(
        EndpointNotification kind,
        bool expected)
    {
        // Which callback the A2DP endpoint's own arrival raises is the one question the probe could not
        // answer - it needs a real phone disconnect, and Task 18's smoke test is where it closes. So
        // this is not a guess at that answer: it admits all three callbacks that can report a device
        // coming into or going out of existence, and rejects the two that by their own definition
        // cannot. If Task 18 finds the arrival lands somewhere else entirely, this predicate and this
        // table are the one place to change.
        Assert.Equal(expected, EndpointMonitor.SignalsEndpointExistence(kind));
    }

    [Fact]
    public void The_COM_client_maps_each_callback_to_its_own_notification()
    {
        using EndpointMonitor monitor = Started(out _);
        var client = new EndpointNotificationClient(monitor);

        // Five one-line adapters is five chances to paste the wrong enum member. Behaviourally a
        // mix-up inside the raising three would be invisible, so the assertion is on the log line,
        // which is the other thing they carry - and the thing Task 18 will read to close the probe's
        // open question. Every argument type here is a plain enum or struct that the projection renders
        // as managed constants, so none of this touches COM.
        client.OnDeviceAdded("id-added");
        client.OnDeviceRemoved("id-removed");
        client.OnDeviceStateChanged("id-state", DeviceState.Active);
        client.OnDefaultDeviceChanged(DataFlow.Capture, Role.Multimedia, "id-default");
        client.OnPropertyValueChanged("id-property", default);

        string[] logged = _log.Entries.Select(e => e.Message).ToArray();

        Assert.Contains(logged, m => m.Contains(nameof(EndpointNotification.DeviceAdded), StringComparison.Ordinal)
                                     && m.Contains("id-added", StringComparison.Ordinal));
        Assert.Contains(logged, m => m.Contains(nameof(EndpointNotification.DeviceRemoved), StringComparison.Ordinal)
                                     && m.Contains("id-removed", StringComparison.Ordinal));
        Assert.Contains(logged, m => m.Contains(nameof(EndpointNotification.DeviceStateChanged), StringComparison.Ordinal)
                                     && m.Contains("id-state", StringComparison.Ordinal));

        // And the two that are filtered out are not logged at all - the log is read by a human looking
        // for one thing, and OnPropertyValueChanged alone would bury it.
        Assert.DoesNotContain(logged, m => m.Contains("id-default", StringComparison.Ordinal));
        Assert.DoesNotContain(logged, m => m.Contains("id-property", StringComparison.Ordinal));
    }

    [Fact]
    public void The_COM_client_raises_through_the_monitor()
    {
        using EndpointMonitor monitor = Started(out _);
        var client = new EndpointNotificationClient(monitor);
        int changes = 0;
        monitor.EndpointsChanged += (_, _) => changes++;

        client.OnDeviceAdded("id");
        client.OnDeviceRemoved("id");
        client.OnDeviceStateChanged("id", DeviceState.NotPresent);
        client.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "id");
        client.OnPropertyValueChanged("id", default);

        // The wiring, end to end: the adapters really do go through the monitor's filter rather than
        // raising on their own.
        Assert.Equal(3, changes);
    }

    // --- beyond the brief: the double --------------------------------------------------------------

    [Fact]
    public void FakeEndpointMonitor_records_Start()
    {
        var monitor = new FakeEndpointMonitor();

        Assert.False(monitor.Started);

        monitor.Start();

        Assert.True(monitor.Started);
    }

    [Fact]
    public void FakeEndpointMonitor_records_Dispose()
    {
        var monitor = new FakeEndpointMonitor();

        Assert.False(monitor.Disposed);

        monitor.Dispose();

        // The real monitor holds a COM registration whose managed client is not rooted by COM. A
        // consumer that forgets to dispose it is the crash, so "did ConnectionManager let go of it?" is
        // a question its teardown test has to be able to ask.
        Assert.True(monitor.Disposed);
    }

    [Fact]
    public void FakeEndpointMonitor_can_raise_without_changing_presence()
    {
        var monitor = new FakeEndpointMonitor { SinkCaptureEndpointPresent = true };
        int changes = 0;
        monitor.EndpointsChanged += (_, _) => changes++;

        monitor.RaiseEndpointsChanged();

        // The ordinary case, not an edge one: MMDevAPI duplicates its notifications and most of them
        // are about some other device entirely, so "the monitor spoke and nothing had changed" is what
        // ConnectionManager will mostly be handed.
        Assert.Equal(1, changes);
        Assert.True(monitor.SinkCaptureEndpointPresent);
    }

    // --- helpers -----------------------------------------------------------------------------------

    /// <summary>A started monitor over a fake registrar, reporting no endpoint.</summary>
    private static EndpointMonitor Started(out FakeEndpointNotificationRegistrar registrar)
    {
        registrar = new FakeEndpointNotificationRegistrar();
        var monitor = new EndpointMonitor(registrar, () => false);
        monitor.Start();
        return monitor;
    }
}
