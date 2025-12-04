using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using NAIGallery;
using NAIGallery.Models;

namespace NAIGallery.Services;

/// <summary>
/// Lock-free in-memory token search index using ConcurrentDictionary.
/// </summary>
internal sealed class TokenSearchIndex : ITokenSearchIndex
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ImageMetadata, byte>> _index = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ImageMetadata, string[]> _metaTokens = new();

    private static readonly char[] Separators = [',', ';', ' '];

    public void Index(ImageMetadata meta)
    {
        if (meta is null) return;
        
        var tokens = Tokenize(meta);

        // Remove old tokens
        if (_metaTokens.TryGetValue(meta, out var oldTokens))
        {
            RemoveTokens(meta, oldTokens);
        }

        // Add new tokens
        _metaTokens[meta] = tokens;
        foreach (var token in tokens)
        {
            var set = _index.GetOrAdd(token, _ => new ConcurrentDictionary<ImageMetadata, byte>());
            set[meta] = 0;
        }
    }

    public void Remove(ImageMetadata meta)
    {
        if (meta is null) return;
        
        if (_metaTokens.TryRemove(meta, out var tokens))
        {
            RemoveTokens(meta, tokens);
        }
    }

    private void RemoveTokens(ImageMetadata meta, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (_index.TryGetValue(token, out var set))
            {
                set.TryRemove(meta, out _);
                if (set.IsEmpty)
                    _index.TryRemove(token, out _);
            }
        }
    }

    public IReadOnlyCollection<ImageMetadata> QueryTokens(IEnumerable<string> tokens)
    {
        var result = new HashSet<ImageMetadata>();
        foreach (var token in tokens)
        {
            if (_index.TryGetValue(token, out var set))
            {
                foreach (var meta in set.Keys) 
                    result.Add(meta);
            }
        }
        return result;
    }

    public IEnumerable<string> Suggest(string prefix, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(prefix)) 
            return [];
        
        var lowerPrefix = prefix.ToLowerInvariant();
        var cap = Math.Min(limit, AppDefaults.SuggestionLimit);
        
        return _index.Keys
            .Where(k => k.StartsWith(lowerPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Take(cap)
            .ToArray();
    }

    private static string[] Tokenize(ImageMetadata meta)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        
        AddTokens(tokens, meta.Tags);
        AddToken(tokens, meta.Prompt);
        AddToken(tokens, meta.NegativePrompt);
        AddToken(tokens, meta.BasePrompt);
        AddToken(tokens, meta.BaseNegativePrompt);
        
        if (meta.CharacterPrompts is not null)
        {
            foreach (var cp in meta.CharacterPrompts)
            {
                AddToken(tokens, cp.Prompt);
                AddToken(tokens, cp.NegativePrompt);
            }
        }
        
        return [.. tokens];
    }

    private static void AddTokens(HashSet<string> tokens, IEnumerable<string>? texts)
    {
        if (texts is null) return;
        foreach (var text in texts)
            AddToken(tokens, text);
    }

    private static void AddToken(HashSet<string> tokens, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        
        foreach (var segment in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.Length < AppDefaults.TokenMinLen || segment.Length > AppDefaults.TokenMaxLen) 
                continue;
            
            tokens.Add(StringPool.Intern(segment.ToLowerInvariant()));
        }
    }
}
