using System.Globalization;
using Avalonia.Data.Converters;

namespace Yaat.Client.ViewModels;

public sealed class DockButtonConverter : IValueConverter
{
    public static readonly DockButtonConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "Pop Out" : "Dock";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
