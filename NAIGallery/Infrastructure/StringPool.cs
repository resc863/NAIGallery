using System;
using System.Collections.Concurrent;
using System.Threading;

namespace NAIGallery;

/// <summary>
/// Simple string pool to reduce duplicate string allocations for frequently repeated tokens.
/// Use for small, lower-cased tokens. Implements a capacity limit to prevent memory leaks.
/// </summary>
internal static class StringPool
{
    private const int MaxCapacity = 50_000;
    private static readonly ConcurrentDictionary<string, string> _pool = new(StringComparer.Ordinal);
    private static int _count;

    public static string Intern(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        
        if (_pool.TryGetValue(value, out var existing))
            return existing;
        
        // Capacity check - if exceeded, skip pooling to prevent unbounded growth
        if (Volatile.Read(ref _count) >= MaxCapacity)
            return value;
        
        var result = _pool.GetOrAdd(value, static s => s);
        
        // Only increment if we actually added a new entry
        if (ReferenceEquals(result, value))
            Interlocked.Increment(ref _count);
        
        return result;
    }
    
    /// <summary>
    /// Clears the string pool. Should be called during major cleanup operations.
    /// </summary>
    public static void Clear()
    {
        _pool.Clear();
        Volatile.Write(ref _count, 0);
    }
    
    /// <summary>
    /// Gets the current number of interned strings.
    /// </summary>
    public static int Count => Volatile.Read(ref _count);
}
