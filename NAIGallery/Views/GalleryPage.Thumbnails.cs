using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAIGallery.Models;
using System;
using NAIGallery.Services;
using Microsoft.UI.Xaml.Media;
using System.Threading.Tasks;

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private double _cachedRasterScale = 1.0; // thread-safe cached scale

    private void EnqueueMeta(ImageMetadata meta, int decodeWidth, bool highPriority = false)
    {
        try { _service.ScheduleThumbnail(meta, decodeWidth, highPriority); }
        catch { }
    }

    private int GetDesiredDecodeWidth()
    {
        double size = _baseItemSize;
        double scale = 1.0;
        try
        {
            // Only access XamlRoot on UI thread to prevent COMException.
            if (DispatcherQueue?.HasThreadAccess == true)
            {
                scale = XamlRoot?.RasterizationScale ?? _cachedRasterScale;
                _cachedRasterScale = scale; // update cache
            }
            else
            {
                // Background thread: use last known value
                scale = _cachedRasterScale;
            }
        }
        catch { scale = _cachedRasterScale > 0 ? _cachedRasterScale : 1.0; }

        // Clamp and bucket to 128 multiples (same logic as before)
        int px = (int)Math.Round(size * scale);
        return (int)Math.Clamp(Math.Round(px / 128.0) * 128.0, 128, 2048);
    }

    private async System.Threading.Tasks.Task ProcessQueueAsync() => await System.Threading.Tasks.Task.CompletedTask;

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement tile || !TryResolveTileMetadata(tile, out var meta))
            return;

        try
        {
            int desiredWidth = GetDesiredDecodeWidth();
            int currentWidth = meta.ThumbnailPixelWidth ?? 0;
            if (meta.Thumbnail == null || currentWidth + 32 < desiredWidth)
            {
                EnqueueMeta(meta, desiredWidth, highPriority: true);
            }

            var image = tile.FindName("connectedElement") as Image ?? FindChildImage(tile, meta.FilePath);
            if (image != null)
            {
                if (image.Tag is string tag && !string.Equals(tag, meta.FilePath, StringComparison.OrdinalIgnoreCase))
                    return;

                if (meta.Thumbnail != null && image.Source != meta.Thumbnail)
                    image.Source = meta.Thumbnail;
            }

            if (meta.Thumbnail == null || currentWidth + 32 < desiredWidth)
            {
                // Realized tiles can appear before the viewport scheduler wakes up,
                // so each tile guarantees its own first thumbnail once loaded.
                _ = EnsureTileThumbnailAsync(tile, meta, desiredWidth);
            }
        }
        catch { }
    }

    private async Task EnsureTileThumbnailAsync(FrameworkElement tile, ImageMetadata meta, int desiredWidth)
    {
        try
        {
            await _service.EnsureThumbnailAsync(meta, desiredWidth, default, allowDownscale: false);

            if (meta.Thumbnail == null || !IsContainerForMetadata(tile, meta))
                return;

            var image = tile.FindName("connectedElement") as Image ?? FindChildImage(tile, meta.FilePath);
            if (image != null && image.Source != meta.Thumbnail)
                image.Source = meta.Thumbnail;
        }
        catch { }
    }

    private bool TryResolveTileMetadata(FrameworkElement element, out ImageMetadata meta)
    {
        if (element.DataContext is ImageMetadata dataContextMeta)
        {
            meta = dataContextMeta;
            return true;
        }

        // ItemsRepeater template roots may not expose DataContext reliably with x:DataType;
        // the FilePath tag gives us a stable route back to the current metadata object.
        if (element.Tag is string path && _service.TryGet(path, out var tagMeta) && tagMeta != null)
        {
            meta = tagMeta;
            return true;
        }

        meta = null!;
        return false;
    }
    
    /// <summary>
    /// 썸네일 로드 후 해당 이미지의 컨테이너를 찾아 직접 갱신
    /// ItemsRepeater의 가상화로 인해 PropertyChanged만으로는 UI가 갱신되지 않는 경우 해결
    /// </summary>
    private void OnThumbnailApplied(ImageMetadata meta)
    {
        // UI 스레드에서 실행되도록 보장
        if (DispatcherQueue?.HasThreadAccess != true)
        {
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => OnThumbnailAppliedCore(meta));
            return;
        }
        
        OnThumbnailAppliedCore(meta);
    }
    
    private void OnThumbnailAppliedCore(ImageMetadata meta)
    {
        if (GalleryView == null || meta?.Thumbnail == null) return;
        
        try
        {
            var indexMap = GetImageIndexMap();
            int index = indexMap.TryGetValue(meta, out var mappedIndex)
                ? mappedIndex
                : ViewModel.Images.IndexOf(meta);
            
            if (index < 0) return;
            
            // 현재 뷰포트 범위 내에 있는지 확인 (범위 확대)
            if (index < _viewStartIndex - 20 || index > _viewEndIndex + 20) return;
            
            var realizedContainer = FindRealizedContainer(index, meta);
            if (realizedContainer == null)
                return;

            FrameworkElement container = realizedContainer;
            
            // 컨테이너 내부 Image 요소 찾아서 Source 직접 갱신
            Image? image = container.FindName("connectedElement") as Image;
            if (image != null && meta.FilePath != null && image.Tag is string tag && !string.Equals(tag, meta.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                image = null;
            }

            image ??= FindChildImage(container, meta.FilePath);
            if (image != null)
            {
                image.Source = meta.Thumbnail;
                image.InvalidateMeasure();
                image.InvalidateArrange();
            }

            container.InvalidateMeasure();
            container.InvalidateArrange();

            // AspectRatio can be learned only after decode. Debounce a masonry reflow so
            // tiles stop using stale square/default rects without waiting for user input.
            RequestReflow(80);
        }
        catch { }
    }

    private FrameworkElement? FindRealizedContainer(int index, ImageMetadata meta)
    {
        try
        {
            if (GalleryView.TryGetElement(index) is FrameworkElement byIndex && IsContainerForMetadata(byIndex, meta))
                return byIndex;
        }
        catch { }

        try
        {
            var host = GetItemsHost();
            if (host == null)
                return null;

            for (int i = 0; i < host.Children.Count; i++)
            {
                if (host.Children[i] is FrameworkElement child && IsContainerForMetadata(child, meta))
                    return child;
            }
        }
        catch { }

        return null;
    }

    private static bool IsContainerForMetadata(FrameworkElement container, ImageMetadata meta)
    {
        if (ReferenceEquals(container.DataContext, meta))
            return true;

        return meta.FilePath != null
            && container.Tag is string tag
            && string.Equals(tag, meta.FilePath, StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// 시각적 트리에서 Image 요소 찾기
    /// filePath 매개변수로 Tag를 검증하여 올바른 이미지인지 확인
    /// </summary>
    private static Image? FindChildImage(DependencyObject parent, string? expectedFilePath = null)
    {
        if (parent is Image img)
        {
            // Tag가 예상 파일 경로와 일치하는지 확인
            if (expectedFilePath != null && img.Tag is string tag && !string.Equals(tag, expectedFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return img;
        }
        
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Image foundImg && foundImg.Name == "connectedElement")
            {
                // Tag가 예상 파일 경로와 일치하는지 확인
                if (expectedFilePath != null && foundImg.Tag is string tag && !string.Equals(tag, expectedFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return foundImg;
            }
            
            var result = FindChildImage(child, expectedFilePath);
            if (result != null) return result;
        }
        
        return null;
    }
}
