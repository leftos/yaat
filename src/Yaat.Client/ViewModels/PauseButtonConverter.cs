using System.Globalization;
using Avalonia.Data.Converters;

namespace Yaat.Client.ViewModels;

public class PauseButtonConverter : IValueConverter
{
    public static readonly PauseButtonConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "Resume" : "Pause";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
