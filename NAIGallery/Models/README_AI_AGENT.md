# Folder: Models

## Purpose
- Data contracts used across services, view-models, and views. Objects are UI-bindable and serializable where appropriate.

## Key types

### ImageMetadata
Represents a single image entry with prompts, parameters, tokenized `Tags`, last-write timestamp, and UI-bound thumbnail.

**Persisted properties:**
- `FilePath`: Absolute path to the image
- `Prompt`, `NegativePrompt`: Legacy/combined prompt text
- `BasePrompt`, `BaseNegativePrompt`: NAI v4 structured prompts
- `CharacterPrompts`: Per-character prompts (NAI v4)
- `Parameters`: Dictionary of generation parameters
- `Tags`: Tokenized tags for search
- `LastWriteTimeTicks`: UTC timestamp for sorting
- `OriginalWidth`, `OriginalHeight`: Original image dimensions

**UI-only properties (`[JsonIgnore]`):**
- `Thumbnail` (ImageSource): Progressive thumbnail
- `ThumbnailPixelWidth`: Current thumbnail resolution
- `IsLoadingThumbnail`: Loading indicator flag
- `AspectRatio`: Computed from dimensions or cached value
- `SearchText`: Cached lowercase search payload
- `TokenSet`: Frozen set of lowercase tokens for fast matching
- `RelativePath`: Path relative to root folder

**Key behaviors:**
- Implements `INotifyPropertyChanged` for XAML binding
- `AspectRatio` computes from `OriginalWidth/OriginalHeight` if available, with clamping (0.1-10.0)

### CharacterPrompt
Name/prompt/negative tuple for NAI v4 character metadata.
- `Name`: Optional display name
- `Prompt`: Character prompt text
- `NegativePrompt`: Character negative prompt text

### ParamEntry
Simple key-value pair for parameter display.
- `Key`: Parameter name
- `Value`: Parameter value

## Guidance
- Add only serializable fields to be saved to on-disk index; mark UI-only fields with `[JsonIgnore]`.
- Any new fields should be considered in `SearchTextBuilder` and tokenization for search.
- Property changes must call `INotifyPropertyChanged` to keep XAML in sync.
- Use the `SetProperty<T>` helper method for property change notifications.