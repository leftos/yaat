using System.Globalization;
using Avalonia.Data.Converters;

namespace Yaat.Client.ViewModels;

public class ConnectButtonConverter : IValueConverter
{
    public static readonly ConnectButtonConverter Instance = new();

    public object Convert(
        object? value, Type targetType,
        object? parameter, CultureInfo culture)
    {
        return value is true ? "Disconnect" : "Connect";
    }

    public object ConvertBack(
        object? value, Type targetType,
        object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
