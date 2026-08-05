using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Klangbruecke.Audio;

/// <summary>
/// The only place in the app that talks to WASAPI endpoints.
///
/// Everything here is a thin wrapper over NAudio and is untested by design: exercising it needs a
/// live A2DP sink endpoint, which needs a phone. The behaviour worth pinning - what the router does
/// with what comes back - is tested against fakes of the interfaces above instead, which is the
/// entire reason this class exists as a separate object.
/// </summary>
public sealed class WasapiDeviceFactory : IAudioDeviceFactory
{
    public ICaptureSource? CreateSinkCapture()
    {
        MMDevice? device = FindSinkCaptureEndpoint();

        return device is null ? null : new WasapiCaptureSource(device);
    }

    public IRenderSink? CreateRender(string? preferredOutputDeviceId)
    {
        MMDevice? device = GetOutputDeviceOrDefault(preferredOutputDeviceId);

        return device is null ? null : new WasapiRenderSink(device);
    }

    /// <summary>
    /// Every active render endpoint, as ids and names rather than as live device objects.
    ///
    /// The enumerator is disposed before the strings are handed back on purpose: the caller is a
    /// menu that is rebuilt on every right-click, and holding endpoint objects open for the life of
    /// a <c>ToolStripMenuItem</c> would be a COM reference per device per menu open.
    /// </summary>
    public IReadOnlyList<AudioOutputDevice> ListOutputs()
    {
        using var enumerator = new MMDeviceEnumerator();

        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioOutputDevice(d.ID, d.FriendlyName))
            .ToList();
    }

    /// <summary>The capture endpoint Windows creates while an A2DP sink connection is open.</summary>
    private static MMDevice? FindSinkCaptureEndpoint()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains("A2DP", StringComparison.OrdinalIgnoreCase)
                              || d.FriendlyName.Contains("SNK", StringComparison.OrdinalIgnoreCase));
    }

    private static MMDevice? GetOutputDeviceOrDefault(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrEmpty(deviceId))
        {
            MMDevice? match = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.ID == deviceId);

            if (match is not null)
            {
                return match;
            }
        }

        return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : null;
    }
}

/// <summary>
/// <see cref="WasapiCapture"/> behind <see cref="ICaptureSource"/>.
///
/// The re-raises pass <c>this</c> because that is what an event raised by this object should say,
/// and for no other reason. Nothing downstream reads it: the router closes over the endpoint when it
/// subscribes rather than trusting the sender, precisely so that this line cannot quietly disable
/// teardown. See <see cref="ICaptureSource"/>.
/// </summary>
internal sealed class WasapiCaptureSource : ICaptureSource
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;

    public WasapiCaptureSource(MMDevice device)
    {
        _device = device;
        _capture = new WasapiCapture(device);

        _capture.DataAvailable += (_, e) => DataAvailable?.Invoke(this, e);
        _capture.RecordingStopped += (_, e) => RecordingStopped?.Invoke(this, e);
    }

    public WaveFormat WaveFormat => _capture.WaveFormat;

    public string FriendlyName => _device.FriendlyName;

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording() => _capture.StartRecording();

    public void StopRecording() => _capture.StopRecording();

    public void Dispose() => _capture.Dispose();
}

/// <summary>
/// <see cref="WasapiOut"/> behind <see cref="IRenderSink"/>.
///
/// The constructor arguments are the measured ones, not defaults: shared mode so WASAPI does the
/// format conversion itself (see <see cref="AudioFormatBridge"/>), event sync, 50 ms of latency.
///
/// Constructing <see cref="WasapiOut"/> here rather than in the router moves one thing that matters:
/// it captures <c>SynchronizationContext.Current</c> in its constructor, and that decides which
/// thread it raises <see cref="PlaybackStopped"/> on. It is still constructed on whichever thread
/// called <c>Start</c> - the UI thread in this app - so the captured context is the same one as
/// before. Do not move this construction onto a threadpool thread.
///
/// The sender note on <see cref="WasapiCaptureSource"/> applies here too.
/// </summary>
internal sealed class WasapiRenderSink : IRenderSink
{
    private readonly MMDevice _device;
    private readonly WasapiOut _output;

    public WasapiRenderSink(MMDevice device)
    {
        _device = device;
        _output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 50);

        _output.PlaybackStopped += (_, e) => PlaybackStopped?.Invoke(this, e);
    }

    /// <summary>
    /// Read through to the endpoint on every access rather than cached at construction, so a
    /// failure to read it surfaces where the router expects it - inside its own try, with the
    /// capture already in hand to name in the failure line.
    /// </summary>
    public WaveFormat MixFormat => _device.AudioClient.MixFormat;

    public string FriendlyName => _device.FriendlyName;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Init(IWaveProvider source) => _output.Init(source);

    public void Play() => _output.Play();

    public void Dispose() => _output.Dispose();
}
