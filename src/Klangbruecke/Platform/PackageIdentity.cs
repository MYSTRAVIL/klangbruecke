using System.Runtime.InteropServices;
using Klangbruecke.Diagnostics;

namespace Klangbruecke.Platform;

/// <summary>
/// Whether this process is running with MSIX package identity.
///
/// The restricted capability <c>phoneLineTransportManagement</c> only applies with identity, so an
/// unpackaged run cannot claim the hands-free role. Detecting that is what makes "dotnet run" usable
/// as a development loop for the music half. See docs/FINDINGS.md §2.
///
/// Asked here rather than via <c>Package.Current</c>, which answers by throwing
/// InvalidOperationException 0x80073D54 when there is no package - a thrown exception on every
/// development run, in a startup path.
/// </summary>
public static class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    // Outside the win32 code range the probe can return, so "there is no package" and "the probe
    // never ran" stay tellable apart. Both mean no calls; only the second is a defect.
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
    /// when the probe itself failed, and only the second is a bug - one that would present as the
    /// calls half quietly never starting in the shipped MSIX, under a log line naming the wrong cause.
    /// This is the only value a test can use to tell the two apart.
    /// </summary>
    public static int ProbeResult { get; } = Probe();

    public static bool IsPackaged => ProbeResult != AppModelErrorNoPackage && ProbeResult != ProbeFailed;

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
            Log.Error("Package identity probe failed; assuming unpackaged.", ex);
            return ProbeFailed;
        }
    }
}
