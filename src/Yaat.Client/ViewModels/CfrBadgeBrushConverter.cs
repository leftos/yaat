using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Yaat.Client.ViewModels;

/// <summary>Brush for the Info-column CFR release-window badge: amber while active, red once expired.</summary>
public class CfrBadgeBrushConverter : IValueConverter
{
    public static readonly CfrBadgeBrushConverter Instance = new();

    private static readonly IBrush Active = new SolidColorBrush(Color.Parse("#FFD700"));
    private static readonly IBrush Expired = new SolidColorBrush(Color.Parse("#FF4444"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? Expired : Active;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
