using Klangbruecke.Audio;
using Klangbruecke.Config;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke.Connection;

/// <summary>
/// Recovers the app from the stale-endpoint wedge (docs/FINDINGS.md §23) by restarting the process
/// when nothing else can.
///
/// <b>The wedge, and why only a restart clears it.</b> After the phone drops the A2DP stream (a call,
/// a link blip), <c>AudioPlaybackConnection</c> can go on reporting Opened while Windows leaves the
/// capture endpoint a device-tree phantom that never comes back. The music half sits in
/// <see cref="MusicState.Linked"/> - "waiting for phone audio" - forever, and the tray's own
/// Disconnect/Connect does not help: that reconnect is in-process, and the phantom outlives it.
/// Measured, a full process relaunch is the only thing that clears it.
///
/// <b>Why it is safe to fire automatically, and where it is careful.</b> Two normal conditions look
/// like the wedge from the connection state alone - a phone that simply is not playing, and a live
/// call - so neither may trigger a restart. Both are ruled out by reading the endpoint's actual
/// condition rather than its mere absence:
/// <list type="bullet">
/// <item>Idle: the endpoint stays <see cref="SinkEndpointCondition.Active"/> whether or not audio
/// flows, so the half is <see cref="MusicState.Up"/>, not Linked - <see cref="ConnectionManager.MusicWaitingForEndpoint"/>
/// is false and this never even probes (measured, FINDINGS §23).</item>
/// <item>Call: the endpoint reads <see cref="SinkEndpointCondition.Unplugged"/> (§14), never
/// <see cref="SinkEndpointCondition.Phantom"/> - so a probe during a call does not restart.</item>
/// </list>
/// It acts on <see cref="SinkEndpointCondition.Phantom"/> alone, only after the condition has held
/// far longer than a slow endpoint arrival (§13 measured 74 s), and only within the
/// <see cref="RestartBudget"/>. If the inference that the wedge presents as <c>Phantom</c> is ever
/// wrong the probe returns something else and this does nothing - a safe failure, logged so the next
/// real wedge gives ground truth, with the manual tray Restart still there.
///
/// <b>Kept out of <see cref="ConnectionManager"/>, on purpose.</b> Like <c>PhoneRemote</c> (FINDINGS
/// §21), it owns an off-thread read and brings its own timer, and the manager is the one lock-free
/// class the app most needs to keep pristine. It reads the manager's cheap
/// <see cref="ConnectionManager.MusicWaitingForEndpoint"/> and nothing else of it.
///
/// <b>Threading.</b> <see cref="ShouldProbe"/> and <see cref="ApplyProbeResult"/> run on the UI thread
/// (the scheduler's callbacks, and the dispatcher post) and own all the state; only the 152-282 ms
/// endpoint probe runs off it, on a threadpool thread that touches nothing here.
/// </summary>
public sealed class SinkWedgeWatchdog : IDisposable
{
    /// <summary>
    /// How long "waiting for phone audio" must hold before a phantom endpoint is treated as the wedge.
    ///
    /// Comfortably past the 74 s a legitimate slow endpoint arrival took (docs/FINDINGS.md §13), so a
    /// merely-slow connect - during which the endpoint can be absent, or briefly a not-yet-materialised
    /// phantom - is never mistaken for the permanent wedge.
    /// </summary>
    public static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(3);

    /// <summary>How often the condition is checked. The probe only runs once the threshold is crossed.</summary>
    public static readonly TimeSpan CheckPeriod = TimeSpan.FromMinutes(1);

    private readonly IScheduler _scheduler;
    private readonly IUiDispatcher _ui;
    private readonly Func<bool> _waitingForEndpoint;
    private readonly ISinkEndpointStateProbe _probe;
    private readonly IAppRestarter _restarter;
    private readonly Settings _settings;

    private IDisposable? _timer;

    /// <summary>When the waiting-for-endpoint condition began, or null when it does not currently hold.</summary>
    private DateTimeOffset? _stuckSince;

    /// <summary>An off-thread probe is out. Shuts the gate so a slow probe is not stacked on by later ticks.</summary>
    private bool _probing;

    private bool _disposed;

    public SinkWedgeWatchdog(
        IScheduler scheduler,
        IUiDispatcher ui,
        Func<bool> waitingForEndpoint,
        ISinkEndpointStateProbe probe,
        IAppRestarter restarter,
        Settings settings)
    {
        _scheduler = scheduler;
        _ui = ui;
        _waitingForEndpoint = waitingForEndpoint;
        _probe = probe;
        _restarter = restarter;
        _settings = settings;
    }

    /// <summary>Begins the periodic check. Call once, from the UI thread.</summary>
    public void Start()
    {
        _timer ??= _scheduler.SchedulePeriodic(CheckPeriod, Tick);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// The periodic glue: decide whether to probe, and if so run the expensive read off the UI thread
    /// and bring the answer back to it. The two decisions either side of the probe -
    /// <see cref="ShouldProbe"/> and <see cref="ApplyProbeResult"/> - are where the logic lives and are
    /// tested directly; this method is the threadpool hop between them and is untested by design, like
    /// <c>ConnectionManager.ProbeEndpointLevel</c>.
    /// </summary>
    private void Tick()
    {
        if (!ShouldProbe(_scheduler.Now))
        {
            return;
        }

        _probing = true;

        _ = Task.Run(() =>
        {
            SinkEndpointCondition condition = _probe.Probe();

            _ui.Post(() =>
            {
                _probing = false;

                if (!_disposed)
                {
                    ApplyProbeResult(condition, _scheduler.Now);
                }
            });
        });
    }

    /// <summary>
    /// Whether to run the endpoint probe now. Tracks how long the waiting-for-endpoint condition has
    /// held: reset the instant it stops, armed once it has held past <see cref="StuckThreshold"/>, and
    /// held off while a previous probe is still out.
    /// </summary>
    public bool ShouldProbe(DateTimeOffset now)
    {
        if (!_waitingForEndpoint())
        {
            _stuckSince = null;
            return false;
        }

        _stuckSince ??= now;

        return !_probing && now - _stuckSince.Value >= StuckThreshold;
    }

    /// <summary>
    /// Act on the probe's answer. Restarts only for <see cref="SinkEndpointCondition.Phantom"/>, only
    /// while the condition still holds, and only within the <see cref="RestartBudget"/>. Every other
    /// answer is logged - so a real wedge that presents differently is on record - and re-bases the
    /// clock, so a long call is not re-probed every tick.
    /// </summary>
    public void ApplyProbeResult(SinkEndpointCondition condition, DateTimeOffset now)
    {
        if (!_waitingForEndpoint())
        {
            // Recovered while the probe was out - the endpoint arrived, or the phone left. Nothing to do.
            _stuckSince = null;
            return;
        }

        TimeSpan heldFor = now - (_stuckSince ?? now);

        if (condition != SinkEndpointCondition.Phantom)
        {
            // Absent for a reason that is not the wedge: Unplugged is a live call (§14); Active, Other
            // and Absent are not the phantom signature. Recorded so a wedge that presents differently is
            // not invisible, and the clock re-based so a long call does not pay for a probe every minute.
            Log.Info(
                $"Wedge watchdog: waiting for phone audio {Describe(heldFor)}, but the sink endpoint is "
                + $"{condition}, not the phantom signature - leaving it (docs/FINDINGS.md §23).");
            _stuckSince = now;
            return;
        }

        if (!RestartBudget.Allows(_settings.RecentAutoRestarts, now))
        {
            Log.Warn(
                $"Wedge watchdog: the sink endpoint has been phantom {Describe(heldFor)}, but "
                + $"{RestartBudget.MaxPerWindow} automatic restarts have already happened within the hour "
                + "- not restarting again. Use the tray's Restart item; see docs/FINDINGS.md §23.");
            _stuckSince = now;
            return;
        }

        _settings.RecentAutoRestarts = RestartBudget.Record(_settings.RecentAutoRestarts, now);
        _settings.Save();

        Log.Warn(
            $"Wedge watchdog: the A2DP sink endpoint has been a phantom for {Describe(heldFor)} while the "
            + "phone is connected - the stale-endpoint wedge (docs/FINDINGS.md §23). Restarting to clear it.");

        _restarter.Restart();
    }

    private static string Describe(TimeSpan span)
    {
        int minutes = (int)span.TotalMinutes;
        int seconds = span.Seconds;
        return minutes > 0 ? $"{minutes}m{seconds}s" : $"{seconds}s";
    }
}
