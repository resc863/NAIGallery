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
    public const int DrainBatch = 16; // Increased from 9 to 16 for better throughput

    // UI responsiveness - optimized for smooth experience
    public const int UiPulsePeriodMs = 100;              // Reduced from 150ms to 100ms for faster response
    public const double UiLagBusyThresholdMs = 50;       // Increased from 40ms to 50ms to reduce throttling
    public const double UiLagEmaBusyThresholdMs = 30;    // Increased from 25ms to 30ms for smoother experience

    // PNG text chunk parsing limits
    public const int PngMaxChunkLength = 64 * 1024 * 1024;  // 64MB
    public const int PngMaxTextChunkLength = 4 * 1024 * 1024; // 4MB

    // Tokenization
    public const int TokenMinLen = 2;
    public const int TokenMaxLen = 64;

    // Suggestions
    public const int SuggestionLimit = 20;
}
