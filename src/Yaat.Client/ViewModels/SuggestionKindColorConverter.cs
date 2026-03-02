using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

public class SuggestionKindColorConverter : IValueConverter
{
    public static readonly SuggestionKindColorConverter Instance = new();

    private static readonly IBrush RouteFix = new SolidColorBrush(Color.Parse("#4EC9B0"));
    private static readonly IBrush Macro = new SolidColorBrush(Color.Parse("#DCDCAA"));
    private static readonly IBrush Default = new SolidColorBrush(Colors.White);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            SuggestionKind.RouteFix => RouteFix,
            SuggestionKind.Macro => Macro,
            _ => Default,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
