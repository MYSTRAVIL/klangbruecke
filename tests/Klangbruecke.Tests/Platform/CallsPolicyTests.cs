using Klangbruecke.Platform;
using Xunit;

namespace Klangbruecke.Tests.Platform;

public sealed class CallsPolicyTests
{
    [Fact]
    public void Decide_IsEnabled_WhenWantedAndPackaged()
    {
        Assert.Equal(CallsAvailability.Enabled, CallsPolicy.Decide(enableCalls: true, isPackaged: true));
    }

    [Fact]
    public void Decide_BlamesPackageIdentity_WhenWantedButUnpackaged()
    {
        Assert.Equal(
            CallsAvailability.DisabledNoPackageIdentity,
            CallsPolicy.Decide(enableCalls: true, isPackaged: false));
    }

    [Fact]
    public void Decide_BlamesTheSetting_WhenTurnedOff()
    {
        Assert.Equal(CallsAvailability.DisabledBySetting, CallsPolicy.Decide(enableCalls: false, isPackaged: true));
    }

    [Fact]
    public void Decide_PrefersTheSetting_OverMissingPackageIdentity()
    {
        // The user's explicit choice is the more useful thing to report back.
        Assert.Equal(CallsAvailability.DisabledBySetting, CallsPolicy.Decide(enableCalls: false, isPackaged: false));
    }

    [Fact]
    public void Explain_NamesMsixWhenIdentityIsMissing()
    {
        string explanation = CallsPolicy.Explain(CallsAvailability.DisabledNoPackageIdentity);

        Assert.Contains("MSIX", explanation);
    }

    // Pins the finding from the Task 6 WinRT probe (progress.md, "UNPACKAGED ANSWER"): unpackaged,
    // GetDeviceSelector, FindAllAsync, FromId and IsRegistered() all worked against the real phone.
    // Only RegisterApp is believed to need the capability. Wording that says the capability "does not
    // apply" without naming registration reads as "enumeration is pointless", which is false and would
    // send the next maintainer looking for a packaging fix to a problem they do not have.
    [Fact]
    public void Explain_BlamesRegistrationRatherThanDiscovery()
    {
        string explanation = CallsPolicy.Explain(CallsAvailability.DisabledNoPackageIdentity);

        Assert.Contains("RegisterApp", explanation);
    }

    [Theory]
    [InlineData(CallsAvailability.Enabled)]
    [InlineData(CallsAvailability.DisabledBySetting)]
    [InlineData(CallsAvailability.DisabledNoPackageIdentity)]
    public void Explain_ReturnsSomethingForEveryCase(CallsAvailability availability)
    {
        Assert.False(string.IsNullOrWhiteSpace(CallsPolicy.Explain(availability)));
    }

    // A switch whose default arm carries the MSIX text hands that explanation to any member added
    // later - so a fourth reason would report the one cause the user cannot act on. Still has to
    // return something: Explain feeds the tray and the log, neither of which can take a throw.
    [Fact]
    public void Explain_DoesNotBlameMsixForAnUnrecognisedValue()
    {
        string explanation = CallsPolicy.Explain((CallsAvailability)99);

        Assert.False(string.IsNullOrWhiteSpace(explanation));
        Assert.DoesNotContain("MSIX", explanation);
    }
}
