using System;
using Microsoft.UI.Xaml;
using NAIGallery.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.Extensions.Logging;
using NAIGallery.Services.Metadata;
using System.Diagnostics.CodeAnalysis;
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
    
    /// <summary>Service provider hosting application singletons.</summary>
    public IServiceProvider Services { get; }

    public T GetRequiredService<T>() where T : notnull => Services.GetRequiredService<T>();

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PngMetadataExtractor))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CompositeMetadataExtractor))]
    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;

        Services = ConfigureServices();
        ApplyPersistedSettings();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        
        services.AddLogging(builder =>
        {
#if DEBUG
            builder.SetMinimumLevel(LogLevel.Debug);
#else
            builder.SetMinimumLevel(LogLevel.Information);
#endif
            builder.AddDebug();
        });

        services.AddSingleton<PngMetadataExtractor>();
        services.AddSingleton<ITokenSearchIndex, TokenSearchIndex>();
        services.AddSingleton<IThumbnailPipeline>(sp => new ThumbnailPipeline(
            AppDefaults.DefaultThumbnailCapacityBytes,
            sp.GetService<ILogger<ThumbnailPipeline>>()));

        // Metadata extractors (composite allows future format additions)
        services.AddSingleton<IMetadataExtractor>(sp => new CompositeMetadataExtractor([
            sp.GetRequiredService<PngMetadataExtractor>()
        ]));

        services.AddSingleton<ImageIndexService>();
        services.AddSingleton<IImageIndexService>(sp => sp.GetRequiredService<ImageIndexService>());
        services.AddSingleton<ViewModels.GalleryViewModel>();
        
        return services.BuildServiceProvider();
    }

    private void ApplyPersistedSettings()
    {
        try
        {
            var settings = AppSettings.Load();
            if (settings.ThumbCacheCapacity.HasValue && 
                Services.GetService(typeof(IImageIndexService)) is IImageIndexService svc)
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
        
        if (Services.GetService(typeof(IImageIndexService)) is IImageIndexService svc)
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
