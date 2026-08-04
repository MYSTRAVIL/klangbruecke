using Klangbruecke.Platform;
using Xunit;

namespace Klangbruecke.Tests.Platform;

public sealed class PackageIdentityTests
{
    // 15700 is APPMODEL_ERROR_NO_PACKAGE, spelled out rather than read off the production constant so
    // the two cannot drift together. Corroborated independently: an unpackaged Package.Current throws
    // InvalidOperationException 0x80073D54, and 0x3D54 is 15700.
    //
    // This is the only test that can tell a working probe from a broken one. IsPackaged is false both
    // when there is no package and when the P/Invoke never resolved, and the second failure would
    // otherwise surface as the calls half quietly never starting in the shipped MSIX - with a log line
    // naming the wrong cause.
    [Fact]
    public void ProbeResult_ReportsNoPackage_InAnUnpackagedTestHost()
    {
        // dotnet test runs the host unpackaged; nothing here can make it otherwise.
        Assert.Equal(15700, PackageIdentity.ProbeResult);
    }

    [Fact]
    public void IsPackaged_IsFalse_InAnUnpackagedTestHost()
    {
        Assert.False(PackageIdentity.IsPackaged);
    }
}
