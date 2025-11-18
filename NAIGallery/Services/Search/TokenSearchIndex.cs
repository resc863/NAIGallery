using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections.Concurrent;
using NAIGallery; // StringPool, AppDefaults
using NAIGallery.Models;

namespace NAIGallery.Services;

/// <summary>
/// 간단한 in-memory 토큰 색인입니다. ConcurrentDictionary 기반으로 lock-free에 가깝게 동작.
/// </summary>
internal sealed class TokenSearchIndex : ITokenSearchIndex
{
    // token -> set(ImageMetadata)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ImageMetadata, byte>> _index = new(StringComparer.Ordinal);
    // meta -> tokens[] (마지막으로 색인된 토큰 스냅샷)
    private readonly ConcurrentDictionary<ImageMetadata, string[]> _metaTokens = new();

    public void Index(ImageMetadata meta)
    {
        if (meta == null) return;
        var tokens = Tokenize(meta);

        // 이전 토큰 제거
        if (_metaTokens.TryGetValue(meta, out var oldTokens))
        {
            foreach (var t in oldTokens)
            {
                if (_index.TryGetValue(t, out var set))
                {
                    set.TryRemove(meta, out _);
                    if (set.IsEmpty)
                        _index.TryRemove(t, out _);
                }
            }
        }

        _metaTokens[meta] = tokens;
        foreach (var t in tokens)
        {
            var set = _index.GetOrAdd(t, _ => new ConcurrentDictionary<ImageMetadata, byte>());
            set[meta] = 0;
        }
    }

    public void Remove(ImageMetadata meta)
    {
        if (meta == null) return;
        if (_metaTokens.TryRemove(meta, out var tokens))
        {
            foreach (var t in tokens)
            {
                if (_index.TryGetValue(t, out var set))
                {
                    set.TryRemove(meta, out _);
                    if (set.IsEmpty)
                        _index.TryRemove(t, out _);
                }
            }
        }
    }

    public IReadOnlyCollection<ImageMetadata> QueryTokens(IEnumerable<string> tokens)
    {
        var result = new HashSet<ImageMetadata>();
        foreach (var t in tokens)
        {
            if (_index.TryGetValue(t, out var set))
            {
                foreach (var m in set.Keys) result.Add(m);
            }
        }
        return result;
    }

    public IEnumerable<string> Suggest(string prefix, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return Enumerable.Empty<string>();
        prefix = prefix.ToLowerInvariant();
        int cap = Math.Min(limit, AppDefaults.SuggestionLimit);
        return _index.Keys
                     .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                     .Take(cap)
                     .ToArray();
    }

    private static string[] Tokenize(ImageMetadata m)
    {
        var hs = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var tok in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (tok.Length < AppDefaults.TokenMinLen || tok.Length > AppDefaults.TokenMaxLen) continue;
                var low = tok.ToLowerInvariant();
                hs.Add(StringPool.Intern(low));
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
