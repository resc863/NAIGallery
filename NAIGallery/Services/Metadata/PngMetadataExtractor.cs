using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NAIGallery.Models;

namespace NAIGallery.Services.Metadata;

/// <summary>
/// Extracts NovelAI / Stable Diffusion style metadata embedded in PNG text chunks.
/// Logic moved from ImageIndexService for testability & single responsibility.
/// </summary>
internal sealed class PngMetadataExtractor : IMetadataExtractor
{
    public ImageMetadata? Extract(string file, string rootFolder)
    {
        try { if (!File.Exists(file)) return null; } catch { return null; }
        var collected = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var raw in PngTextChunkReader.ReadRawTextChunks(file))
                if (!string.IsNullOrWhiteSpace(raw)) collected.Add(raw.Trim());
        }
        catch { return null; }

        string? prompt = null, negative = null, basePrompt = null, baseNegative = null;
        List<CharacterPrompt>? characterPrompts = null; List<string?>? charNegatives = null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in collected)
        {
            if (e.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(e);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        ParseV4(root, ref basePrompt, ref baseNegative, ref characterPrompts, ref charNegatives);
                        if (root.TryGetProperty("prompt", out var pProp)) prompt ??= pProp.GetString();
                        if (root.TryGetProperty("negative_prompt", out var npProp)) negative ??= npProp.GetString();
                        if (root.TryGetProperty("uc", out var ucProp)) negative ??= ucProp.GetString();
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.NameEquals("prompt") || prop.NameEquals("negative_prompt") || prop.NameEquals("uc") ||
                                prop.NameEquals("base_prompt") || prop.NameEquals("negative_base_prompt") ||
                                prop.NameEquals("character_prompts") || prop.NameEquals("v4_prompt") || prop.NameEquals("v4_negative_prompt")) continue;
                            parameters[prop.Name] = prop.Value.ToString();
                        }
                        continue;
                    }
                }
                catch { }
            }
            if (e.Contains("Negative prompt:", StringComparison.OrdinalIgnoreCase))
            {
                var lines = e.Replace("\r", string.Empty).Split('\n');
                if (lines.Length > 0 && string.IsNullOrEmpty(prompt)) prompt = lines[0];
                foreach (var line in lines)
                {
                    if (line.StartsWith("Negative prompt:", StringComparison.OrdinalIgnoreCase))
                        negative = line["Negative prompt:".Length..].Trim();
                    if (line.Contains(':' ) && line.Contains(',') && line.IndexOf(':') < line.IndexOf(','))
                    {
                        foreach (var seg in line.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var kv = seg.Split(':', 2);
                            if (kv.Length == 2) parameters[kv[0].Trim()] = kv[1].Trim();
                        }
                    }
                }
            }
        }

        if (charNegatives != null && characterPrompts != null)
        {
            for (int i = 0; i < characterPrompts.Count && i < charNegatives.Count; i++)
            {
                var neg = CleanSegment(charNegatives[i]);
                if (!string.IsNullOrWhiteSpace(neg))
                {
                    if (string.IsNullOrWhiteSpace(characterPrompts[i].NegativePrompt)) characterPrompts[i].NegativePrompt = neg;
                    else characterPrompts[i].NegativePrompt += ", " + neg;
                }
            }
        }

        prompt = CleanSegment(prompt); negative = CleanSegment(negative);
        basePrompt = CleanSegment(basePrompt); baseNegative = CleanSegment(baseNegative);
        if (characterPrompts != null)
        {
            int idx = 1;
            foreach (var cp in characterPrompts)
            {
                cp.Prompt = CleanSegment(cp.Prompt);
                cp.NegativePrompt = CleanSegment(cp.NegativePrompt);
                if (string.IsNullOrWhiteSpace(cp.Name)) cp.Name = $"Character {idx}";
                idx++;
            }
        }

        var tags = BuildTags(basePrompt, characterPrompts, prompt);
        var fi = new FileInfo(file);
        return new ImageMetadata
        {
            FilePath = file,
            RelativePath = Path.GetRelativePath(rootFolder, file),
            Prompt = prompt,
            NegativePrompt = negative,
            BasePrompt = basePrompt,
            BaseNegativePrompt = baseNegative,
            CharacterPrompts = characterPrompts,
            Parameters = parameters.Count > 0 ? parameters : null,
            Tags = tags,
            LastWriteTimeTicks = fi.LastWriteTimeUtc.Ticks
        };
    }

    private static void ParseV4(JsonElement root, ref string? basePrompt, ref string? baseNegative,
        ref List<CharacterPrompt>? characterPrompts, ref List<string?>? charNegatives)
    {
        try
        {
            if (root.TryGetProperty("v4_prompt", out var v4p) && v4p.ValueKind == JsonValueKind.Object)
                ParseV4Prompt(v4p, ref basePrompt, ref characterPrompts, false, ref charNegatives);
            if (root.TryGetProperty("v4_negative_prompt", out var v4np) && v4np.ValueKind == JsonValueKind.Object)
                ParseV4Prompt(v4np, ref baseNegative, ref characterPrompts, true, ref charNegatives);
            if (root.TryGetProperty("base_prompt", out var bpProp)) basePrompt ??= bpProp.GetString();
            if (root.TryGetProperty("negative_base_prompt", out var nbpProp)) baseNegative ??= nbpProp.GetString();
            if (root.TryGetProperty("character_prompts", out var charsProp) && charsProp.ValueKind == JsonValueKind.Array)
            {
                characterPrompts ??= new();
                foreach (var c in charsProp.EnumerateArray())
                    characterPrompts.Add(new CharacterPrompt
                    {
                        Name = c.TryGetProperty("name", out var nProp) ? nProp.GetString() : null,
                        Prompt = c.TryGetProperty("prompt", out var cpProp) ? cpProp.GetString() : null,
                        NegativePrompt = c.TryGetProperty("negative_prompt", out var cnpProp) ? cnpProp.GetString() : null
                    });
            }
        }
        catch { }
    }

    private static void ParseV4Prompt(JsonElement v4Obj, ref string? baseCaption, ref List<CharacterPrompt>? characterPrompts, bool isNegative, ref List<string?>? negativeListRef)
    {
        try
        {
            if (v4Obj.TryGetProperty("caption", out var captionObj) && captionObj.ValueKind == JsonValueKind.Object)
            {
                if (captionObj.TryGetProperty("base_caption", out var baseCapProp)) baseCaption ??= baseCapProp.GetString();

                // In NovelAI v4 schema, char_captions typically lives under the "caption" object.
                // Support both locations for compatibility (under caption first, then fallback to v4Obj).
                JsonElement chars;
                bool hasChars = captionObj.TryGetProperty("char_captions", out chars) && chars.ValueKind == JsonValueKind.Array;
                if (!hasChars && v4Obj.TryGetProperty("char_captions", out var topChars) && topChars.ValueKind == JsonValueKind.Array)
                {
                    chars = topChars; hasChars = true;
                }

                if (hasChars)
                {
                    int i = 0;
                    foreach (var c in chars.EnumerateArray())
                    {
                        string? charCaption = c.TryGetProperty("char_caption", out var cc) ? cc.GetString() : null;
                        if (!isNegative)
                        {
                            characterPrompts ??= new();
                            if (characterPrompts.Count <= i) characterPrompts.Add(new CharacterPrompt { Prompt = charCaption });
                            else if (string.IsNullOrWhiteSpace(characterPrompts[i].Prompt)) characterPrompts[i].Prompt = charCaption;
                        }
                        else
                        {
                            negativeListRef ??= new();
                            while (negativeListRef.Count <= i) negativeListRef.Add(null);
                            negativeListRef[i] = charCaption;
                        }
                        i++;
                    }
                }
            }
        }
        catch { }
    }

    private static string? CleanSegment(string? s)
    { if (string.IsNullOrWhiteSpace(s)) return s; s = s.Trim(); while (s.EndsWith(',')) s = s[..^1].TrimEnd(); return s; }

    private static List<string> BuildTags(string? basePrompt, List<CharacterPrompt>? characters, string? fallback)
    {
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(basePrompt)) tags.Add(basePrompt);
        if (characters != null)
            foreach (var cp in characters)
                if (!string.IsNullOrWhiteSpace(cp.Prompt)) tags.Add(cp.Prompt!);
        if (tags.Count == 0 && !string.IsNullOrWhiteSpace(fallback)) tags.Add(fallback!);
        if (tags.Count > 0)
        {
            tags = string.Join(',', tags)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return tags;
    }
}
