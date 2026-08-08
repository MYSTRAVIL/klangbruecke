using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Klangbruecke.Diagnostics;

/// <summary>
/// The tail of the log spanning multiple days, for the Copy Diagnostics snapshot. Never throws -
/// diagnostics must not be the reason the app fails, matching <see cref="FileLog"/>'s own boundary.
/// </summary>
public static class LogTail
{
    /// <summary>
    /// Returns the last <paramref name="count"/> lines from log files, spanning up to
    /// <paramref name="maxDaysBack"/> days to ensure a snapshot taken just after midnight includes
    /// the prior evening. Oldest day first, so the returned lines are chronological.
    /// </summary>
    public static IReadOnlyList<string> ReadRecent(string directory, DateTimeOffset asOf, int count, int maxDaysBack = 7)
    {
        try
        {
            if (count <= 0)
            {
                return Array.Empty<string>();
            }

            // Span the day boundary: a snapshot taken just after midnight would otherwise miss the
            // prior evening. Oldest day first, so the last <count> lines are the most recent.
            var lines = new List<string>();
            for (int back = maxDaysBack - 1; back >= 0; back--)
            {
                string path = Path.Combine(directory, FileLog.FileNameFor(asOf.AddDays(-back)));
                if (File.Exists(path))
                {
                    lines.AddRange(File.ReadAllLines(path));
                }
            }

            int take = Math.Min(count, lines.Count);
            return take == 0 ? Array.Empty<string>() : lines.GetRange(lines.Count - take, take);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            return Array.Empty<string>();
        }
    }
}
