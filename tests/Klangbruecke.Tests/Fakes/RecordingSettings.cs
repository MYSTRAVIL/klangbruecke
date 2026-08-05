using Klangbruecke.Config;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// Settings that record a save instead of writing one.
///
/// This is not only about observing the call. <see cref="Settings.Save"/> writes
/// %LOCALAPPDATA%\Klangbruecke\settings.json - the real file the installed app reads on this very
/// machine - so a suite that let it run would overwrite the developer's own phone selection with
/// whatever a test happened to construct, and the failure would show up hours later as "the app
/// forgot my phone". That is the whole reason <see cref="Settings.Save"/> is virtual.
///
/// <see cref="OnSave"/> is what lets a test assert <em>ordering</em> rather than only that a save
/// happened: the one property the brief pins about <c>SelectPhone</c> is that the phone id reaches
/// the settings file before anything is connected, because the packaged build has to be able to come
/// back to it after a reboot even if this attempt fails.
/// </summary>
public sealed class RecordingSettings : Settings
{
    public int SaveCount { get; private set; }

    /// <summary>Runs inside <see cref="Save"/>, so a test can see the world as the save saw it.</summary>
    public Action? OnSave { get; set; }

    public override void Save()
    {
        SaveCount++;
        OnSave?.Invoke();
    }
}
