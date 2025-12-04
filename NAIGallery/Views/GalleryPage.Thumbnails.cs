using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAIGallery.Models;
using System;
using NAIGallery.Services;
using Microsoft.UI.Xaml.Media;

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private double _cachedRasterScale = 1.0; // thread-safe cached scale

    private void EnqueueMeta(ImageMetadata meta, int decodeWidth, bool highPriority = false)
    {
        try { (_service as ImageIndexService)?.Schedule(meta, decodeWidth, highPriority); }
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
    
    /// <summary>
    /// 썸네일이 적용된 후 해당 아이템의 컨테이너를 찾아 직접 갱신
    /// ItemsRepeater의 가상화로 인해 PropertyChanged만으로는 UI가 갱신되지 않는 문제 해결
    /// </summary>
    private void OnThumbnailApplied(ImageMetadata meta)
    {
        if (GalleryView == null || meta?.Thumbnail == null) return;
        
        try
        {
            // ViewModel.Images에서 해당 아이템의 인덱스 찾기
            int index = -1;
            for (int i = 0; i < ViewModel.Images.Count; i++)
            {
                if (ReferenceEquals(ViewModel.Images[i], meta))
                {
                    index = i;
                    break;
                }
            }
            
            if (index < 0) return;
            
            // 가시 영역 내에 있는지 확인
            if (index < _viewStartIndex - 10 || index > _viewEndIndex + 10) return;
            
            // ItemsRepeater에서 해당 인덱스의 컨테이너 찾기
            var container = GalleryView.TryGetElement(index);
            if (container == null) return;
            
            // 컨테이너 내의 Image 요소 찾아서 Source 직접 설정
            var image = FindChildImage(container);
            if (image != null && image.Source != meta.Thumbnail)
            {
                image.Source = meta.Thumbnail;
            }
        }
        catch { }
    }
    
    /// <summary>
    /// 시각적 트리에서 Image 요소 찾기
    /// </summary>
    private static Image? FindChildImage(DependencyObject parent)
    {
        if (parent is Image img) return img;
        
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Image foundImg && foundImg.Name == "connectedElement")
                return foundImg;
            
            var result = FindChildImage(child);
            if (result != null) return result;
        }
        
        return null;
    }
}
