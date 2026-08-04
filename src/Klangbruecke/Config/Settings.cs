using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klangbruecke.Config;

/// <summary>
/// Persisted to %LOCALAPPDATA%\Klangbruecke\settings.json.
/// </summary>
public sealed class Settings
{
    /// <summary>Bluetooth device id to auto-connect the A2DP sink to.</summary>
    public string? PhoneDeviceId { get; set; }

    /// <summary>MMDevice id of the render endpoint music should be routed to. Null = system default.</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>Reconnect automatically when the phone comes back into range.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Route call audio through the HFP hands-free transport.</summary>
    public bool EnableCalls { get; set; } = true;

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
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings must not stop the app starting.
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
