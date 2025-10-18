Folder: Services/Tags

Purpose
- Fast tag prefix suggest via a simple trie.

Files
- `TagTrie`: case-insensitive prefix trie storing original tag casing at terminal nodes.

Notes
- Thread-safe with a single coarse lock; good enough for small updates during indexing.
- Suggestions are bounded and ordered stably by traversal.