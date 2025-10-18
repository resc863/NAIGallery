Folder: Models

Purpose
- Data contracts used across services, view-models, and views. Objects are UI-bindable and serializable where appropriate.

Key types
- `ImageMetadata`
  - Represents a single image entry with prompts, parameters, tokenized `Tags`, last-write timestamp, and UI-bound thumbnail.
  - UI properties: `Thumbnail` (ImageSource), `ThumbnailPixelWidth`, `AspectRatio` implement progressive image upgrades without layout jumps.
  - `SearchText` is a cached lower-cased payload for fast substring scoring.
- `CharacterPrompt`
  - Name/prompt/negative tuple for NAI v4 character metadata.

Guidance
- Add only serializable fields to be saved to on-disk index; mark UI-only fields with `[JsonIgnore]`.
- Any new fields should be considered in `ImageIndexService.BuildSearchText` and tokenization for search.
- Property changes must call `INotifyPropertyChanged` to keep XAML in sync.