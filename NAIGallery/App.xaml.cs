using System;
using Microsoft.UI.Xaml;
using NAIGallery.Services;
using Microsoft.UI.Dispatching;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices;

namespace NAIGallery;

/// <summary>
/// App entry point: wires up DI services and creates the main window.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    
    /// <summary>Currently active main window instance.</summary>
    public Window? MainWindow => _window;
    
    /// <summary>Application singleton container.</summary>
    public AppServices Services { get; }

    public T GetRequiredService<T>() where T : notnull => Services.GetRequiredService<T>();

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;

        Services = new AppServices();
        ApplyPersistedSettings();
    }

    private void ApplyPersistedSettings()
    {
        try
        {
            var settings = AppSettings.Load();
            if (settings.ThumbCacheCapacity.HasValue && 
                Services.ImageIndexService is IImageIndexService svc)
            {
                svc.ThumbnailCacheCapacity = settings.ThumbCacheCapacity.Value;
            }
        }
        catch { }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogException(e);
        e.Handled = ShouldHandleException(e.Exception);
        
#if !DEBUG
        // In Release builds, prevent app crash for most exceptions
        e.Handled = true;
#endif
    }

    private static void LogException(Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Debug.WriteLine($"[UNHANDLED EXCEPTION] {e.Exception?.GetType().Name}: {e.Message}");
        Debug.WriteLine($"Stack Trace: {e.Exception?.StackTrace}");

        var inner = e.Exception?.InnerException;
        while (inner != null)
        {
            Debug.WriteLine($"[INNER EXCEPTION] {inner.GetType().Name}: {inner.Message}");
            Debug.WriteLine($"Stack Trace: {inner.StackTrace}");
            inner = inner.InnerException;
        }
    }

    private static bool ShouldHandleException(Exception? exception)
    {
        if (exception == null) return false;
        
        // COM exceptions (WinUI/COM related)
        if (exception is COMException comEx)
        {
            Debug.WriteLine($"[COM EXCEPTION] HResult: 0x{comEx.HResult:X8}");
            return true;
        }
        
        // Common recoverable exceptions
        if (exception is InvalidOperationException or
            ObjectDisposedException or
            TaskCanceledException or
            OperationCanceledException)
        {
            return true;
        }
        
        return false;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        
        if (Services.ImageIndexService is IImageIndexService svc)
        {
            try
            {
                var dq = DispatcherQueue.GetForCurrentThread();
                Models.ImageMetadata.InitializeDispatcher(dq);
                svc.InitializeDispatcher(dq);
            }
            catch { }
        }
        
        _window.Activate();
    }
}
