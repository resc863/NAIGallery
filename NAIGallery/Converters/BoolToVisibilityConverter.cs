using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace NAIGallery.Converters;

/// <summary>
/// Converts boolean values to Visibility states.
/// Default: true ¡æ Visible, false ¡æ Collapsed.
/// With parameter "Invert": true ¡æ Collapsed, false ¡æ Visible.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool invert = parameter is string s && 
                      s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        
        bool boolValue = value is bool b && b;
        
        if (invert) boolValue = !boolValue;
        
        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
