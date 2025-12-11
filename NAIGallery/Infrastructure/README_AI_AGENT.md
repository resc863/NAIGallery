# Folder: Infrastructure

## Purpose
- Shared utilities, constants, and cross-cutting concerns.

## Files

### AppDefaults.cs
Centralized constants and magic numbers:

| Category | Constants |
|----------|-----------|
| Thumbnail Cache | `DefaultThumbnailCapacityBytes`, `MinThumbnailCacheCapacity`, `SmallThumbMin`, `SmallThumbMax`, `DrainBatch` |
| UI Responsiveness | `UiPulsePeriodMs`, `UiLagBusyThresholdMs`, `UiLagEmaBusyThresholdMs` |
| PNG Parsing | `PngMaxChunkLength`, `PngMaxTextChunkLength` |
| Search & Tokenization | `TokenMinLen`, `TokenMaxLen`, `SuggestionLimit` |

### AppSettings.cs
- User-configurable settings persisted to `%LOCALAPPDATA%\NAIGallery\settings.json`.
- Properties: `ThumbCacheCapacity`
- Static methods: `Load()`, instance method `Save()`

### StringPool.cs
- String interning for memory efficiency.
- Used by tokenization to avoid duplicate string allocations.
- Capacity-limited (50,000 entries) to prevent unbounded growth.
- Methods: `Intern(string)`, `Clear()`, property `Count`

### Telemetry.cs
- Metrics and counters for decode errors, latency, etc.
- Uses `System.Diagnostics.Metrics` for lightweight instrumentation.
- Counters: `DecodeIoErrors`, `DecodeFormatErrors`, `DecodeCanceled`, `DecodeUnknownErrors`
- COM error counters: `ComUnsupportedFormat`, `ComOutOfMemory`, `ComAccessDenied`, `ComWrongState`, `ComDeviceLost`, `ComOther`
- Histogram: `DecodeLatencyMs`

## Usage
```csharp
// Access defaults
int capacity = AppDefaults.DefaultThumbnailCapacityBytes;

// Load/save settings
var settings = AppSettings.Load();
settings.ThumbCacheCapacity = 10000;
settings.Save();

// Intern strings for memory efficiency
string interned = StringPool.Intern(token);
int poolSize = StringPool.Count;
StringPool.Clear(); // During major cleanup

// Record telemetry
Telemetry.DecodeIoErrors.Add(1);
Telemetry.DecodeLatencyMs.Record(elapsed.TotalMilliseconds);
```

## Guidance
- Add new app-wide constants to `AppDefaults`.
- Use `StringPool.Intern()` for frequently repeated strings (tags, tokens).
- Record metrics via `Telemetry` for debugging decode issues.
- Call `StringPool.Clear()` during major cleanup operations (e.g., reindexing).
