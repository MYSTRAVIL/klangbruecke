using Klangbruecke.Config;
using Klangbruecke.Diagnostics;
using Klangbruecke.Platform;

namespace Klangbruecke.Companion;

/// <summary>
/// Owns the phone-media-remote's lifecycle as one opt-in unit, so the composition root and the tray
/// have a single thing to switch on and off. It is deliberately <em>not</em> part of
/// <see cref="Klangbruecke.Connection.ConnectionManager"/>: the companion shares none of that class's
/// four state machines and has its own off-thread callbacks, so entangling it there would put a foreign
/// seam through the one lock-free, single-threaded object the app most needs to keep pristine. It lives
/// beside the manager instead (docs/FINDINGS.md §21).
///
/// <b>Off costs nothing.</b> While <see cref="Settings.PhoneRemoteEnabled"/> is false, no transport,
/// no SMTC session, and no RFCOMM discovery exist - <see cref="Start"/> builds them lazily and
/// <see cref="Stop"/> tears them all down. Someone who never installs the companion app, or leaves the
/// toggle off, gets exactly the app they had before.
///
/// <b>UI thread only.</b> <see cref="SmtcPublisher"/> is HWND-affine and <see cref="CompanionLink"/> is
/// single-threaded, so every method here must be called on the UI thread. The composition root calls
/// <see cref="ApplySetting"/> at startup and disposes after the message loop; the tray calls
/// <see cref="SetEnabled"/> from a menu click, which WinForms already runs on the UI thread.
/// </summary>
internal sealed class PhoneRemote : IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly IScheduler _scheduler;
    private readonly Settings _settings;

    private CompanionLink? _link;
    private bool _disposed;

    public PhoneRemote(IUiDispatcher ui, IScheduler scheduler, Settings settings)
    {
        _ui = ui;
        _scheduler = scheduler;
        _settings = settings;
    }

    /// <summary>Start or stop to match the current setting. Called once at startup.</summary>
    public void ApplySetting()
    {
        if (_disposed)
        {
            return;
        }

        if (_settings.PhoneRemoteEnabled)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    /// <summary>The tray toggle: persist the choice and apply it live, no restart.</summary>
    public void SetEnabled(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _settings.PhoneRemoteEnabled = enabled;
        _settings.Save();

        ApplySetting();
    }

    private void Start()
    {
        if (_link is not null)
        {
            // Already running - ApplySetting is idempotent so an enabled->enabled re-apply is a no-op.
            return;
        }

        // Marshal the transport's read-loop callbacks onto the UI thread; everything downstream then
        // runs single-threaded like the rest of Connection/.
        var transport = new UiMarshalingTransport(new RfcommCompanionTransport(), _ui);
        var publisher = new SmtcPublisher();

        _link = new CompanionLink(transport, publisher, _scheduler);

        // Fire-and-forget: StartAsync catches its own throws and backs off. The first connect is a
        // ~14 s uncached SDP discovery (docs/FINDINGS.md §21), so it must not block startup.
        _ = _link.StartAsync();

        Log.Info("Phone remote enabled.");
    }

    private void Stop()
    {
        if (_link is null)
        {
            return;
        }

        // Quietly, because this also runs on the shutdown path: CompanionLink.Dispose cascades to the
        // transport and the SMTC publisher, and a throw here would be a WER dialog in an app that must
        // never show a window - the same reasoning as ConnectionManager's teardown.
        Teardown.Quietly(_link.Dispose, "dispose the phone remote");
        _link = null;

        Log.Info("Phone remote disabled.");
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
