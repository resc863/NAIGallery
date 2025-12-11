Folder: Services/Search

## Purpose
- Lightweight token-based search index for gallery items.

## Files

### ITokenSearchIndex
Interface abstraction for indexing/removal and token OR queries.

### TokenSearchIndex
Thread-safe, lock-free in-memory implementation using `ConcurrentDictionary`.

**Key methods:**
- `Index(ImageMetadata)`: Indexes metadata by extracting tokens
- `Remove(ImageMetadata)`: Removes metadata from index
- `QueryTokens(IEnumerable<string>)`: Returns all metadata matching any token (OR semantics)
- `Suggest(string prefix, int limit)`: Prefix-based token suggestions

### SearchTextBuilder
Static helper for building search text and token sets.

**Key methods:**
- `BuildSearchText(ImageMetadata)`: Returns lowercase concatenated search payload
- `BuildFrozenTokenSet(ImageMetadata)`: Returns `FrozenSet<string>` for fast membership testing

## Behavior
- Tokenization: lower-cased tokens derived from `Tags`, prompts, and character prompts; split on comma/semicolon/space.
- Token length filtered by `AppDefaults.TokenMinLen` (2) and `TokenMaxLen` (64).
- Uses `StringPool.Intern()` for memory-efficient token storage.
- Query: OR semantics across tokens; scoring/refinement happens in `ImageIndexService.SearchByTag`.
- Suggest: prefix lookup across known tokens, bounded by `AppDefaults.SuggestionLimit`.

## Usage
- Only `ImageIndexService` should interact with the index to keep state consistent.