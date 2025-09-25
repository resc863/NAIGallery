using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAIGallery.Services;
using Microsoft.Extensions.DependencyInjection;
using Windows.Storage; // for ApplicationData
using Microsoft.UI.Dispatching; // DispatcherQueue

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

        public App()
        {
            InitializeComponent();
            var services = new ServiceCollection();
            services.AddSingleton<ImageIndexService>();
            Services = services.BuildServiceProvider();

            // Apply persisted user settings to services when available
            try
            {
                if (Services.GetService(typeof(ImageIndexService)) is ImageIndexService svc)
                {
                    var local = ApplicationData.Current.LocalSettings;
                    if (local.Values.TryGetValue("ThumbCacheCapacity", out object? val) && val != null)
                    {
                        if (val is int i) svc.ThumbnailCacheCapacity = i;
                        else if (val is string s && int.TryParse(s, out var j)) svc.ThumbnailCacheCapacity = j;
                    }
                }
            }
            catch { }
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            if (Services.GetService(typeof(ImageIndexService)) is ImageIndexService svc)
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
