using System;
using System.Collections.Generic;
using System.Linq;

namespace Klangbruecke.Connection;

/// <summary>
/// First-present-wins auto-pick over a remembered phone set. Pure, so its rules are pinned by tests;
/// presence is a predicate the caller backs with a live link-status read. Never thrashes a working
/// (present) incumbent, even when another remembered phone is also present.
/// </summary>
public static class PhonePicker
{
    public static string? Pick(string? activeId, IReadOnlyList<string> remembered, Func<string, bool> isPresent)
    {
        // 1. Keep a present incumbent that is still remembered.
        if (activeId is not null && remembered.Contains(activeId) && isPresent(activeId))
        {
            return activeId;
        }

        // 2. Otherwise the first remembered phone that is present.
        foreach (string id in remembered)
        {
            if (isPresent(id))
            {
                return id;
            }
        }

        // 3. None present: keep watching the remembered incumbent so its fast-reconnect edge still fires.
        if (activeId is not null && remembered.Contains(activeId))
        {
            return activeId;
        }

        // 4. No usable incumbent: watch the first remembered phone, or nothing.
        return remembered.Count > 0 ? remembered[0] : null;
    }
}
