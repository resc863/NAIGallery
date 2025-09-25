using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAIGallery.Views;
using Microsoft.UI.Xaml.Media.Animation; // ConnectedAnimationService

namespace NAIGallery
{
    /// <summary>
    /// Main application window hosting the NavigationView and root frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            this.InitializeComponent();
            if (RootFrame.Content == null)
                RootFrame.Navigate(typeof(GalleryPage));

            RootFrame.Navigated += (_, __) => UpdateBackButton();
            UpdateBackButton();
        }

        private void UpdateBackButton()
        {
            try
            {
                if (NavView != null && RootFrame != null)
                {
                    NavView.IsBackEnabled = RootFrame.CanGoBack;
                }
            }
            catch { }
        }

        /// <summary>
        /// Cancels any pending connected animations to avoid lingering visuals when changing sections.
        /// </summary>
        private static void CancelConnectedAnimations()
        {
            try
            {
                var cas = ConnectedAnimationService.GetForCurrentView();
                cas.GetAnimation("ForwardConnectedAnimation")?.Cancel();
                cas.GetAnimation("BackConnectedAnimation")?.Cancel();
            }
            catch { }
        }

        private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            try
            {
                if (RootFrame.CanGoBack)
                {
                    CancelConnectedAnimations();
                    RootFrame.GoBack();
                }
            }
            finally { UpdateBackButton(); }
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                // Cancel any active connected animations to avoid lingering image visuals
                CancelConnectedAnimations();
                if (RootFrame.Content?.GetType() != typeof(SettingsPage))
                    RootFrame.Navigate(typeof(SettingsPage));
                UpdateBackButton();
                return;
            }
            if (args.SelectedItem is NavigationViewItem item)
            {
                var tag = item.Tag as string;
                if (tag == "Gallery" && RootFrame.Content?.GetType() != typeof(GalleryPage))
                {
                    // Also cancel animations when switching sections
                    CancelConnectedAnimations();
                    RootFrame.Navigate(typeof(GalleryPage));
                }
            }
            UpdateBackButton();
        }
    }
}
