using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAIGallery.Models;
using Windows.Foundation;

namespace NAIGallery.Controls;

/// <summary>
/// AOT-friendly masonry layout for ItemsRepeater that realizes only items near the viewport.
/// </summary>
public sealed partial class MasonryLayout : VirtualizingLayout
{
    private const double RealizationBuffer = 900.0;
    private readonly List<Rect> _layoutRects = [];
    private readonly Dictionary<int, UIElement> _realized = [];
    private Size _extent;
    private double _lastAvailableWidth = -1;
    private int _lastItemCount = -1;

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(
            nameof(MinItemWidth),
            typeof(double),
            typeof(MasonryLayout),
            new PropertyMetadata(200.0, OnLayoutPropertyChanged));

    public double MinColumnSpacing
    {
        get => (double)GetValue(MinColumnSpacingProperty);
        set => SetValue(MinColumnSpacingProperty, value);
    }

    public static readonly DependencyProperty MinColumnSpacingProperty =
        DependencyProperty.Register(
            nameof(MinColumnSpacing),
            typeof(double),
            typeof(MasonryLayout),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public double MinRowSpacing
    {
        get => (double)GetValue(MinRowSpacingProperty);
        set => SetValue(MinRowSpacingProperty, value);
    }

    public static readonly DependencyProperty MinRowSpacingProperty =
        DependencyProperty.Register(
            nameof(MinRowSpacing),
            typeof(double),
            typeof(MasonryLayout),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(object),
            typeof(MasonryLayout),
            new PropertyMetadata(null, OnLayoutPropertyChanged));

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MasonryLayout layout)
            layout.ResetLayoutCache();
    }

    public void ResetLayoutCache()
    {
        _lastAvailableWidth = -1;
        _lastItemCount = -1;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        int itemCount = context.ItemCount;
        double availableWidth = NormalizeAvailableWidth(availableSize.Width);
        if (itemCount != _lastItemCount || Math.Abs(availableWidth - _lastAvailableWidth) >= 0.5)
            RecycleAll(context);

        EnsureLayoutRects(itemCount, availableWidth);

        var realizationRect = ExpandRealizationRect(context.RealizationRect, availableWidth, availableSize.Height);
        var needed = new HashSet<int>();

        for (int i = 0; i < _layoutRects.Count; i++)
        {
            var rect = _layoutRects[i];
            if (!Intersects(rect, realizationRect))
                continue;

            var element = context.GetOrCreateElementAt(i);
            element.Measure(new Size(rect.Width, rect.Height));
            _realized[i] = element;
            needed.Add(i);
        }

        RecycleUnneeded(context, needed);
        return _extent;
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        foreach (var pair in _realized)
        {
            int index = pair.Key;
            if ((uint)index >= (uint)_layoutRects.Count)
                continue;

            pair.Value.Arrange(_layoutRects[index]);
        }

        return _extent;
    }

    private void EnsureLayoutRects(int itemCount, double availableWidth)
    {
        if (itemCount == _lastItemCount && Math.Abs(availableWidth - _lastAvailableWidth) < 0.5)
            return;

        _layoutRects.Clear();
        _lastItemCount = itemCount;
        _lastAvailableWidth = availableWidth;

        if (itemCount <= 0)
        {
            _extent = new Size(availableWidth, 0);
            return;
        }

        double minItemWidth = Math.Max(32.0, MinItemWidth);
        double columnSpacing = Math.Max(0.0, MinColumnSpacing);
        double rowSpacing = Math.Max(0.0, MinRowSpacing);
        int columnCount = Math.Max(1, (int)Math.Floor((availableWidth + columnSpacing) / (minItemWidth + columnSpacing)));
        double columnWidth = Math.Max(1.0, (availableWidth - (columnCount - 1) * columnSpacing) / columnCount);
        var columnHeights = new double[columnCount];

        for (int i = 0; i < itemCount; i++)
        {
            double aspectRatio = GetAspectRatio(i);
            double height = Math.Max(1.0, columnWidth / aspectRatio);
            int column = FindShortestColumn(columnHeights);
            double x = column * (columnWidth + columnSpacing);
            double y = columnHeights[column];

            _layoutRects.Add(new Rect(x, y, columnWidth, height));
            columnHeights[column] = y + height + rowSpacing;
        }

        double extentHeight = 0;
        for (int i = 0; i < columnHeights.Length; i++)
            extentHeight = Math.Max(extentHeight, columnHeights[i]);

        if (extentHeight > 0)
            extentHeight -= rowSpacing;

        _extent = new Size(availableWidth, Math.Max(0, extentHeight));
    }

    private double GetAspectRatio(int index)
    {
        try
        {
            if (ItemsSource is IList list && (uint)index < (uint)list.Count && list[index] is ImageMetadata meta)
                return Math.Clamp(meta.AspectRatio, 0.1, 10.0);
        }
        catch { }

        return 1.0;
    }

    private void RecycleUnneeded(VirtualizingLayoutContext context, HashSet<int> needed)
    {
        List<int>? toRecycle = null;
        foreach (var pair in _realized)
        {
            if (needed.Contains(pair.Key))
                continue;

            toRecycle ??= [];
            toRecycle.Add(pair.Key);
        }

        if (toRecycle == null)
            return;

        foreach (int index in toRecycle)
        {
            if (_realized.Remove(index, out var element))
                context.RecycleElement(element);
        }
    }

    private void RecycleAll(VirtualizingLayoutContext context)
    {
        if (_realized.Count == 0)
            return;

        foreach (var element in _realized.Values)
            context.RecycleElement(element);

        _realized.Clear();
    }

    private static double NormalizeAvailableWidth(double width)
    {
        return double.IsInfinity(width) || double.IsNaN(width) || width <= 0 ? 1.0 : width;
    }

    private static Rect ExpandRealizationRect(Rect rect, double fallbackWidth, double fallbackHeight)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height))
        {
            double height = double.IsInfinity(fallbackHeight) || double.IsNaN(fallbackHeight) || fallbackHeight <= 0
                ? 1200.0
                : fallbackHeight;
            rect = new Rect(0, 0, fallbackWidth, height);
        }

        return new Rect(
            rect.X,
            Math.Max(0, rect.Y - RealizationBuffer),
            Math.Max(fallbackWidth, rect.Width),
            rect.Height + RealizationBuffer * 2.0);
    }

    private static bool Intersects(Rect a, Rect b)
    {
        return a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
    }

    private static int FindShortestColumn(double[] columnHeights)
    {
        int shortest = 0;
        double shortestHeight = columnHeights[0];
        for (int i = 1; i < columnHeights.Length; i++)
        {
            if (columnHeights[i] < shortestHeight)
            {
                shortest = i;
                shortestHeight = columnHeights[i];
            }
        }

        return shortest;
    }
}
