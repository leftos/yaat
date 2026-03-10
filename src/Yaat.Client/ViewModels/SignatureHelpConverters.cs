using System.Globalization;
using Avalonia.Data.Converters;

namespace Yaat.Client.ViewModels;

public class GreaterThanOneConverter : IValueConverter
{
    public static readonly GreaterThanOneConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is int n && n > 1;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class PlusOneConverter : IValueConverter
{
    public static readonly PlusOneConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is int n ? n + 1 : value!;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
