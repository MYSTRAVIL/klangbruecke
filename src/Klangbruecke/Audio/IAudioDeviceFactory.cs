using NAudio.Wave;

namespace Klangbruecke.Audio;

/// <summary>
/// A render endpoint as the tray menu needs it: an id to persist and a name to draw.
///
/// Deliberately a copy rather than the <c>MMDevice</c> it was read from. The menu outlives the
/// enumeration that produced it - it is rebuilt on every right-click and its items are captured by
/// click handlers - and every property on a live endpoint object is a COM call that can start
/// throwing the moment the device is unplugged.
/// </summary>
public readonly record struct AudioOutputDevice(string Id, string Name);

/// <summary>
/// One capture endpoint, reduced to what <see cref="AudioRouter"/> asks of it.
///
/// Implementations must raise both events with <c>this</c> as the sender. The router compares
/// <c>sender</c> against the instance it is holding and drops anything else as stale - see
/// <see cref="AudioRouter"/>'s recording-stopped handler - so an adapter that forwarded the inner
/// NAudio object as sender would fail that comparison on every raise, and a dead capture would
/// never tear the route down. That failure is silent: nothing throws, the tray keeps claiming it
/// is routing, and the A2DP endpoint stays held open.
/// </summary>
public interface ICaptureSource : IDisposable
{
    WaveFormat WaveFormat { get; }
    string FriendlyName { get; }
    event EventHandler<WaveInEventArgs>? DataAvailable;
    event EventHandler<StoppedEventArgs>? RecordingStopped;
    void StartRecording();
    void StopRecording();
}

/// <summary>
/// One render endpoint, reduced to what <see cref="AudioRouter"/> asks of it.
///
/// The same sender contract as <see cref="ICaptureSource"/> applies to
/// <see cref="PlaybackStopped"/>, and for the same reason.
///
/// <see cref="MixFormat"/> is a property rather than something the factory reads once because
/// reading it is itself a plausible failure point - it activates the audio client on first touch -
/// and the router's failure path is written around that read being the thing that threw.
/// </summary>
public interface IRenderSink : IDisposable
{
    WaveFormat MixFormat { get; }
    string FriendlyName { get; }
    event EventHandler<StoppedEventArgs>? PlaybackStopped;
    void Init(IWaveProvider source);
    void Play();
}

/// <summary>
/// Where endpoints come from. The seam that keeps WASAPI out of <see cref="AudioRouter"/>.
///
/// The router's hazards - the publish-before-start ordering, the stale-session checks, the
/// posted teardown - are all orderings between events that only real hardware used to be able to
/// raise. Everything COM-shaped lives behind this interface so a test can raise them instead;
/// see <see cref="WasapiDeviceFactory"/> for the only implementation that talks to a device.
/// </summary>
public interface IAudioDeviceFactory
{
    /// <summary>Null when no A2DP SNK capture endpoint is present.</summary>
    ICaptureSource? CreateSinkCapture();

    /// <summary>Falls back to the default multimedia render endpoint. Null when there is none.</summary>
    IRenderSink? CreateRender(string? preferredOutputDeviceId);

    IReadOnlyList<AudioOutputDevice> ListOutputs();
}
