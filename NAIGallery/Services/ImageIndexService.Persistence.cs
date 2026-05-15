using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
            var list = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonContext.Default.ListImageMetadata) ?? new();
            int loaded = 0, withDimensions = 0;
            
            foreach (var meta in list)
            {
                if (meta.LastWriteTimeTicks == null)
                {
                    try { meta.LastWriteTimeTicks = new FileInfo(meta.FilePath).LastWriteTimeUtc.Ticks; } catch { }
                }
                
                if (meta.OriginalWidth.HasValue && meta.OriginalHeight.HasValue)
                    withDimensions++;
                
                if (!string.IsNullOrWhiteSpace(meta.FilePath))
                {
                    UpsertMetadata(meta, folder, notify: false);
                    loaded++;
                }
            }
            
            InvalidateSorted();
            AppLog.Info($"Loaded {loaded} images from index, {withDimensions} have original dimensions");
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Failed to load image index from {path}", ex);
        }
    }

    private static void NormalizeFilePath(ImageMetadata meta, string folder)
    {
        if (string.IsNullOrWhiteSpace(meta.FilePath) && meta.RelativePath != null)
            meta.FilePath = Path.GetFullPath(Path.Combine(folder, meta.RelativePath));
        else if (meta.FilePath != null && !Path.IsPathRooted(meta.FilePath))
            meta.FilePath = Path.GetFullPath(Path.Combine(folder, meta.FilePath));

        if (!string.IsNullOrWhiteSpace(meta.FilePath))
            meta.RelativePath = Path.GetRelativePath(folder, meta.FilePath);
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
                    FilePath = Path.GetRelativePath(folder, m.FilePath),
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
                
            var json = System.Text.Json.JsonSerializer.Serialize(list, AppJsonContext.Default.ListImageMetadata);
            await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
            File.Copy(temp, path, true);
            File.Delete(temp);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            AppLog.Warning($"Failed to save image index to {path}");
        }
    }
    
    #endregion
}
