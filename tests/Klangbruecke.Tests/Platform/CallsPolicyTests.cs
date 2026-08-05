using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;
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

    // Enumerated rather than listed. A hand-written list would have made the totality test the one
    // place a fourth CallsAvailability member could be added without anything noticing - which is the
    // very arrival Explain_DoesNotBlameMsixForAnUnrecognisedValue below exists to survive.
    public static IEnumerable<object[]> EveryAvailability() =>
        Enum.GetValues<CallsAvailability>().Select(availability => new object[] { availability });

    [Theory]
    [MemberData(nameof(EveryAvailability))]
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

    // --- how far a verdict lets a run go (was CallTransportPlan, folded in here) ---
    //
    // <b>ShouldEnumerate and ShouldRegister have had no caller since Task 17.</b> Their only one was
    // TrayContext.ConnectCallsAsync, and the connect path is CallsHalf's now - which asks neither. The
    // tests below still pin the rule and are worth keeping for that, because the rule is what the fix
    // needs; but read every "does", "costs" and "still enumerates" in them as a description of the
    // rule, not of the running app.
    //
    // What the app actually does today: an unpackaged run attempts RegisterApp, fails, backs off and
    // retries at the 60 s ceiling for the life of the process, because ShouldRegister is exactly the
    // gate that used to stop it. The banner on CallsPolicy.ShouldEnumerate carries the whole story and
    // the fix; this note exists because a reader who lands here first would otherwise get the old one.

    [Fact]
    public void Enabled_DoesBothSteps()
    {
        Assert.True(CallsPolicy.ShouldEnumerate(CallsAvailability.Enabled));
        Assert.True(CallsPolicy.ShouldRegister(CallsAvailability.Enabled));
    }

    // The departure from the plan text, pinned because reverting it is a one-line change that looks
    // like a tidy-up. Discovery was verified to work with no package identity; only RegisterApp needs
    // the restricted capability. Skipping enumeration as well would cost a development run the one
    // calls-side fact it can establish - whether the phone's transport is discoverable at all.
    //
    // Conditional, not current: nothing consults either verdict today. See the note above.
    [Fact]
    public void NoPackageIdentity_StillEnumerates_ButDoesNotRegister()
    {
        Assert.True(CallsPolicy.ShouldEnumerate(CallsAvailability.DisabledNoPackageIdentity));
        Assert.False(CallsPolicy.ShouldRegister(CallsAvailability.DisabledNoPackageIdentity));
    }

    // The user's setting is the one verdict that suppresses enumeration too: they asked for the calls
    // half to be off, and enumerating anyway would fill the log with a device they said not to touch.
    [Fact]
    public void DisabledBySetting_DoesNeither()
    {
        Assert.False(CallsPolicy.ShouldEnumerate(CallsAvailability.DisabledBySetting));
        Assert.False(CallsPolicy.ShouldRegister(CallsAvailability.DisabledBySetting));
    }

    // A fourth availability reason added later must not inherit permission to claim the hands-free
    // role. Enumeration is read-only and safe to default on; registration is not.
    [Fact]
    public void AnUnrecognisedVerdict_NeverRegisters()
    {
        Assert.False(CallsPolicy.ShouldRegister((CallsAvailability)99));
    }

    // --- log level ---

    // The user's own choice is ordinary progress; a capability that cannot apply is not. Splitting on
    // the reason is what lets one rule cover both halves - the alternative, one flat level for every
    // "disabled", is what left [WRN] telling half the story about missing package identity.
    [Theory]
    [InlineData(CallsAvailability.Enabled, LogLevel.Info)]
    [InlineData(CallsAvailability.DisabledBySetting, LogLevel.Info)]
    [InlineData(CallsAvailability.DisabledNoPackageIdentity, LogLevel.Warn)]
    public void LevelFor_WarnsOnlyWhenTheHalfCannotRun(CallsAvailability availability, LogLevel expected)
    {
        Assert.Equal(expected, CallsPolicy.LevelFor(availability));
    }

    // The rule is shared with the music half, and the whole point of the fix was that the two gates
    // had drifted apart. Asserting them against each other is the only thing that notices if one
    // moves: missing package identity is the same root cause on both sides and must read the same.
    [Fact]
    public void LevelFor_AgreesWithTheMusicHalfOnMissingPackageIdentity()
    {
        Assert.Equal(
            AudioSinkPolicy.LevelFor(isPackaged: false),
            CallsPolicy.LevelFor(CallsAvailability.DisabledNoPackageIdentity));

        Assert.Equal(AudioSinkPolicy.LevelFor(isPackaged: true), CallsPolicy.LevelFor(CallsAvailability.Enabled));
    }

    // An unrecognised verdict is loud rather than quietly filed as ordinary progress - the same
    // instinct as Explain's and ShouldRegister's default arms.
    [Fact]
    public void LevelFor_WarnsAboutAnUnrecognisedVerdict()
    {
        Assert.Equal(LogLevel.Warn, CallsPolicy.LevelFor((CallsAvailability)99));
    }

    // --- the tray's own item (Task 17) ---
    //
    // Three values from one call rather than three rules the call site combines, because the two ways
    // of getting it wrong are both combinations: an item that says "(needs MSIX)" while still being
    // clickable, and one that reads as switched on while nothing can ever register. Returned together,
    // neither is representable.

    [Fact]
    public void MenuItem_NamesMsix_AndRefusesTheClick_WhenUnpackaged()
    {
        // The whole of what an unpackaged run can honestly offer here. The predecessor of this item
        // showed checked and did nothing, which is the worst of the three: the user reads the switch
        // as on, the calls half retries forever, and nothing anywhere names the reason.
        Assert.Equal(
            ("Route calls to PC (needs MSIX)", false, false),
            CallsPolicy.MenuItem(isPackaged: false, enableCalls: true));
    }

    [Fact]
    public void MenuItem_IsPlainAndClickable_WhenPackaged()
    {
        Assert.Equal(
            ("Route calls to PC", true, true),
            CallsPolicy.MenuItem(isPackaged: true, enableCalls: true));
    }

    // The tick follows the setting, and only the setting. It is the one of the three that has to move
    // when the user clicks, and an item whose tick came from anywhere else would be one they could
    // switch off and never switch back on.
    [Fact]
    public void MenuItem_UnticksWhenTheUserTurnsCallsOff_ButStaysClickable()
    {
        Assert.Equal(
            ("Route calls to PC", true, false),
            CallsPolicy.MenuItem(isPackaged: true, enableCalls: false));
    }

    // Package identity, never the availability verdict. Decide() answers DisabledBySetting the moment
    // calls are switched off, so an item gated on that would disable itself on the click that turned
    // it off - a switch with exactly one use.
    [Fact]
    public void MenuItem_StaysClickable_ForEverySettingWhenPackaged()
    {
        Assert.True(CallsPolicy.MenuItem(isPackaged: true, enableCalls: true).Enabled);
        Assert.True(CallsPolicy.MenuItem(isPackaged: true, enableCalls: false).Enabled);
    }

    // Unpackaged, the setting cannot rescue the item - the restricted capability is not something a
    // preference can grant. Asserted for both settings so a call site that ORed the two could not pass.
    [Fact]
    public void MenuItem_IsRefused_ForEverySettingWhenUnpackaged()
    {
        Assert.False(CallsPolicy.MenuItem(isPackaged: false, enableCalls: true).Enabled);
        Assert.False(CallsPolicy.MenuItem(isPackaged: false, enableCalls: false).Enabled);
    }

    // The suffix is the only part of the two texts that differs, so the item cannot start naming a
    // cause it has not been given. Pinned separately from the exact strings above because "the
    // packaged text is a prefix of the unpackaged one" is what makes the pair read as one item in two
    // conditions rather than as two unrelated labels.
    [Fact]
    public void MenuItem_SaysTheSameThing_AndOnlyAddsTheReason()
    {
        Assert.StartsWith(
            CallsPolicy.MenuItem(isPackaged: true, enableCalls: true).Text,
            CallsPolicy.MenuItem(isPackaged: false, enableCalls: true).Text,
            StringComparison.Ordinal);
    }
}
