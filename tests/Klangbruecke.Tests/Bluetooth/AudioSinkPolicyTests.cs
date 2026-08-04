using Klangbruecke.Bluetooth;
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
}
