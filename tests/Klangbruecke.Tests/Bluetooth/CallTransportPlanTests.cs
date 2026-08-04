using Klangbruecke.Bluetooth;
using Klangbruecke.Platform;
using Xunit;

namespace Klangbruecke.Tests.Bluetooth;

public sealed class CallTransportPlanTests
{
    [Fact]
    public void Enabled_DoesBothSteps()
    {
        Assert.True(CallTransportPlan.ShouldEnumerate(CallsAvailability.Enabled));
        Assert.True(CallTransportPlan.ShouldRegister(CallsAvailability.Enabled));
    }

    // The departure from the plan text, pinned because reverting it is a one-line change that looks
    // like a tidy-up. Discovery was verified to work with no package identity; only RegisterApp needs
    // the restricted capability. Skipping enumeration as well would cost every development run the one
    // calls-side fact it can establish - whether the phone's transport is discoverable at all.
    [Fact]
    public void NoPackageIdentity_StillEnumerates_ButDoesNotRegister()
    {
        Assert.True(CallTransportPlan.ShouldEnumerate(CallsAvailability.DisabledNoPackageIdentity));
        Assert.False(CallTransportPlan.ShouldRegister(CallsAvailability.DisabledNoPackageIdentity));
    }

    // The user's setting is the one verdict that suppresses enumeration too: they asked for the calls
    // half to be off, and enumerating anyway would fill the log with a device they said not to touch.
    [Fact]
    public void DisabledBySetting_DoesNeither()
    {
        Assert.False(CallTransportPlan.ShouldEnumerate(CallsAvailability.DisabledBySetting));
        Assert.False(CallTransportPlan.ShouldRegister(CallsAvailability.DisabledBySetting));
    }

    // A fourth availability reason added later must not inherit permission to claim the hands-free
    // role. Enumeration is read-only and safe to default on; registration is not.
    [Fact]
    public void AnUnrecognisedVerdict_NeverRegisters()
    {
        Assert.False(CallTransportPlan.ShouldRegister((CallsAvailability)99));
    }
}
