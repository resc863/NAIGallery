Folder: Services/Metadata

Purpose
- Extract structured metadata (prompts, parameters, tags) from image files.

Files
- `IMetadataExtractor`: interface for pluggable extractors.
- `PngMetadataExtractor`: parses PNG text chunks (NovelAI/Stable Diffusion style), builds `ImageMetadata` with tags.
- `CompositeMetadataExtractor`: tries multiple extractors in order.
- `PngTextChunkReader` (in parent Services): low-level reader for PNG text chunks used by PNG extractor.

Extension points
- Implement `IMetadataExtractor` for JPEG/WebP/etc and add to `App` composite registration.

Testing tips
- Provide sample PNGs with embedded `tEXt`/`iTXt`/`zTXt` chunks.
- Validate that `BasePrompt`, `CharacterPrompts`, and `Parameters` hydrate correctly.
- Ensure tags are deduplicated and used by search/token index.