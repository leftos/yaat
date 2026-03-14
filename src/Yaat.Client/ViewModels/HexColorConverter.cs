using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Two-way converter between a hex color string (e.g. "#00FF00") and <see cref="Color"/>.
/// Used by ColorPicker controls that bind to string-typed ViewModel properties.
/// </summary>
public sealed class HexColorConverter : IValueConverter
{
    public static readonly HexColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && Color.TryParse(hex, out var color))
        {
            return color;
        }

        return Colors.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        return "#FFFFFF";
    }
}
