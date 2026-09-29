using System.ComponentModel;
using System.Runtime.InteropServices;
using Klangbruecke.Diagnostics;
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
public sealed class WasapiDeviceFactory : IAudioDeviceFactory, ISinkEndpointStateProbe
{
    /// <summary>
    /// <see cref="ISinkEndpointStateProbe.Probe"/>, so the same object Program.Main already builds is
    /// the watchdog's probe. Just forwards to the static read below.
    /// </summary>
    public SinkEndpointCondition Probe() => GetSinkCaptureEndpointCondition();
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
    /// The enumerator and every endpoint it yields are disposed before the strings are handed back:
    /// the caller is a menu rebuilt on every right-click, and each endpoint left open is a COM
    /// reference - a cached property store at least - held until a finalizer runs. The record copies
    /// the id and name, so nothing here needs to outlive the loop. See <see cref="AudioOutputDevice"/>.
    /// </summary>
    public IReadOnlyList<AudioOutputDevice> ListOutputs()
    {
        using var enumerator = new MMDeviceEnumerator();

        var outputs = new List<AudioOutputDevice>();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                outputs.Add(new AudioOutputDevice(device.ID, device.FriendlyName));
            }
        }

        return outputs;
    }

    /// <summary>
    /// Is that endpoint there right now?
    ///
    /// Here rather than in <see cref="EndpointMonitor"/> so there is exactly one definition of what
    /// "the endpoint" is. <see cref="AudioRouter"/> and this factory already agree on it; a third copy
    /// of "friendly name contains A2DP or SNK" living in the monitor is how the app would start
    /// reporting an endpoint present that the router then cannot open, or the reverse.
    ///
    /// Costly, and the caller has to know it: a full <c>EnumerateAudioEndPoints</c>, measured at
    /// <b>152-282 ms</b> on this machine. Never call it from an <c>IMMNotificationClient</c> callback.
    ///
    /// The device is disposed, unlike in <see cref="CreateSinkCapture"/>, where it is handed to a
    /// <see cref="WasapiCapture"/> that owns it from then on. Nothing here outlives the answer.
    /// </summary>
    public static bool IsSinkCaptureEndpointPresent()
    {
        using MMDevice? device = FindSinkCaptureEndpoint();

        return device is not null;
    }

    /// <summary>
    /// The capture endpoint Windows creates while an A2DP sink connection is open.
    ///
    /// The match is the caller's to own and dispose; every other endpoint enumerated is disposed here.
    /// A <c>FirstOrDefault</c> would leak each non-matching endpoint it iterated past to a finalizer.
    /// </summary>
    private static MMDevice? FindSinkCaptureEndpoint()
    {
        using var enumerator = new MMDeviceEnumerator();

        MMDevice? found = null;
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            bool isSinkCapture =
                found is null
                && (device.FriendlyName.Contains("A2DP", StringComparison.OrdinalIgnoreCase)
                    || device.FriendlyName.Contains("SNK", StringComparison.OrdinalIgnoreCase));

            if (isSinkCapture)
            {
                found = device;
            }
            else
            {
                device.Dispose();
            }
        }

        return found;
    }

    /// <summary>
    /// The sink capture endpoint's current condition, enumerating <see cref="DeviceState.All"/> rather
    /// than <see cref="DeviceState.Active"/> so the states <see cref="IsSinkCaptureEndpointPresent"/>
    /// collapses to "absent" - a call's <c>Unplugged</c> and the wedge's <c>NotPresent</c> - are told
    /// apart. See <see cref="SinkEndpointCondition"/> for why the watchdog needs the difference.
    ///
    /// Never throws, like <see cref="IsSinkCaptureEndpointPresent"/>: it is read from the watchdog's
    /// tick, and an escaping COM failure there would kill the one loop meant to recover the app. A read
    /// that cannot answer returns <see cref="SinkEndpointCondition.Absent"/>, which the watchdog does not
    /// act on - the safe direction, since a false "phantom" would restart the app for nothing.
    /// </summary>
    public static SinkEndpointCondition GetSinkCaptureEndpointCondition()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            SinkEndpointCondition found = SinkEndpointCondition.Absent;
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All))
            {
                using (device)
                {
                    string name;
                    try
                    {
                        name = device.FriendlyName;
                    }
                    catch (Exception)
                    {
                        // Reading FriendlyName throws COMException 0xE000020B on some endpoints; skip
                        // that one rather than abandon the scan and miss the sink endpoint further down.
                        continue;
                    }

                    if (name.Contains("A2DP", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("SNK", StringComparison.OrdinalIgnoreCase))
                    {
                        found = MapCondition(device.State);
                    }
                }
            }

            return found;
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading the A2DP sink capture endpoint condition failed: {ex.Message}");
            return SinkEndpointCondition.Absent;
        }
    }

    private static SinkEndpointCondition MapCondition(DeviceState state) => state switch
    {
        DeviceState.Active => SinkEndpointCondition.Active,
        DeviceState.Unplugged => SinkEndpointCondition.Unplugged,
        DeviceState.NotPresent => SinkEndpointCondition.Phantom,
        _ => SinkEndpointCondition.Other,
    };

    private static MMDevice? GetOutputDeviceOrDefault(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrEmpty(deviceId))
        {
            // The match is the caller's to own; the rest are disposed here rather than left to a
            // finalizer. A FirstOrDefault leaks every endpoint before the match - and all of them when
            // there is none, which is the common case once the chosen device has been unplugged.
            MMDevice? match = null;
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (match is null && device.ID == deviceId)
                {
                    match = device;
                }
                else
                {
                    device.Dispose();
                }
            }

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
/// One worker-thread registration with the Windows Multimedia Class Scheduler Service.
///
/// Registration and reversion are thread-affine. The NAudio adapters below therefore construct their
/// workers without a synchronization context: stopped events stay on the worker that registered, so
/// that worker can revert before it exits. <see cref="AudioRouter"/> already marshals teardown itself.
/// </summary>
internal sealed class MmcssThreadRegistration
{
    private const string TaskName = "Pro Audio";

    private readonly string _worker;
    private IntPtr _handle;
    private int _managedThreadId;
    private bool _attempted;

    public MmcssThreadRegistration(string worker) => _worker = worker;

    public void EnsureRegistered()
    {
        if (_attempted)
        {
            return;
        }

        _attempted = true;
        uint taskIndex = 0;
        _handle = NativeMethods.AvSetMmThreadCharacteristics(TaskName, ref taskIndex);

        if (_handle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Log.Warn($"Registering the {_worker} worker with MMCSS failed: "
                     + new Win32Exception(error).Message);
            return;
        }

        _managedThreadId = Environment.CurrentManagedThreadId;
        Log.Info($"{_worker} worker registered with MMCSS task '{TaskName}'.");
    }

    public void Revert()
    {
        IntPtr handle = _handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _handle = IntPtr.Zero;

        if (Environment.CurrentManagedThreadId != _managedThreadId)
        {
            Log.Warn($"The {_worker} worker's MMCSS registration could not be reverted on its "
                     + "own thread; Windows will release it when the thread exits.");
            return;
        }

        if (!NativeMethods.AvRevertMmThreadCharacteristics(handle))
        {
            int error = Marshal.GetLastWin32Error();
            Log.Warn($"Reverting the {_worker} worker's MMCSS registration failed: "
                     + new Win32Exception(error).Message);
        }
    }

    private static class NativeMethods
    {
        [DllImport(
            "avrt.dll",
            EntryPoint = "AvSetMmThreadCharacteristicsW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
    }
}

internal static class AudioWorkerFactory
{
    /// <summary>
    /// NAudio 2 captures the current synchronization context in both WASAPI constructors. Clearing it
    /// only for construction keeps stopped callbacks on their worker threads, where their thread-affine
    /// MMCSS registrations can be reverted. The caller's context is restored even if construction fails.
    /// </summary>
    public static T Create<T>(Func<T> factory)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            return factory();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}

/// <summary>
/// <see cref="WasapiCapture"/> behind <see cref="ICaptureSource"/>.
///
/// NAudio 2 does not register its capture thread with MMCSS. The first data callback runs on that
/// thread, so it registers before handing the packet to the router. The stopped callback runs on the
/// same thread and reverts the registration before forwarding the event.
/// </summary>
internal sealed class WasapiCaptureSource : ICaptureSource
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly MmcssThreadRegistration _mmcss = new("A2DP capture");

    public WasapiCaptureSource(MMDevice device)
    {
        _device = device;
        _capture = AudioWorkerFactory.Create(() => new WasapiCapture(device));

        _capture.DataAvailable += (_, e) =>
        {
            _mmcss.EnsureRegistered();
            DataAvailable?.Invoke(this, e);
        };
        _capture.RecordingStopped += (_, e) =>
        {
            _mmcss.Revert();
            RecordingStopped?.Invoke(this, e);
        };
    }

    public WaveFormat WaveFormat => _capture.WaveFormat;

    public string FriendlyName => _device.FriendlyName;

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording() => _capture.StartRecording();

    public void StopRecording() => _capture.StopRecording();

    // The device after the capture, not instead of it: WasapiCapture does not dispose the MMDevice it
    // was handed (measured against NAudio 2.2.1), so the endpoint's own cached COM objects live until a
    // finalizer without this. The capture is torn down first; MMDevice.Dispose is idempotent and safe
    // after it.
    public void Dispose()
    {
        _capture.Dispose();
        _device.Dispose();
    }
}

/// <summary>
/// Registers the NAudio render worker with MMCSS on its first source read.
///
/// WasapiOut performs that read on the play thread before starting the audio client, so the thread is
/// scheduled as time-sensitive for the complete live stream. No audio buffer is allocated or copied
/// here; every read passes straight through to the router's existing provider.
/// </summary>
internal sealed class MmcssWaveProvider : IWaveProvider
{
    private readonly IWaveProvider _source;
    private readonly MmcssThreadRegistration _mmcss;

    public MmcssWaveProvider(IWaveProvider source, MmcssThreadRegistration mmcss)
    {
        _source = source;
        _mmcss = mmcss;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        _mmcss.EnsureRegistered();
        return _source.Read(buffer, offset, count);
    }
}

/// <summary>
/// <see cref="WasapiOut"/> behind <see cref="IRenderSink"/>.
///
/// Shared mode keeps WASAPI's format conversion (see <see cref="AudioFormatBridge"/>); event sync and
/// 50 ms of latency are the measured settings. The source wrapper adds the MMCSS registration that
/// NAudio 2's render worker lacks.
/// </summary>
internal sealed class WasapiRenderSink : IRenderSink
{
    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly MmcssThreadRegistration _mmcss = new("audio render");

    public WasapiRenderSink(MMDevice device)
    {
        _device = device;
        _output = AudioWorkerFactory.Create(
            () => new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 50));

        _output.PlaybackStopped += (_, e) =>
        {
            _mmcss.Revert();
            PlaybackStopped?.Invoke(this, e);
        };
    }

    /// <summary>
    /// Read through to the endpoint on every access rather than cached at construction, so a
    /// failure to read it surfaces where the router expects it - inside its own try, with the
    /// capture already in hand to name in the failure line.
    /// </summary>
    public WaveFormat MixFormat => _device.AudioClient.MixFormat;

    public string FriendlyName => _device.FriendlyName;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Init(IWaveProvider source) => _output.Init(new MmcssWaveProvider(source, _mmcss));

    public void Play() => _output.Play();

    // The device after the output. WasapiOut does not dispose the MMDevice it was handed, and it shares
    // that device's cached AudioClient - so disposing the output first releases the client, and the
    // later MMDevice.Dispose is a safe no-op over it (both measured against NAudio 2.2.1). Without this
    // the endpoint's cached COM objects live until a finalizer.
    public void Dispose()
    {
        _output.Dispose();
        _device.Dispose();
    }
}
