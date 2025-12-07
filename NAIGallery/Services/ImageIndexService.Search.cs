using System;
using System.Collections.Generic;
using System.Linq;
using NAIGallery.Models;

namespace NAIGallery.Services;

public partial class ImageIndexService
{
    #region Search
    
    public IEnumerable<ImageMetadata> SearchByTag(string query)
        => Search(query, andMode: false, partialMode: true);

    public IEnumerable<ImageMetadata> Search(string query, bool andMode, bool partialMode)
    {
        if (string.IsNullOrWhiteSpace(query)) return All;
        
        var tokens = query
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToArray();
            
        if (tokens.Length == 0) return All;

        var candidateSet = GatherCandidates(tokens, partialMode);
        if (candidateSet.Count == 0) return Array.Empty<ImageMetadata>();

        return ScoreAndFilterResults(candidateSet, tokens, andMode, partialMode);
    }

    private HashSet<ImageMetadata> GatherCandidates(string[] tokens, bool partialMode)
    {
        var exactCandidates = _searchIndex.QueryTokens(tokens);
        var candidateSet = new HashSet<ImageMetadata>(exactCandidates);

        if (partialMode)
        {
            foreach (var m in _index.Values)
            {
                var set = m.TokenSet ??= SearchTextBuilder.BuildFrozenTokenSet(m);
                foreach (var tok in tokens)
                {
                    if (set.Any(t => t.Contains(tok, StringComparison.Ordinal)))
                    {
                        candidateSet.Add(m);
                        break;
                    }
                }
            }
        }

        return candidateSet;
    }

    private IEnumerable<ImageMetadata> ScoreAndFilterResults(HashSet<ImageMetadata> candidateSet, string[] tokens, bool andMode, bool partialMode)
    {
        var results = new List<(ImageMetadata m, bool any, int tagHits, int score, int tokenHits)>();
        
        foreach (var m in candidateSet)
        {
            var hay = m.SearchText ??= SearchTextBuilder.BuildSearchText(m);
            var set = m.TokenSet ??= SearchTextBuilder.BuildFrozenTokenSet(m);
            
            int score = 0, tagHits = 0, tokenHits = 0;
            bool any = false, allMatch = true;
            
            foreach (var tok in tokens)
            {
                bool matched = false;
                
                if (set.Contains(tok))
                {
                    matched = true;
                    score += Math.Max(1, tok.Length);
                }
                else if (partialMode && set.Any(t => t.Contains(tok, StringComparison.Ordinal)))
                {
                    matched = true;
                    score += Math.Max(1, tok.Length / 2);
                }
                else if (!partialMode)
                {
                    int idx = hay.IndexOf(tok, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        matched = true;
                        int occ = 0;
                        while (idx >= 0)
                        {
                            occ++;
                            idx = hay.IndexOf(tok, idx + tok.Length, StringComparison.Ordinal);
                        }
                        score += occ * Math.Max(1, tok.Length);
                    }
                }
                
                if (matched)
                {
                    any = true;
                    tokenHits++;
                    if (m.Tags.Any(t => t.Contains(tok, StringComparison.OrdinalIgnoreCase))) 
                        tagHits++;
                }
                else if (andMode)
                {
                    allMatch = false;
                }
            }
            
            if (!any) continue;
            if (andMode && !allMatch) continue;
            
            results.Add((m, any, tagHits, score, tokenHits));
        }

        return results
            .OrderByDescending(r => r.tagHits)
            .ThenByDescending(r => r.tokenHits)
            .ThenByDescending(r => r.score)
            .Select(r => r.m);
    }

    public IEnumerable<string> SuggestTags(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return Enumerable.Empty<string>();
        
        var trie = _tagTrie.Suggest(prefix, AppDefaults.SuggestionLimit);
        if (!trie.Any())
        {
            lock (_tagLock)
            {
                return _tagSet
                    .Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t)
                    .Take(AppDefaults.SuggestionLimit)
                    .ToList();
            }
        }
        
        var merged = new HashSet<string>(trie, StringComparer.OrdinalIgnoreCase);
        lock (_tagLock)
        {
            foreach (var t in _tagSet)
            {
                if (merged.Count >= AppDefaults.SuggestionLimit) break;
                if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) 
                    merged.Add(t);
            }
        }
        
        return merged.OrderBy(t => t).Take(AppDefaults.SuggestionLimit).ToList();
    }
    
    #endregion
}
