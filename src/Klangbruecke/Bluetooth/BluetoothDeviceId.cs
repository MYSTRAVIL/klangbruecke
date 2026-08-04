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
    // Two tokens are worse to return than nothing at all: accepting either makes every id carrying
    // it correlate with every other, silently restoring the arbitrary pairing this class exists to
    // remove. 000000000000 is what a radio reports before it has initialised. 00805F9B34FB is the
    // tail of the Bluetooth base UUID, which every classic profile GUID ends in - so two different
    // phones would reduce to one key. Neither is reachable on any observed id; they become reachable
    // when a truncated or nested brace defeats the strip below.
    private static readonly HashSet<string> CollidingTokens =
        new(StringComparer.Ordinal) { "000000000000", "00805F9B34FB" };

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
            return NormalizeOrReject(separated[^1].Value);
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
        return bare.Success ? NormalizeOrReject(bare.Value) : null;
    }

    /// <summary>Strips separators and uppercases, or returns null if the match cannot be an address.</summary>
    private static string? NormalizeOrReject(string match)
    {
        string address = match
            .Replace(":", string.Empty)
            .Replace("-", string.Empty)
            .ToUpperInvariant();

        return CollidingTokens.Contains(address) ? null : address;
    }

    // Lookbehind rather than \b: the address is often glued to a word, as in
    // "Bluetooth#Bluetoothf8:e4:...", where \b would not anchor. The backreference forces one
    // consistent separator, without which the pattern happily matches the last four pairs of
    // one address plus the first two of the next.
    //
    // Assumes a separated run is a whole number of addresses. It has to: "<radio>-<device>" is one
    // unbroken 12-pair run and splitting it on the sixth pair is the entire point, so the pattern
    // cannot also tell a 6-pair run from the first six pairs of an 8-pair one. A run of 7 or 8 pairs
    // yields the first 6 and drops the rest. No device id is known to produce one.
    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{2}(?<sep>[:-])(?:[0-9a-fA-F]{2}\k<sep>){4}[0-9a-fA-F]{2}(?![0-9a-fA-F])")]
    private static partial Regex SeparatedAddress();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex BracedSection();

    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{12}(?![0-9a-fA-F])")]
    private static partial Regex BareAddress();
}
