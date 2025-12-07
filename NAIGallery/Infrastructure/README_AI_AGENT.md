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
- User-configurable settings (if any).
- Consider storing cache size preferences here.

### StringPool.cs
- String interning for memory efficiency.
- Used by tokenization to avoid duplicate string allocations.

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

// Intern strings for memory efficiency
string interned = StringPool.Intern(token);

// Record telemetry
Telemetry.DecodeIoErrors.Add(1);
Telemetry.DecodeLatencyMs.Record(elapsed.TotalMilliseconds);
```

## Guidance
- Add new app-wide constants to `AppDefaults`.
- Use `StringPool.Intern()` for frequently repeated strings (tags, tokens).
- Record metrics via `Telemetry` for debugging decode issues.
