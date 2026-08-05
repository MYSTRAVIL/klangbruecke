using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;
using Klangbruecke.Tests.Diagnostics;
using Windows.Media.Audio;
using Xunit;

namespace Klangbruecke.Tests.Bluetooth;

/// <summary>
/// The part of <see cref="IAudioSinkService"/> that can be exercised without a phone.
///
/// No test here opens a connection, and none may: unpackaged,
/// <c>AudioPlaybackConnection.TryCreateFromId</c> terminates the process with an
/// AccessViolationException raised inside the CsWinRT ABI shim - a corrupted-state exception that no
/// managed handler sees. In this assembly that would not fail a test, it would kill the test host and
/// abort the run. See docs/FINDINGS.md §8 and <see cref="AudioSinkPolicy"/>.
/// </summary>
public sealed class AudioSinkServiceContractTests : IDisposable
{
    private readonly ILog _original = Log.Current;
    private readonly RecordingLog _log = new();
    private readonly AudioSinkService _sink = new();

    public AudioSinkServiceContractTests() => Log.Current = _log;

    public void Dispose()
    {
        Log.Current = _original;
        _sink.Dispose();
    }

    [Fact]
    public void Disconnect_on_a_fresh_service_does_not_throw()
    {
        // Stage 1's teardown paths call Disconnect without knowing whether anything is held - a
        // reconnect that gave up, a deliberate tray disconnect, a state machine unwinding. A throw
        // from the never-connected case would make every one of those conditional at the call site.
        Assert.Null(Record.Exception(() => _sink.Disconnect()));
    }

    [Fact]
    public void IsConnected_is_false_before_connecting()
    {
        // Scoped to the WinRT connection object and nothing else. It is deliberately NOT a claim
        // about the "Line (<phone> A2DP SNK)" capture endpoint: the app's own log shows that endpoint
        // absent for an unbounded interval after the connection reports Opened, and folding the two
        // facts into one property makes that interval unrepresentable. IAudioEndpointMonitor owns the
        // endpoint half.
        Assert.False(_sink.IsConnected);
    }

    [Fact]
    public void ConnectedDeviceId_is_null_before_connecting()
    {
        Assert.Null(_sink.ConnectedDeviceId);
    }

    /// <summary>
    /// The guard at the top of <c>ConnectAsync</c>, from the outside.
    ///
    /// "Without reaching TryCreateFromId" is asserted by the only instrument that exists for it:
    /// every line below the await runs, which it could not do if the call had been reached - the
    /// process would be gone, taking the whole run with it and reporting no result at all. That is
    /// also why this test can never be made to assert the crash directly.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_returns_false_unpackaged_without_reaching_TryCreateFromId()
    {
        // The premise, asserted rather than assumed. If a future host ever ran packaged this test
        // would silently start exercising the opposite path - and that path opens real hardware.
        Assert.False(PackageIdentity.IsPackaged);

        bool opened = await _sink.ConnectAsync("bluetooth#bluetoothaa:bb:cc:dd:ee:ff-11:22:33:44:55:66");

        Assert.False(opened);

        // Reaching here at all is the "process survived" assertion.
        Assert.False(_sink.IsConnected);
        Assert.Null(_sink.ConnectedDeviceId);

        // Named, because the line exists to tell a reader that a caller skipped the policy rather
        // than that the machine is unpackaged - the two are different defects and only one is a bug
        // in this codebase. Warn regardless of AudioSinkPolicy.LevelFor: TrayContext returns before
        // ever calling here, so this entry and the tray's own verdict can never appear in one run.
        Assert.Contains(
            _log.Entries,
            e => e.Level == LogLevel.Warn && e.Message.Contains("AudioSinkPolicy"));
    }

    // A contract marker, labelled as one because it cannot currently fail. Deleting the
    // `if (_disposed) return;` guard from Dispose leaves every test in this class green: Disconnect()
    // is itself idempotent, so a second pass through Dispose is observably a no-op either way. The
    // toolchain does not catch it either - the deletion emits CS0414 (field assigned but never used),
    // and there is no TreatWarningsAsErrors anywhere in this repo, so the build simply succeeds.
    //
    // So do not read a green run here as evidence that the guard is present. It becomes load-bearing,
    // and this test becomes capable of failing, the moment Dispose does something Disconnect does
    // not - and whoever makes that change owns the assertion for it.
    [Fact]
    public void Dispose_is_idempotent()
    {
        var sink = new AudioSinkService();
        sink.Dispose();

        Assert.Null(Record.Exception(sink.Dispose));
    }

    [Fact]
    public void AudioSinkConnectionState_maps_both_WinRT_values()
    {
        // The WinRT side first, or this test's name is only half true. Translate's else branch
        // collapses everything that is not Opened to Closed, so a third value arriving in a future
        // projection would be mapped silently rather than caught. This is the assertion that would
        // catch it - verified live: expecting 3 fails with "Actual: 2".
        Assert.Equal(2, Enum.GetValues<AudioPlaybackConnectionState>().Length);

        // Both values, in the WinRT order, and only those two.
        Assert.Equal(
            new[] { AudioSinkConnectionState.Closed, AudioSinkConnectionState.Opened },
            Enum.GetValues<AudioSinkConnectionState>());

        Assert.NotEqual(AudioSinkConnectionState.Closed, AudioSinkConnectionState.Opened);

        // And the translation itself, which is the only reason the enum exists. Reading these enum
        // constants touches no ABI - the projection renders them as plain C# constants - so this is
        // safe in a host where activating the connection object is not.
        Assert.Equal(AudioSinkConnectionState.Closed, AudioSinkService.Translate(AudioPlaybackConnectionState.Closed));
        Assert.Equal(AudioSinkConnectionState.Opened, AudioSinkService.Translate(AudioPlaybackConnectionState.Opened));
    }

    // --- beyond the brief's six: the "both, not either" contract on a state read ---
    //
    // The brief states twice that StateChanged is raised *in addition to* the existing Report, not
    // instead of it, and a WinRT AudioPlaybackConnection cannot be constructed without a phone - so
    // without these two, either line could be deleted and the suite would stay green. See
    // AudioSinkService.PublishState for why the seam is shaped the way it is.

    [Fact]
    public void A_connection_state_read_is_reported_as_status()
    {
        var seen = new List<StatusMessage>();
        _sink.Status += (_, m) => seen.Add(m);

        _sink.PublishState(AudioPlaybackConnectionState.Opened);

        Assert.Equal(new StatusMessage("A2DP sink state: Opened", LogLevel.Info), Assert.Single(seen));
    }

    [Fact]
    public void A_connection_state_read_also_raises_StateChanged()
    {
        var seen = new List<AudioSinkConnectionState>();
        _sink.StateChanged += (_, s) => seen.Add(s);

        _sink.PublishState(AudioPlaybackConnectionState.Closed);

        Assert.Equal(AudioSinkConnectionState.Closed, Assert.Single(seen));
    }
}
