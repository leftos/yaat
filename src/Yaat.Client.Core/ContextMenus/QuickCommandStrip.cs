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
/// command (<see cref="MenuCommandText"/>); a label row above the buttons says the same of the button under the pointer or
/// keyboard focus, and prompts while the pointer is off the strip. Plain controls only, so it works on every view, the
/// aircraft list included.
/// </summary>
public static class QuickCommandStrip
{
    /// <summary>The style class the strip's menu item carries, by which <see cref="IsStrip"/> knows it.</summary>
    public const string StripClass = "quick-command-strip";

    /// <summary>How many buttons a row holds.</summary>
    public const int RowLength = 5;

    /// <summary>The resource key of the monospace font a command text is drawn in.</summary>
    internal const string MonoFontKey = "MonoFont";

    private const double CellSize = 40;
    private const double GlyphSize = 22;
    private const double Gap = 6;
    private const string NotchPath = "M6 0V6H0z";
    private const string PromptTitle = "Quick commands";
    private const string PromptDetail = "point at an icon";
    private const string SubmenuDetail = "opens a submenu";
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
        List<(QuickCommandStripItem Item, MenuItem Built)> built = [];
        foreach (QuickCommandStripItem item in items)
        {
            if (item.Entry.Build(aircraft, context, host) is { } menuItem)
            {
                built.Add((item, menuItem));
            }
        }

        return FromBuilt(menu, built);
    }

    /// <summary>
    /// The strip over menu items already built, one button each, in order, or null when there are none. A button stands
    /// for its item exactly as in <see cref="Build"/>; its tag is the strip item's catalog entry id, so two items of one
    /// entry (a Taxi to runway per runway end) share it. Each item must be in no other menu. The caller bounds the count:
    /// nothing here holds it to <see cref="QuickCommandGlyphs.StripCapacity"/>.
    /// </summary>
    /// <param name="menu">The menu the strip sits in, which a chosen command closes.</param>
    /// <param name="built">Each button's entry and glyph, with the menu item it stands for.</param>
    public static MenuItem? FromBuilt(ContextMenu menu, IReadOnlyList<(QuickCommandStripItem Item, MenuItem Built)> built)
    {
        var label = new StripLabel();
        List<Control> buttons = [.. built.Select(pair => BuildButton(menu, pair.Item, pair.Built, label))];
        if (buttons.Count == 0)
        {
            return null;
        }

        var strip = new MenuItem
        {
            Header = new StackPanel { Spacing = Gap, Children = { label.Panel, Rows(buttons) } },
            StaysOpenOnClick = true,
        };
        strip.Classes.Add(StripClass);
        strip.PointerExited += (_, _) => label.ShowPrompt();
        return strip;
    }

    /// <summary>
    /// What the strip's label row reads now: the title (the entry under the pointer or focus, or the prompt) and the
    /// detail under it (the command it sends, that it opens a submenu, the prompt's hint, or nothing).
    /// </summary>
    public static (string Title, string Detail) Label(MenuItem strip) =>
        (strip.Header is StackPanel { Children: [StackPanel { Children: [TextBlock title, TextBlock detail] }, ..] })
            ? (title.Text ?? "", detail.Text ?? "")
            : ("", "");

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
        (strip.Header is StackPanel { Children: [_, StackPanel rows] })
            ? [.. rows.Children.OfType<StackPanel>().SelectMany(row => row.Children.OfType<Button>())]
            : [];

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
        string label = EntryLabel(entry, built);
        return (FixedCommand(built) is { } command) ? $"{label} — {command}" : label;
    }

    private static string EntryLabel(MenuCatalogEntry entry, MenuItem built) => built.Header as string ?? entry.Label;

    /// <summary>The one command <paramref name="built"/> sends, or null for a submenu or an item with no fixed command.</summary>
    private static string? FixedCommand(MenuItem built) =>
        ((built.Items.Count == 0) && (MenuCommandText.GetCommand(built) is { Length: > 0 } command)) ? command : null;

    private static Button BuildButton(ContextMenu menu, QuickCommandStripItem item, MenuItem built, StripLabel label)
    {
        bool opensSubmenu = built.Items.Count > 0;
        string title = opensSubmenu ? $"{EntryLabel(item.Entry, built)} ›" : EntryLabel(item.Entry, built);
        string detail = opensSubmenu ? SubmenuDetail : FixedCommand(built) ?? "";
        var button = new Button
        {
            Content = GlyphCell(item.Glyph, opensSubmenu),
            Padding = new Thickness(0),
            Tag = item.Entry.Id,
        };
        ToolTip.SetTip(button, Tooltip(item.Entry, built));
        button.PointerEntered += (_, _) => label.Show(title, detail);
        button.GotFocus += (_, _) => label.Show(title, detail);
        if (opensSubmenu)
        {
            AttachSubmenuFlyout(button, SubmenuFlyout(menu, built));
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
    /// Makes <paramref name="button"/> open <paramref name="flyout"/> on a click. While the flyout is open the button's
    /// tooltip is closed and switched off, so it never covers the flyout; it comes back when the flyout closes.
    /// </summary>
    private static void AttachSubmenuFlyout(Button button, MenuFlyout flyout)
    {
        FlyoutBase.SetAttachedFlyout(button, flyout);
        button.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(button);
        flyout.Opened += (_, _) =>
        {
            ToolTip.SetIsOpen(button, false);
            ToolTip.SetServiceEnabled(button, false);
        };
        flyout.Closed += (_, _) => ToolTip.SetServiceEnabled(button, true);
    }

    /// <summary>
    /// A flyout over <paramref name="built"/>'s own items, moved over unchanged; choosing any command in it, at any
    /// depth, closes the flyout and the menu after the item's own handler has run. A lazily built entry
    /// (<see cref="LazySubmenu"/>) fills the flyout when it first opens, as it fills its own submenu.
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

        flyout.Opened += (_, _) => CloseOnChoices(LazySubmenu.Fill(built, flyout.Items), flyout, menu);
        return flyout;
    }

    private static void CloseOnChoices(IReadOnlyList<Control> items, MenuFlyout flyout, ContextMenu menu)
    {
        foreach (Control item in items)
        {
            CloseOnChoice(item, flyout, menu);
        }
    }

    private static void CloseOnChoice(object? item, MenuFlyout flyout, ContextMenu menu)
    {
        if (item is not MenuItem menuItem)
        {
            return;
        }

        // A nested lazy submenu (Taxi to runway's Other runways) builds its items when it opens: wire those then.
        if (LazySubmenu.WhenFilled(menuItem, children => CloseOnChoices(children, flyout, menu)))
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

    /// <summary>
    /// The label row above the buttons: the entry under the pointer or keyboard focus on the first line, and on a dimmer
    /// monospace line under it the command it sends; the prompt while the pointer is off the strip. It is as wide as a
    /// full row of buttons: a longer title wraps onto a second line (and is trimmed only past it), a longer command is
    /// trimmed, and the row always keeps room for two title lines, so the menu keeps its size as the label changes.
    /// </summary>
    private sealed class StripLabel
    {
        private const double DimOpacity = 0.7;
        private const int TitleMaxLines = 2;
        private const double TitleLineHeight = 18;
        private const double DetailLineHeight = 16;

        private readonly TextBlock _title = new()
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            LineHeight = TitleLineHeight,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = TitleMaxLines,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        private readonly TextBlock _detail = new()
        {
            FontSize = 12,
            Opacity = DimOpacity,
            LineHeight = DetailLineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        public StripLabel()
        {
            _detail.Bind(TextBlock.FontFamilyProperty, _detail.GetResourceObservable(MonoFontKey));
            Panel = new StackPanel
            {
                Width = (RowLength * CellSize) + ((RowLength - 1) * Gap),
                MinHeight = (TitleMaxLines * TitleLineHeight) + DetailLineHeight,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _title, _detail },
            };
            ShowPrompt();
        }

        public StackPanel Panel { get; }

        public void Show(string title, string detail)
        {
            _title.Text = title;
            _title.Opacity = 1;
            _detail.Text = detail;
        }

        public void ShowPrompt()
        {
            _title.Text = PromptTitle;
            _title.Opacity = DimOpacity;
            _detail.Text = PromptDetail;
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
