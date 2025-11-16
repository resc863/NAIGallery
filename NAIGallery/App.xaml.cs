using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAIGallery.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching; // DispatcherQueue
using Microsoft.Extensions.Logging;
using NAIGallery.Services.Metadata;
using System.Diagnostics.CodeAnalysis; // DynamicDependency
using System.IO;
using System.Text.Json;

namespace NAIGallery
{
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

        public class AppSettings // changed from sealed private to public
        {
            public int? ThumbCacheCapacity { get; set; }
        }

        private static string GetSettingsPath()
        {
            try
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dir = Path.Combine(root, "NAIGallery");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.json");
            }
            catch { return Path.Combine(Path.GetTempPath(), "NAIGallery.settings.json"); }
        }

        private static AppSettings LoadSettings()
        {
            try
            {
                var path = GetSettingsPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public static void SaveSettings(AppSettings s)
        {
            try
            {
                var path = GetSettingsPath();
                var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        // DynamicDependency attributes help the trimmer keep required members (AOT friendly)
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PngMetadataExtractor))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CompositeMetadataExtractor))]
        public App()
        {
            InitializeComponent();
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

            // Metadata extractors (composite allows future format additions)
            services.AddSingleton<IMetadataExtractor>(sp => new CompositeMetadataExtractor(new IMetadataExtractor[]
            {
                new PngMetadataExtractor()
                // Future: new JpegMetadataExtractor(), new WebpMetadataExtractor(), etc.
            }));

            services.AddSingleton<IImageIndexService, ImageIndexService>();
            Services = services.BuildServiceProvider();

            // Apply persisted settings via plain file storage
            try
            {
                var settings = LoadSettings();
                if (settings.ThumbCacheCapacity.HasValue && Services.GetService(typeof(IImageIndexService)) is IImageIndexService svc)
                {
                    svc.ThumbnailCacheCapacity = settings.ThumbCacheCapacity.Value;
                }
            }
            catch { }
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            if (Services.GetService(typeof(IImageIndexService)) is IImageIndexService svc)
            {
                try
                {
                    var dq = DispatcherQueue.GetForCurrentThread();
                    svc.InitializeDispatcher(dq);
                }
                catch { }
            }
            _window.Activate();
        }
    }
}
