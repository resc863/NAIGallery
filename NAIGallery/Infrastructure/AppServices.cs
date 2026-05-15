using System;
using NAIGallery.Services;
using NAIGallery.Services.Metadata;
using NAIGallery.ViewModels;

namespace NAIGallery;

/// <summary>
/// Small AOT-friendly composition root for application singletons.
/// </summary>
public sealed class AppServices
{
    private readonly PngMetadataExtractor _pngMetadataExtractor = new();
    private readonly ITokenSearchIndex _searchIndex = new TokenSearchIndex();
    private readonly IThumbnailPipeline _thumbnailPipeline = new ThumbnailPipeline(AppDefaults.DefaultThumbnailCapacityBytes);

    public AppServices()
    {
        MetadataExtractor = new CompositeMetadataExtractor([_pngMetadataExtractor]);
        ImageIndexService = new ImageIndexService(_searchIndex, _thumbnailPipeline, MetadataExtractor);
        GalleryViewModel = new GalleryViewModel(ImageIndexService);
    }

    public IMetadataExtractor MetadataExtractor { get; }

    public ImageIndexService ImageIndexService { get; }

    public GalleryViewModel GalleryViewModel { get; }

    public T GetRequiredService<T>() where T : notnull
    {
        if (typeof(T) == typeof(IImageIndexService))
            return (T)(object)ImageIndexService;
        if (typeof(T) == typeof(ImageIndexService))
            return (T)(object)ImageIndexService;
        if (typeof(T) == typeof(GalleryViewModel))
            return (T)(object)GalleryViewModel;

        throw new InvalidOperationException($"Service '{typeof(T).FullName}' is not registered.");
    }
}
