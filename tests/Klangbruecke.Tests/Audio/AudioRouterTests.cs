using Klangbruecke.Audio;
using Klangbruecke.Diagnostics;
using Klangbruecke.Tests.Diagnostics;
using Klangbruecke.Tests.Fakes;
using NAudio.Wave;
using Xunit;

namespace Klangbruecke.Tests.Audio;

/// <summary>
/// The five orderings inside <see cref="AudioRouter"/> that no compiler and no reviewer catches.
///
/// Every one of them was written after a live failure and defended by nothing but a comment: before
/// this file, reverting any of the five left the whole suite green. Each test below was checked by
/// reverting the property it names and confirming it went red, which is the only evidence that it
/// defends anything at all.
/// </summary>
public sealed class AudioRouterTests : IDisposable
{
    private static readonly WaveFormat Format = new(48000, 16, 2);

    private readonly ILog _original = Log.Current;
    private readonly RecordingLog _log = new();

    public AudioRouterTests() => Log.Current = _log;

    public void Dispose() => Log.Current = _original;

    /// <summary>
    /// Inline delivery, which <see cref="AudioRouter"/>'s own constructor comment warns is safe "only
    /// in a test whose sink does not join anything".
    ///
    /// Said deliberately rather than reached for by habit: <see cref="FakeCaptureSource"/> and
    /// <see cref="FakeRenderSink"/> start no thread and join none, so the self-join the posted
    /// teardown exists to avoid cannot happen here. What it buys is that the teardown runs inside the
    /// raise, which is what makes the synchronous orderings observable - what IsRunning reads at the
    /// instant Status fires, and whether Stop re-enters itself from its own Dispose.
    ///
    /// Anything that needs the *race* rather than the ordering uses
    /// <see cref="DeferringUiDispatcher"/> instead. Inline delivery closes that window by
    /// construction and cannot express it.
    /// </summary>
    private static ImmediateUiDispatcher Inline() => new();

    /// <summary>Starts a route and hands back the pair the factory made for it.</summary>
    private static (FakeCaptureSource Capture, FakeRenderSink Sink) Start(
        AudioRouter router,
        FakeAudioDeviceFactory factory)
    {
        Assert.True(router.Start(null));

        return (factory.Captures[^1], factory.Renders[^1]);
    }

    /// <summary>The one line a teardown always writes, so "did it tear down?" is answerable.</summary>
    private List<string> TeardownWarnings() =>
        _log.Entries
            .Where(e => e.Level == LogLevel.Warn && e.Message.StartsWith("Tearing the route down"))
            .Select(e => e.Message)
            .ToList();

    // The tray's Output menu asks the router rather than holding a factory of its own, so that
    // nothing above this class ever names a WASAPI type. A pass-through, and it still needs an
    // assertion: returned empty instead, the menu would silently offer nothing but "System default"
    // and the user would have no way to tell that from a machine with one sound card.
    [Fact]
    public void ListOutputs_comes_from_the_factory()
    {
        var factory = new FakeAudioDeviceFactory
        {
            Outputs = new[]
            {
                new AudioOutputDevice("{0.0.0.00000000}.{aaaa}", "Speakers"),
                new AudioOutputDevice("{0.0.0.00000000}.{bbbb}", "Headset"),
            },
        };

        using var router = new AudioRouter(Inline(), factory);

        Assert.Equal(factory.Outputs, router.ListOutputs());
    }

    // ---- Property 1: the session is published before StartRecording and Play ---------------------

    [Fact]
    public void Capture_stopping_during_StartRecording_is_not_discarded_as_stale()
    {
        var ui = new DeferringUiDispatcher();
        var factory = new FakeAudioDeviceFactory();
        var capture = new FakeCaptureSource("Phone (A2DP SNK)", Format);
        capture.DuringStartRecording =
            () => capture.RaiseRecordingStopped(new InvalidOperationException("the endpoint vanished"));
        factory.EnqueueCapture(capture);

        using var router = new AudioRouter(ui, factory);
        int stopped = 0;
        router.Stopped += (_, _) => stopped++;

        router.Start(null);

        // The session has to exist before StartRecording is called, or an event raised from inside it
        // finds none, is read as arriving after the route ended, and is dropped. Nothing is posted,
        // nothing tears down, and the router goes on reporting a route whose capture is already dead.
        Assert.Single(ui.Captured);

        Assert.Equal(1, ui.Drain());
        Assert.False(router.IsRunning);
        Assert.Equal(1, stopped);
        Assert.Equal(1, capture.CountOf("Dispose"));
    }

    [Fact]
    public void Playback_stopping_during_Play_is_not_discarded_as_stale()
    {
        var ui = new DeferringUiDispatcher();
        var factory = new FakeAudioDeviceFactory();
        var sink = new FakeRenderSink("Speakers", Format);
        sink.DuringPlay = () => sink.RaisePlaybackStopped(new InvalidOperationException("the device vanished"));
        factory.EnqueueRender(sink);

        using var router = new AudioRouter(ui, factory);
        int stopped = 0;
        router.Stopped += (_, _) => stopped++;

        router.Start(null);

        Assert.Single(ui.Captured);

        Assert.Equal(1, ui.Drain());
        Assert.False(router.IsRunning);
        Assert.Equal(1, stopped);
        Assert.Equal(1, sink.CountOf("Dispose"));
    }

    // ---- Property 2: EndSession runs before Report in both stopped handlers ----------------------

    [Fact]
    public void IsRunning_is_false_when_the_capture_stopped_status_is_raised()
    {
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(Inline(), factory);
        (FakeCaptureSource capture, _) = Start(router, factory);

        // Subscribed after Start so the only message this can see is the failure one.
        bool? runningWhenReported = null;
        router.Status += (_, message) =>
        {
            if (message.Level == LogLevel.Error)
            {
                runningWhenReported = router.IsRunning;
            }
        };

        capture.RaiseRecordingStopped(new InvalidOperationException("capture died"));

        // Status is raised synchronously, so a subscriber reading IsRunning here reads it mid-handler.
        // Told the capture died by a router that still answers true, a reconnect subscriber would see
        // no work to do.
        Assert.NotNull(runningWhenReported);
        Assert.False(runningWhenReported);
    }

    [Fact]
    public void IsRunning_is_false_when_the_playback_stopped_status_is_raised()
    {
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(Inline(), factory);
        (_, FakeRenderSink sink) = Start(router, factory);

        bool? runningWhenReported = null;
        router.Status += (_, message) =>
        {
            if (message.Level == LogLevel.Error)
            {
                runningWhenReported = router.IsRunning;
            }
        };

        sink.RaisePlaybackStopped(new InvalidOperationException("output died"));

        Assert.NotNull(runningWhenReported);
        Assert.False(runningWhenReported);
    }

    // ---- Property 3: Stop nulls the session before it unsubscribes and disposes ------------------

    [Fact]
    public void Stop_is_not_re_entered_when_disposal_re_raises_stopped()
    {
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(Inline(), factory);
        (FakeCaptureSource capture, _) = Start(router, factory);

        // WasapiCapture raises this while Dispose winds its capture thread down, and the unsubscribe a
        // line earlier does not prevent it: the delegate list was already read.
        capture.RaisesStoppedOnDispose = true;

        int stopped = 0;
        router.Stopped += (_, _) => stopped++;

        router.Stop();

        // The session is gone before Dispose is called, so the event Dispose raises ends no session
        // and posts no teardown. Nulled any later and Stop runs its body a second time from inside
        // itself - stopping and disposing the same endpoints twice - and announces a deliberate
        // disconnect as a route failure.
        Assert.Equal(1, capture.CountOf("StopRecording"));
        Assert.Equal(1, capture.CountOf("Dispose"));
        Assert.Equal(0, stopped);
        Assert.Empty(TeardownWarnings());
    }

    [Fact]
    public void A_queued_teardown_is_a_no_op_after_Stop_already_ran()
    {
        var ui = new DeferringUiDispatcher();
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(ui, factory);
        (FakeCaptureSource capture, _) = Start(router, factory);

        int stopped = 0;
        router.Stopped += (_, _) => stopped++;

        capture.RaiseRecordingStopped(new InvalidOperationException("capture died"));
        Assert.Single(ui.Captured);

        router.Stop();

        Assert.Equal(1, ui.Drain());

        // The lambda ran, found the session it was posted for gone, and did nothing. On a real STA
        // thread this is not hypothetical: Thread.Join pumps, so the Dispose calls inside Stop can
        // dispatch this very lambda from inside themselves.
        Assert.Equal(0, stopped);
        Assert.Empty(TeardownWarnings());
        Assert.Equal(1, capture.CountOf("Dispose"));
    }

    // ---- Property 4: the posted teardown checks the session is still the current one -------------

    [Fact]
    public void A_teardown_posted_before_a_restart_does_not_kill_the_new_route()
    {
        var ui = new DeferringUiDispatcher();
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(ui, factory);
        (FakeCaptureSource first, _) = Start(router, factory);

        first.RaiseRecordingStopped(new InvalidOperationException("capture died"));
        Assert.Single(ui.Captured);

        // The window between the post and the drain is exactly long enough for a menu click.
        router.Stop();
        (FakeCaptureSource second, FakeRenderSink secondSink) = Start(router, factory);

        int stopped = 0;
        router.Stopped += (_, _) => stopped++;

        Assert.Equal(1, ui.Drain());

        // Without the check, a recoverable failure becomes a silent one: the user reconnects, the
        // stale teardown lands, and the route they just started is torn down with no status line.
        Assert.True(router.IsRunning);
        Assert.Equal(0, second.CountOf("Dispose"));
        Assert.Equal(0, secondSink.CountOf("Dispose"));
        Assert.Equal(0, stopped);
    }

    // ---- Property 5: stopped events from an endpoint the router no longer holds are dropped ------

    [Fact]
    public void A_stopped_event_from_a_replaced_capture_is_ignored()
    {
        var ui = new DeferringUiDispatcher();
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(ui, factory);
        (FakeCaptureSource first, _) = Start(router, factory);
        (FakeCaptureSource second, _) = Start(router, factory);
        Assert.NotSame(first, second);

        int stopped = 0;
        router.Stopped += (_, _) => stopped++;
        var reported = new List<StatusMessage>();
        router.Status += (_, message) => reported.Add(message);

        first.RaiseRecordingStopped(new InvalidOperationException("the old capture, late"));

        // Nothing was even posted. The handler recognised the endpoint as one it is no longer holding
        // and left before it could end the session the live route is running on - which is the only
        // reason a late event from a replaced capture is not a teardown of a healthy route.
        Assert.Empty(ui.Captured);
        Assert.True(router.IsRunning);
        Assert.Equal(0, second.CountOf("Dispose"));
        Assert.Equal(0, stopped);
        Assert.Empty(reported);
    }

    [Fact]
    public void A_stopped_event_from_a_replaced_output_is_ignored()
    {
        var ui = new DeferringUiDispatcher();
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(ui, factory);
        (_, FakeRenderSink first) = Start(router, factory);
        (FakeCaptureSource secondCapture, FakeRenderSink second) = Start(router, factory);
        Assert.NotSame(first, second);

        int stopped = 0;
        router.Stopped += (_, _) => stopped++;
        var reported = new List<StatusMessage>();
        router.Status += (_, message) => reported.Add(message);

        first.RaisePlaybackStopped(new InvalidOperationException("the old output, late"));

        Assert.Empty(ui.Captured);
        Assert.True(router.IsRunning);
        Assert.Equal(0, second.CountOf("Dispose"));
        Assert.Equal(0, secondCapture.CountOf("Dispose"));
        Assert.Equal(0, stopped);
        Assert.Empty(reported);
    }

    // ---- The trap that replaced the sender contract ----------------------------------------------
    //
    // Beyond the seventeen the plan names, and the reason the two above can exist at all. The router
    // used to identify the endpoint by the event's own sender, which made the adapters' re-raises a
    // contract no compiler checked: turning `(_, e) => X?.Invoke(this, e)` into
    // `(s, e) => X?.Invoke(s, e)` compiled, kept every test green, and disabled teardown on hardware,
    // because the guard would then be comparing a WasapiCapture against the adapter holding it. No
    // fake could catch that - a fake replaces the adapter wholesale and raises with `this` by
    // construction - so the contract was deleted rather than tested. These two are what stop it
    // coming back: read the sender again and they go red.

    [Fact]
    public void A_capture_stopped_event_tears_down_whatever_sender_it_carries()
    {
        var ui = new DeferringUiDispatcher();
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(ui, factory);
        (FakeCaptureSource capture, _) = Start(router, factory);

        capture.RaiseRecordingStopped(new InvalidOperationException("capture died"), sender: new object());

        Assert.Single(ui.Captured);
        Assert.Equal(1, ui.Drain());
        Assert.False(router.IsRunning);
    }

    [Fact]
    public void A_playback_stopped_event_tears_down_whatever_sender_it_carries()
    {
        var ui = new DeferringUiDispatcher();
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(ui, factory);
        (_, FakeRenderSink sink) = Start(router, factory);

        sink.RaisePlaybackStopped(new InvalidOperationException("output died"), sender: new object());

        Assert.Single(ui.Captured);
        Assert.Equal(1, ui.Drain());
        Assert.False(router.IsRunning);
    }

    // ---- Properties 3 and 4 together: one death, or two, is still one teardown -------------------

    [Fact]
    public void Both_halves_reporting_stopped_tears_down_once()
    {
        var ui = new DeferringUiDispatcher();
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(ui, factory);
        (FakeCaptureSource capture, FakeRenderSink sink) = Start(router, factory);

        int stopped = 0;
        router.Stopped += (_, _) => stopped++;

        // An endpoint that vanishes mid-stream takes both halves with it, and each notices for itself.
        capture.RaiseRecordingStopped(new InvalidOperationException("capture died"));
        sink.RaisePlaybackStopped(new InvalidOperationException("output died"));

        Assert.Equal(2, ui.Captured.Count);

        Assert.Equal(2, ui.Drain());

        // Two posts, one teardown. The second finds the session it named already replaced by null.
        Assert.Equal(1, stopped);
        Assert.Single(TeardownWarnings());
        Assert.Equal(1, capture.CountOf("Dispose"));
        Assert.Equal(1, sink.CountOf("Dispose"));
    }

    // ---- The format-pair log line, and the two ways Start declines -------------------------------

    [Fact]
    public void Start_logs_the_capture_and_render_format_pair_when_they_match()
    {
        var factory = new FakeAudioDeviceFactory
        {
            CaptureFormat = new WaveFormat(48000, 16, 2),
            RenderFormat = new WaveFormat(48000, 16, 2),
        };

        using var router = new AudioRouter(Inline(), factory);
        Start(router, factory);

        // Unconditional, not only on failure: this pair is the first thing anyone needs when routing
        // misbehaves on hardware that cannot be reproduced here, and it is worth as much matched.
        var line = Assert.Single(_log.Entries, e => e.Message.StartsWith("Capture="));
        Assert.Equal(LogLevel.Info, line.Level);
        Assert.Equal("Capture=48000Hz 16bit 2ch Pcm Render=48000Hz 16bit 2ch Pcm - matched.", line.Message);
    }

    [Fact]
    public void Start_logs_the_format_pair_when_they_differ()
    {
        var factory = new FakeAudioDeviceFactory
        {
            CaptureFormat = new WaveFormat(48000, 16, 2),
            RenderFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2),
        };

        using var router = new AudioRouter(Inline(), factory);
        Start(router, factory);

        var line = Assert.Single(_log.Entries, e => e.Message.StartsWith("Capture="));
        Assert.Equal(LogLevel.Info, line.Level);
        Assert.Equal(
            "Capture=48000Hz 16bit 2ch Pcm Render=44100Hz 32bit 2ch IeeeFloat"
                + " - differ, WASAPI shared mode is converting.",
            line.Message);
    }

    [Fact]
    public void Start_failure_names_both_formats_when_the_MixFormat_read_threw()
    {
        var factory = new FakeAudioDeviceFactory();
        var thrown = new InvalidOperationException("the audio client would not activate");
        factory.EnqueueRender(new FakeRenderSink("Speakers", Format) { MixFormatThrows = thrown });

        using var router = new AudioRouter(Inline(), factory);
        var reported = new List<StatusMessage>();
        router.Status += (_, message) => reported.Add(message);

        Assert.False(router.Start(null));

        // The read that would have produced the Info pair is what threw, so the failure line is the
        // only place the capture format is ever named. Naming it there is why it is read from the
        // field before Stop nulls it.
        Assert.DoesNotContain(_log.Entries, e => e.Message.StartsWith("Capture="));

        var failure = Assert.Single(_log.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal("Routing failed. Capture=48000Hz 16bit 2ch Pcm Render=unknown", failure.Message);
        Assert.Same(thrown, failure.Exception);

        // Error, matching the log entry. At Info this was the second of two entries for one throw at
        // two different levels.
        StatusMessage status = Assert.Single(reported);
        Assert.Equal(LogLevel.Error, status.Level);
        Assert.Equal($"Could not start routing: {thrown.Message}", status.Text);

        Assert.False(router.IsRunning);
        Assert.Equal(1, factory.Captures[0].CountOf("Dispose"));
        Assert.Equal(1, factory.Renders[0].CountOf("Dispose"));
    }

    [Fact]
    public void Start_returns_false_and_reports_when_there_is_no_capture_endpoint()
    {
        var factory = new FakeAudioDeviceFactory();
        factory.EnqueueCapture(null);

        using var router = new AudioRouter(Inline(), factory);
        var reported = new List<StatusMessage>();
        router.Status += (_, message) => reported.Add(message);

        Assert.False(router.Start(null));

        StatusMessage status = Assert.Single(reported);
        // Pinned because the wording is load-bearing, not decorative. It must not claim anything
        // about the connection: docs/FINDINGS.md section 4 retracted "absent endpoint means nothing
        // is holding a connection open", and this message fires in the one case where that reading
        // is actively wrong - the endpoint vanishing between the monitor's read and Start, with the
        // connection open.
        Assert.Equal("No A2DP sink endpoint to capture from; not starting the route.", status.Text);

        // Info, not Error. There is no route to start and that is an ordinary state - the endpoint
        // lags the connection by an unbounded interval (docs/FINDINGS.md section 4).
        Assert.Equal(LogLevel.Info, status.Level);

        Assert.False(router.IsRunning);
        Assert.Empty(factory.RequestedOutputIds);
    }

    [Fact]
    public void Start_returns_false_and_reports_when_there_is_no_render_endpoint()
    {
        var factory = new FakeAudioDeviceFactory();
        factory.EnqueueRender(null);

        using var router = new AudioRouter(Inline(), factory);
        var reported = new List<StatusMessage>();
        router.Status += (_, message) => reported.Add(message);

        Assert.False(router.Start(null));

        StatusMessage status = Assert.Single(reported);
        Assert.Equal("No usable output device.", status.Text);
        Assert.Equal(LogLevel.Info, status.Level);

        // The capture is already open by this point, and it is the handle that holds the A2DP
        // endpoint - the one thing the app reads as proof it is connected at all. Left open for a
        // route that never started, it is a lie the next Start has to work around.
        Assert.Equal(1, factory.Captures[0].CountOf("Dispose"));
        Assert.False(router.IsRunning);
    }

    // ---- The Stopped event's contract ------------------------------------------------------------

    [Fact]
    public void Stopped_event_fires_after_IsRunning_is_false()
    {
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(Inline(), factory);
        (FakeCaptureSource capture, _) = Start(router, factory);

        bool? runningWhenStopped = null;
        bool restarted = false;
        router.Stopped += (_, _) =>
        {
            runningWhenStopped = router.IsRunning;
            restarted = router.Start(null);
        };

        capture.RaiseRecordingStopped(new InvalidOperationException("capture died"));

        Assert.NotNull(runningWhenStopped);
        Assert.False(runningWhenStopped);

        // The subscriber this event exists for reconnects. Raised before the teardown finished, it
        // would be deciding what to do about a route that still claims to be running - and its Start
        // would race the Stop still to come.
        Assert.True(restarted);
        Assert.True(router.IsRunning);
        Assert.Equal(2, factory.Captures.Count);
    }

    [Fact]
    public void Stopped_event_does_not_fire_on_a_deliberate_Stop()
    {
        var factory = new FakeAudioDeviceFactory();
        using var router = new AudioRouter(Inline(), factory);
        (FakeCaptureSource capture, FakeRenderSink sink) = Start(router, factory);

        int stopped = 0;
        router.Stopped += (_, _) => stopped++;

        router.Stop();

        // Tray Disconnect is not a route failure. Echoed back, it is how a reconnect subscriber
        // restarts what the user just switched off.
        Assert.Equal(0, stopped);
        Assert.False(router.IsRunning);
        Assert.Equal(1, capture.CountOf("Dispose"));
        Assert.Equal(1, sink.CountOf("Dispose"));
        Assert.Empty(TeardownWarnings());
    }
}
