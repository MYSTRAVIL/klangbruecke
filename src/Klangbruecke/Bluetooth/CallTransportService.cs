using Klangbruecke.Diagnostics;
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
/// It does NOT require a Limited Access Feature token - Microsoft removed this API from the
/// LAF list. Do not add token generation here. See docs/FINDINGS.md §2.
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
                Log.Info("RegisterApp returned; the hands-free role is claimed.");
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
