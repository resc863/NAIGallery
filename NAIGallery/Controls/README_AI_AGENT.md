# Folder: Controls

## Purpose
- Reusable light-weight XAML controls.

## Files

### AspectPresenter.cs
A lightweight `ContentControl` that maintains a fixed aspect ratio for its content to reduce layout shifts while thumbnails progressively upgrade.

**Dependency Property:**
- `AspectRatio`: Width/Height ratio (default 1.0)

**Behavior:**
- If `Height` is set ¡æ computes `Width = Height * AspectRatio`
- If `Width` is set ¡æ computes `Height = Width / AspectRatio`
- If both set ¡æ uses both directly
- If neither set ¡æ uses available size heuristics

**Usage:**
```xml
<controls:AspectPresenter 
    Height="{x:Bind TileLineHeight}" 
    AspectRatio="{x:Bind meta.AspectRatio, Mode=OneWay}">
    <Image Source="{x:Bind meta.Thumbnail, Mode=OneWay}" />
</controls:AspectPresenter>
```

## Notes
- Bind `AspectRatio` to `ImageMetadata.AspectRatio` and constrain either width or height.
- Invalidates measure when `AspectRatio` changes.
- Arranges child with final size to avoid layout cycles.