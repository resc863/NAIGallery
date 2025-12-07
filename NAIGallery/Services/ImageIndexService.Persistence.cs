using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAIGallery.Models;

namespace NAIGallery.Services;

public partial class ImageIndexService
{
    #region Persistence
    
    private async Task LoadIndexAsync(string folder)
    {
        var path = Path.Combine(folder, IndexFileName);
        if (!File.Exists(path)) return;
        
        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<ImageMetadata>>(json) ?? new();
            int loaded = 0, withDimensions = 0;
            
            foreach (var meta in list)
            {
                NormalizeFilePath(meta, folder);
                meta.Tags ??= new();
                meta.SearchText = SearchTextBuilder.BuildSearchText(meta);
                meta.TokenSet = SearchTextBuilder.BuildFrozenTokenSet(meta);
                
                if (meta.LastWriteTimeTicks == null)
                {
                    try { meta.LastWriteTimeTicks = new FileInfo(meta.FilePath).LastWriteTimeUtc.Ticks; } catch { }
                }
                
                if (meta.OriginalWidth.HasValue && meta.OriginalHeight.HasValue)
                    withDimensions++;
                
                if (!string.IsNullOrWhiteSpace(meta.FilePath))
                {
                    _index[meta.FilePath] = meta;
                    foreach (var t in meta.Tags) _tagSet.Add(t);
                    _searchIndex.Index(meta);
                    foreach (var t in meta.Tags) _tagTrie.Add(t);
                    loaded++;
                }
            }
            
            InvalidateSorted();
            _logger?.LogInformation("Loaded {Loaded} images from index, {WithDimensions} have original dimensions", loaded, withDimensions);
        }
        catch { }
    }

    private static void NormalizeFilePath(ImageMetadata meta, string folder)
    {
        if (string.IsNullOrWhiteSpace(meta.FilePath) && meta.RelativePath != null)
            meta.FilePath = Path.GetFullPath(Path.Combine(folder, meta.RelativePath));
        else if (meta.FilePath != null && !Path.IsPathRooted(meta.FilePath))
            meta.FilePath = Path.GetFullPath(Path.Combine(folder, meta.FilePath));
    }

    private async Task SaveIndexAsync(string folder)
    {
        var path = Path.Combine(folder, IndexFileName);
        var temp = path + ".tmp";
        
        try
        {
            var list = _index.Values
                .Where(m => m.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                .Select(m => new ImageMetadata
                {
                    FilePath = m.FilePath,
                    RelativePath = Path.GetRelativePath(folder, m.FilePath),
                    Prompt = m.Prompt,
                    NegativePrompt = m.NegativePrompt,
                    BasePrompt = m.BasePrompt,
                    BaseNegativePrompt = m.BaseNegativePrompt,
                    CharacterPrompts = m.CharacterPrompts,
                    Parameters = m.Parameters,
                    Tags = m.Tags,
                    LastWriteTimeTicks = m.LastWriteTimeTicks,
                    OriginalWidth = m.OriginalWidth,
                    OriginalHeight = m.OriginalHeight
                }).ToList();
                
            var json = System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
            File.Copy(temp, path, true);
            File.Delete(temp);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
    
    #endregion
}
