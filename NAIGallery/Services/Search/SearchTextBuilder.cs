using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Text;
using NAIGallery.Models;

namespace NAIGallery.Services;

/// <summary>
/// Helper methods for building search text and token sets from ImageMetadata.
/// </summary>
internal static class SearchTextBuilder
{
    private static readonly char[] Separators = [',', ';', ' '];

    /// <summary>
    /// Builds a lowercase search text from all searchable fields of the metadata.
    /// </summary>
    public static string BuildSearchText(ImageMetadata m)
    {
        var sb = new StringBuilder();
        
        foreach (var t in m.Tags) 
            sb.Append(t).Append(' ');
        
        AppendIfNotEmpty(sb, m.Prompt);
        AppendIfNotEmpty(sb, m.NegativePrompt);
        AppendIfNotEmpty(sb, m.BasePrompt);
        AppendIfNotEmpty(sb, m.BaseNegativePrompt);
        
        if (m.CharacterPrompts != null)
        {
            foreach (var cp in m.CharacterPrompts)
            {
                AppendIfNotEmpty(sb, cp.Prompt);
                AppendIfNotEmpty(sb, cp.NegativePrompt);
            }
        }
        
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Builds an immutable frozen set of lowercase tokens for fast search matching.
    /// </summary>
    public static IReadOnlySet<string> BuildFrozenTokenSet(ImageMetadata m)
    {
        var hs = new HashSet<string>(StringComparer.Ordinal);
        
        foreach (var t in m.Tags) 
            AddTokens(hs, t);
        
        AddTokens(hs, m.Prompt);
        AddTokens(hs, m.NegativePrompt);
        AddTokens(hs, m.BasePrompt);
        AddTokens(hs, m.BaseNegativePrompt);
        
        if (m.CharacterPrompts != null)
        {
            foreach (var cp in m.CharacterPrompts)
            {
                AddTokens(hs, cp.Prompt);
                AddTokens(hs, cp.NegativePrompt);
            }
        }
        
        return FrozenSet.ToFrozenSet(hs, StringComparer.Ordinal);
    }

    private static void AppendIfNotEmpty(StringBuilder sb, string? text)
    {
        if (!string.IsNullOrEmpty(text))
            sb.Append(text).Append(' ');
    }

    private static void AddTokens(HashSet<string> hs, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        
        foreach (var tok in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (tok.Length <= 1 || tok.Length > 64) continue;
            hs.Add(tok.ToLowerInvariant());
        }
    }
}
