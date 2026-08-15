using System.Runtime.InteropServices;
using System.Windows.Forms;
using Klangbruecke.Diagnostics;
using Windows.Media;
using WinRT;

namespace Klangbruecke.Companion;

/// <summary>
/// The PC's now-playing surface below the <see cref="ISmtcPublisher"/> seam: it owns a hidden HWND,
/// binds a <see cref="SystemMediaTransportControls"/> to it via the FINDINGS 20.1 interop recipe, and
/// mirrors a <see cref="MediaSnapshot"/> into that session (which ModernFlyouts and the native overlay
/// render). Transport buttons the user presses on the PC come back out as
/// <see cref="ISmtcPublisher.CommandRequested"/> for the orchestrator to forward to the phone.
///
/// <b>Thread affinity.</b> The hidden window must be created, published to, and disposed on the one
/// thread that runs the WinForms message loop - the tray app's UI thread - because that is where its
/// message pump lives. <see cref="Publish"/> is called by the orchestrator on that thread by contract.
/// The SMTC <c>ButtonPressed</c> callback, by contrast, arrives on a thread-pool thread; this class
/// does no window work there, it only raises <see cref="CommandRequested"/>, and the caller marshals
/// back onto the UI thread the same way the rest of <c>Connection/</c> does.
///
/// <b>Never crashes.</b> Binding the SMTC can fail on a machine where the interop does not resolve; a
/// tray app that must never show a window also must never die trying to publish to one, so a failed
/// bind is logged and leaves the publisher a live no-op rather than throwing out of the constructor.
/// </summary>
internal sealed class SmtcPublisher : ISmtcPublisher
{
    private readonly HiddenMessageWindow _window;
    private SystemMediaTransportControls? _smtc;
    private bool _disposed;

    public event EventHandler<MediaCommand>? CommandRequested;

    public SmtcPublisher()
    {
        _window = new HiddenMessageWindow();

        try
        {
            // Accessing Handle forces the HWND into existence on the current (UI) thread without ever
            // showing the window - all SMTC needs is a top-level window with a message pump to bind to.
            IntPtr hwnd = _window.Handle;
            _smtc = SmtcInterop.GetForWindow(hwnd);
            _smtc.ButtonPressed += OnButtonPressed;
            Log.Info($"SMTC publisher bound to a hidden HWND (0x{hwnd.ToInt64():X}).");
        }
        catch (Exception ex)
        {
            // Degraded, not dead: Publish becomes a no-op and no buttons fire, but the tray app lives.
            Log.Error(
                "The SMTC publisher could not bind the system media controls; the phone remote will " +
                "show no now-playing surface this session.",
                ex);
            _smtc = null;
        }
    }

    public void Publish(MediaSnapshot snapshot)
    {
        SystemMediaTransportControls? smtc = _smtc;
        if (smtc is null || _disposed)
        {
            return;
        }

        try
        {
            if (!snapshot.HasSession)
            {
                // The phone has nothing playing: tear the session down rather than leave a blank or
                // stale one showing. Disabling plus a cleared DisplayUpdater is what removes it from
                // the overlay.
                smtc.IsPlayEnabled = false;
                smtc.IsPauseEnabled = false;
                smtc.IsNextEnabled = false;
                smtc.IsPreviousEnabled = false;
                smtc.IsEnabled = false;
                smtc.PlaybackStatus = MediaPlaybackStatus.Closed;

                SystemMediaTransportControlsDisplayUpdater cleared = smtc.DisplayUpdater;
                cleared.ClearAll();
                cleared.Update();
                return;
            }

            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;
            smtc.PlaybackStatus =
                snapshot.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

            SystemMediaTransportControlsDisplayUpdater updater = smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = snapshot.Title ?? string.Empty;
            updater.MusicProperties.Artist = snapshot.Artist ?? string.Empty;
            if (!string.IsNullOrEmpty(snapshot.Album))
            {
                updater.MusicProperties.AlbumTitle = snapshot.Album;
            }

            updater.Update();
        }
        catch (Exception ex)
        {
            Log.Error("The SMTC publisher failed to publish a snapshot.", ex);
        }
    }

    private void OnButtonPressed(
        SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // Fires on a thread-pool thread. Map only the four Phase-2 transport buttons; ignore the rest
        // (stop, record, fast-forward, channel...) - a button this app cannot forward is one it must
        // not raise.
        MediaCommand? command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => MediaCommand.Play,
            SystemMediaTransportControlsButton.Pause => MediaCommand.Pause,
            SystemMediaTransportControlsButton.Next => MediaCommand.Next,
            SystemMediaTransportControlsButton.Previous => MediaCommand.Previous,
            _ => null,
        };

        if (command is null)
        {
            return;
        }

        try
        {
            CommandRequested?.Invoke(this, command.Value);
        }
        catch (Exception ex)
        {
            Log.Error("An SMTC CommandRequested handler threw.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        SystemMediaTransportControls? smtc = _smtc;
        if (smtc is not null)
        {
            Teardown.Quietly(() => smtc.ButtonPressed -= OnButtonPressed, "unhook the SMTC button handler");
            Teardown.Quietly(
                () =>
                {
                    // Leave nothing showing after us.
                    smtc.IsEnabled = false;
                    SystemMediaTransportControlsDisplayUpdater updater = smtc.DisplayUpdater;
                    updater.ClearAll();
                    updater.Update();
                },
                "clear the SMTC session");
            _smtc = null;
        }

        // Must run on the UI thread, same as construction; the orchestrator disposes the link (and so
        // this) via Teardown.Quietly on that thread.
        Teardown.Quietly(_window.Dispose, "dispose the SMTC host window");
    }

    /// <summary>
    /// A window whose only job is to own an HWND with a message pump for SMTC to bind to. Pushed
    /// off-screen and forced never-visible so it can never flash or take the taskbar even if something
    /// calls Show on it.
    /// </summary>
    private sealed class HiddenMessageWindow : Form
    {
        public HiddenMessageWindow()
        {
            Text = "Klangbruecke.Smtc";
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-32000, -32000);
            Size = new System.Drawing.Size(1, 1);
            Opacity = 0;
        }

        // Belt and braces: the HWND is created lazily on Handle access; this guarantees the window is
        // never actually made visible regardless of any Show call.
        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);
    }
}

/// <summary>
/// The crux of FINDINGS 20.1, ported verbatim from the validated spike: obtain
/// <c>ISystemMediaTransportControlsInterop</c> and call <c>GetForWindow</c> by walking the interop
/// vtable with a function pointer.
///
/// .NET 5+ removed the built-in WinRT projection, so the classic "[ComImport] interface +
/// Marshal.GetObjectForIUnknown + cast" path throws InvalidCastException when it hits the WinRT
/// activation factory. This sidesteps the RCW entirely and calls the interop vtable directly with
/// function pointers.
/// </summary>
internal static unsafe class SmtcInterop
{
    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void WindowsCreateString(
        string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        IntPtr activatableClassId, [In] ref Guid iid, out IntPtr factory);

    // ISystemMediaTransportControlsInterop (derives from IInspectable).
    private static readonly Guid IID_ISystemMediaTransportControlsInterop =
        new("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A");

    // IInspectable: supported by every WinRT object. We request this from GetForWindow rather than
    // typeof(SystemMediaTransportControls).GUID -- CsWinRT does NOT stamp the projected runtime-class
    // type with its default interface IID, so that GUID yields E_NOINTERFACE. Requesting IInspectable
    // always succeeds; MarshalInspectable.FromAbi then QIs to the right interface lazily on first use.
    private static readonly Guid IID_IInspectable =
        new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    private const string RuntimeClassName = "Windows.Media.SystemMediaTransportControls";

    public static SystemMediaTransportControls GetForWindow(IntPtr hwnd)
    {
        Guid interopIid = IID_ISystemMediaTransportControlsInterop;

        WindowsCreateString(RuntimeClassName, RuntimeClassName.Length, out IntPtr hClass);
        try
        {
            // RoGetActivationFactory QIs the factory to the interop interface for us, so 'factory'
            // already points at ISystemMediaTransportControlsInterop*.
            RoGetActivationFactory(hClass, ref interopIid, out IntPtr factory);
            try
            {
                // IInspectable-derived vtable layout:
                //   0 QueryInterface  1 AddRef  2 Release
                //   3 GetIids  4 GetRuntimeClassName  5 GetTrustLevel
                //   6 GetForWindow(HWND appWindow, REFIID riid, void** ppv)
                IntPtr* vtbl = *(IntPtr**)factory;
                var getForWindow =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[6];

                Guid riid = IID_IInspectable;
                IntPtr result;
                int hr = getForWindow(factory, hwnd, &riid, &result);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                try
                {
                    // Turn the raw IInspectable* into the CsWinRT projection.
                    return MarshalInspectable<SystemMediaTransportControls>.FromAbi(result);
                }
                finally
                {
                    if (result != IntPtr.Zero)
                    {
                        Marshal.Release(result);
                    }
                }
            }
            finally
            {
                if (factory != IntPtr.Zero)
                {
                    Marshal.Release(factory);
                }
            }
        }
        finally
        {
            if (hClass != IntPtr.Zero)
            {
                WindowsDeleteString(hClass);
            }
        }
    }
}
