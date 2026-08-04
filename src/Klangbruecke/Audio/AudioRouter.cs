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
    /// <summary>
    /// One Start..Stop session: both its identity and whether it is still alive.
    ///
    /// Liveness belongs here rather than in a field of the router because a worker thread can be
    /// preempted between reading the current session and marking it dead. A router-level flag written
    /// at that point lands on whichever session is current when the write finally retires, and after
    /// an intervening Stop/Start pair that is a healthy new one - so IsRunning would report a live
    /// route as dead. The one consumer is TrayContext.SelectOutput, which would then silently decline
    /// to re-point the stream, with no status line to say why. Scoped to the session, the same write
    /// cannot escape the session it describes.
    /// </summary>
    private sealed class Session
    {
        // Set on NAudio's worker threads, read on the UI thread. Volatile for the release/acquire
        // edge, not for atomicity.
        public volatile bool Dead;
    }

    private readonly IUiDispatcher _ui;

    // Volatile: both are read from NAudio's capture and play threads by the sender guards below, and
    // written on the thread that runs Start and Stop.
    private volatile WasapiCapture? _capture;
    private volatile WasapiOut? _output;

    private BufferedWaveProvider? _buffer;

    /// <summary>
    /// The current session, or null between Stop and the next Start. Also the authority on whether a
    /// deferred teardown is still wanted: a stopped event can be in flight while the UI thread
    /// replaces the session, so by the time the teardown runs the fields it would tear down may
    /// belong to a healthy new route.
    /// </summary>
    private volatile Session? _session;

    private bool _disposed;

    /// <summary>
    /// The dispatcher is not a convenience. RequestTeardown is free of deadlock only because the
    /// dispatcher actually defers work off the thread that raised the stopped event - see there for
    /// what happens otherwise. <see cref="ImmediateUiDispatcher"/> runs every action inline and would
    /// reinstate the self-join in full, so it is safe here only in a test that never starts a route.
    /// </summary>
    public AudioRouter(IUiDispatcher ui) => _ui = ui;

    public bool IsRunning => _session is { Dead: false };

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

            // Before either worker thread exists, so no stopped event can be raised against a session
            // that has not been published yet and be discarded as stale. This is also what makes
            // IsRunning true, a few statements earlier than the old flag did; the difference is not
            // observable, because any failure below reaches Stop and nulls it again.
            _session = new Session();

            _capture.StartRecording();
            _output.Play();

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
        // Mirrors the playback guard below: this can be raised after Stop dropped this capture or a
        // later Start replaced it, and reporting that session dead would describe the wrong one.
        // NAudio snapshots the delegate before raising, so unsubscribing in Stop does not reliably
        // prevent this call - which is why the guard exists at all, and why _capture is volatile:
        // this is the one check the session token cannot back up, because it runs before it.
        if (!ReferenceEquals(sender, _capture))
        {
            return;
        }

        if (e.Exception is not null)
        {
            Log.Error("Capture stopped.", e.Exception);
            Report($"Capture stopped: {e.Exception.Message}");
        }

        RequestTeardown("capture");
    }

    /// <summary>
    /// WasapiOut turns play-thread failures into this event and nothing else. Unhandled, a dead
    /// stream leaves IsRunning set and the tray still claiming it is routing.
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Raised via SynchronizationContext.Post, so it can arrive after Stop dropped this output
        // or a later Start replaced it. Reporting then would describe the wrong session as dead.
        // Volatile for the same reason as the capture guard above.
        if (!ReferenceEquals(sender, _output))
        {
            return;
        }

        if (e.Exception is not null)
        {
            Log.Error("Playback stopped.", e.Exception);
            Report($"Playback stopped: {e.Exception.Message}");
        }

        RequestTeardown("playback");
    }

    /// <summary>
    /// Stops both halves after either one dies, on the dispatcher thread rather than here.
    ///
    /// Half a session is worse than none. A dead capture leaves WasapiOut playing the zero-fill a
    /// drained BufferedWaveProvider returns; a dead output leaves WasapiCapture recording into a
    /// buffer nobody drains, and because DiscardOnBufferOverflow is set that is a callback spinning
    /// forever, throwing every buffer away, while holding the A2DP capture endpoint open. That
    /// endpoint is how the app proves it is connected at all (docs/FINDINGS.md section 4), and this
    /// is the phone-initiated-reconnect path.
    ///
    /// The teardown must not run on the thread that raised the event, and this is measured against
    /// NAudio 2.2.1 on this machine rather than reasoned about. WasapiOut captures
    /// SynchronizationContext.Current in its constructor and, when that was null, calls the handler
    /// directly on its play thread. WasapiOut.Dispose -> Stop then does playThread.Join() whenever
    /// playbackState is not already Stopped. Joining the running thread from itself parks it forever:
    /// a probe against the real library hung there and never returned. Calling Stop() from this
    /// handler would therefore have introduced the deadlock the old do-nothing handlers avoided by
    /// accident.
    ///
    /// Do not narrow that to "only when it threw". PlayThread has exactly one assignment of Stopped,
    /// on the single fall-through at the end of its try, so every abnormal exit reaches the finally
    /// still marked Playing. That includes an early return when FillBuffer reports end-of-stream, and
    /// it includes a throw from anywhere in the body - which is wide: the DMO resampler construction,
    /// audioClient.BufferSize, Start, CurrentPadding, every GetBuffer/ReleaseBuffer COM call, and
    /// audioClient.Stop() itself, which runs one line *before* the Stopped assignment. An endpoint
    /// that vanishes mid-stream, which is what a phone walking out of range looks like, lands there.
    /// The end-of-stream return happens to be dormant for us - BufferedWaveProvider.ReadFully
    /// defaults to true, so its Read pads with zeroes and never returns 0 - but that is a default on
    /// a different class, not a property of this one, and it is not what makes this safe.
    ///
    /// Posting through the dispatcher removes it in both configurations. With no
    /// SynchronizationContext the event arrives on the play thread, ControlUiDispatcher sees
    /// InvokeRequired and defers to the UI thread, and the play thread runs to completion - so the
    /// later Join finds a thread that has already exited. With one installed - and a Control
    /// constructor does install it, so TrayContext has one today - the event already arrives on the
    /// UI thread, Post runs inline, and the Join is on a different thread and returns at once. It
    /// also keeps teardown on the one thread that already serialises Start, Stop and Dispose, so no
    /// new concurrency domain appears.
    ///
    /// The capture half does not have the same hazard - WasapiCapture nulls its thread field before
    /// raising, so Dispose finds nothing to join - but it is routed the same way rather than relying
    /// on that asymmetry holding.
    /// </summary>
    private void RequestTeardown(string half)
    {
        Session? session = _session;
        if (session is null)
        {
            // Stop already ran. Nothing to tear down, and posting would only risk killing whatever
            // starts next.
            return;
        }

        // Eagerly, because the teardown below is deferred and until it lands the tray must not go on
        // claiming the route is live. Written through the token rather than to a field of the router,
        // so that a Stop/Start pair completing between the read above and this write cannot mark the
        // new session dead; see Session.
        session.Dead = true;

        _ui.Post(() =>
        {
            // The window between the post and here is exactly long enough for a menu click to have
            // started a new route. Tearing that one down would turn a recoverable failure into a
            // silent one, and this is also what makes a second post - the other half reporting the
            // same death - a no-op instead of a double teardown.
            if (!ReferenceEquals(session, _session))
            {
                return;
            }

            Log.Warn($"Tearing the route down: the {half} half stopped.");
            Stop();
        });
    }

    public void Stop()
    {
        // First, and this ordering is load-bearing twice over. It makes a stopped event racing this
        // teardown read null and decline to post another - and, less obviously, it is what makes a
        // teardown that is *already* queued harmless. Thread.Join on an STA thread pumps, so the
        // Dispose calls below can dispatch that queued lambda from inside themselves; it finds a null
        // session, returns, and never re-enters this method. Do not move this after the unsubscribes.
        _session = null;

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;

            // Already stopped, or the endpoint vanished with the connection.
            Teardown.Quietly(_capture.StopRecording, "stop capture");
            Teardown.Quietly(_capture.Dispose, "dispose capture");
            _capture = null;
        }

        if (_output is not null)
        {
            // Before Dispose, which joins the play thread that raises it. A deliberate teardown is
            // not a failure, and reporting it as one would overwrite the real status.
            _output.PlaybackStopped -= OnPlaybackStopped;

            Teardown.Quietly(_output.Dispose, "dispose output");
            _output = null;
        }

        _buffer = null;
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
