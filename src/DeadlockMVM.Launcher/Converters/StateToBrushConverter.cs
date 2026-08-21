using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DeadlockMVM.Launcher.Converters;

[ValueConversion(typeof(string), typeof(System.Windows.Media.Brush))]
public class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            "PLAYING" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E)),
            "PAUSED"  => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)),
            _         => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6F, 0x71, 0x6C)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}