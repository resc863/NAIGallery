using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace NAIGallery.Converters;

/// <summary>
/// Converts null to Collapsed and non-null to Visible. Inverse of NullToVisibilityConverter.
/// </summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
