NAIGallery AI Agent Onboarding

Goal
- This document collection orients an AI agent to the repository structure and coding patterns.

Project
- WinUI 3 app on .NET 9 targeting Windows. Single project: `NAIGallery`.

How things fit together
- `App` configures DI and services.
- `ImageIndexService` is the fa?ade used by UI: indexing, search, thumbnail management.
- `ThumbnailPipeline` performs decode/cache/apply with worker queues.
- `GalleryPage` uses the service to request thumbnails based on viewport and handles UI effects.
- `ImageDetailPage` shows the selected image with metadata and CA transitions.

Additions you might make
- New metadata extractors or formats.
- Search improvements or filters.
- UI tweaks in views; keep heavy work in services.

Build/run
- Ensure Windows App SDK is installed. Standard dotnet build for net9.0-windows TFM.

Coding standards
- Favor async/await, avoid blocking UI.
- Use `StringComparer.Ordinal/IgnoreCase` explicitly.
- Guard all file/IO with try/catch and respect cancellation tokens.

See folder-level guides for details.