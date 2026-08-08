using System.Collections.Generic;
using Klangbruecke.Config;
using Xunit;

namespace Klangbruecke.Tests.Config;

public sealed class SettingsTests
{
    [Fact]
    public void Migrate_seeds_remembered_from_a_lone_selected_phone()
    {
        var s = new Settings { PhoneDeviceId = "phone-A", RememberedPhoneIds = new List<string>() };
        Settings.Migrate(s);
        Assert.Equal(new[] { "phone-A" }, s.RememberedPhoneIds);
    }

    [Fact]
    public void Migrate_leaves_an_existing_remembered_set_alone()
    {
        var s = new Settings { PhoneDeviceId = "phone-A", RememberedPhoneIds = new List<string> { "phone-B" } };
        Settings.Migrate(s);
        Assert.Equal(new[] { "phone-B" }, s.RememberedPhoneIds);
    }

    [Fact]
    public void Migrate_with_no_selected_phone_leaves_the_set_empty()
    {
        var s = new Settings { PhoneDeviceId = null, RememberedPhoneIds = new List<string>() };
        Settings.Migrate(s);
        Assert.Empty(s.RememberedPhoneIds);
    }

    [Fact]
    public void EventSounds_defaults_on()
    {
        Assert.True(new Settings().EventSounds);
    }
}
