Folder: Services

Purpose
- Non-UI logic: indexing, metadata extraction, token search, tag suggestion, thumbnail pipeline.

Key components
- `ImageIndexService` (IImageIndexService)
  - Orchestrates indexing, search, tag store, dispatcher init, and thumbnail pipeline usage.
  - Persists index as `nai_index.json` per root folder.
  - Exposes scheduling helpers: `Schedule`, `BoostVisible`, `UpdateViewport` via pipeline.
- Metadata
  - `IMetadataExtractor` abstraction, implemented by `PngMetadataExtractor` and composed via `CompositeMetadataExtractor`.
  - `PngTextChunkReader` reads tEXt/zTXt/iTXt chunks with safe caps.
- Search
  - `ITokenSearchIndex` and `TokenSearchIndex`: in-memory token to metadata map with suggest and OR query.
- Tags
  - `TagTrie`: case-insensitive prefix trie for fast suggestions.
- Thumbnails
  - `IThumbnailPipeline` and `ThumbnailPipeline`: progressive decode, LRU cache, UI apply batching, scheduling queues.

Threading
- Use background tasks for IO/compute; marshal UI-bound work via `DispatcherQueue` only from the pipeline.

Extending
- Add new extractors and register in App. For different image formats, implement `Extract` returning `ImageMetadata`.
- If adding new searchable fields, update tokenization in `TokenSearchIndex` and `BuildSearchText`.

Safety
- All file access wrapped in try/catch; cancellation respected where provided.
- Thumbnail cache uses `ArrayPool<byte>` and capacity pruning to avoid leaks.