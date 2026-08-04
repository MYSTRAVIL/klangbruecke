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

            _output = new WasapiOut(sink, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
            _output.Init(_buffer);

            _capture.StartRecording();
            _output.Play();

            IsRunning = true;
            Report($"Routing '{source.FriendlyName}' -> '{sink.FriendlyName}'.");
            return true;
        }
        catch (Exception ex)
        {
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
            Report($"Capture stopped: {e.Exception.Message}");
        }

        IsRunning = false;
    }

    public void Stop()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;

            try
            {
                _capture.StopRecording();
            }
            catch (Exception)
            {
                // Already stopped or the endpoint vanished with the connection.
            }

            _capture.Dispose();
            _capture = null;
        }

        _output?.Dispose();
        _output = null;
        _buffer = null;
        IsRunning = false;
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
