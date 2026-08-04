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

    public event EventHandler<string>? Status;

    private void Report(string message) => Status?.Invoke(this, message);

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
            if (!_device.IsRegistered())
            {
                _device.RegisterApp();
            }

            bool connected = await _device.ConnectAsync();
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
            Report($"Call transport threw: {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        if (_device is not null)
        {
            try
            {
                if (_device.IsRegistered())
                {
                    _device.UnregisterApp();
                }
            }
            catch (Exception)
            {
                // Unregistering is best effort; the app is going away regardless.
            }

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
