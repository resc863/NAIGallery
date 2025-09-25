using NAIGallery.Models;

namespace NAIGallery.Services.Metadata;

/// <summary>
/// Abstraction for extracting metadata (prompts, parameters, tags) from an image file.
/// Enables unit testing and future format extensions without modifying the index service.
/// </summary>
public interface IMetadataExtractor
{
    /// <summary>
    /// Extract structured metadata for the given image file.
    /// Returns null on failure or unsupported format.
    /// </summary>
    ImageMetadata? Extract(string file, string rootFolder);
}