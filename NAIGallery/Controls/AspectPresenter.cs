using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace NAIGallery.Controls
{
    /// <summary>
    /// A lightweight presenter that maintains a fixed aspect ratio.
    /// If Height is provided (non-NaN), it computes Width = Height * AspectRatio.
    /// Else if Width is provided (non-NaN) it computes Height = Width / AspectRatio.
    /// Otherwise it falls back to available size heuristics.
    /// </summary>
    public sealed class AspectPresenter : ContentControl
    {
        public AspectPresenter()
        {
            // Default ContentControl style is fine.
        }

        public double AspectRatio
        {
            get => (double)GetValue(AspectRatioProperty);
            set => SetValue(AspectRatioProperty, value);
        }

        public static readonly DependencyProperty AspectRatioProperty =
            DependencyProperty.Register(
                nameof(AspectRatio), typeof(double), typeof(AspectPresenter),
                new PropertyMetadata(1.0, OnAspectChanged));

        private static void OnAspectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AspectPresenter ap)
            {
                ap.InvalidateMeasure();
                ap.InvalidateArrange();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double ar = AspectRatio <= 0 ? 1.0 : AspectRatio;

            bool hasExplicitHeight = !double.IsNaN(Height);
            bool hasExplicitWidth = !double.IsNaN(Width);

            double w, h;

            if (hasExplicitHeight && !hasExplicitWidth)
            {
                // Height given -> derive width
                h = Height;
                w = h * ar;
            }
            else if (hasExplicitWidth && !hasExplicitHeight)
            {
                // Width given -> derive height
                w = Width;
                h = w / ar;
            }
            else if (hasExplicitWidth && hasExplicitHeight)
            {
                // Both specified, just respect them (caller responsibility to keep ratio consistent)
                w = Width;
                h = Height;
            }
            else
            {
                // Neither dimension explicitly set -> attempt from available height first (legacy behavior)
                h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
                if (h > 0)
                {
                    w = h * ar;
                }
                else if (!double.IsInfinity(availableSize.Width))
                {
                    w = availableSize.Width;
                    h = w / ar;
                }
                else
                {
                    // Fallback square
                    h = 0;
                    w = 0;
                }
            }

            if (w < 0) w = 0; if (h < 0) h = 0;

            if (Content is UIElement child)
            {
                child.Measure(new Size(w, h));
            }
            return new Size(w, h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double ar = AspectRatio <= 0 ? 1.0 : AspectRatio;
            bool hasExplicitHeight = !double.IsNaN(Height);
            bool hasExplicitWidth = !double.IsNaN(Width);
            double w = finalSize.Width;
            double h = finalSize.Height;

            if (hasExplicitHeight && !hasExplicitWidth)
            {
                h = Height;
                w = h * ar;
            }
            else if (hasExplicitWidth && !hasExplicitHeight)
            {
                w = Width;
                h = w / ar;
            }
            else if (hasExplicitWidth && hasExplicitHeight)
            {
                w = Width; h = Height;
            }
            else
            {
                // Neither explicit: adjust using whichever final dimension we actually received.
                if (h <= 0 && w > 0) h = w / ar;
                else if (w <= 0 && h > 0) w = h * ar;
                else if (h > 0 && w > 0)
                {
                    // Prefer width-based correction to avoid stretching in staggered columns
                    h = w / ar;
                }
            }

            if (Content is UIElement child)
            {
                child.Arrange(new Rect(0, 0, w, h));
            }
            return new Size(w, h);
        }
    }
}
