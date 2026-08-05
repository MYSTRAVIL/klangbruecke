namespace Klangbruecke.Bluetooth;

/// <summary>
/// What a read of the phone's <c>BluetoothDevice.ConnectionStatus</c> said.
///
/// <see cref="Unknown"/> is every way the read can fail to produce an answer - an address that will
/// not parse, a null device back from WinRT, a throw from the ABI - collapsed into one value.
/// Callers must treat it as <see cref="Disconnected"/>: see <c>LinkMachine.OnLinkStatusRead</c>.
/// </summary>
public enum BluetoothLinkStatus
{
    Unknown,
    Disconnected,
    Connected,
}
