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
    public const int DrainBatch = 12;

    // UI responsiveness
    public const int UiPulsePeriodMs = 250;
    public const double UiLagBusyThresholdMs = 40;       // immediate spike
    public const double UiLagEmaBusyThresholdMs = 25;    // smoothed

    // PNG text chunk parsing limits
    public const int PngMaxChunkLength = 64 * 1024 * 1024;  // 64MB
    public const int PngMaxTextChunkLength = 4 * 1024 * 1024; // 4MB

    // Tokenization
    public const int TokenMinLen = 2;
    public const int TokenMaxLen = 64;

    // Suggestions
    public const int SuggestionLimit = 20;
}
