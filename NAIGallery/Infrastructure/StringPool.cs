using System;
using System.Collections.Concurrent;

namespace NAIGallery;

/// <summary>
/// Simple string pool to reduce duplicate string allocations for frequently repeated tokens.
/// Use for small, lower-cased tokens.
/// </summary>
internal static class StringPool
{
    private static readonly ConcurrentDictionary<string, string> _pool = new(StringComparer.Ordinal);

    public static string Intern(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return _pool.GetOrAdd(value, static s => s);
    }
}
