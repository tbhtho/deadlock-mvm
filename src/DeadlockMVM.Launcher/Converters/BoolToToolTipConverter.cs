using System;
using System.Globalization;
using System.Windows.Data;

namespace DeadlockMVM.Launcher.Converters;

[ValueConversion(typeof(bool), typeof(string))]
public class BoolToToolTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|');
        if (parts?.Length == 2 && value is bool b)
            return b ? parts[0] : parts[1];
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}