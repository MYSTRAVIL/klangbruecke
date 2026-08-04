using Klangbruecke.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Klangbruecke.Audio;

/// <summary>
/// Bridges the A2DP sink capture endpoint to a chosen render endpoint.
///
/// The per-app volume mixer cannot redirect this stream, which is why the routing is done
/// in-process rather than by asking Windows to move it.
/// </summary>
public sealed class AudioRouter : IDisposable
{
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private bool _disposed;

    public bool IsRunning { get; private set; }

    public event EventHandler<string>? Status;

    private void Report(string message) => Status?.Invoke(this, message);

    /// <summary>The capture endpoint Windows creates while an A2DP sink connection is open.</summary>
    public static MMDevice? FindSinkCaptureEndpoint()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains("A2DP", StringComparison.OrdinalIgnoreCase)
                              || d.FriendlyName.Contains("SNK", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<MMDevice> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    public static MMDevice? GetOutputDeviceOrDefault(string? deviceId)
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

    public bool Start(MMDevice source, MMDevice sink)
    {
        Stop();

        // Declared outside the try so the failure path can name both formats. Reading it is
        // itself a plausible failure point, hence the null until it is known.
        WaveFormat? outputFormat = null;

        try
        {
            _capture = new WasapiCapture(source);
            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                // Enough slack to ride out scheduling hiccups without adding audible latency.
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
            };

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            outputFormat = sink.AudioClient.MixFormat;

            // Unconditional. This pair is the first thing anyone needs when routing misbehaves on
            // hardware that cannot be reproduced here, and it is worth as much when the formats
            // match as when they do not.
            Log.Info($"Capture={AudioFormatBridge.Describe(_capture.WaveFormat)} " +
                     $"Render={AudioFormatBridge.Describe(outputFormat)}" +
                     (AudioFormatBridge.Differ(_capture.WaveFormat, outputFormat)
                         ? " - differ, WASAPI shared mode is converting."
                         : " - matched."));

            _output = new WasapiOut(sink, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Init(_buffer);

            _capture.StartRecording();
            _output.Play();

            IsRunning = true;
            Report($"Routing '{source.FriendlyName}' -> '{sink.FriendlyName}'.");
            return true;
        }
        catch (Exception ex)
        {
            // Repeat both formats here rather than relying on the Info line above: the failure can
            // be the MixFormat read itself, in which case that line never ran.
            string capture = _capture is null ? "unknown" : AudioFormatBridge.Describe(_capture.WaveFormat);
            string render = outputFormat is null ? "unknown" : AudioFormatBridge.Describe(outputFormat);

            Log.Error($"Routing failed. Capture={capture} Render={render}", ex);

            Report($"Could not start routing: {ex.Message}");
            Stop();
            return false;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Log.Error("Capture stopped.", e.Exception);
            Report($"Capture stopped: {e.Exception.Message}");
        }

        IsRunning = false;
    }

    /// <summary>
    /// WasapiOut turns play-thread failures into this event and nothing else. Unhandled, a dead
    /// stream leaves IsRunning set and the tray still claiming it is routing.
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Raised via SynchronizationContext.Post, so it can arrive after Stop dropped this output
        // or a later Start replaced it. Clearing IsRunning then would report the wrong session dead.
        if (!ReferenceEquals(sender, _output))
        {
            return;
        }

        if (e.Exception is not null)
        {
            Log.Error("Playback stopped.", e.Exception);
            Report($"Playback stopped: {e.Exception.Message}");
        }

        IsRunning = false;
    }

    public void Stop()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;

            // Already stopped, or the endpoint vanished with the connection.
            Quietly(_capture.StopRecording, "stop capture");
            Quietly(_capture.Dispose, "dispose capture");
            _capture = null;
        }

        if (_output is not null)
        {
            // Before Dispose, which joins the play thread that raises it. A deliberate teardown is
            // not a failure, and reporting it as one would overwrite the real status.
            _output.PlaybackStopped -= OnPlaybackStopped;

            Quietly(_output.Dispose, "dispose output");
            _output = null;
        }

        _buffer = null;
        IsRunning = false;
    }

    /// <summary>
    /// Teardown must finish even if a step fails. Stop runs from Start's catch block and from
    /// TrayContext.Dispose - the latter during Application.Run teardown, outside the message loop's
    /// exception guard, where a throw escapes Main and dies with a WER dialog. A throw that skipped
    /// the null-out after it would also leave a dead object in the field, so every later Start
    /// would fail on it and routing would never recover without a restart.
    /// </summary>
    private static void Quietly(Action step, string what)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Log.Warn($"Ignoring failure to {what}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
