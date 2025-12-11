# NAIGallery AI Agent Onboarding

## Goal
- This document collection orients an AI agent to the repository structure and coding patterns.

## Project
- WinUI 3 app on **.NET 10** targeting Windows. Single project: `NAIGallery`.

## Architecture Overview

```
NAIGallery/
戍式 App.xaml.cs              # DI, service registration
戍式 MainWindow.xaml.cs       # Navigation host
戍式 Controls/                # Custom UI controls (AspectPresenter)
戍式 Converters/              # XAML value converters
戍式 Infrastructure/          # AppDefaults, AppSettings, StringPool, Telemetry
戍式 Models/                  # ImageMetadata, CharacterPrompt, ParamEntry
戍式 Services/                # Core business logic
弛   戍式 ImageIndexService.cs          # Main facade (partial class)
弛   戍式 ImageIndexService.Indexing.cs # Folder indexing logic
弛   戍式 ImageIndexService.Search.cs   # Search & tag suggestion
弛   戍式 ImageIndexService.Persistence.cs # Index load/save
弛   戍式 Metadata/            # PNG metadata extraction
弛   戍式 Search/              # Token search index
弛   戍式 Tags/                # TagTrie for suggestions
弛   戌式 Thumbnails/          # Thumbnail pipeline (single file)
戍式 ViewModels/              # GalleryViewModel (MVVM)
戌式 Views/                   # UI pages (partial classes)
```

## How Things Fit Together
- `App` configures DI and services.
- `ImageIndexService` is the fa?ade used by UI: indexing, search, thumbnail management.
  - Split into 4 partial files for maintainability (~80-230 lines each)
- `ThumbnailPipeline` performs decode/cache/apply with worker queues.
  - **Single file** (~350 lines) - consolidated for simplicity
- `GalleryPage` uses the service to request thumbnails based on viewport and handles UI effects.
  - Split into 8 partial files by responsibility
- `ImageDetailPage` shows the selected image with metadata and CA transitions.

## Partial Class Structure

### ImageIndexService (4 files)
| File | Responsibility | ~Lines |
|------|---------------|--------|
| `ImageIndexService.cs` | Fields, events, constructor, index access, thumbnail management | 140 |
| `ImageIndexService.Indexing.cs` | Folder indexing, dimension scan, metadata extraction | 230 |
| `ImageIndexService.Search.cs` | Search logic, tag suggestions | 120 |
| `ImageIndexService.Persistence.cs` | Index load/save (JSON) | 80 |

### ThumbnailPipeline (1 file - consolidated)
| File | Responsibility | ~Lines |
|------|---------------|--------|
| `ThumbnailPipeline.cs` | Fields, events, decoding, apply queue, workers, public API | 350 |

### GalleryPage (8 files)
| File | Responsibility |
|------|---------------|
| `GalleryPage.xaml.cs` | Main entry, event handlers |
| `GalleryPage.Layout.cs` | Viewport calculation, item scheduling |
| `GalleryPage.Thumbnails.cs` | Decode width, enqueue helpers |
| `GalleryPage.Interaction.cs` | Hover, tap, zoom helpers |
| `GalleryPage.Animations.cs` | Implicit animations, scale effects |
| `GalleryPage.Navigation.cs` | Connected animations, path management |
| `GalleryPage.Infrastructure.cs` | Scroll handling, realization helpers |
| `GalleryPage.ZoomPrime.cs` | Initial prime, preload logic |

## Additions You Might Make
- New metadata extractors or formats.
- Search improvements or filters.
- UI tweaks in views; keep heavy work in services.

## Build/Run
- Ensure Windows App SDK is installed.
- Target: `net10.0-windows10.0.26100.0`
- Standard `dotnet build` or Visual Studio 2022+

## Coding Standards
- Favor async/await, avoid blocking UI.
- Use `StringComparer.Ordinal/OrdinalIgnoreCase` explicitly.
- Guard all file/IO with try/catch and respect cancellation tokens.
- Keep individual files under 400 lines for AI agent workability.
- Use partial classes to split large classes by responsibility.

## See Folder-Level Guides for Details
- `Services/README_AI_AGENT.md`
- `Services/Thumbnails/README_AI_AGENT.md`
- `Views/README_AI_AGENT.md`
- Other folder READMEs