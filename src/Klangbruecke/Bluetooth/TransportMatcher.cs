namespace Klangbruecke.Bluetooth;

/// <summary>Why a transport was, or was not, chosen. The log level follows from this.</summary>
public enum TransportMatchOutcome
{
    /// <summary>The transport carries the same Bluetooth address as the selected phone.</summary>
    AddressMatch,

    /// <summary>No address match, but only one transport existed, so it was taken anyway.</summary>
    SoleCandidate,

    /// <summary>Nothing was enumerated. Not an error - the phone may simply not offer HFP.</summary>
    NoCandidates,

    /// <summary>Several transports, none of them the phone's. Connects nothing, deliberately.</summary>
    Ambiguous,
}

/// <summary>A phone-line transport reduced to the two fields matching needs, so this is testable
/// without constructing a WinRT <c>DeviceInformation</c>, which nothing outside WinRT can do.</summary>
public readonly record struct TransportCandidate(string Id, string Name);

/// <param name="Match">The transport to connect, or null to connect nothing.</param>
/// <param name="Outcome">Which rule produced <paramref name="Match"/>.</param>
/// <param name="Reason">Log-grade sentence naming the ids and addresses involved.</param>
public readonly record struct TransportMatchResult(
    TransportCandidate? Match,
    TransportMatchOutcome Outcome,
    string Reason);

/// <summary>
/// Picks the phone-line transport belonging to the chosen phone rather than whichever one enumerates
/// first. The A2DP selector and the phone-line selector return different id shapes for the same phone,
/// so they are correlated on the Bluetooth address they share.
///
/// Verified against the real phone: the A2DP id (110a, ...\SNK) and the transport id (111f,
/// ...\service) both reduce to C01C6A90E174. The two-phone collision this exists to prevent has never
/// been reproduced on this machine - only one phone is paired - so the pinned tests below are the only
/// evidence the rules behave, and they matter more here than usual.
/// </summary>
public static class TransportMatcher
{
    public static TransportMatchResult Match(IReadOnlyList<TransportCandidate> candidates, string? phoneDeviceId)
    {
        if (candidates.Count == 0)
        {
            return new TransportMatchResult(
                null,
                TransportMatchOutcome.NoCandidates,
                $"No phone-line transport enumerated for phone id={phoneDeviceId}.");
        }

        string? wanted = BluetoothDeviceId.TryExtractAddress(phoneDeviceId);

        if (wanted is not null)
        {
            foreach (TransportCandidate candidate in candidates)
            {
                // Ordinal is safe because TryExtractAddress already uppercases: both sides arrive
                // normalized, so a lowercase id on one selector still matches an uppercase one.
                if (string.Equals(BluetoothDeviceId.TryExtractAddress(candidate.Id), wanted, StringComparison.Ordinal))
                {
                    return new TransportMatchResult(
                        candidate,
                        TransportMatchOutcome.AddressMatch,
                        $"Matched transport '{candidate.Name}' to phone address {wanted} "
                        + $"({candidates.Count} candidate(s) considered).");
                }
            }
        }

        if (candidates.Count == 1)
        {
            // One candidate is not a coin flip, so the old behaviour is kept - but it is still taken
            // blind, and the two ways of arriving here are worth telling apart in the log. An address
            // that extracted on both sides and simply differs means this transport belongs to a
            // *different* paired phone; that is the wrong-phone bug, surviving only because refusing
            // would also break the case where a phone legitimately reports different addresses per
            // profile, which has not been observed but has not been ruled out either.
            string? only = BluetoothDeviceId.TryExtractAddress(candidates[0].Id);
            string detail = wanted is not null && only is not null
                ? $"its address {only} differs from the phone's {wanted}"
                : $"no address could be extracted (phone={wanted ?? "none"}, transport={only ?? "none"})";

            return new TransportMatchResult(
                candidates[0],
                TransportMatchOutcome.SoleCandidate,
                $"Falling back to the only transport '{candidates[0].Name}' because {detail}. "
                + $"Phone id={phoneDeviceId}, transport id={candidates[0].Id}");
        }

        return new TransportMatchResult(
            null,
            TransportMatchOutcome.Ambiguous,
            $"No transport matched phone id={phoneDeviceId} among {candidates.Count} candidates; "
            + "connecting none rather than guessing.");
    }
}
