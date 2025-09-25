using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media; // ImageSource

namespace NAIGallery.Models;

/// <summary>
/// Represents a single image entry in the gallery, including prompts, tags, parameters,
/// filesystem metadata and a UI-bound thumbnail with progressive upgrade support.
/// </summary>
public class ImageMetadata : INotifyPropertyChanged
{
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

    /// <summary>UTC last write time ticks used for date sorting.</summary>
    public long? LastWriteTimeTicks { get; set; }

    private ImageSource? _thumbnail;

    /// <summary>
    /// UI-bound thumbnail image (progressively upgraded). Not serialized.
    /// </summary>
    [JsonIgnore]
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { if (_thumbnail != value) { _thumbnail = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Effective pixel width of the current <see cref="Thumbnail"/> for progressive upgrades.
    /// </summary>
    [JsonIgnore]
    public int? ThumbnailPixelWidth { get; set; }

    // New: aspect ratio (width/height). Default 1.0 to avoid razor-thin placeholders before decode completes.
    private double _aspectRatio = 1.0;
    [JsonIgnore]
    public double AspectRatio
    {
        get => _aspectRatio;
        set { if (_aspectRatio != value) { _aspectRatio = value <= 0 ? 1.0 : value; OnPropertyChanged(); } }
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    private void OnPropertyChanged([CallerMemberName] string? name=null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
