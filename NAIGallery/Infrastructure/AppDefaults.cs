namespace NAIGallery;

/// <summary>
/// Centralized defaults and magic numbers used across the app.
/// Consider wiring to settings later if needed.
/// </summary>
internal static class AppDefaults
{
    // Thumbnails
    public const int DefaultThumbnailCapacityBytes = 5000; // logical capacity hint; actual bytes managed internally
    public const int MinThumbnailCacheCapacity = 100;
    public const int SmallThumbMin = 96;
    public const int SmallThumbMax = 160;
    public const int DrainBatch = 9; // Reduced from 12 to 9 for better UI responsiveness

    // UI responsiveness - more aggressive tuning
    public const int UiPulsePeriodMs = 200;              // Reduced from 250ms to 200ms for faster response
    public const double UiLagBusyThresholdMs = 30;       // Reduced from 40ms to 30ms for earlier detection
    public const double UiLagEmaBusyThresholdMs = 20;    // Reduced from 25ms to 20ms for smoother experience

    // PNG text chunk parsing limits
    public const int PngMaxChunkLength = 64 * 1024 * 1024;  // 64MB
    public const int PngMaxTextChunkLength = 4 * 1024 * 1024; // 4MB

    // Tokenization
    public const int TokenMinLen = 2;
    public const int TokenMaxLen = 64;

    // Suggestions
    public const int SuggestionLimit = 20;
}
