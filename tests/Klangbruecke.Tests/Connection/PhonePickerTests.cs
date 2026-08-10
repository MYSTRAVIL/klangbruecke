using System;
using System.Collections.Generic;
using Klangbruecke.Connection;
using Xunit;

namespace Klangbruecke.Tests.Connection;

public sealed class PhonePickerTests
{
    private static readonly IReadOnlyList<string> AB = new[] { "A", "B" };

    [Fact]
    public void Keeps_a_present_incumbent_even_if_another_is_also_present()
    {
        // A is active and present; B also present. First-present-wins never thrashes the incumbent.
        Assert.Equal("A", PhonePicker.Pick("A", AB, _ => true));
        Assert.Equal("B", PhonePicker.Pick("B", AB, _ => true));
    }

    [Fact]
    public void Picks_the_first_present_when_the_incumbent_is_absent()
    {
        Assert.Equal("B", PhonePicker.Pick("A", AB, id => id == "B"));
    }

    [Fact]
    public void Keeps_watching_the_absent_incumbent_when_none_is_present()
    {
        Assert.Equal("A", PhonePicker.Pick("A", AB, _ => false));
    }

    [Fact]
    public void Falls_back_to_the_first_remembered_when_there_is_no_incumbent()
    {
        Assert.Equal("A", PhonePicker.Pick(null, AB, _ => false));
    }

    [Fact]
    public void An_incumbent_no_longer_remembered_is_dropped()
    {
        Assert.Equal("A", PhonePicker.Pick("X", AB, _ => false));
    }

    [Fact]
    public void No_remembered_phones_picks_nothing()
    {
        Assert.Null(PhonePicker.Pick("A", Array.Empty<string>(), _ => true));
        Assert.Null(PhonePicker.Pick(null, Array.Empty<string>(), _ => true));
    }
}
