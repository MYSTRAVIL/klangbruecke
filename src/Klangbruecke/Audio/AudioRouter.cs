using Klangbruecke.Diagnostics;
using NAudio.Wave;

namespace Klangbruecke.Audio;

/// <summary>
/// Bridges the A2DP sink capture endpoint to a chosen render endpoint.
///
/// The per-app volume mixer cannot redirect this stream, which is why the routing is done
/// in-process rather than by asking Windows to move it.
/// </summary>
public sealed class AudioRouter : IAudioRouter
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
    private readonly IAudioDeviceFactory _devices;

    // All three are volatile for the same reason: they are read on NAudio's capture and play threads -
    // _capture and _output by the sender guards below, _buffer by OnDataAvailable - and written on the
    // thread that runs Start and Stop. _buffer's stale read is the mildest of the three (samples added
    // to a dead but still-referenced buffer, which is then collected), but leaving one of three
    // unmarked would read as a considered decision that the other two were special.
    private volatile ICaptureSource? _capture;
    private volatile IRenderSink? _output;
    private volatile BufferedWaveProvider? _buffer;

    /// <summary>
    /// The current session, or null between Stop and the next Start. Also the authority on whether a
    /// deferred teardown is still wanted: a stopped event can be in flight while the UI thread
    /// replaces the session, so by the time the teardown runs the fields it would tear down may
    /// belong to a healthy new route.
    /// </summary>
    private volatile Session? _session;

    /// <summary>
    /// The two stopped subscriptions, kept only so <see cref="Stop"/> can take them off again.
    ///
    /// They are closures rather than method groups because each one carries the endpoint it was
    /// subscribed for into the handler; see <see cref="OnRecordingStopped"/> for why that identity
    /// must not come from the event's own sender. Written and read on the thread that runs Start and
    /// Stop, and on no other, so unlike the endpoint fields above they need no volatile.
    /// </summary>
    private EventHandler<StoppedEventArgs>? _onRecordingStopped;
    private EventHandler<StoppedEventArgs>? _onPlaybackStopped;

    private bool _disposed;

    /// <summary>
    /// The dispatcher is not a convenience. RequestTeardown is free of deadlock only because the
    /// dispatcher actually defers work off the thread that raised the stopped event - see there for
    /// what happens otherwise. <see cref="ImmediateUiDispatcher"/> runs every action inline and would
    /// reinstate the self-join in full, so it is safe here only in a test whose sink does not join
    /// anything.
    ///
    /// The factory is the seam that keeps every COM type out of this class. Nothing below constructs
    /// an endpoint, which is what makes the orderings in Start, the stopped handlers and Stop
    /// reachable from a test at all.
    /// </summary>
    public AudioRouter(IUiDispatcher ui, IAudioDeviceFactory devices)
    {
        _ui = ui;
        _devices = devices;
    }

    public bool IsRunning => _session is { Dead: false };

    public event EventHandler<StatusMessage>? Status;

    /// <summary>
    /// Raised after a route that died on its own has been torn down, on the dispatcher thread.
    /// See <see cref="IAudioRouter.Stopped"/> for why it is neither raised from the stopped handlers
    /// nor from a deliberate <see cref="Stop"/>.
    /// </summary>
    public event EventHandler? Stopped;

    /// <summary>
    /// Info unless said otherwise. The level travels with the message because this class is the only
    /// thing that knows it - see <see cref="StatusMessage"/>.
    /// </summary>
    private void Report(string message, LogLevel level = LogLevel.Info) =>
        Status?.Invoke(this, new StatusMessage(message, level));

    public bool Start(string? preferredOutputDeviceId)
    {
        Stop();

        // Declared outside the try so the failure path can name both formats. Reading it is
        // itself a plausible failure point, hence the null until it is known.
        WaveFormat? outputFormat = null;

        try
        {
            // Both lookups are inside the try, not before it. Opening an endpoint is the single most
            // failure-prone thing this method does - the phone can walk away between the enumeration
            // and the activation - and outside the try a throw would escape Start with the capture
            // half already open and nothing to close it.
            ICaptureSource? source = _devices.CreateSinkCapture();
            if (source is null)
            {
                // Per docs/FINDINGS.md section 4 this is the expected state when nothing is holding a
                // connection open, not a bug. It is also exactly what a failed connect looks like,
                // which is why the A2DP connect result is logged by the caller rather than inferred
                // from here.
                Report("No A2DP sink endpoint - nothing is holding a connection open.");
                return false;
            }

            // Published to the field immediately, before anything that can throw: the catch below
            // names the capture format, and it can only do that from a field it can still see.
            _capture = source;

            IRenderSink? sink = _devices.CreateRender(preferredOutputDeviceId);
            if (sink is null)
            {
                // Stop before Report, matching the catch below - but here it earns its place twice
                // over, because the capture above is already open and it is the handle that holds the
                // A2DP endpoint. Returning without it would leave the endpoint busy for a route that
                // never started, and that endpoint is how the app proves it is connected at all.
                Stop();
                Report("No usable output device.");
                return false;
            }

            _output = sink;

            // Both endpoint names, before the stream starts: a route that runs silently is almost
            // always the right source paired with the wrong sink, and a failure past this point may
            // never reach the status line that repeats them.
            Log.Info($"Routing source='{source.FriendlyName}' sink='{sink.FriendlyName}'.");

            _buffer = new BufferedWaveProvider(source.WaveFormat)
            {
                // Enough slack to ride out scheduling hiccups without adding audible latency.
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
            };

            source.DataAvailable += OnDataAvailable;

            // The capture goes into the handler from here rather than out of the event's sender.
            // See OnRecordingStopped.
            _onRecordingStopped = (_, e) => OnRecordingStopped(source, e);
            source.RecordingStopped += _onRecordingStopped;

            outputFormat = sink.MixFormat;

            // Unconditional. This pair is the first thing anyone needs when routing misbehaves on
            // hardware that cannot be reproduced here, and it is worth as much when the formats
            // match as when they do not.
            Log.Info($"Capture={AudioFormatBridge.Describe(source.WaveFormat)} " +
                     $"Render={AudioFormatBridge.Describe(outputFormat)}" +
                     (AudioFormatBridge.Differ(source.WaveFormat, outputFormat)
                         ? " - differ, WASAPI shared mode is converting."
                         : " - matched."));

            _onPlaybackStopped = (_, e) => OnPlaybackStopped(sink, e);
            sink.PlaybackStopped += _onPlaybackStopped;
            sink.Init(_buffer);

            // Before either worker thread exists, so no stopped event can be raised against a session
            // that has not been published yet and be discarded as stale. This is also what makes
            // IsRunning true, a few statements earlier than the old flag did; the catch below nulls it
            // before anything can observe the difference.
            _session = new Session();

            source.StartRecording();
            sink.Play();

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

            // Stop before Report, not after. A throw from StartRecording or Play lands here with the
            // session already published, so reporting first would raise Status synchronously while
            // IsRunning still reads true for a route that never started. Nothing in today's subscriber
            // chain reads it - but that is a fact about the current subscribers, not an invariant, and
            // this ordering makes it one. The format strings above are read before Stop on purpose:
            // Stop nulls _capture.
            Stop();

            // Error, matching the Log.Error above: at Info this was the second of two entries for one
            // throw, at two different levels, which is the specific way a severity column stops being
            // worth reading.
            Report($"Could not start routing: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    /// <summary>
    /// <paramref name="source"/> is the capture this handler was subscribed for, closed over by the
    /// subscription in <see cref="Start"/> rather than read out of the event's own sender.
    ///
    /// Taken from the event, the identity would be whatever the implementation chose to pass, and the
    /// guard below would be resting on a convention no compiler checks: an adapter that forwarded its
    /// inner NAudio object through - a one-character edit, <c>(s, e)</c> for <c>(_, e)</c> - would
    /// fail the comparison on every raise it ever made. Nothing throws when that happens. The route
    /// just never notices its own capture died, the tray goes on claiming it is routing, and the A2DP
    /// endpoint stays held open. Supplied from the subscription it cannot be wrong, and the guard says
    /// exactly what it reads as: is this from the capture I am holding now?
    /// </summary>
    private void OnRecordingStopped(ICaptureSource source, StoppedEventArgs e)
    {
        // Mirrors the playback guard below: this can be raised after Stop dropped this capture or a
        // later Start replaced it, and reporting that session dead would describe the wrong one.
        // NAudio snapshots the delegate before raising, so unsubscribing in Stop does not reliably
        // prevent this call - which is why the guard exists at all, and why _capture is volatile:
        // this is the one check the session token cannot back up, because it runs before it.
        if (!ReferenceEquals(source, _capture))
        {
            return;
        }

        // Before the Report below, for the reason Start's catch gives: Status is raised
        // synchronously, and no subscriber should be told the capture died by a router that still
        // answers IsRunning with true.
        Session? session = EndSession();

        if (e.Exception is not null)
        {
            Log.Error("Capture stopped.", e.Exception);
            Report($"Capture stopped: {e.Exception.Message}", LogLevel.Error);
        }

        RequestTeardown(session, "capture");
    }

    /// <summary>
    /// WasapiOut turns play-thread failures into this event and nothing else. Unhandled, a dead
    /// stream leaves IsRunning set and the tray still claiming it is routing.
    ///
    /// <paramref name="sink"/> comes from the subscription, for the reason given on the capture
    /// handler above.
    /// </summary>
    private void OnPlaybackStopped(IRenderSink sink, StoppedEventArgs e)
    {
        // Raised via SynchronizationContext.Post, so it can arrive after Stop dropped this output
        // or a later Start replaced it. Reporting then would describe the wrong session as dead.
        // Volatile for the same reason as the capture guard above.
        if (!ReferenceEquals(sink, _output))
        {
            return;
        }

        // Before the Report below; see the capture handler above.
        Session? session = EndSession();

        if (e.Exception is not null)
        {
            Log.Error("Playback stopped.", e.Exception);
            Report($"Playback stopped: {e.Exception.Message}", LogLevel.Error);
        }

        RequestTeardown(session, "playback");
    }

    /// <summary>
    /// Marks the current session dead and hands it back, or returns null if Stop already ran.
    ///
    /// Split from the posting below so that the two handlers can mark the route dead before they
    /// raise Status. The write is scoped to the session that was read, never to a field of the
    /// router: a worker preempted here can otherwise land its write on a healthy session that a
    /// Stop/Start pair created in the meantime.
    /// </summary>
    private Session? EndSession()
    {
        Session? session = _session;
        if (session is not null)
        {
            session.Dead = true;
        }

        return session;
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
    /// Do not narrow that to "only when it threw". PlayThread contains exactly one assignment of
    /// Stopped, near the end of its try, and only audioClient.Reset() follows it there. So a failure
    /// with no consumer Stop behind it - the spontaneous kind - reaches the finally still marked
    /// Playing whatever caused it: an early return when FillBuffer reports end-of-stream, or a throw
    /// from the DMO resampler construction, audioClient.BufferSize, Start, CurrentPadding, any
    /// GetBuffer/ReleaseBuffer COM call, or audioClient.Stop() itself, which runs immediately before
    /// that assignment. An endpoint that vanishes mid-stream, which is what a phone walking out of
    /// range looks like, lands there.
    ///
    /// What the field cannot do is tell you which statement faulted. Stop() and Pause() write it from
    /// other threads - Stop() sets Stopped before joining, which is how the render loop is broken at
    /// all - so after a consumer-initiated stop the same throw from audioClient.Stop() arrives marked
    /// Stopped instead. That case is not the hazard, because a consumer stop has already joined the
    /// play thread; it is only a reason not to read playbackState as a fault locator.
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
    private void RequestTeardown(Session? session, string half)
    {
        if (session is null)
        {
            // Stop already ran. Nothing to tear down, and posting would only risk killing whatever
            // starts next.
            return;
        }

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

            // After Stop, and from here rather than from the handler that noticed the failure. Two
            // separate reasons, and losing either one is a defect:
            //
            // After Stop, so a subscriber sees IsRunning false and cannot re-enter the teardown it is
            // being told about. Raised before, a reconnect subscriber would be deciding what to do
            // about a route that still claims to be running.
            //
            // From the posted lambda, so the subscriber runs on the dispatcher thread. Raised from
            // OnPlaybackStopped it would run on NAudio's play thread, and a subscriber that stops or
            // restarts the router from there reinstates in full the self-join this whole indirection
            // exists to avoid - see the deadlock described above.
            Stopped?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// Straight through to the factory, which is the whole point: the tray asks the router rather
    /// than holding an <see cref="IAudioDeviceFactory"/> of its own, so no caller above this class
    /// ever names a WASAPI type. See <see cref="IAudioRouter.ListOutputs"/>.
    /// </summary>
    public IReadOnlyList<AudioOutputDevice> ListOutputs() => _devices.ListOutputs();

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
            _capture.RecordingStopped -= _onRecordingStopped;
            _onRecordingStopped = null;

            // Already stopped, or the endpoint vanished with the connection.
            Teardown.Quietly(_capture.StopRecording, "stop capture");
            Teardown.Quietly(_capture.Dispose, "dispose capture");
            _capture = null;
        }

        if (_output is not null)
        {
            // Before Dispose, which joins the play thread that raises it. A deliberate teardown is
            // not a failure, and reporting it as one would overwrite the real status.
            _output.PlaybackStopped -= _onPlaybackStopped;
            _onPlaybackStopped = null;

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
