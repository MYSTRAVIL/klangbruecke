using System.Drawing;
using System.Reflection;
using Klangbruecke.Diagnostics;

namespace Klangbruecke;

/// <summary>
/// The three tray glyphs, loaded once from embedded resources and handed out by
/// <see cref="TrayIconStatus"/>. <see cref="TrayIconPolicy"/> decides which status a connection state
/// is; this only owns the <see cref="Icon"/> handles and their lifetime.
///
/// Loaded at the system small-icon size so the notification area gets the frame it will actually
/// draw - the .ico carries 16 through 48, and <see cref="SystemInformation.SmallIconSize"/> is
/// DPI-aware, so a 125% display picks the 20 rather than upscaling the 16.
///
/// Owned by <c>Program.RunTray</c>, not by the tray: <see cref="TrayContext"/> borrows an icon to
/// assign and never disposes one, exactly as it borrows the presenter and the manager. Disposing a
/// handle the <c>NotifyIcon</c> is still showing would blank the tray, so this is disposed after the
/// tray tears the icon down - the reverse-declaration order of the <c>using</c>s in RunTray.
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Icon _active;
    private readonly Icon _busy;
    private readonly Icon _idle;

    public TrayIcons()
    {
        _active = Load("tray-active.ico");
        _busy = Load("tray-busy.ico");
        _idle = Load("tray-idle.ico");
    }

    /// <summary>The glyph for a status. Never null; the handle is owned here and outlives every call.</summary>
    public Icon For(TrayIconStatus status) => status switch
    {
        TrayIconStatus.Active => _active,
        TrayIconStatus.Busy => _busy,
        _ => _idle,
    };

    // Matched by suffix rather than a hard-coded manifest name: MSBuild composes the logical name from
    // the root namespace and the folder, and pinning the exact string here would break silently the
    // day either moves. Exactly one resource ends in each file name, so Single is the assertion.
    private static Icon Load(string fileName)
    {
        Assembly assembly = typeof(TrayIcons).Assembly;

        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(fileName, StringComparison.Ordinal));

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded tray icon stream '{resourceName}' was null.");

        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    // Guarded one at a time, as the tray's own teardown is: a throw from one Dispose must not skip the
    // other two, and this runs during shutdown where a throw reaches no message-loop handler.
    public void Dispose()
    {
        Teardown.Quietly(_active.Dispose, "dispose the active tray icon");
        Teardown.Quietly(_busy.Dispose, "dispose the busy tray icon");
        Teardown.Quietly(_idle.Dispose, "dispose the idle tray icon");
    }
}
