using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Yaat.Client.Models;

namespace Yaat.Client.ViewModels;

public sealed class TerminalEntryColorConverter : IValueConverter
{
    public static readonly TerminalEntryColorConverter Instance = new();

    private static readonly IBrush CommandBrush = Brushes.White;
    private static readonly IBrush ResponseBrush = Brushes.LightGreen;
    private static readonly IBrush SystemBrush = Brushes.Gray;
    private static readonly IBrush SayBrush = Brushes.Orange;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is TerminalEntryKind kind
            ? kind switch
            {
                TerminalEntryKind.Command => CommandBrush,
                TerminalEntryKind.Response => ResponseBrush,
                TerminalEntryKind.System => SystemBrush,
                TerminalEntryKind.Say => SayBrush,
                _ => CommandBrush,
            }
            : CommandBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
