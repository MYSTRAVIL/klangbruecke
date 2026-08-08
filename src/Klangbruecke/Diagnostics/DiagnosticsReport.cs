using System;
using System.Collections.Generic;
using System.Text;

namespace Klangbruecke.Diagnostics;

/// <summary>The paste-ready snapshot behind Copy Diagnostics. Pure, so its shape is pinned.</summary>
public static class DiagnosticsReport
{
    public static string Build(
        Version version,
        string os,
        string state,
        string detail,
        IReadOnlyList<string> recentLogLines)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(os);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(recentLogLines);

        var sb = new StringBuilder();
        sb.AppendLine("Klangbruecke diagnostics - review before sharing (includes device names).");
        sb.AppendLine();
        sb.AppendLine($"Version: {version}");
        sb.AppendLine($"OS:      {os}");
        sb.AppendLine($"State:   {state} - {detail}");
        sb.AppendLine();
        sb.AppendLine($"Recent log ({recentLogLines.Count} lines):");

        foreach (string line in recentLogLines)
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }
}
