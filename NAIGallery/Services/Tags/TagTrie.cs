using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NAIGallery.Services;

/// <summary>
/// Simple case-insensitive prefix trie for tag suggestion.
/// Stores full tag strings at terminal nodes (distinct, original casing preserved).
/// Thread-safe for concurrent Add + prefix queries via coarse lock (expected low contention).
/// </summary>
internal sealed class TagTrie
{
    private sealed class Node
    {
        public readonly Dictionary<char, Node> Children = new();
        public readonly List<string> Terminal = new(); // store full tag values ending here
    }

    private readonly Node _root = new();
    private readonly IEqualityComparer<string> _tagComparer = StringComparer.OrdinalIgnoreCase;
    private readonly object _lock = new();
    private int _count; // approximate number of unique tags

    public int Count => Volatile.Read(ref _count);

    public void Add(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        var span = tag.AsSpan();
        lock (_lock)
        {
            var node = _root;
            for (int i = 0; i < span.Length; i++)
            {
                char c = char.ToLowerInvariant(span[i]);
                if (!node.Children.TryGetValue(c, out var next))
                {
                    next = new Node();
                    node.Children[c] = next;
                }
                node = next;
            }
            // avoid duplicates (case-insensitive)
            if (!node.Terminal.Any(t => _tagComparer.Equals(t, tag)))
            {
                node.Terminal.Add(tag);
                Interlocked.Increment(ref _count);
            }
        }
    }

    public IEnumerable<string> Suggest(string prefix, int limit)
    {
        if (string.IsNullOrWhiteSpace(prefix) || limit <= 0) return Enumerable.Empty<string>();
        prefix = prefix.Trim();
        List<string> results = new(limit);
        lock (_lock)
        {
            var node = _root;
            foreach (var ch in prefix)
            {
                var c = char.ToLowerInvariant(ch);
                if (!node.Children.TryGetValue(c, out node)) return Array.Empty<string>();
            }
            // DFS iterative to avoid deep recursion
            var stack = new Stack<Node>();
            stack.Push(node);
            while (stack.Count > 0 && results.Count < limit)
            {
                var cur = stack.Pop();
                if (cur.Terminal.Count > 0)
                {
                    foreach (var t in cur.Terminal)
                    {
                        results.Add(t);
                        if (results.Count >= limit) break;
                    }
                }
                // push children in lexical order for stable ordering
                foreach (var kv in cur.Children.OrderByDescending(k => k.Key))
                    stack.Push(kv.Value);
            }
        }
        return results;
    }
}
