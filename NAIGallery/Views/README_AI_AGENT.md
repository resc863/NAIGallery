Folder: Views

Purpose
- XAML pages and code-behind responsible for gallery grid, detail page, settings, and related UI behaviors.

Structure
- `GalleryPage` split into partial classes:
  - `Layout`: realized item enumeration, viewport range calc, scheduling boosts.
  - `Thumbnails`: desired decode width and enqueue helper into service pipeline.
  - `Interaction`: hover/tap, zoom helpers, visual tree utilities.
  - `Animations`: implicit animations and zoom scale effects.
  - `Navigation`: connected animations, path carrying between pages.
  - `Infrastructure`: other shared helpers for scrolling/realization.
- `ImageDetailPage`: connected animation target, zoom/pan, and metadata presentation.
- `SettingsPage`: controls cache size and reindexing.

Guidance
- Avoid long-running work on the UI thread; call into services for IO/decoding.
- When returning to gallery, resume pipeline and flush apply queue so blanks fill quickly.