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

    [Fact]
    public void Build_throws_ArgumentNullException_when_version_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => AboutText.Build(null!));
    }

    [Fact]
    public void RepoUrl_is_the_GitHub_repository()
    {
        Assert.Equal("https://github.com/MYSTRAVIL/klangbruecke", AboutText.RepoUrl);
    }
}
