using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Client.ViewModels.Authoring;

namespace Yaat.Client.Views.Authoring;

/// <summary>
/// Find Route's flyout content over a <see cref="RouteFinderViewModel"/>: the query with its editable boxes and Search,
/// the Sources bar, a line per failed source, the routes with their source badges, counts and notes, Show all, the cruise
/// altitude Apply would file, the real callsign and type, Apply and Cancel. Escape anywhere in the flyout asks it to close.
/// </summary>
public partial class RouteFinderView : UserControl
{
    // The top level the view shows in while attached: the flyout's popup, whose keys Escape is watched on.
    private TopLevel? _top;

    /// <summary>Initializes a new instance of the <see cref="RouteFinderView"/> class.</summary>
    public RouteFinderView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when Escape is pressed in the flyout.</summary>
    public event EventHandler? CloseRequested;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// A source's pill colours (csko blue, navdata grey, preferred green, history purple, any other grey), and red for a failed
/// source's pill; the value is a source name or a <see cref="RouteSourcePill"/>.
/// </summary>
public sealed class RouteSourceBrushes : IValueConverter
{
    /// <summary>The pill's background.</summary>
    public static readonly RouteSourceBrushes Background = new(0);

    /// <summary>The pill's border.</summary>
    public static readonly RouteSourceBrushes Border = new(1);

    /// <summary>The pill's text.</summary>
    public static readonly RouteSourceBrushes Foreground = new(2);

    // Background, border and text, per look.
    private static readonly IBrush[] Blue = Look("#1C2D3C", "#2F5C80", "#7FB4E6");
    private static readonly IBrush[] Grey = Look("#333333", "#555555", "#BDBDBD");
    private static readonly IBrush[] Green = Look("#1D3324", "#356A45", "#8FD19E");
    private static readonly IBrush[] Purple = Look("#2A2440", "#5A4A8A", "#C9B3FF");
    private static readonly IBrush[] Red = Look("#3D1F1F", "#7A3434", "#FF8A8A");

    private readonly int _part;

    private RouteSourceBrushes(int part) => _part = part;

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is RouteSourcePill { Failed: true })
        {
            return Red[_part];
        }

        string? source = (value as RouteSourcePill)?.Source ?? (value as string);
        IBrush[] look = source switch
        {
            "csko" => Blue,
            "preferred" => Green,
            "history" => Purple,
            _ => Grey,
        };
        return look[_part];
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static IBrush[] Look(string background, string border, string text) =>
        [
            new ImmutableSolidColorBrush(Color.Parse(background)),
            new ImmutableSolidColorBrush(Color.Parse(border)),
            new ImmutableSolidColorBrush(Color.Parse(text)),
        ];
}
