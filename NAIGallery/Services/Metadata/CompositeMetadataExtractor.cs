using System.Collections.Generic;
using NAIGallery.Models;

namespace NAIGallery.Services.Metadata;

/// <summary>
/// Tries multiple underlying extractors in order until one returns a non-null result.
/// Enables easy registration of additional formats.
/// </summary>
internal sealed class CompositeMetadataExtractor : IMetadataExtractor
{
    private readonly IReadOnlyList<IMetadataExtractor> _extractors;
    public CompositeMetadataExtractor(IReadOnlyList<IMetadataExtractor> extractors) => _extractors = extractors;

    public ImageMetadata? Extract(string file, string rootFolder, int? knownWidth = null, int? knownHeight = null)
    {
        foreach (var ex in _extractors)
        {
            try
            {
                var meta = ex.Extract(file, rootFolder, knownWidth, knownHeight);
                if (meta != null) return meta;
            }
            catch { }
        }
        return null;
    }
}
