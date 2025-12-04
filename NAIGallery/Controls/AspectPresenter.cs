using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace NAIGallery.Controls;

/// <summary>
/// A lightweight presenter that maintains a fixed aspect ratio.
/// If Height is provided (non-NaN), it computes Width = Height * AspectRatio.
/// Else if Width is provided (non-NaN) it computes Height = Width / AspectRatio.
/// Otherwise it falls back to available size heuristics.
/// </summary>
public sealed class AspectPresenter : ContentControl
{
    private const double DefaultAspectRatio = 1.0;

    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    public static readonly DependencyProperty AspectRatioProperty =
        DependencyProperty.Register(
            nameof(AspectRatio),
            typeof(double),
            typeof(AspectPresenter),
            new PropertyMetadata(DefaultAspectRatio, OnAspectChanged));

    private static void OnAspectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AspectPresenter presenter)
        {
            presenter.InvalidateMeasure();
            presenter.InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (width, height) = CalculateDimensions(availableSize);

        if (Content is UIElement child)
            child.Measure(new Size(width, height));

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (width, height) = CalculateDimensions(finalSize);

        if (Content is UIElement child)
            child.Arrange(new Rect(0, 0, width, height));

        return new Size(width, height);
    }

    private (double Width, double Height) CalculateDimensions(Size availableSize)
    {
        double aspectRatio = AspectRatio <= 0 ? DefaultAspectRatio : AspectRatio;
        bool hasExplicitHeight = !double.IsNaN(Height);
        bool hasExplicitWidth = !double.IsNaN(Width);

        double width, height;

        if (hasExplicitHeight && !hasExplicitWidth)
        {
            // Height given -> derive width
            height = Height;
            width = height * aspectRatio;
        }
        else if (hasExplicitWidth && !hasExplicitHeight)
        {
            // Width given -> derive height
            width = Width;
            height = width / aspectRatio;
        }
        else if (hasExplicitWidth && hasExplicitHeight)
        {
            // Both specified - respect them
            width = Width;
            height = Height;
        }
        else
        {
            // Neither dimension explicitly set
            (width, height) = CalculateFromAvailableSize(availableSize, aspectRatio);
        }

        return (width < 0 ? 0 : width, height < 0 ? 0 : height);
    }

    private static (double Width, double Height) CalculateFromAvailableSize(Size availableSize, double aspectRatio)
    {
        double height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        double width;

        if (height > 0)
        {
            width = height * aspectRatio;
        }
        else if (!double.IsInfinity(availableSize.Width))
        {
            width = availableSize.Width;
            height = width / aspectRatio;
        }
        else
        {
            // Fallback - no dimensions available
            width = 0;
            height = 0;
        }

        return (width, height);
    }
}
