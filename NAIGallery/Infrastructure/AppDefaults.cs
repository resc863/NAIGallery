namespace NAIGallery;

/// <summary>
/// Centralized defaults and magic numbers used across the app.
/// Consider wiring to settings later if needed.
/// </summary>
internal static class AppDefaults
{
    #region Thumbnail Cache
    
    /// <summary>Logical capacity hint for thumbnail cache.</summary>
    public const int DefaultThumbnailCapacityBytes = 5000;
    
    /// <summary>Minimum allowed thumbnail cache capacity.</summary>
    public const int MinThumbnailCacheCapacity = 100;
    
    /// <summary>Minimum size for small thumbnails (progressive loading).</summary>
    public const int SmallThumbMin = 96;
    
    /// <summary>Maximum size for small thumbnails (progressive loading).</summary>
    public const int SmallThumbMax = 160;
    
    /// <summary>Number of thumbnails to apply per UI drain batch.</summary>
    public const int DrainBatch = 16; // Increased for faster thumbnail application
    
    #endregion

    #region UI Responsiveness
    
    /// <summary>UI pulse monitoring interval in milliseconds.</summary>
    public const int UiPulsePeriodMs = 100;
    
    /// <summary>Instant lag threshold to consider UI busy (ms). Relaxed from 50 to 80.</summary>
    public const double UiLagBusyThresholdMs = 80;
    
    /// <summary>EMA lag threshold to consider UI busy (ms). Relaxed from 35 to 50.</summary>
    public const double UiLagEmaBusyThresholdMs = 50;
    
    #endregion

    #region PNG Parsing
    
    /// <summary>Maximum PNG chunk size to parse (64MB).</summary>
    public const int PngMaxChunkLength = 64 * 1024 * 1024;
    
    /// <summary>Maximum text chunk size to parse (4MB).</summary>
    public const int PngMaxTextChunkLength = 4 * 1024 * 1024;
    
    #endregion

    #region Search & Tokenization
    
    /// <summary>Minimum token length for indexing.</summary>
    public const int TokenMinLen = 2;
    
    /// <summary>Maximum token length for indexing.</summary>
    public const int TokenMaxLen = 64;
    
    /// <summary>Maximum number of tag suggestions to return.</summary>
    public const int SuggestionLimit = 20;
    
    #endregion
}
