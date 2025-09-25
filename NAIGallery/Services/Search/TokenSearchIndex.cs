using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NAIGallery.Models;

namespace NAIGallery.Services;

/// <summary>
/// 간단한 in-memory 토큰 역인덱스 구현. Thread-safe (lock 기반).
/// </summary>
internal sealed class TokenSearchIndex : ITokenSearchIndex
{
    private readonly Dictionary<string, HashSet<ImageMetadata>> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<ImageMetadata, string[]> _metaTokens = new();
    private readonly object _lock = new();

    public void Index(ImageMetadata meta)
    {
        if (meta == null) return;
        var tokens = Tokenize(meta);
        lock (_lock)
        {
            // 기존 토큰 제거
            if (_metaTokens.TryGetValue(meta, out var oldTokens))
            {
                foreach (var t in oldTokens)
                {
                    if (_index.TryGetValue(t, out var set) && set.Remove(meta) && set.Count == 0)
                        _index.Remove(t);
                }
            }
            _metaTokens[meta] = tokens;
            foreach (var t in tokens)
            {
                if (!_index.TryGetValue(t, out var set))
                    _index[t] = set = new();
                set.Add(meta);
            }
        }
    }

    public void Remove(ImageMetadata meta)
    {
        if (meta == null) return;
        lock (_lock)
        {
            if (_metaTokens.TryGetValue(meta, out var tokens))
            {
                foreach (var t in tokens)
                {
                    if (_index.TryGetValue(t, out var set) && set.Remove(meta) && set.Count == 0)
                        _index.Remove(t);
                }
                _metaTokens.Remove(meta);
            }
        }
    }

    public IReadOnlyCollection<ImageMetadata> QueryTokens(IEnumerable<string> tokens)
    {
        var result = new HashSet<ImageMetadata>();
        lock (_lock)
        {
            foreach (var t in tokens)
            {
                if (_index.TryGetValue(t, out var set))
                {
                    foreach (var m in set) result.Add(m);
                }
            }
        }
        return result;
    }

    public IEnumerable<string> Suggest(string prefix, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return Enumerable.Empty<string>();
        prefix = prefix.ToLowerInvariant();
        List<string> list;
        lock (_lock)
        {
            list = _index.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                               .Take(limit)
                               .ToList();
        }
        return list;
    }

    private static string[] Tokenize(ImageMetadata m)
    {
        var hs = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var tok in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (tok.Length <= 1 || tok.Length > 64) continue;
                hs.Add(tok.ToLowerInvariant());
            }
        }
        foreach (var t in m.Tags) Add(t);
        Add(m.Prompt); Add(m.NegativePrompt); Add(m.BasePrompt); Add(m.BaseNegativePrompt);
        if (m.CharacterPrompts != null)
        {
            foreach (var cp in m.CharacterPrompts)
            {
                Add(cp.Prompt); Add(cp.NegativePrompt);
            }
        }
        return hs.ToArray();
    }
}
