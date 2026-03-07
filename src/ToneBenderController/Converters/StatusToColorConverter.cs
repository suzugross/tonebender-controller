using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ToneBenderController.Converters;

/// <summary>
/// Converts BuildLogEntry.Status string to a foreground Brush.
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "success" => new SolidColorBrush(Color.FromRgb(0x1E, 0x8C, 0x50)),
            "error" => new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)),
            "warn" => new SolidColorBrush(Color.FromRgb(0xDC, 0x78, 0x1E)),
            _ => new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
