using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The parts the panel flyouts (Taxi to runway, Air taxi, Cleared / Expect approach, heading, altitude, speed) share: the style
/// that keeps the one menu row holding a panel at its resting background, the panel's separator line, the title line, the
/// monospace font, the box's opening focus, and the brushes of a refusal and of the present value's mark.
/// </summary>
internal static class FlyoutPanelParts
{
    private static readonly IImmutableSolidColorBrush SeparatorBrush = new ImmutableSolidColorBrush(Color.Parse("#3D3D3D"));

    /// <summary>The grey of a panel flyout's title line and its other quiet labels.</summary>
    public static IImmutableSolidColorBrush TitleBrush { get; } = new ImmutableSolidColorBrush(Color.Parse("#A4ADB8"));

    /// <summary>The red of a box's refusal (<c>Not a valid altitude</c>).</summary>
    public static IImmutableSolidColorBrush RefusalBrush { get; } = new ImmutableSolidColorBrush(Color.Parse("#E08A8A"));

    /// <summary>The fill of the present value's row or chip.</summary>
    public static IImmutableSolidColorBrush NowFill { get; } = new ImmutableSolidColorBrush(Color.Parse("#2F4A6E"));

    /// <summary>The edge of the present value's row or chip, and the assigned altitude's glyph.</summary>
    public static IImmutableSolidColorBrush NowEdge { get; } = new ImmutableSolidColorBrush(Color.Parse("#5B8DEF"));

    /// <summary>
    /// Keeps a menu row of class <paramref name="panelClass"/> at its resting background while it is selected or under the
    /// pointer.
    /// </summary>
    public static Style PlainPanelRowStyle(string panelClass)
    {
        Selector LayoutRoot(Selector? x, string state) =>
            x.OfType<MenuItem>().Class(panelClass).Class(state).Template().OfType<Border>().Name("PART_LayoutRoot");

        return new Style(x => Selectors.Or(LayoutRoot(x, ":selected"), LayoutRoot(x, ":pointerover")))
        {
            Setters = { new Setter(Border.BackgroundProperty, Brushes.Transparent) },
        };
    }

    /// <summary>The title line: <paramref name="title"/> left, <paramref name="now"/> right in the monospace font.</summary>
    public static Grid TitleLine(TextBlock title, TextBlock now)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(11, 6, 11, 2) };
        grid.Children.Add(title);
        now.Margin = new Thickness(12, 0, 0, 0);
        Mono(now);
        Grid.SetColumn(now, 1);
        grid.Children.Add(now);
        return grid;
    }

    /// <summary>Draws <paramref name="text"/> in the monospace font.</summary>
    public static void Mono(TextBlock text) => text.Bind(TextBlock.FontFamilyProperty, text.GetResourceObservable(QuickCommandStrip.MonoFontKey));

    /// <summary>
    /// Focuses <paramref name="box"/> once laid out and while <paramref name="isOpen"/> still holds, then runs
    /// <paramref name="after"/>.
    /// </summary>
    public static void FocusLater(TextBox box, Func<bool> isOpen, Action after) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!isOpen())
                {
                    return;
                }

                box.Focus(NavigationMethod.Directional);
                after();
            },
            DispatcherPriority.Loaded
        );

    /// <summary>A one-pixel separator line across the panel.</summary>
    public static Border SeparatorLine() =>
        new()
        {
            Height = 1,
            Background = SeparatorBrush,
            Margin = new Thickness(2, 4),
        };
}
