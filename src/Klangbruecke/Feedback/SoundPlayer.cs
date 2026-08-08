using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using Klangbruecke.Diagnostics;

namespace Klangbruecke.Feedback;

/// <summary>
/// Plays the embedded chimes to the default output. Thin OS plumbing, untested by design like
/// WasapiDeviceFactory: each Play is guarded so a playback fault cannot crash the tray. The WAV bytes
/// are read once from the assembly manifest (same pattern as TrayIcons).
/// </summary>
public sealed class SoundPlayer : ISoundPlayer
{
    private readonly System.Media.SoundPlayer _connect;
    private readonly System.Media.SoundPlayer _disconnect;
    private readonly System.Media.SoundPlayer _degraded;

    public SoundPlayer()
    {
        _connect = Load("connect.wav");
        _disconnect = Load("disconnect.wav");
        _degraded = Load("degraded.wav");
    }

    public void Play(SoundEvent e)
    {
        try
        {
            Pick(e).Play(); // async; returns immediately, plays on a worker thread
        }
        catch (Exception ex)
        {
            Log.Warn($"Playing the {e} chime failed: {ex.Message}");
        }
    }

    private System.Media.SoundPlayer Pick(SoundEvent e) => e switch
    {
        SoundEvent.Connected => _connect,
        SoundEvent.Disconnected => _disconnect,
        _ => _degraded,
    };

    private static System.Media.SoundPlayer Load(string fileName)
    {
        Assembly assembly = typeof(SoundPlayer).Assembly;
        string name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
        // Copy to a MemoryStream the SoundPlayer keeps; the manifest stream is not seekable-for-replay.
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded chime '{name}' was null.");
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        var player = new System.Media.SoundPlayer(buffer);
        player.Load();
        return player;
    }
}
