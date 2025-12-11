# Folder: Converters

## Purpose
- Small XAML converters used by bindings.

## Files

### VisibilityConverters.cs

Contains a base class and two visibility converters:

- **`VisibilityConverterBase`**: Abstract base class providing common invert logic via `parameter="Invert"`.

- **`BoolToVisibilityConverter`**: Converts boolean values to Visibility states.
  - Default: `true` ¡æ `Visible`, `false` ¡æ `Collapsed`
  - With parameter `"Invert"`: `true` ¡æ `Collapsed`, `false` ¡æ `Visible`

- **`NullToVisibilityConverter`**: Converts null values to Visibility states.
  - Default: `null` ¡æ `Visible`, non-null ¡æ `Collapsed` (useful for loading labels)
  - With parameter `"Invert"`: `null` ¡æ `Collapsed`, non-null ¡æ `Visible`

## Usage in XAML
```xml
<!-- Show loading label when thumbnail is null -->
<TextBlock Visibility="{x:Bind meta.Thumbnail, Converter={StaticResource NullToVisibility}}" />

<!-- Show content when thumbnail is loaded (inverted) -->
<Image Visibility="{x:Bind meta.Thumbnail, Converter={StaticResource NullToVisibility}, ConverterParameter=Invert}" />

<!-- Show element when IsLoading is true -->
<ProgressRing Visibility="{x:Bind IsLoading, Converter={StaticResource BoolToVisibility}}" />

<!-- Hide element when IsLoading is true (inverted) -->
<ContentControl Visibility="{x:Bind IsLoading, Converter={StaticResource BoolToVisibility}, ConverterParameter=Invert}" />
```

## Notes
- Keep converters stateless and fast; they run on the UI thread.
- All converters throw `NotSupportedException` for `ConvertBack` as they are one-way only.