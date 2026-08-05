using Klangbruecke.Audio;
using NAudio.Wave;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// A capture endpoint that raises what only real hardware used to be able to raise.
///
/// Three of its capabilities exist for one test each, and each corresponds to a real ordering in
/// NAudio: a stopped event raised from inside <see cref="StartRecording"/>, a stopped event raised
/// from inside <see cref="Dispose"/>, and a stopped event that reaches a handler the router has
/// already unsubscribed.
/// </summary>
public sealed class FakeCaptureSource : ICaptureSource
{
    /// <summary>
    /// The handler NAudio's capture thread would be holding.
    ///
    /// Not a shortcut, and not optional. WasapiCapture reads the delegate list before it raises, so
    /// unsubscribing in <c>Stop</c> does not reliably prevent the call - which is the entire reason
    /// the router guards on which endpoint an event came from. Raising through the live event
    /// instead would make every "the router already dropped this one" test pass because no handler
    /// was invoked at all: green tests certifying a guard they never reached.
    /// </summary>
    private EventHandler<StoppedEventArgs>? _held;

    private bool _raisedFromDispose;

    public FakeCaptureSource(string friendlyName, WaveFormat waveFormat)
    {
        FriendlyName = friendlyName;
        WaveFormat = waveFormat;
    }

    /// <summary>Method names in the order the router called them.</summary>
    public List<string> Calls { get; } = new();

    public WaveFormat WaveFormat { get; }

    public string FriendlyName { get; }

    /// <summary>
    /// Runs inside <see cref="StartRecording"/>, after the handler has been taken. The seam for
    /// "the capture died before Start had finished starting it".
    /// </summary>
    public Action? DuringStartRecording { get; set; }

    /// <summary>
    /// When set, <see cref="Dispose"/> raises the stopped event once, as WasapiCapture's does while
    /// it winds its capture thread down.
    /// </summary>
    public bool RaisesStoppedOnDispose { get; set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public int CountOf(string call) => Calls.Count(c => c == call);

    public void StartRecording()
    {
        Calls.Add(nameof(StartRecording));
        _held = RecordingStopped;
        DuringStartRecording?.Invoke();
    }

    public void StopRecording() => Calls.Add(nameof(StopRecording));

    public void Dispose()
    {
        Calls.Add(nameof(Dispose));

        // Once, and the once matters. A router that re-entered Stop from this raise would call
        // Dispose again, and an unguarded re-raise would recurse until the stack ran out - killing
        // the run instead of failing the test that is watching for exactly that re-entry.
        if (RaisesStoppedOnDispose && !_raisedFromDispose)
        {
            _raisedFromDispose = true;
            RaiseRecordingStopped();
        }
    }

    public void RaiseDataAvailable(byte[] buffer)
        => DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, buffer.Length));

    /// <summary>
    /// Raises the stopped event. <paramref name="sender"/> defaults to <c>this</c>, which is what a
    /// well-behaved implementation would pass - and it can be overridden precisely because nothing
    /// downstream is allowed to depend on it. See <see cref="ICaptureSource"/>.
    /// </summary>
    public void RaiseRecordingStopped(Exception? exception = null, object? sender = null)
        => (_held ?? RecordingStopped)?.Invoke(sender ?? this, new StoppedEventArgs(exception));
}

/// <summary>
/// A render endpoint, with the same three capabilities as <see cref="FakeCaptureSource"/> plus one
/// more: <see cref="MixFormat"/> can throw. Reading the mix format activates the audio client on
/// first touch, and the router's failure path is written around that read being the thing that threw.
/// </summary>
public sealed class FakeRenderSink : IRenderSink
{
    private readonly WaveFormat _mixFormat;

    // See the note on FakeCaptureSource: WasapiOut takes the handler on its play thread too.
    private EventHandler<StoppedEventArgs>? _held;

    private bool _raisedFromDispose;

    public FakeRenderSink(string friendlyName, WaveFormat mixFormat)
    {
        FriendlyName = friendlyName;
        _mixFormat = mixFormat;
    }

    /// <summary>Method names in the order the router called them.</summary>
    public List<string> Calls { get; } = new();

    public string FriendlyName { get; }

    /// <summary>When set, every read of <see cref="MixFormat"/> throws it.</summary>
    public Exception? MixFormatThrows { get; set; }

    public WaveFormat MixFormat => MixFormatThrows is null ? _mixFormat : throw MixFormatThrows;

    /// <summary>Runs inside <see cref="Play"/>, after the handler has been taken.</summary>
    public Action? DuringPlay { get; set; }

    /// <summary>When set, <see cref="Dispose"/> raises the stopped event once.</summary>
    public bool RaisesStoppedOnDispose { get; set; }

    /// <summary>What the router handed to <see cref="Init"/>, or null if it never got that far.</summary>
    public IWaveProvider? Initialized { get; private set; }

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public int CountOf(string call) => Calls.Count(c => c == call);

    public void Init(IWaveProvider source)
    {
        Calls.Add(nameof(Init));
        Initialized = source;
    }

    public void Play()
    {
        Calls.Add(nameof(Play));
        _held = PlaybackStopped;
        DuringPlay?.Invoke();
    }

    public void Dispose()
    {
        Calls.Add(nameof(Dispose));

        if (RaisesStoppedOnDispose && !_raisedFromDispose)
        {
            _raisedFromDispose = true;
            RaisePlaybackStopped();
        }
    }

    /// <summary>See <see cref="FakeCaptureSource.RaiseRecordingStopped"/> on the sender.</summary>
    public void RaisePlaybackStopped(Exception? exception = null, object? sender = null)
        => (_held ?? PlaybackStopped)?.Invoke(sender ?? this, new StoppedEventArgs(exception));
}

/// <summary>
/// Hands out endpoints without touching WASAPI.
///
/// By default every <c>Start</c> gets a fresh working pair, which is what most tests want - a
/// restart has to produce endpoints the router has not seen before, or the "a replaced endpoint's
/// stopped event is ignored" tests would have nothing to replace. Tests that need a specific
/// endpoint, or none at all, enqueue one first.
/// </summary>
public sealed class FakeAudioDeviceFactory : IAudioDeviceFactory
{
    private readonly Queue<ICaptureSource?> _queuedCaptures = new();
    private readonly Queue<IRenderSink?> _queuedRenders = new();
    private int _created;

    /// <summary>Every capture handed out, oldest first. A missing endpoint - a null - is not one.</summary>
    public List<FakeCaptureSource> Captures { get; } = new();

    public List<FakeRenderSink> Renders { get; } = new();

    /// <summary>What the router asked <see cref="CreateRender"/> for, call by call.</summary>
    public List<string?> RequestedOutputIds { get; } = new();

    public IReadOnlyList<AudioOutputDevice> Outputs { get; set; } = Array.Empty<AudioOutputDevice>();

    /// <summary>The format of the pairs this factory builds itself. They match by default.</summary>
    public WaveFormat CaptureFormat { get; set; } = new WaveFormat(48000, 16, 2);

    public WaveFormat RenderFormat { get; set; } = new WaveFormat(48000, 16, 2);

    /// <summary>Hands <paramref name="capture"/> - or, for null, no endpoint - to the next Start.</summary>
    public void EnqueueCapture(ICaptureSource? capture) => _queuedCaptures.Enqueue(capture);

    public void EnqueueRender(IRenderSink? render) => _queuedRenders.Enqueue(render);

    public ICaptureSource? CreateSinkCapture()
    {
        ICaptureSource? source = _queuedCaptures.Count > 0
            ? _queuedCaptures.Dequeue()
            : new FakeCaptureSource($"Phone {++_created} (A2DP SNK)", CaptureFormat);

        if (source is FakeCaptureSource fake)
        {
            Captures.Add(fake);
        }

        return source;
    }

    public IRenderSink? CreateRender(string? preferredOutputDeviceId)
    {
        RequestedOutputIds.Add(preferredOutputDeviceId);

        IRenderSink? sink = _queuedRenders.Count > 0
            ? _queuedRenders.Dequeue()
            : new FakeRenderSink($"Speakers {_created}", RenderFormat);

        if (sink is FakeRenderSink fake)
        {
            Renders.Add(fake);
        }

        return sink;
    }

    public IReadOnlyList<AudioOutputDevice> ListOutputs() => Outputs;
}
