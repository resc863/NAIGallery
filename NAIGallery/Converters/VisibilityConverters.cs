using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace NAIGallery.Converters;

/// <summary>
/// Base class for visibility converters providing common invert logic.
/// </summary>
public abstract class VisibilityConverterBase : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        bool shouldBeVisible = EvaluateVisibility(value);
        
        if (invert) shouldBeVisible = !shouldBeVisible;
        
        return shouldBeVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    protected abstract bool EvaluateVisibility(object value);
}

/// <summary>
/// Converts boolean values to Visibility states.
/// Default: true ¡æ Visible, false ¡æ Collapsed.
/// With parameter "Invert": true ¡æ Collapsed, false ¡æ Visible.
/// </summary>
public sealed class BoolToVisibilityConverter : VisibilityConverterBase
{
    protected override bool EvaluateVisibility(object value) 
        => value is bool b && b;
}

/// <summary>
/// Converts null values to Visibility states.
/// Default: null ¡æ Visible, non-null ¡æ Collapsed (useful for loading labels).
/// With parameter "Invert": null ¡æ Collapsed, non-null ¡æ Visible.
/// </summary>
public sealed class NullToVisibilityConverter : VisibilityConverterBase
{
    protected override bool EvaluateVisibility(object value) 
        => value == null;
}
