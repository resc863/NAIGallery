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

    // UI responsiveness - balanced tuning for smooth scrolling and interaction
    public const int UiPulsePeriodMs = 150;              // Reduced from 200ms to 150ms for faster response
    public const double UiLagBusyThresholdMs = 40;       // Increased from 30ms to 40ms to reduce false positives
    public const double UiLagEmaBusyThresholdMs = 25;    // Increased from 20ms to 25ms for smoother experience

    // PNG text chunk parsing limits
    public const int PngMaxChunkLength = 64 * 1024 * 1024;  // 64MB
    public const int PngMaxTextChunkLength = 4 * 1024 * 1024; // 4MB

    // Tokenization
    public const int TokenMinLen = 2;
    public const int TokenMaxLen = 64;

    // Suggestions
    public const int SuggestionLimit = 20;
}
