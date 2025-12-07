# Folder: Services

## Purpose
- Non-UI logic: indexing, metadata extraction, token search, tag suggestion, thumbnail pipeline.

## Key Components

### ImageIndexService (IImageIndexService) - Partial Class (4 files)

The main service fa?ade is split into multiple files for maintainability:

| File | Purpose |
|------|---------|
| `ImageIndexService.cs` | Core fields, events, constructor, index access, initialization, thumbnail management delegation |
| `ImageIndexService.Indexing.cs` | `IndexFolderAsync`, dimension scanning, metadata extraction with parallel processing |
| `ImageIndexService.Search.cs` | `Search`, `SearchByTag`, `SuggestTags` - query and scoring logic |
| `ImageIndexService.Persistence.cs` | `LoadIndexAsync`, `SaveIndexAsync` - JSON persistence per folder |

**Key responsibilities:**
- Orchestrates indexing, search, tag store, dispatcher init, and thumbnail pipeline usage.
- Persists index as `nai_index.json` per root folder.
- Exposes scheduling helpers: `Schedule`, `BoostVisible`, `UpdateViewport` via pipeline.

### Metadata
- `IMetadataExtractor` abstraction, implemented by `PngMetadataExtractor` and composed via `CompositeMetadataExtractor`.
- `PngTextChunkReader` reads tEXt/zTXt/iTXt chunks with safe caps.

### Search
- `ITokenSearchIndex` and `TokenSearchIndex`: in-memory token to metadata map with suggest and OR query.
- `SearchTextBuilder`: builds search text and frozen token sets for fast matching.

### Tags
- `TagTrie`: case-insensitive prefix trie for fast suggestions.

### Thumbnails
- `IThumbnailPipeline` and `ThumbnailPipeline` (partial class, 4 files): progressive decode, LRU cache, UI apply batching, scheduling queues.
- See `Thumbnails/README_AI_AGENT.md` for details.

## Threading
- Use background tasks for IO/compute; marshal UI-bound work via `DispatcherQueue` only from the pipeline.
- Indexing uses producer-consumer pattern with `System.Threading.Channels`.

## Extending
- Add new extractors and register in App. For different image formats, implement `Extract` returning `ImageMetadata`.
- If adding new searchable fields, update tokenization in `TokenSearchIndex` and `SearchTextBuilder`.

## Safety
- All file access wrapped in try/catch; cancellation respected where provided.
- Thumbnail cache uses `ArrayPool<byte>` and capacity pruning to avoid leaks.