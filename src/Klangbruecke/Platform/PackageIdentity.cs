using System.Runtime.InteropServices;
using Klangbruecke.Diagnostics;

namespace Klangbruecke.Platform;

/// <summary>
/// Whether this process is running with MSIX package identity.
///
/// The restricted capability <c>phoneLineTransportManagement</c> only applies with identity, so an
/// unpackaged run cannot claim the hands-free role, and saying so quietly is what stops every
/// development run sitting in a permanent error state retrying something that cannot succeed.
///
/// Since the §8 finding this carries more than that: <c>AudioSinkPolicy</c> gates
/// <c>AudioPlaybackConnection.TryCreateFromId</c> on this answer, and that call kills an unpackaged
/// process with an <c>AccessViolationException</c> no managed handler can see. A wrong answer here is
/// not a degraded feature; it is an uncatchable crash. See docs/FINDINGS.md §2 and §8.
///
/// Asked here rather than via <c>Package.Current</c>, which answers by throwing
/// InvalidOperationException 0x80073D54 when there is no package - a thrown exception on every
/// development run, in a startup path.
/// </summary>
public static class PackageIdentity
{
    // The two codes that mean "there is a package". 15700 (APPMODEL_ERROR_NO_PACKAGE) is deliberately
    // not named here: nothing decides on it, and the test that pins it spells the literal out so the
    // two cannot drift together.
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    // Negative, so it cannot collide with a win32 code, which keeps "there is no package" and "the
    // probe never ran" tellable apart in ProbeResult. Both mean no calls; only the second is a defect.
    private const int ProbeFailed = -1;

    // PWSTR out-parameter, declared as a pointer because this call never asks for the name - only
    // whether there is one - so nothing needs marshalling back and CharSet does not arise.
    // ExactSpelling because appmodel.h declares one Unicode entry point with no A/W pair; without it
    // the runtime probes a GetCurrentPackageFullNameW that does not exist before finding the real one.
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);

    /// <summary>
    /// Raw result of the identity probe, 15700 (APPMODEL_ERROR_NO_PACKAGE) when unpackaged.
    ///
    /// Exposed because <see cref="IsPackaged"/> is false both when there is genuinely no package and
    /// when the probe itself failed, and only the second is a bug - one that would present as both
    /// halves quietly never starting in the shipped MSIX, under a log line naming the wrong cause.
    /// This is the only value a test can use to tell the two apart, and it only covers the unpackaged
    /// direction; packaging/Test-PackageIdentity.ps1 covers the other.
    /// </summary>
    public static int ProbeResult { get; } = Probe();

    /// <summary>
    /// An allowlist rather than "anything but APPMODEL_ERROR_NO_PACKAGE", because the two wrong
    /// answers do not cost the same. Wrongly unpackaged disables a half that would have worked;
    /// wrongly packaged pins the app in the retry loop this class exists to prevent, and since the
    /// Task 8 finding in docs/FINDINGS.md §8 it would also let the music half reach a call that
    /// terminates the process outright. An unanticipated return code therefore lands on the cheap
    /// side, not the expensive one. The documented return set is closed, so this should never fire.
    /// </summary>
    public static bool IsPackaged => ProbeResult is ErrorSuccess or ErrorInsufficientBuffer;

    private static int Probe()
    {
        try
        {
            uint length = 0;

            // A zero length with a null buffer asks only whether a package exists: the call returns
            // ERROR_INSUFFICIENT_BUFFER when it does and APPMODEL_ERROR_NO_PACKAGE when it does not,
            // and writes no name either way.
            return GetCurrentPackageFullName(ref length, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            // Caught around the call and not around the property read, because this runs inside a
            // static initialiser: an escaping exception becomes a TypeInitializationException that the
            // CLR then caches, so every later read of IsPackaged rethrows it, from whichever WinRT or
            // NAudio callback thread touches it first, in an app with no window to show it in.
            //
            // Unreachable short of a broken kernel32 - the export predates the 19041 floor this app
            // targets - so it is logged rather than handled: false is the quiet direction, and a
            // wrongly-packaged answer would pin the app in the retry loop this whole class prevents.
            try
            {
                Log.Error("Package identity probe failed; assuming unpackaged.", ex);
            }
            catch (Exception)
            {
                // The argument above only holds if the handler cannot throw either, and this one
                // could: production FileLog.Write swallows everything, but Log.Current is settable,
                // so a test double or a Stage 1 sink is one assignment away from turning the guard
                // into the TypeInitializationException it was written to prevent.
            }

            return ProbeFailed;
        }
    }
}
