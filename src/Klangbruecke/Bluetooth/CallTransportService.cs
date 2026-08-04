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
public sealed class CallTransportService : IDisposable
{
    private PhoneLineTransportDevice? _device;
    private bool _disposed;

    public bool IsConnected { get; private set; }

    public event EventHandler<StatusMessage>? Status;

    /// <summary>
    /// Info unless said otherwise. The level travels with the message because this class is the only
    /// thing that knows it - see <see cref="StatusMessage"/>.
    /// </summary>
    private void Report(string message, LogLevel level = LogLevel.Info) =>
        Status?.Invoke(this, new StatusMessage(message, level));

    /// <summary>Paired devices offering a phone-line transport (i.e. phones).</summary>
    public static async Task<IReadOnlyList<DeviceInformation>> FindDevicesAsync()
    {
        string selector = PhoneLineTransportDevice.GetDeviceSelector();
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);

        // Runs unpackaged too, and the whole point of letting it: this list, in full, is the answer to
        // whether the phone's transport is discoverable at all - the one calls-side fact a development
        // run can establish before the packaged build ever claims the hands-free role.
        Log.Info($"Phone-line selector matched {devices.Count} device(s).");
        foreach (DeviceInformation device in devices)
        {
            Log.Info($"  Transport candidate '{device.Name}' id={device.Id}");
        }

        return devices.ToList();
    }

    public async Task<bool> ConnectAsync(string deviceId)
    {
        Disconnect();

        try
        {
            _device = PhoneLineTransportDevice.FromId(deviceId);
            if (_device is null)
            {
                Report("No phone-line transport for that device.");
                return false;
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
                    Report("RegisterApp did not throw but the role was not claimed.", LogLevel.Error);
                    return false;
                }
            }

            // Bracketed for the same reason, one call further on. This one is awaited, so a failure
            // can also arrive as a faulted task caught below - but it cannot do both, and only the
            // bracket survives the case that kills the process.
            Log.Info("Connecting the call transport (PhoneLineTransportDevice.ConnectAsync).");
            bool connected = await _device.ConnectAsync();
            Log.Info($"PhoneLineTransportDevice.ConnectAsync returned {connected}.");

            if (!connected)
            {
                Report("Call transport refused to connect. Check the pairing first - " +
                       "BTHUSB events 35/16/24 in the System log indicate a stale pairing, " +
                       "not an API problem. See docs/FINDINGS.md §3.");
                return false;
            }

            IsConnected = true;
            Report("Call transport connected.");
            return true;
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
            Report($"Call transport threw: {ex.Message}", LogLevel.Error);
            return false;
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

            _device = null;
        }

        IsConnected = false;
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
