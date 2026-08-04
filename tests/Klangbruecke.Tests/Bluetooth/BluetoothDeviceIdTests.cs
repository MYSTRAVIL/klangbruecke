using Klangbruecke.Bluetooth;
using Xunit;

namespace Klangbruecke.Tests.Bluetooth;

public sealed class BluetoothDeviceIdTests
{
    [Fact]
    public void TryExtractAddress_FindsTheAddressInABthenumId()
    {
        string id = @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002\7&1a2b3c4d&0&0018092E5A5D_C00000000";

        Assert.Equal("0018092E5A5D", BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_IgnoresTheHexTailOfAGuid()
    {
        // 00805f9b34fb is 12 hex digits and would be matched by a naive scan.
        string id = @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}";

        Assert.Null(BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_HandlesColonSeparatedForm()
    {
        string id = "Bluetooth#Bluetoothf8:e4:e3:11:22:33-00:18:09:2e:5a:5d";

        Assert.Equal("0018092E5A5D", BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_UppercasesTheResult()
    {
        string id = @"BTHENUM\7&1a2b3c4d&0&0018092e5a5d_C00000000";

        Assert.Equal("0018092E5A5D", BluetoothDeviceId.TryExtractAddress(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no address here")]
    public void TryExtractAddress_ReturnsNullWhenAbsent(string? id)
    {
        Assert.Null(BluetoothDeviceId.TryExtractAddress(id));
    }

    // The two ids this class exists to reconcile, both observed. AudioPlaybackConnection's selector
    // returns the first and PhoneLineTransportDevice.GetDeviceSelector() the second, for one phone.
    // Different profile GUIDs (110a AudioSource against 111f HandsfreeAudioGateway) and different
    // interface classes: the address is the only token they share, which is the premise of Task 8.
    // Corroborated independently against HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses.
    [Theory]
    [InlineData(@"\\?\BTHENUM#{0000110a-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&C01C6A90E174_C00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\SNK")]
    [InlineData(@"\\?\BTHENUM#{0000111f-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&C01C6A90E174_C00000000#{bd41df2d-addd-4fc9-a194-b9881d2a2efa}\service")]
    public void TryExtractAddress_ReducesBothSelectorsToTheSameAddress(string id)
    {
        Assert.Equal("C01C6A90E174", BluetoothDeviceId.TryExtractAddress(id));
    }

    // Observed on this machine via Get-PnpDevice, not invented: C01C6A90E174 and 98B60E383721 are
    // classic paired devices, 3CFA06150692 a BLE one. These are the only id shapes anyone has
    // actually seen here; every other test below rests on a plausible shape, not a recorded one.
    [Theory]
    [InlineData(@"BTHENUM\{00001105-0000-1000-8000-00805F9B34FB}_VID&000100E0_PID&4111\B&62612BF&0&C01C6A90E174_C00000000", "C01C6A90E174")]
    [InlineData(@"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002057E_PID&2009\B&62612BF&0&98B60E383721_C00000000", "98B60E383721")]
    [InlineData(@"BTHENUM\{A82EFA21-AE5C-3DDE-9BBC-F16DA7B16C5A}_VID&000100E0_PID&4111\B&62612BF&0&C01C6A90E174_C00000000", "C01C6A90E174")]
    [InlineData(@"BTHENUM\DEV_98B60E383721\B&62612BF&0&BLUETOOTHDEVICE_98B60E383721", "98B60E383721")]
    [InlineData(@"BTHLE\DEV_3CFA06150692\B&1EBF6DE0&0&3CFA06150692", "3CFA06150692")]
    [InlineData(@"BTHLEDEVICE\{00000001-5F60-4C4F-9C83-A7953298D40D}_DEV_VID&02045E_PID&0B22_REV&0517_3CFA06150692\C&391C7DA3&0&002C", "3CFA06150692")]
    public void TryExtractAddress_ReadsObservedDeviceIds(string id, string expected)
    {
        Assert.Equal(expected, BluetoothDeviceId.TryExtractAddress(id));
    }

    // Also observed here. The MMDEVAPI id is the sharp one: its interface GUID ends in 3922FD77EEDC,
    // so a scan that skipped the brace strip would hand back an audio endpoint's GUID tail as if it
    // were a phone. The HFP audio node carries no address at all.
    [Theory]
    [InlineData(@"BTHHFENUM\BTHHFPAUDIO\C&2C490825&1&97")]
    [InlineData(@"BTH\MS_BTHBRB\A&19565C58&0&1")]
    [InlineData(@"SWD\MMDEVAPI\{0.0.0.00000000}.{1402881C-7506-422B-85EC-3922FD77EEDC}")]
    [InlineData(@"USB\VID_0E8D&PID_0616&MI_00\9&13C806E8&0&0000")]
    public void TryExtractAddress_ReturnsNullForObservedIdsWithoutAnAddress(string id)
    {
        Assert.Null(BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_IgnoresTheTailOfATrailingInterfaceGuid()
    {
        // 6994ad04-...-00a0c9223196 is the stock Bluetooth device interface class, and its last group
        // is 12 hex. Braces here are balanced, so this pins the brace strip - not the first-match
        // rule, which the test below covers.
        string id = @"\\?\BTHENUM#{0000110b-0000-1000-8000-00805f9b34fb}_VID&000100e0_PID&4111#b&62612bf&0&c01c6a90e174_c00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}";

        Assert.Equal("C01C6A90E174", BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_PrefersTheAddressToGuidDebrisWhenTheBraceStripIsDefeated()
    {
        // The hazard the first-match rule is actually for, and the only case where first and last
        // diverge on a realistic id. An unclosed brace - a truncated log line - leaves the interface
        // GUID in place, so a second 12-hex run survives after the address. Last match would return
        // 00A0C9223196.
        string id = @"BTHENUM\DEV_C01C6A90E174\{6994ad04-93ef-11d0-a3cc-00a0c9223196";

        Assert.Equal("C01C6A90E174", BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_DoesNotFuseTheTextEitherSideOfAStrippedSection()
    {
        // Pins the space in the brace substitution. Replacing with string.Empty instead joins ABCDEF
        // to 001122 and yields ABCDEF001122 - an address assembled out of two runs that were never
        // adjacent. Not a real id shape; a guard on the substitution.
        string id = @"BTHENUM\ABCDEF{0000110b-0000-1000-8000-00805f9b34fb}001122";

        Assert.Null(BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_DoesNotMatchInsideALongerHexRun()
    {
        // An 18-digit run has no 12-digit window that is not flanked by hex, so this is not an
        // address that happens to be prefixed - it is not an address at all.
        string id = @"BTHENUM\ABCDEF0018092E5A5D";

        Assert.Null(BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_AnchorsWhereAWordBoundaryWouldNot()
    {
        // 'x' and '0' are both word characters, so \b never fires between them. Real ids glue the
        // address to a word this way, as "Bluetooth#Bluetoothf8:..." does.
        Assert.Equal("0018092E5A5D", BluetoothDeviceId.TryExtractAddress("Bluetoothx0018092e5a5d"));
    }

    [Fact]
    public void TryExtractAddress_DoesNotStitchTwoSeparatedAddressesTogether()
    {
        // A guard on the regex rather than on any real id. Letting ':' and '-' mix inside one match
        // lets the pattern take the head of one address and the tail of the next: here it would
        // return 112233445566, a value belonging to no device. Only an offset boundary like this
        // one - where the run does not start on a pair boundary - exercises the backreference.
        string id = "x11:22:33:44-55-66-77-88-99:aa:bb";

        Assert.Equal("445566778899", BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_TakesTheRemoteHalfOfADashSeparatedPair()
    {
        // "<local radio>-<remote device>", the one shape known to carry two addresses. The phone is
        // the second, so the last match wins - the first is this PC's own radio.
        //
        // This is also where \b does its real damage. Unable to anchor at f8, it starts mid-address
        // and returns the single stitched run e4-e3-11-22-33-00 - one wrong answer with nothing to
        // suggest it is wrong. The lookarounds, not the backreference, are what stop that.
        string id = "Bluetooth#Bluetoothf8-e4-e3-11-22-33-00-18-09-2e-5a-5d";

        Assert.Equal("0018092E5A5D", BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_TakesTheFirstOfTwoBareRuns()
    {
        // Pins the deliberate asymmetry with the separated form above, on a synthetic id. No observed
        // id carries two different bare addresses; the runs that do follow one are GUID tails, which
        // is why the earlier run is the safer guess here and the later one is there.
        string id = @"BTHENUM\DEV_0018092E5A5D\7&1a2b3c4d&0&C01C6A90E174_C00000000";

        Assert.Equal("0018092E5A5D", BluetoothDeviceId.TryExtractAddress(id));
    }

    // Returning either of these would let every id carrying it match every other, which is the
    // arbitrary pairing this class exists to prevent - and it would do so silently, which is why
    // they are worth rejecting even though no observed id reaches them. The last two rows are the
    // two ways to defeat the brace strip and expose the Bluetooth base UUID tail, which every
    // classic profile GUID on this machine ends in.
    [Theory]
    [InlineData("Bluetooth#Bluetooth00:00:00:00:00:00")]
    [InlineData(@"BTHENUM\DEV_000000000000\B&62612BF&0&BLUETOOTHDEVICE_000000000000")]
    [InlineData(@"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb")]
    [InlineData(@"BTHENUM\{outer{inner}00805f9b34fb}")]
    public void TryExtractAddress_RejectsTokensThatWouldMatchEveryDevice(string id)
    {
        Assert.Null(BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_PrefersTheSeparatedFormOverABareRun()
    {
        // Pins the precedence rather than claiming it is right: no id is known to carry both, so if
        // one turns up in the Task 9 logs this is the test to revisit.
        string id = "Bluetooth#Bluetoothf8:e4:e3:11:22:33-00:18:09:2e:5a:5d#C01C6A90E174";

        Assert.Equal("0018092E5A5D", BluetoothDeviceId.TryExtractAddress(id));
    }
}
