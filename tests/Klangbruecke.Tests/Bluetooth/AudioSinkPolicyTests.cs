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
}
