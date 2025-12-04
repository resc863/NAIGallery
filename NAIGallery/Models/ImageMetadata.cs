using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace NAIGallery.Models;

/// <summary>
/// Represents a single image entry in the gallery, including prompts, tags, parameters,
/// filesystem metadata and a UI-bound thumbnail with progressive upgrade support.
/// </summary>
public class ImageMetadata : INotifyPropertyChanged
{
    private const double DefaultAspectRatio = 1.0;
    private const double MinAspectRatio = 0.1;
    private const double MaxAspectRatio = 10.0;

    /// <summary>Absolute file path to the image on disk.</summary>
    public required string FilePath { get; set; }

    /// <summary>Path relative to the chosen root folder (not persisted to UI).</summary>
    [JsonIgnore]
    public string? RelativePath { get; set; }

    /// <summary>Legacy/combined prompt text (if available).</summary>
    public string? Prompt { get; set; }

    /// <summary>Legacy/combined negative prompt text (if available).</summary>
    public string? NegativePrompt { get; set; }

    // NovelAI v4 extended prompts
    /// <summary>Structured base prompt (NAI v4).</summary>
    public string? BasePrompt { get; set; }

    /// <summary>Structured base negative prompt (NAI v4).</summary>
    public string? BaseNegativePrompt { get; set; }

    /// <summary>Per-character structured prompts (NAI v4).</summary>
    public List<CharacterPrompt>? CharacterPrompts { get; set; }

    /// <summary>Misc generation parameters (sampler, steps, etc.).</summary>
    public Dictionary<string,string>? Parameters { get; set; }

    /// <summary>Tokenized tags derived from structured/legacy prompts for search.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Lower-cased, cached search payload built from tags and prompts.</summary>
    [JsonIgnore]
    public string? SearchText { get; set; }

    /// <summary>Precomputed tokens for fast search matching (lower-cased). Not serialized.</summary>
    [JsonIgnore]
    public IReadOnlySet<string>? TokenSet { get; set; }

    /// <summary>UTC last write time ticks used for date sorting.</summary>
    public long? LastWriteTimeTicks { get; set; }

    /// <summary>Original image width in pixels (persisted for aspect ratio calculation).</summary>
    public int? OriginalWidth { get; set; }

    /// <summary>Original image height in pixels (persisted for aspect ratio calculation).</summary>
    public int? OriginalHeight { get; set; }

    private ImageSource? _thumbnail;

    /// <summary>
    /// UI-bound thumbnail image (progressively upgraded). Not serialized.
    /// </summary>
    [JsonIgnore]
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    private int? _thumbnailPixelWidth;
    
    /// <summary>
    /// Effective pixel width of the current <see cref="Thumbnail"/> for progressive upgrades.
    /// </summary>
    [JsonIgnore]
    public int? ThumbnailPixelWidth 
    { 
        get => _thumbnailPixelWidth;
        set => SetProperty(ref _thumbnailPixelWidth, value);
    }

    private bool _isLoadingThumbnail;
    
    /// <summary>
    /// Indicates whether this item's thumbnail is currently being loaded/decoded.
    /// Used for UI to show loading indicator only for items actually in the pipeline.
    /// </summary>
    [JsonIgnore]
    public bool IsLoadingThumbnail
    {
        get => _isLoadingThumbnail;
        set => SetProperty(ref _isLoadingThumbnail, value);
    }

    // New: aspect ratio (width/height). Default 1.0 to avoid razor-thin placeholders before decode completes.
    private double _aspectRatio = DefaultAspectRatio;
    
    /// <summary>
    /// Aspect ratio computed from OriginalWidth/OriginalHeight or cached value.
    /// Getter computes from original dimensions if available.
    /// </summary>
    [JsonIgnore]
    public double AspectRatio
    {
        get 
        {
            // Always compute from OriginalWidth/OriginalHeight if available
            if (OriginalWidth.HasValue && OriginalHeight.HasValue && OriginalHeight.Value > 0)
                return (double)OriginalWidth.Value / OriginalHeight.Value;
            
            return _aspectRatio <= 0 ? DefaultAspectRatio : _aspectRatio;
        }
        set 
        { 
            var clampedValue = Math.Clamp(value, MinAspectRatio, MaxAspectRatio);
            if (clampedValue <= 0) clampedValue = DefaultAspectRatio;
            
            if (Math.Abs(_aspectRatio - clampedValue) > 0.001)
            {
                _aspectRatio = clampedValue;
                OnPropertyChanged();
            } 
        }
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>
/// Describes a single character prompt (and optional negative) in NAI v4 metadata.
/// </summary>
public class CharacterPrompt
{
    /// <summary>Optional display name.</summary>
    public string? Name { get; set; }

    /// <summary>Prompt text for the character.</summary>
    public string? Prompt { get; set; }

    /// <summary>Negative prompt text for the character.</summary>
    public string? NegativePrompt { get; set; }
}

public class ParamEntry
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
