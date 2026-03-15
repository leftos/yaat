using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Yaat.Client.Models;

namespace Yaat.Client.ViewModels;

public class SmartStatusSeverityBrushConverter : IValueConverter
{
    public static readonly SmartStatusSeverityBrushConverter Instance = new();

    private static readonly IBrush Normal = new SolidColorBrush(Colors.White);
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#FFD700"));
    private static readonly IBrush Critical = new SolidColorBrush(Color.Parse("#FF4444"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            SmartStatusSeverity.Warning => Warning,
            SmartStatusSeverity.Critical => Critical,
            _ => Normal,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
