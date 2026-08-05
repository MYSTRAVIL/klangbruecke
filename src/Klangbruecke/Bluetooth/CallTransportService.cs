using Klangbruecke.Diagnostics;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;

namespace Klangbruecke.Bluetooth;

/// <summary>
/// Owns the call half: routes cellular call audio from the phone to this PC's headset
/// via the Bluetooth HFP hands-free role.
///
/// Requires the restricted capability <c>phoneLineTransportManagement</c>, which only works
/// with MSIX package identity. Sideloading needs no Microsoft approval.
///
/// Whether it also needs a Limited Access Feature token is **open**, and the claim that it does
/// not is under investigation. That claim traces to one Microsoft support reply relayed in MyPhone
/// issue #26, which the same commenter contradicted ten days later; the LAF feature id is present
/// in this machine's registry; and Sefirah performs the unlock precisely when the build is below
/// 22000, i.e. on Windows 10, which this is. <see cref="ProbeLimitedAccessFeature"/> logs the gate
/// status on every connect so the answer comes from the machine rather than from a forum thread.
/// See docs/FINDINGS.md §2 and §12.
/// </summary>
public sealed class CallTransportService : ICallTransportService
{
    private PhoneLineTransportDevice? _device;
    private bool _disposed;

    /// <summary>
    /// Asked of the device every time, never cached. Catching the role going false - another app
    /// claimed it, the phone re-paired, Windows dropped it - is the reconcile loop's whole job, and a
    /// cached bool answers with the state at the moment of the last connect instead. Deliberately
    /// unguarded: the two <c>IsRegistered()</c> calls in <see cref="ConnectAsync"/> are unguarded
    /// too, and a read that throws is a fault the caller's guard should see rather than a role that
    /// is absent. Swallowing it would report "not registered" for a state that is actually unknown,
    /// which is a different answer than this property is allowed to give.
    ///
    /// This is a live ABI call, so callers must not put it anywhere a throw would strand them.
    /// <c>TrayContext.RebuildMenuAsync</c> handles that by ordering rather than by catching - it adds
    /// Exit before reading this - and the reconcile loop that needs to tell "role dropped" from
    /// "could not read" reads <see cref="ReadRegistration"/> instead, which is a tri-state of its own
    /// rather than a swallow in here.
    /// </summary>
    public bool IsRegistered => _device is not null && _device.IsRegistered();

    /// <summary>
    /// The guarded read, and the only place in this class where a failed <c>IsRegistered()</c> is
    /// swallowed. It is allowed to swallow it precisely because it does not have to lie about the
    /// result: <see cref="RegistrationStatus.Unknown"/> is a third answer, and
    /// <c>CallsHalf.ReconcileAsync</c> - the caller this exists for - acts on it by doing nothing.
    ///
    /// The guard belongs here rather than at that call site because the call site is on a 30 s timer,
    /// where an escaping throw unwinds past the callback into <c>Application.ThreadException</c>.
    /// <see cref="IsRegistered"/> stays unguarded and stays as it is; the tray menu depends on its
    /// current semantics and defends itself by ordering.
    ///
    /// No device is <see cref="RegistrationStatus.NotRegistered"/>, not Unknown. There is nothing to
    /// ask, which is a known answer - the role cannot be held through a device this class does not
    /// have.
    ///
    /// What the guard covers is the ABI call, and only that. <see cref="Report"/> invokes
    /// <see cref="Status"/>, so a subscriber that throws escapes this method into the very callback
    /// the guard exists to protect. Left as it is, consistently with the rest of this class - the
    /// connect path's catch calls <see cref="Report"/> on the same terms - because a catch around a
    /// subscriber's throw is one nothing in this codebase can reach, and an untestable swallow is
    /// worse than a named limit.
    /// </summary>
    public RegistrationStatus ReadRegistration()
    {
        if (_device is null)
        {
            return RegistrationStatus.NotRegistered;
        }

        try
        {
            return _device.IsRegistered() ? RegistrationStatus.Registered : RegistrationStatus.NotRegistered;
        }
        catch (Exception ex)
        {
            // Report only, deliberately not also Log.Error the way the connect path does. That path
            // runs once per attempt; this one runs every 30 seconds for as long as the app is up, and
            // an ABI that has started failing would write the same pair of lines twice a minute
            // forever. Whatever is listening to Status is what puts this in the log.
            Report($"Could not read the hands-free registration: {ex.Message}", LogLevel.Warn);
            return RegistrationStatus.Unknown;
        }
    }

    public event EventHandler<StatusMessage>? Status;

    /// <summary>
    /// Info unless said otherwise. The level travels with the message because this class is the only
    /// thing that knows it - see <see cref="StatusMessage"/>.
    /// </summary>
    private void Report(string message, LogLevel level = LogLevel.Info) =>
        Status?.Invoke(this, new StatusMessage(message, level));

    /// <summary>Paired devices offering a phone-line transport (i.e. phones).</summary>
    public async Task<IReadOnlyList<TransportCandidate>> FindTransportsAsync()
    {
        string selector = PhoneLineTransportDevice.GetDeviceSelector();
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);

        // Runs unpackaged too, and the whole point of letting it: this list, in full, is the answer to
        // whether the phone's transport is discoverable at all - the one calls-side fact a development
        // run can establish before the packaged build ever claims the hands-free role.
        Log.Info($"Phone-line selector matched {devices.Count} device(s).");

        // Projected inside the same loop that logs, so the record and the line describing it are one
        // read of one DeviceInformation - the same reason AudioSinkService.FindDevicesAsync does it
        // this way. Two passes would let a caller compare a log line against a record that came from
        // a different snapshot.
        var candidates = new List<TransportCandidate>(devices.Count);
        foreach (DeviceInformation device in devices)
        {
            Log.Info($"  Transport candidate '{device.Name}' id={device.Id}");
            candidates.Add(new TransportCandidate(device.Id, device.Name));
        }

        return candidates;
    }

    public async Task<CallTransportResult> ConnectAsync(string transportDeviceId)
    {
        Disconnect();

        // Tracked across the try so the catch can tell a throw that left the role claimed from one
        // that did not. Reporting Registered=false after a successful RegisterApp is the same defect
        // this method exists to fix, arriving by the other door: the reconcile loop would re-run
        // RequestAccessAsync/RegisterApp against a role this process already holds.
        bool registered = false;

        try
        {
            // Assigned before the first await, and that ordering is load-bearing outside this method.
            // Disconnect() reads this field, so a release arriving while this call is suspended can
            // still find the device and unregister it - which is the only thing that stops a
            // mid-flight teardown leaking a role that gets claimed a moment later. The natural
            // tidy-up, assigning only once RegisterApp has succeeded, would break that silently:
            // a Disconnect during the await would have nothing to release. CallsHalf guarantees no
            // *second* ConnectAsync starts while this one runs (CallsHalf._inFlight); it cannot
            // guarantee that no Disconnect lands in the middle of one, and must not have to.
            _device = PhoneLineTransportDevice.FromId(transportDeviceId);
            if (_device is null)
            {
                const string missing = "No phone-line transport for that device.";
                Report(missing);
                return CallTransportResult.NotClaimed(missing);
            }

            // Registering claims the hands-free role for this app. If another app already
            // holds it, this is where things fail - unpair and retry rather than assuming
            // the API is unavailable.
            bool alreadyRegistered = _device.IsRegistered();
            Log.Info($"Phone-line transport resolved; IsRegistered={alreadyRegistered}.");

            ProbeLimitedAccessFeature();

            // Both reference implementations that get RegisterApp to work (Sefirah, MyPhone) call
            // this first; this app went straight to RegisterApp and got E_ACCESSDENIED every time.
            // Its own status is worth more than its bool: Allowed here with RegisterApp still
            // refused is the shape MyPhone #26 reports, and points past permission at the LAF gate.
            var access = await _device.RequestAccessAsync();
            Log.Info($"PhoneLineTransportDevice.RequestAccessAsync returned {access}.");

            if (!alreadyRegistered)
            {
                // Bracketed by log lines the way AudioSinkService brackets TryCreateFromId, and for
                // the same reason. docs/FINDINGS.md §2 records RegisterApp as the one call on this
                // path that has never been executed - it is where the restricted capability actually
                // bites - and §8 has already caught a CsWinRT statics shim answering a capability
                // problem with an AccessViolationException rather than a catchable failure. An AV is
                // a corrupted-state exception: the catch below cannot see it and nothing can log it
                // after the fact. The only instrument left is absence, so "Registering..." with no
                // line under it means the process died inside RegisterApp.
                Log.Info("Registering this app for the hands-free role (PhoneLineTransportDevice.RegisterApp).");
                _device.RegisterApp();

                // Sefirah re-checks rather than trusting the return, because RegisterApp can come
                // back having done nothing. Treating a silent no-op as success is how this reads as
                // working while the phone never offers the PC.
                bool nowRegistered = _device.IsRegistered();
                Log.Info($"RegisterApp returned; IsRegistered={nowRegistered}.");

                if (!nowRegistered)
                {
                    const string unclaimed = "RegisterApp did not throw but the role was not claimed.";
                    Report(unclaimed, LogLevel.Error);
                    return CallTransportResult.NotClaimed(unclaimed);
                }
            }

            // Set on both branches - already registered, or registered just now - because both mean
            // this process holds the role. Everything below is a fact about the transport and cannot
            // change that.
            registered = true;

            // Bracketed for the same reason, one call further on. This one is awaited, so a failure
            // can also arrive as a faulted task caught below - but it cannot do both, and only the
            // bracket survives the case that kills the process.
            Log.Info("Connecting the call transport (PhoneLineTransportDevice.ConnectAsync).");
            bool connected = await _device.ConnectAsync();
            Log.Info($"PhoneLineTransportDevice.ConnectAsync returned {connected}.");

            // Recorded, never graded on. This bool is False on every run on this machine, including
            // the ones where real cellular calls routed both directions - see docs/FINDINGS.md §12
            // and CallTransportResult.Claimed, which owns the rule.
            CallTransportResult result = CallTransportResult.Claimed(connected);
            Report(result.Reason);
            return result;
        }
        catch (Exception ex)
        {
            // Both, and in this order, because they are not the same record. Report carries
            // ex.Message; only this call carries the exception, and FileLog renders it with
            // ToString() - a faulted WinRT async op's message is "One or more errors occurred." and
            // the entire cause lives in the inner exception and the stack. This is the overload that
            // rendering was built for, and the two components making the WinRT calls this stage
            // exists to instrument were the only ones never calling it.
            //
            // Unconditional, rather than folded into Report: Report reaches the log only if
            // something is subscribed to Status, and a throw during startup or teardown is exactly
            // when nothing may be.
            Log.Error("The call transport connect path threw.", ex);

            // Error, matching the line above, so one throw is not described at two levels.
            string reason = $"Call transport threw: {ex.Message}";
            Report(reason, LogLevel.Error);

            // registered, not false: a throw from the transport connect below RegisterApp leaves the
            // role claimed, and the caller must not be told to claim it again. TransportConnected is
            // null because the call either never ran or never answered.
            return new CallTransportResult(registered, null, reason);
        }
    }

    /// <summary>
    /// Asks Windows whether this API is still behind a Limited Access Feature gate, and logs the
    /// answer. Diagnostic only - it deliberately passes an invalid token, so it cannot unlock
    /// anything, and generating a real one is a decision for a human (see CLAUDE.md).
    ///
    /// The status is the whole point:
    ///   AvailableWithoutToken - the gate is gone, and FINDINGS §2 is right that no token is needed
    ///   Available             - unlocked, i.e. a valid token WOULD be required
    ///   Unavailable           - the gate is live and unmet, which is the likely cause of
    ///                           RegisterApp's E_ACCESSDENIED on this build
    ///
    /// Worth measuring rather than assuming: the feature id below is present in this machine's
    /// registry under HKLM\...\AppModel\LimitedAccessFeatures, which is not what an OS looks like
    /// when it has dropped a feature from the list, and Sefirah performs this unlock precisely when
    /// the build is below 22000 - i.e. on Windows 10, which is this machine.
    /// </summary>
    private static void ProbeLimitedAccessFeature()
    {
        const string featureId = "com.microsoft.windows.applicationmodel.phonelinetransportdevice_v1";

        try
        {
            LimitedAccessFeatureRequestResult result = LimitedAccessFeatures.TryUnlockFeature(
                featureId,
                "KLANGBRUECKE-DIAGNOSTIC-PROBE-NOT-A-TOKEN",
                "Klangbruecke diagnostic probe");

            Log.Info($"LimitedAccessFeatures status for phonelinetransportdevice_v1: {result.Status}.");
        }
        catch (Exception ex)
        {
            // Never fatal: this is instrumentation, and the connect path must fail on its own
            // terms rather than on the failure of something asking why it failed.
            Log.Warn($"Could not probe the limited-access feature gate: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        if (_device is not null)
        {
            // Local copy: the lambda would otherwise re-read the nullable field.
            PhoneLineTransportDevice device = _device;

            // Teardown.Quietly rather than a silent catch. A failed unregister is diagnostically
            // live, not noise: it leaves the hands-free role claimed by a process that believes it
            // let go, and it is therefore the reason the *next* RegisterApp fails. Swallowed, that
            // arrives as an unexplainable second run with nothing in the log connecting the two.
            Teardown.Quietly(
                () =>
                {
                    if (device.IsRegistered())
                    {
                        device.UnregisterApp();
                    }
                },
                "unregister the call transport's hands-free role");

            // After the unregister, and unconditionally: IsRegistered reads through this field, so
            // clearing it is what makes the live read answer false once the role has been let go.
            _device = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disconnect();
    }
}
