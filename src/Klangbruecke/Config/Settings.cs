using System.Text.Json;
using System.Text.Json.Serialization;
using Klangbruecke.Diagnostics;

namespace Klangbruecke.Config;

/// <summary>
/// Persisted to %LOCALAPPDATA%\Klangbruecke\settings.json.
///
/// Literally that path in the installed build too, but only because the manifest disables Desktop
/// Bridge write virtualization. Without that opt-out this file would land in
/// %LOCALAPPDATA%\Packages\&lt;PFN&gt;\LocalCache\Local\ while GetFolderPath kept returning the path
/// above - which matters because deleting this file by hand is the documented recovery from a
/// bricked auto-connect. See docs/FINDINGS.md §9.
/// </summary>
public class Settings
{
    /// <summary>Bluetooth device id to auto-connect the A2DP sink to.</summary>
    public string? PhoneDeviceId { get; set; }

    /// <summary>MMDevice id of the render endpoint music should be routed to. Null = system default.</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>Reconnect automatically when the phone comes back into range.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Route call audio through the HFP hands-free transport.</summary>
    public bool EnableCalls { get; set; } = true;

    /// <summary>Phones to auto-connect: whichever is present wins (first-present). See PhonePicker.</summary>
    public List<string> RememberedPhoneIds { get; set; } = new();

    /// <summary>Play a chime on connect / disconnect / degrade.</summary>
    public bool EventSounds { get; set; } = true;

    [JsonIgnore]
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klangbruecke");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return Migrate(JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings());
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings must not stop the app starting - but silently starting
            // from defaults presents as "it forgot my phone", which reads like a Bluetooth fault.
            Log.Warn($"Could not read settings, starting from defaults: {ex.Message}");
        }

        return Migrate(new Settings());
    }

    // Seed the remembered set from a pre-bundle-2 single selection, so an upgrade keeps auto-connecting
    // the one phone the user had picked. Idempotent: only fires when nothing is remembered yet.
    internal static Settings Migrate(Settings settings)
    {
        if (settings.RememberedPhoneIds.Count == 0 && settings.PhoneDeviceId is not null)
        {
            settings.RememberedPhoneIds.Add(settings.PhoneDeviceId);
        }

        return settings;
    }

    /// <summary>
    /// Writes the file. Virtual, and the class is open, for one reason: this path is the real
    /// %LOCALAPPDATA% file that the installed app on this machine reads at startup, so a suite that
    /// let it run would overwrite the developer's own saved phone with whatever a test constructed -
    /// and the damage would surface hours later as "the app forgot my phone", which reads as a
    /// Bluetooth fault. <c>ConnectionManager</c> saves on five separate setters and more than ten
    /// tests exercise them; not one of those tests can reach a setter without coming through here.
    /// </summary>
    public virtual void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - the run continues on the in-memory copy. The cost lands at the next start,
            // as a selection that did not stick, so the record of it has to be made here.
            Log.Warn($"Could not save settings: {ex.Message}");
        }
    }
}
