using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAIGallery.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System;
using NAIGallery.ViewModels;

namespace NAIGallery.Views;

/// <summary>
/// Settings page for cache size and reindexing.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly IImageIndexService _service;
    private readonly GalleryViewModel _vm;

    public SettingsPage()
    {
        InitializeComponent();
        var app = (App)Application.Current;
        _service = app.GetRequiredService<IImageIndexService>();
        _vm = app.GetRequiredService<GalleryViewModel>();

        // Initialize UI from persisted settings if present
        try
        {
            var settings = AppSettings.Load();
            int cap = _service.ThumbnailCacheCapacity;
            if (settings.ThumbCacheCapacity.HasValue)
                cap = settings.ThumbCacheCapacity.Value;

            _service.ThumbnailCacheCapacity = cap;
            cap = _service.ThumbnailCacheCapacity;
            ThumbCacheTextBox.Text = BytesToMegabytes(cap).ToString();
            CacheStatusText.Text = $"메모리 캐시 용량: {BytesToMegabytes(cap)} MB";

            var chkAnd = FindName("ChkAndMode") as CheckBox;
            var chkPartial = FindName("ChkPartial") as CheckBox;
            if (chkAnd != null) chkAnd.IsChecked = _vm.SearchAndMode;
            if (chkPartial != null) chkPartial.IsChecked = _vm.SearchPartialMode;
        }
        catch
        {
            ThumbCacheTextBox.Text = BytesToMegabytes(_service.ThumbnailCacheCapacity).ToString();
            CacheStatusText.Text = $"메모리 캐시 용량: {BytesToMegabytes(_service.ThumbnailCacheCapacity)} MB";
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
            _service.ThumbnailCacheCapacity = MegabytesToBytes(val);
            SaveSettings(settings => settings.ThumbCacheCapacity = _service.ThumbnailCacheCapacity);
            CacheStatusText.Text = $"캐시 용량 적용됨: {BytesToMegabytes(_service.ThumbnailCacheCapacity)} MB";
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

    private void SearchMode_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        var chkAnd = FindName("ChkAndMode") as CheckBox;
        var chkPartial = FindName("ChkPartial") as CheckBox;
        _vm.SearchAndMode = chkAnd?.IsChecked == true;
        _vm.SearchPartialMode = chkPartial?.IsChecked == true;
        SaveSettings(settings =>
        {
            settings.SearchAndMode = _vm.SearchAndMode;
            settings.SearchPartialMode = _vm.SearchPartialMode;
        });
    }

    private static void SaveSettings(Action<AppSettings> update)
    {
        try
        {
            var settings = AppSettings.Load();
            update(settings);
            settings.Save();
        }
        catch { }
    }

    private static int BytesToMegabytes(int bytes)
        => Math.Max(1, (int)Math.Round(bytes / 1024.0 / 1024.0));

    private static int MegabytesToBytes(int megabytes)
    {
        long bytes = Math.Max(1, megabytes) * 1024L * 1024L;
        return bytes > int.MaxValue ? int.MaxValue : (int)bytes;
    }
}
