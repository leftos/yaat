using System.Globalization;
using Avalonia.Data.Converters;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Renders a runway designator in FAA display form (no leading zero on single-digit
/// runways: "08R" → "8R") for DataGrid column bindings. The bound value stays the
/// zero-padded canonical for identity/lookups; this only affects what the user sees.
/// </summary>
public class RunwayDisplayConverter : IValueConverter
{
    public static readonly RunwayDisplayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s ? RunwayIdentifier.ToDisplayDesignator(s) : value;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
