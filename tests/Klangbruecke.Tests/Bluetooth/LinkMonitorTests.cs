using Klangbruecke.Bluetooth;
using Windows.Devices.Bluetooth;
using Xunit;

namespace Klangbruecke.Tests.Bluetooth;

/// <summary>
/// The part of <see cref="LinkMonitor"/> that can be exercised without a phone.
///
/// No test here reaches <c>BluetoothDevice.FromBluetoothAddressAsync</c> or starts a real
/// <see cref="Windows.Devices.Enumeration.DeviceWatcher"/>. Not for fear of the call - it was run
/// out-of-process against this machine's radio during Task 11 and answered cleanly, unpackaged,
/// which closes risk #2 in the Stage 1 design - but because its answer is whatever the room is doing:
/// a phone in range makes a green run, the same phone in a pocket downstairs makes a red one, and a
/// suite that reports the weather is worse than no suite at all.
///
/// So every test below stops at an id whose address cannot be extracted, or at a pure function.
/// What that leaves uncovered is recorded in the task report rather than smuggled past in a test
/// that only passes on one desk.
/// </summary>
public sealed class LinkMonitorTests
{
    // --- the brief's five ---

    [Fact]
    public void DevicePresent_is_false_before_Watch()
    {
        using var monitor = new LinkMonitor();

        // Selection is an intent, not an observation - LinkMachine.OnPhoneSelected lands in Absent
        // for the same reason. A monitor that started out claiming presence would drive the machine
        // to Present on no evidence at all, and the first thing Stage 1 does on startup is select
        // the remembered phone.
        Assert.False(monitor.DevicePresent);
    }

    [Fact]
    public void StopWatching_before_Watch_does_not_throw()
    {
        using var monitor = new LinkMonitor();

        // Stage 1 calls StopWatching without knowing whether anything is running - a deselect, a
        // deliberate disconnect, a manager unwinding after a failed start. A throw from the
        // never-watched case would make every one of those conditional at the call site.
        Assert.Null(Record.Exception(monitor.StopWatching));
    }

    // A contract marker, labelled as one because it cannot currently fail - the same situation as
    // AudioSinkServiceContractTests.Dispose_is_idempotent. Deleting the `if (_disposed) return;`
    // guard leaves this green: Dispose does nothing but StopWatching, which is itself idempotent, so
    // a second pass is observably a no-op either way. The toolchain does not catch the deletion
    // either - it emits CS0414, and nothing in this repo treats warnings as errors.
    //
    // So do not read a green run here as evidence that the guard is present. It becomes load-bearing,
    // and this test becomes capable of failing, the moment Dispose does something StopWatching does
    // not - and whoever makes that change owns the assertion for it.
    [Fact]
    public void Dispose_is_idempotent()
    {
        var monitor = new LinkMonitor();
        monitor.Dispose();

        Assert.Null(Record.Exception(monitor.Dispose));
    }

    [Fact]
    public async Task ReadLinkStatusAsync_returns_Unknown_when_no_device_is_watched()
    {
        using var monitor = new LinkMonitor();

        // The reconcile loop polls this every 30 s and is not required to check first whether a
        // phone is selected. Unknown rather than a throw, and Unknown rather than Disconnected: the
        // machine collapses both to Absent, but only Unknown says the read never produced an answer.
        Assert.Equal(BluetoothLinkStatus.Unknown, await monitor.ReadLinkStatusAsync());
    }

    [Fact]
    public async Task ReadLinkStatusAsync_returns_Unknown_for_an_id_with_no_extractable_address()
    {
        // "garbage" carries no 12-hex run, so TryExtractAddress returns null and there is no ulong to
        // hand to WinRT. The failure has to land as Unknown rather than as a throw: this runs on the
        // reconcile tick, and an escaping exception there kills the loop that is the app's only
        // backstop against a dropped watcher event - the predecessor's defining bug, restored.
        Assert.Equal(BluetoothLinkStatus.Unknown, await LinkMonitor.ReadLinkStatusAsync("garbage"));
    }

    // --- beyond the brief's five ---
    //
    // Everything above stops short of the three decisions this class actually makes: which candidate
    // id counts as the watched phone, how a 12-char hex address becomes the ulong WinRT wants, and
    // which ConnectionStatus counts as connected. All three are pure, none touches the ABI, and
    // without these none of them is asserted anywhere.

    [Fact]
    public async Task ReadLinkStatusAsync_returns_Unknown_for_a_null_id()
    {
        // The instance path with no phone watched routes through here, but so would a caller holding
        // a nullable id. Guarded explicitly because TryExtractAddress's null handling is its
        // contract, not this class's, and a future change there must not turn this into a throw.
        Assert.Equal(BluetoothLinkStatus.Unknown, await LinkMonitor.ReadLinkStatusAsync(null));
    }

    [Fact]
    public void TryParseAddress_reads_a_device_id_as_hexadecimal()
    {
        // The whole of risk #2's second half. TryExtractAddress returns 12 uppercase hex characters
        // and FromBluetoothAddressAsync wants a ulong, so this conversion sits between them - and
        // parsing "C01C6A90E174" without NumberStyles.HexNumber does not return a wrong address, it
        // throws, which the catch below would quietly turn into Unknown forever. The app would poll
        // every 30 s, learn nothing, and sit in Absent with a phone in the room.
        //
        // The id the A2DP selector actually returned for the phone on this machine, copied from the
        // probe output rather than invented - lowercase hex, with a GUID whose last group is a
        // competing 12-hex run that BluetoothDeviceId has to strip before it can find the address.
        // Composition is the point: extraction is BluetoothDeviceIdTests' subject, and this asserts
        // that what comes out of it survives the trip to a ulong.
        Assert.True(
            LinkMonitor.TryParseAddress(
                @"\\?\BTHENUM#{0000110a-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&C01C6A90E174_C00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\SNK",
                out ulong address));

        Assert.Equal(0xC01C6A90E174UL, address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void TryParseAddress_rejects_an_id_carrying_no_address(string? deviceId)
    {
        Assert.False(LinkMonitor.TryParseAddress(deviceId, out ulong address));

        // Zero, not a stale value. 000000000000 is a real address a radio reports before it has
        // initialised - BluetoothDeviceId rejects it as a colliding token for exactly that reason -
        // so an out parameter left holding whatever the caller passed in would be a plausible-looking
        // address rather than an obvious miss.
        Assert.Equal(0UL, address);
    }

    [Fact]
    public void IsWatchedDevice_matches_the_watched_id_regardless_of_case()
    {
        // Windows device interface ids are case-insensitive, and the id the watcher hands back is not
        // guaranteed to be byte-identical to the one that came out of FindAllAsync and got persisted
        // in Settings. An ordinal comparison here would drop every edge for a phone that is present,
        // which reads exactly like a phone that is not - and nothing else in the app would contradict
        // it until the 30 s reconcile.
        Assert.True(
            LinkMonitor.IsWatchedDevice(
                @"\\?\BTHENUM#{0000110a-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&C01C6A90E174_C00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\SNK",
                @"\\?\BTHENUM#{0000110A-0000-1000-8000-00805F9B34FB}_VID&000100E0_PID&4111#B&62612BF&0&C01C6A90E174_C00000000#{6994AD04-93EF-11D0-A3CC-00A0C9223196}\SNK"));
    }

    [Fact]
    public void IsWatchedDevice_rejects_another_device()
    {
        // The whole point of watching one id. The A2DP selector matches every paired phone, so an
        // unfiltered watcher would report a second phone's arrival as the selected phone's - the
        // arbitrary-pairing failure BluetoothDeviceId exists to remove, arriving through the watcher
        // instead of through the transport.
        // Both real, both from this machine: the phone and the Pro Controller.
        Assert.False(
            LinkMonitor.IsWatchedDevice(
                @"BTHENUM\DEV_C01C6A90E174\B&62612BF&0&BLUETOOTHDEVICE_C01C6A90E174",
                @"BTHENUM\DEV_98B60E383721\B&62612BF&0&BLUETOOTHDEVICE_98B60E383721"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsWatchedDevice_matches_nothing_when_no_device_is_watched(string? watched)
    {
        // A DeviceWatcher can still deliver a queued event after it has been stopped - LinkMachine
        // guards the same case from the other side. Matching on a null or empty watched id would let
        // a stale edge resurrect a phone the user just cleared.
        Assert.False(
            LinkMonitor.IsWatchedDevice(watched, @"BTHENUM\DEV_C01C6A90E174\B&62612BF&0&BLUETOOTHDEVICE_C01C6A90E174"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void IsWatchedDevice_never_matches_two_absent_ids(string? watched, string? candidate)
    {
        // The guard's whole reason for existing, and the one input shape where deleting it changes
        // the answer: string.Equals(null, null) is true, so an unguarded comparison would report
        // "nothing is watched" as a match for "a device with no id" - a stopped watcher's queued
        // event turned into a phone-appeared edge.
        Assert.False(LinkMonitor.IsWatchedDevice(watched, candidate));
    }

    [Fact]
    public void Translate_maps_both_WinRT_connection_values()
    {
        // The WinRT side first, or this test's name is only half true. Translate collapses everything
        // that is not Connected to Disconnected, so a third value in a future projection would be
        // mapped silently rather than caught. Same assertion, same reasoning, as
        // AudioSinkServiceContractTests on AudioPlaybackConnectionState.
        Assert.Equal(2, Enum.GetValues<BluetoothConnectionStatus>().Length);

        // Reading these constants touches no ABI - the projection renders them as plain C#
        // constants - which is what makes this safe in a host where activating a BluetoothDevice
        // is not.
        Assert.Equal(BluetoothLinkStatus.Disconnected, LinkMonitor.Translate(BluetoothConnectionStatus.Disconnected));
        Assert.Equal(BluetoothLinkStatus.Connected, LinkMonitor.Translate(BluetoothConnectionStatus.Connected));
    }

    [Fact]
    public void Translate_never_returns_Unknown()
    {
        // Unknown means "the read produced no answer". A ConnectionStatus in hand is an answer, so
        // mapping one to Unknown would make a genuinely disconnected phone indistinguishable from a
        // failed read - and the suppression latch tells deliberate disconnect from range exit on
        // exactly that distinction.
        Assert.DoesNotContain(
            Enum.GetValues<BluetoothConnectionStatus>(),
            status => LinkMonitor.Translate(status) == BluetoothLinkStatus.Unknown);
    }
}
