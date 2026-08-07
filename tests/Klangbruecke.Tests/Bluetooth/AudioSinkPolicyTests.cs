using Klangbruecke.Bluetooth;
using Klangbruecke.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests.Bluetooth;

public sealed class AudioSinkPolicyTests
{
    [Fact]
    public void CanOpenConnection_OnlyWhenPackaged()
    {
        Assert.True(AudioSinkPolicy.CanOpenConnection(isPackaged: true));
        Assert.False(AudioSinkPolicy.CanOpenConnection(isPackaged: false));
    }

    // The gate is not a preference and must never become one. Unpackaged, the call it guards raises
    // an AccessViolationException inside the CsWinRT ABI shim and terminates the process - and since
    // TrayContext persists PhoneDeviceId before connecting, letting it through once bricks every
    // later launch with a log that says "starting." and nothing else. Reproduced with a valid live
    // device id, so there is no id for which skipping this is safe.
    [Fact]
    public void CanOpenConnection_IsFalseUnpackaged_WhateverElseIsTrue()
    {
        Assert.False(AudioSinkPolicy.CanOpenConnection(isPackaged: false));
    }

    // The reader who meets this line is looking for their own mistake. Naming the call, and the entry
    // that shows it crashing a bare test host with no app code in the frame, is what stops them.
    [Fact]
    public void Explain_NamesTheCallAndTheFinding_WhenUnpackaged()
    {
        string explanation = AudioSinkPolicy.Explain(isPackaged: false);

        Assert.Contains("TryCreateFromId", explanation);
        Assert.Contains("MSIX", explanation);
        Assert.Contains("FINDINGS.md", explanation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explain_ReturnsSomethingEitherWay(bool isPackaged)
    {
        Assert.False(string.IsNullOrWhiteSpace(AudioSinkPolicy.Explain(isPackaged)));
    }

    [Fact]
    public void Explain_DoesNotBlamePackagingWhenPackaged()
    {
        Assert.DoesNotContain("MSIX", AudioSinkPolicy.Explain(isPackaged: true));
    }

    // Explain(true) was dead code for as long as both call sites sat inside "if (!CanOpenConnection)",
    // which made the assertion above guard nothing - and meant a healthy packaged run announced
    // "Calls enabled." and said nothing at all about music. TrayContext now logs it on both branches,
    // so this pins the line Task 9 will look for.
    [Fact]
    public void Explain_AnnouncesMusicWhenItCanRun()
    {
        Assert.Equal("Music enabled.", AudioSinkPolicy.Explain(isPackaged: true));
    }

    // Shared with the calls half; CallsPolicyTests asserts the two against each other. Missing package
    // identity is the same root cause on both sides, and while they disagreed anyone grepping [WRN]
    // saw only one of them.
    [Theory]
    [InlineData(true, LogLevel.Info)]
    [InlineData(false, LogLevel.Warn)]
    public void LevelFor_WarnsOnlyWhenTheHalfCannotRun(bool isPackaged, LogLevel expected)
    {
        Assert.Equal(expected, AudioSinkPolicy.LevelFor(isPackaged));
    }

    // --- the tray's own item (Task 17) ---
    //
    // Unpackaged, the app has no surface anywhere that names MSIX for the music half. It used to: the
    // tray's own connect path raised "Music needs the packaged build (MSIX)." into the tooltip, and
    // that path is gone. The calls half got a greyed menu item saying why; without this the music half
    // gets a phone list that looks perfectly ordinary, a permanent RetryBackoff, and no explanation
    // outside the log.

    [Fact]
    public void MenuItem_NamesMsix_WhenUnpackaged()
    {
        Assert.Equal(
            ("Phone (music needs MSIX)", true),
            AudioSinkPolicy.MenuItem(isPackaged: false));
    }

    [Fact]
    public void MenuItem_IsPlain_WhenPackaged()
    {
        Assert.Equal(("Phone", true), AudioSinkPolicy.MenuItem(isPackaged: true));
    }

    /// <summary>
    /// <b>The asymmetry with the calls item, recorded where it can be broken.</b> Unpackaged, the
    /// calls item is disabled and this one is not, and that is a decision rather than an oversight:
    /// picking a phone is what starts the <c>DeviceWatcher</c> and the transport enumeration, both of
    /// which work with no package identity and are the only calls-side and link-side facts an
    /// unpackaged run can establish at all - the same trade <see cref="Klangbruecke.Platform.CallsPolicy.ShouldEnumerate"/>
    /// already refuses to give up in the other direction, and what Task 18's smoke test reads.
    ///
    /// So the label carries the whole of the warning and the click stays available. Anyone who later
    /// decides the greyed-out treatment should be symmetric has to change this line and read this
    /// note first.
    /// </summary>
    [Fact]
    public void MenuItem_StaysClickable_EvenUnpackaged()
    {
        Assert.True(AudioSinkPolicy.MenuItem(isPackaged: false).Enabled);
        Assert.True(AudioSinkPolicy.MenuItem(isPackaged: true).Enabled);
    }

    // Same shape as the calls item: one label in two conditions, not two unrelated labels. A menu
    // whose entry renames itself is one the user has to re-find.
    [Fact]
    public void MenuItem_SaysTheSameThing_AndOnlyAddsTheReason()
    {
        Assert.StartsWith(
            AudioSinkPolicy.MenuItem(isPackaged: true).Text,
            AudioSinkPolicy.MenuItem(isPackaged: false).Text,
            StringComparison.Ordinal);
    }
}
