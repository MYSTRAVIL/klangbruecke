using System;
using Klangbruecke.App;
using Xunit;

namespace Klangbruecke.Tests.App;

public sealed class AboutTextTests
{
    [Fact]
    public void Build_names_the_app_and_the_three_part_version()
    {
        string text = AboutText.Build(new Version(0, 2, 2, 0));

        Assert.Contains("Klangbruecke", text);
        Assert.Contains("0.2.2", text);
    }
}
