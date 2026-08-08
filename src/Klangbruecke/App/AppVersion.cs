using System;
using System.Reflection;

namespace Klangbruecke.App;

/// <summary>The running assembly's version. Kept in step with the manifest by hand (see Program).</summary>
public static class AppVersion
{
    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
}
