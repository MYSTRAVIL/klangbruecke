using Klangbruecke.Diagnostics;

namespace Klangbruecke.Bluetooth;

/// <summary>
/// A paired device that can act as an audio source for this PC, reduced to the two facts anything
/// above this seam needs.
///
/// Deliberately not <c>DeviceInformation</c>: that type cannot be constructed outside WinRT, so
/// every caller that named it was untestable by construction. <c>Id</c> is the WinRT device
/// interface id - the input to <see cref="BluetoothDeviceId"/>'s address extraction and to the
/// transport correlation built on it - not a Bluetooth address.
/// </summary>
public readonly record struct PhoneDevice(string Id, string Name);

/// <summary>
/// The two states <c>AudioPlaybackConnection</c> reports, translated out of WinRT so a state
/// machine can be driven by a fake.
/// </summary>
public enum AudioSinkConnectionState
{
    Closed,
    Opened,
}

/// <summary>
/// The music half of the app, as its callers see it: find the phones, open the connection that makes
/// this PC an A2DP sink, and be told when that connection changes state.
///
/// Separate from <see cref="AudioSinkService"/> so the reconnect machinery can be driven without a
/// phone, without package identity, and therefore without the call that terminates an unpackaged
/// process outright - see <see cref="AudioSinkPolicy"/> and docs/FINDINGS.md §8.
/// </summary>
public interface IAudioSinkService : IDisposable
{
    string? ConnectedDeviceId { get; }

    /// <summary>
    /// The WinRT connection object is open. Deliberately NOT a claim about the capture endpoint:
    /// the two are separated by an unbounded interval (spec finding #2), and folding them into one
    /// property makes that interval unrepresentable.
    /// </summary>
    bool IsConnected { get; }

    event EventHandler<StatusMessage>? Status;

    /// <summary>
    /// Every state the connection object reports, translated. Raised in addition to
    /// <see cref="Status"/>, which carries the same read as text for the tray and the log - a
    /// subscriber that acts and a reader that watches are different audiences, and neither is served
    /// by the other's channel.
    /// </summary>
    event EventHandler<AudioSinkConnectionState>? StateChanged;

    Task<IReadOnlyList<PhoneDevice>> FindDevicesAsync();
    Task<bool> ConnectAsync(string deviceId);
    void Disconnect();
}
