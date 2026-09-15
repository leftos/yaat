using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Paints the Aircraft List's filed-type marker amber when the aircraft is physically a different type
/// than its flight plan files (<see cref="Yaat.Client.Models.AircraftModel.HasFiledTypeMismatch"/>), and
/// leaves the inherited foreground alone otherwise. Matches the amber the radar datablock tints the type
/// token with (<c>TargetRenderer.TypeMismatchColor</c>).
/// </summary>
public class TypeMismatchBrushConverter : IValueConverter
{
    public static readonly TypeMismatchBrushConverter Instance = new();

    private static readonly IBrush Mismatch = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x30));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Mismatch : AvaloniaProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
