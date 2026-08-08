using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Klangbruecke.Diagnostics;

/// <summary>
/// The tail of a day's log file, for the Copy Diagnostics snapshot. Never throws - diagnostics must
/// not be the reason the app fails, matching <see cref="FileLog"/>'s own boundary.
/// </summary>
public static class LogTail
{
    public static IReadOnlyList<string> ReadRecent(string directory, DateTimeOffset day, int count)
    {
        try
        {
            string path = Path.Combine(directory, FileLog.FileNameFor(day));
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }

            string[] lines = File.ReadAllLines(path);
            int take = Math.Clamp(count, 0, lines.Length);
            return lines[^take..];
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            return Array.Empty<string>();
        }
    }
}
