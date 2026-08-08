using System;

namespace Klangbruecke.App;

/// <summary>The About dialog's body. Pure, so the wording is pinned by a test.</summary>
public static class AboutText
{
    public const string RepoUrl = "https://github.com/MYSTRAVIL/klangbruecke";

    // Three-part version: the packaged build carries a four-part number whose fourth part is
    // meaningless to a user (and the GitHub tag never has it).
    public static string Build(Version version) =>
        $"Klangbruecke {version.ToString(3)}\n" +
        "Phone audio on your PC over Bluetooth - music and calls, in the tray.";
}
