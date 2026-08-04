using System.Text.RegularExpressions;

namespace Klangbruecke.Bluetooth;

/// <summary>
/// Pulls the Bluetooth address out of a Windows device id.
///
/// The A2DP selector and the phone-line selector return different id shapes for the same phone,
/// and the address is the only token they share. Matching on it is what stops the call transport
/// binding to whichever phone happens to enumerate first.
/// </summary>
public static partial class BluetoothDeviceId
{
    // A radio that has not finished initialising reports this. Accepting it would make every id
    // carrying it correlate with every other - the arbitrary pairing this class exists to prevent.
    private const string UnsetAddress = "000000000000";

    public static string? TryExtractAddress(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        // The separated form is unambiguous, so try it before touching anything else. The last
        // such run wins: ids of the form "<radio>-<device>" put the remote device second.
        MatchCollection separated = SeparatedAddress().Matches(deviceId);
        if (separated.Count > 0)
        {
            return Normalize(separated[^1].Value);
        }

        // GUID sections end in a 12-hex-digit group that a bare scan would match, so drop every
        // braced section before looking for a plain run. Substituting a space rather than nothing
        // keeps the text either side of a section from fusing into a run that was never there.
        string stripped = BracedSection().Replace(deviceId, " ");

        // First rather than last, the opposite of the separated form, because the shapes differ:
        // every observed id carries the address exactly once, while the competing 12-hex runs all
        // live in trailing interface GUIDs. Should a truncated id ever defeat the brace strip, the
        // earlier run is the address and the later one is GUID debris.
        Match bare = BareAddress().Match(stripped);
        return bare.Success ? Normalize(bare.Value) : null;
    }

    private static string? Normalize(string match)
    {
        string address = match
            .Replace(":", string.Empty)
            .Replace("-", string.Empty)
            .ToUpperInvariant();

        return address == UnsetAddress ? null : address;
    }

    // Lookbehind rather than \b: the address is often glued to a word, as in
    // "Bluetooth#Bluetoothf8:e4:...", where \b would not anchor. The backreference forces one
    // consistent separator, without which the pattern happily matches the last four pairs of
    // one address plus the first two of the next.
    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{2}(?<sep>[:-])(?:[0-9a-fA-F]{2}\k<sep>){4}[0-9a-fA-F]{2}(?![0-9a-fA-F])")]
    private static partial Regex SeparatedAddress();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex BracedSection();

    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{12}(?![0-9a-fA-F])")]
    private static partial Regex BareAddress();
}
