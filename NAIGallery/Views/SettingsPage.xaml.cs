using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAIGallery.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System;
using Windows.Storage;

namespace NAIGallery.Views;

/// <summary>
/// Settings page for cache size and reindexing.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly ImageIndexService _service;

    public SettingsPage()
    {
        InitializeComponent();
        _service = ((App)Application.Current).Services.GetService(typeof(ImageIndexService)) as ImageIndexService ?? new ImageIndexService();

        // Initialize UI from persisted settings if present
        try
        {
            var local = ApplicationData.Current.LocalSettings;
            int cap = _service.ThumbnailCacheCapacity;
            if (local.Values.TryGetValue("ThumbCacheCapacity", out object? val) && val != null)
            {
                if (val is int i) cap = i;
                else if (val is string s && int.TryParse(s, out var j)) cap = j;
            }
            _service.ThumbnailCacheCapacity = cap;
            ThumbCacheTextBox.Text = cap.ToString();
            CacheStatusText.Text = $"메모리 캐시 항목 수: {cap}"; // display only
        }
        catch
        {
            ThumbCacheTextBox.Text = _service.ThumbnailCacheCapacity.ToString();
            CacheStatusText.Text = $"메모리 캐시 항목 수: {_service.ThumbnailCacheCapacity}";
        }
    }

    private async void Reindex_Click(object sender, RoutedEventArgs e)
    {
        if (StatusText == null) return;
        try
        {
            StatusText.Text = "폴더 선택 중...";
            var picker = new FolderPicker();
            var hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainWindow);
            InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) { StatusText.Text = "취소됨"; return; }
            StatusText.Text = "인덱싱 중...";
            await _service.IndexFolderAsync(folder.Path);
            StatusText.Text = "완료";
        }
        catch (Exception ex)
        {
            StatusText.Text = "오류: " + ex.Message;
        }
    }

    private void ApplyThumbCache_Click(object sender, RoutedEventArgs e)
    {
        if (ThumbCacheTextBox == null || CacheStatusText == null) return;
        if (int.TryParse(ThumbCacheTextBox.Text, out var val))
        {
            // Clamp via service property (has floor 100 in setter)
            _service.ThumbnailCacheCapacity = val;
            ApplicationData.Current.LocalSettings.Values["ThumbCacheCapacity"] = _service.ThumbnailCacheCapacity;
            CacheStatusText.Text = $"캐시 용량 적용됨: {_service.ThumbnailCacheCapacity}";
        }
        else
        {
            CacheStatusText.Text = "숫자를 입력하세요";
        }
    }

    private void ClearThumbCache_Click(object sender, RoutedEventArgs e)
    {
        _service.ClearThumbnailCache();
        CacheStatusText.Text = "캐시를 삭제했습니다";
    }
}
