using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The quick-command icon strip at the top of an aircraft menu: one menu item whose header holds up to two rows of five
/// glyph buttons, the first row filled first and an empty second row hidden. Each button stands for its catalog entry's
/// own menu item, built through the entry's builder: a sending or prompting item is clicked through that item's own Click
/// handler, and a submenu opens its items as a flyout under the button (the button shows a corner notch). Either way the
/// menu closes once a command is chosen. The tooltip names the entry and, for an item that sends one fixed command, that
/// command (<see cref="MenuCommandText"/>). Plain controls only, so it works on every view, the aircraft list included.
/// </summary>
public static class QuickCommandStrip
{
    /// <summary>The style class the strip's menu item carries, by which <see cref="IsStrip"/> knows it.</summary>
    public const string StripClass = "quick-command-strip";

    /// <summary>How many buttons a row holds.</summary>
    public const int RowLength = 5;

    private const double CellSize = 40;
    private const double GlyphSize = 22;
    private const double Gap = 6;
    private const string NotchPath = "M6 0V6H0z";
    private static readonly Color NotchColor = Color.Parse("#9AA3AD");

    /// <summary>
    /// The strip for <paramref name="items"/>, in order, or null when there are none or none of them builds a menu item
    /// for this aircraft (an entry whose builder has nothing to show is left out, as in All Commands).
    /// </summary>
    /// <param name="menu">The menu the strip sits in, which a chosen command closes.</param>
    /// <param name="items">The resolved strip entries (<see cref="QuickCommandResolution.Strip"/>).</param>
    /// <param name="aircraft">The aircraft the menu commands.</param>
    /// <param name="context">The menu's click and session.</param>
    /// <param name="host">The send path, popups and choices the entries' items use.</param>
    public static MenuItem? Build(
        ContextMenu menu,
        IReadOnlyList<QuickCommandStripItem> items,
        IMenuAircraft aircraft,
        MenuContext context,
        IMenuHost host
    )
    {
        List<Control> buttons = [];
        foreach (QuickCommandStripItem item in items)
        {
            if (item.Entry.Build(aircraft, context, host) is { } built)
            {
                buttons.Add(BuildButton(menu, item, built));
            }
        }

        if (buttons.Count == 0)
        {
            return null;
        }

        var strip = new MenuItem { Header = Rows(buttons), StaysOpenOnClick = true };
        strip.Classes.Add(StripClass);
        return strip;
    }

    /// <summary>
    /// Lays <paramref name="cells"/> out as the strip does, in order: up to two rows of <see cref="RowLength"/>, the first
    /// row filled first and an empty second row hidden. The menu's strip and the Settings preview both lay out through it.
    /// </summary>
    public static StackPanel Rows(IReadOnlyList<Control> cells)
    {
        var first = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gap };
        var second = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gap };
        foreach (Control cell in cells)
        {
            StackPanel row = (first.Children.Count < RowLength) ? first : second;
            row.Children.Add(cell);
        }

        second.IsVisible = second.Children.Count > 0;
        return new StackPanel { Spacing = Gap, Children = { first, second } };
    }

    /// <summary>
    /// The square a strip button shows: <paramref name="glyph"/> centred, with the corner notch when the button opens a
    /// submenu. The menu's buttons and the Settings preview both draw their glyphs through it.
    /// </summary>
    public static Grid GlyphCell(QuickCommandGlyph glyph, bool opensSubmenu)
    {
        var cell = new Grid { Width = CellSize, Height = CellSize };
        cell.Children.Add(GlyphIcon(glyph));
        if (opensSubmenu)
        {
            cell.Children.Add(Notch());
        }

        return cell;
    }

    /// <summary>The glyph's stroke path on its 24 px view box, scaled to the strip's glyph size and drawn in its family's colour.</summary>
    public static Viewbox GlyphIcon(QuickCommandGlyph glyph) =>
        new()
        {
            Width = GlyphSize,
            Height = GlyphSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children =
                {
                    new Path
                    {
                        Data = Geometry.Parse(glyph.PathData),
                        Stroke = new SolidColorBrush(FamilyColor(glyph.Family)),
                        StrokeThickness = 1.75,
                        StrokeLineCap = PenLineCap.Round,
                        StrokeJoin = PenLineJoin.Round,
                    },
                },
            },
        };

    /// <summary>Whether <paramref name="item"/> is a quick-command strip.</summary>
    public static bool IsStrip(MenuItem item) => item.Classes.Contains(StripClass);

    /// <summary>The strip's buttons, row one then row two; each button's <see cref="Control.Tag"/> is its catalog entry's id.</summary>
    public static IReadOnlyList<Button> Buttons(MenuItem strip) =>
        strip.Header is StackPanel rows ? [.. rows.Children.OfType<StackPanel>().SelectMany(row => row.Children.OfType<Button>())] : [];

    /// <summary>The colour a glyph of <paramref name="family"/> is drawn in.</summary>
    public static Color FamilyColor(QuickCommandGlyphFamily family) =>
        family switch
        {
            QuickCommandGlyphFamily.Tower => Color.Parse("#E8A33D"),
            QuickCommandGlyphFamily.Ground => Color.Parse("#4FB8A8"),
            QuickCommandGlyphFamily.Flight => Color.Parse("#7AA7F0"),
            QuickCommandGlyphFamily.Pattern => Color.Parse("#B98BE8"),
            QuickCommandGlyphFamily.ScopeAndSim => Color.Parse("#AEB6C0"),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown quick-command glyph family."),
        };

    /// <summary>
    /// The tooltip for <paramref name="built"/>: its header and the command it sends (<c>Hold position — HOLD</c>), or the
    /// header alone for a submenu or an item that sends no fixed command.
    /// </summary>
    public static string Tooltip(MenuCatalogEntry entry, MenuItem built)
    {
        string label = built.Header as string ?? entry.Label;
        return ((built.Items.Count == 0) && (MenuCommandText.GetCommand(built) is { Length: > 0 } command)) ? $"{label} — {command}" : label;
    }

    private static Button BuildButton(ContextMenu menu, QuickCommandStripItem item, MenuItem built)
    {
        bool opensSubmenu = built.Items.Count > 0;
        var button = new Button
        {
            Content = GlyphCell(item.Glyph, opensSubmenu),
            Padding = new Thickness(0),
            Tag = item.Entry.Id,
        };
        ToolTip.SetTip(button, Tooltip(item.Entry, built));
        if (opensSubmenu)
        {
            FlyoutBase.SetAttachedFlyout(button, SubmenuFlyout(menu, built));
            button.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(button);
        }
        else
        {
            button.Click += (_, _) =>
            {
                built.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.Close();
            };
        }

        return button;
    }

    /// <summary>
    /// A flyout over <paramref name="built"/>'s own items, moved over unchanged; choosing any command in it, at any
    /// depth, closes the flyout and the menu after the item's own handler has run.
    /// </summary>
    private static MenuFlyout SubmenuFlyout(ContextMenu menu, MenuItem built)
    {
        var flyout = new MenuFlyout();
        List<object?> moved = [.. built.Items];
        built.Items.Clear();
        foreach (object? child in moved)
        {
            flyout.Items.Add(child);
            CloseOnChoice(child, flyout, menu);
        }

        return flyout;
    }

    private static void CloseOnChoice(object? item, MenuFlyout flyout, ContextMenu menu)
    {
        if (item is not MenuItem menuItem)
        {
            return;
        }

        if (menuItem.Items.Count == 0)
        {
            menuItem.Click += (_, _) =>
            {
                flyout.Hide();
                menu.Close();
            };
            return;
        }

        foreach (object? child in menuItem.Items)
        {
            CloseOnChoice(child, flyout, menu);
        }
    }

    /// <summary>The corner notch that marks a button opening a submenu.</summary>
    private static Path Notch() =>
        new()
        {
            Data = Geometry.Parse(NotchPath),
            Fill = new SolidColorBrush(NotchColor),
            Width = 6,
            Height = 6,
            Margin = new Thickness(0, 0, 3, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
}
