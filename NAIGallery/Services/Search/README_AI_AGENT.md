Folder: Services/Search

Purpose
- Lightweight token-based search index for gallery items.

Files
- `ITokenSearchIndex`: abstraction for indexing/removal and token OR queries.
- `TokenSearchIndex`: thread-safe in-memory implementation mapping tokens to `ImageMetadata`.

Behavior
- Tokenization: lower-cased tokens derived from `Tags`, prompts, and character prompts; split on comma/semicolon/space.
- Query: OR semantics across tokens; scoring/refinement happens in `ImageIndexService.SearchByTag`.
- Suggest: prefix lookup across known tokens.

Usage
- Only `ImageIndexService` should interact with the index to keep state consistent.