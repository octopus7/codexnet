using System;
using System.Collections.Generic;

namespace YoutubeCli;

internal static class AlwaysDebug
{
    // IDs to always print date-related debug info for, regardless of --debug flag
    private static readonly HashSet<string> IdSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "m7kqGRW4rGw",
        "WbPdGBSROco",
    };

    public static bool EnabledFor(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        return IdSet.Contains(id);
    }
}

