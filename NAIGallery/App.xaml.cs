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
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;

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
            
            // 전역 예외 처리 핸들러 등록
            UnhandledException += App_UnhandledException;
            
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

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // 예외 정보 로깅
            Debug.WriteLine($"[UNHANDLED EXCEPTION] {e.Exception?.GetType().Name}: {e.Message}");
            Debug.WriteLine($"Stack Trace: {e.Exception?.StackTrace}");
            
            // 내부 예외 확인
            var inner = e.Exception?.InnerException;
            while (inner != null)
            {
                Debug.WriteLine($"[INNER EXCEPTION] {inner.GetType().Name}: {inner.Message}");
                Debug.WriteLine($"Stack Trace: {inner.StackTrace}");
                inner = inner.InnerException;
            }
            
            // 특정 예외는 처리됨으로 표시하여 앱 크래시 방지
            if (e.Exception is System.Runtime.InteropServices.COMException comEx)
            {
                // WinUI/COM 관련 예외는 대부분 복구 가능
                Debug.WriteLine($"[COM EXCEPTION] HResult: 0x{comEx.HResult:X8}");
                e.Handled = true;
            }
            else if (e.Exception is InvalidOperationException)
            {
                // UI 스레드 관련 예외
                e.Handled = true;
            }
            else if (e.Exception is ObjectDisposedException)
            {
                // 이미 dispose된 객체 접근
                e.Handled = true;
            }
            else if (e.Exception is TaskCanceledException || e.Exception is OperationCanceledException)
            {
                // 취소된 작업
                e.Handled = true;
            }
            
            // 처리되지 않은 예외는 기본 동작 (디버거 중단) 유지
            // Release 빌드에서는 앱이 크래시하지 않도록 처리
#if !DEBUG
            e.Handled = true;
#endif
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
