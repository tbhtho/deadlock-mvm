using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DeadlockMVM.Launcher.Converters;

/// <summary>Maps a boolean to one of two brushes (for status indicator dots).</summary>
public sealed class BooleanToBrushConverter : IValueConverter
{
    public System.Windows.Media.Brush TrueBrush { get; set; } = System.Windows.Media.Brushes.Green;

    public System.Windows.Media.Brush FalseBrush { get; set; } = System.Windows.Media.Brushes.Gray;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
