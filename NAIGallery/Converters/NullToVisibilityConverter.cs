using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace NAIGallery.Converters;

/// <summary>
/// Converts null to Visible and non-null to Collapsed. Useful for simple loading labels.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value == null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
