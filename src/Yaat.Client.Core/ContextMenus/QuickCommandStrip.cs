using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Path = Avalonia.Controls.Shapes.Path;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// A submenu's items that follow live data on whichever surface shows them, registered with
/// <see cref="QuickCommandStrip.FollowInFlyout"/>: told when a strip button's flyout showing them opens, with the flyout's
/// own item list to keep current and the action that closes the flyout and the menu, when the pointer enters and leaves
/// it, and when it closes.
/// </summary>
public interface IStripFlyoutContent
{
    /// <summary>The flyout showing the items has opened.</summary>
    /// <param name="items">The flyout's item list, which now holds the items.</param>
    /// <param name="close">Closes the flyout and the menu, for a choice made where the strip wires no close of its own.</param>
    void Opened(ItemCollection items, Action close);

    /// <summary>The pointer has entered the open flyout (<paramref name="inside"/> true) or left it (false).</summary>
    /// <param name="inside">Whether the pointer is now over the flyout.</param>
    void PointerInside(bool inside);

    /// <summary>The flyout showing the items has closed.</summary>
    void Closed();
}

/// <summary>
/// The quick-command icon strip at the top of an aircraft menu: one menu item whose header holds up to two rows of five
/// glyph buttons, the first row filled first and an empty second row hidden. Each button stands for its catalog entry's
/// own menu item, built through the entry's builder: a sending or prompting item is clicked through that item's own Click
/// handler, and a submenu opens its items as a flyout beside the menu (the button shows a corner notch), which stays open,
/// its button latched, until that button is clicked again, another icon's flyout replaces it, another row's submenu opens,
/// Escape closes it or the menu closes. Either way the menu closes once a command is chosen. While the pointer is on a
/// button, or keyboard focus is, the header's state-line row (<see cref="QuickCommandLabelLine"/>) names the entry and, for
/// an item that sends one fixed command, that command (<see cref="MenuCommandText"/>); the state line comes back when the
/// pointer leaves the strip and no button holds focus. Plain controls only, so it works on every view, the aircraft list
/// included.
/// </summary>
public static class QuickCommandStrip
{
    /// <summary>The style class the strip's menu item carries, by which <see cref="IsStrip"/> knows it.</summary>
    public const string StripClass = "quick-command-strip";

    /// <summary>The style class a submenu button carries while its flyout is open.</summary>
    public const string StickyClass = "sticky";

    /// <summary>The style class every strip button carries, which the strip's well styles select.</summary>
    private const string ButtonClass = "strip-button";

    /// <summary>How many buttons a row holds.</summary>
    public const int RowLength = 5;

    /// <summary>The resource key of the monospace font a command text is drawn in.</summary>
    internal const string MonoFontKey = "MonoFont";

    private const double CellSize = 40;
    private const double GlyphSize = 22;
    private const double CellGlyphSize = 32;
    private const double GlyphViewBox = 24;
    private const double GlyphStrokeThickness = 1.9;
    private const double Gap = 6;
    private const string NotchPath = "M6 0V6H0z";
    private const string ButtonPresenterPart = "PART_ContentPresenter";
    private static readonly Color NotchColor = Color.Parse("#9AA3AD");
    private static readonly Color WellBackground = Color.Parse("#1F2226");
    private static readonly Color WellPointerOverBackground = Color.Parse("#2C3036");
    private static readonly Color WellPressedBackground = Color.Parse("#16181B");
    private static readonly Color StickyBackground = Color.Parse("#24406A");

    /// <summary>The dark well a strip button's glyph sits in at rest; the Settings preview draws its cells on it too.</summary>
    public static IImmutableSolidColorBrush WellBrush { get; } = new ImmutableSolidColorBrush(WellBackground);
    private static readonly ILogger Log = AppLog.CreateLogger("QuickCommandStrip");
    private static readonly ConditionalWeakTable<MenuItem, IStripFlyoutContent> FlyoutContents = [];
    private static readonly ConditionalWeakTable<MenuItem, string> InertLabels = [];

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
    /// nothing here holds it to <see cref="QuickCommandGlyphs.StripCapacity"/>. A button's label shows in the menu's
    /// header line (<see cref="QuickCommandLabelLine.Ensure"/>), which a menu without a state line gets, blank, under its
    /// title; a menu with no items, and so no title, shows no label.
    /// </summary>
    /// <param name="menu">The menu the strip sits in, which a chosen command closes.</param>
    /// <param name="built">Each button's entry and glyph, with the menu item it stands for.</param>
    public static MenuItem? FromBuilt(ContextMenu menu, IReadOnlyList<(QuickCommandStripItem Item, MenuItem Built)> built)
    {
        if (built.Count == 0)
        {
            return null;
        }

        var tracker = new StripLabelTracker(QuickCommandLabelLine.Ensure(menu));
        var strip = new MenuItem { StaysOpenOnClick = true };
        var flyouts = new StripFlyouts(menu, strip);
        List<Control> buttons = [.. built.Select(pair => BuildButton(menu, pair.Item, pair.Built, tracker, flyouts))];
        strip.Header = Rows(buttons);
        strip.Classes.Add(StripClass);
        strip.Styles.AddRange(ButtonStyles());
        strip.PointerExited += (_, _) => tracker.LeaveStrip();
        return strip;
    }

    /// <summary>
    /// Opens <paramref name="button"/>'s submenu flyout beside the menu exactly as a click on it does, unless it is open
    /// already.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="button"/> is not a strip button that opens a submenu.</exception>
    public static void OpenSubmenu(Button button)
    {
        if (FlyoutBase.GetAttachedFlyout(button) is not MenuFlyout flyout)
        {
            throw new ArgumentException($"The strip button '{button.Tag}' opens no submenu flyout.", nameof(button));
        }

        if (!flyout.IsOpen)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
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
    /// The square a strip button shows: <paramref name="glyph"/> centred, larger than an inline glyph so it fills most of
    /// the button, with the corner notch when the button opens a submenu. The menu's buttons and the Settings preview both
    /// draw their glyphs through it.
    /// </summary>
    public static Grid GlyphCell(QuickCommandGlyph glyph, bool opensSubmenu)
    {
        var cell = new Grid { Width = CellSize, Height = CellSize };
        Viewbox icon = GlyphIcon(glyph);
        icon.Width = CellGlyphSize;
        icon.Height = CellGlyphSize;
        cell.Children.Add(icon);
        if (opensSubmenu)
        {
            cell.Children.Add(Notch());
        }

        return cell;
    }

    /// <summary>
    /// The glyph's stroke path on its 24 px view box, scaled to the inline glyph size and drawn in its family's colour,
    /// mirrored and rotated about the box's centre as the glyph says (<see cref="GlyphTransform"/>). A rotated glyph's
    /// corners reach a little past the box; nothing clips them.
    /// </summary>
    public static Viewbox GlyphIcon(QuickCommandGlyph glyph) =>
        new()
        {
            Width = GlyphSize,
            Height = GlyphSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas
            {
                Width = GlyphViewBox,
                Height = GlyphViewBox,
                RenderTransformOrigin = RelativePoint.TopLeft,
                RenderTransform = GlyphTransform(glyph),
                Children =
                {
                    new Path
                    {
                        Data = Geometry.Parse(glyph.PathData),
                        Stroke = new SolidColorBrush(FamilyColor(glyph.Family)),
                        StrokeThickness = GlyphStrokeThickness,
                        StrokeLineCap = PenLineCap.Round,
                        StrokeJoin = PenLineJoin.Round,
                    },
                },
            },
        };

    /// <summary>
    /// How <paramref name="glyph"/>'s path is turned on its view box, with the transform origin at the box's top left: one
    /// rotation about the centre, preceded for a mirrored glyph by the top-to-bottom mirror about the centre line
    /// (<c>y → 24 − y</c>).
    /// </summary>
    private static Transform GlyphTransform(QuickCommandGlyph glyph)
    {
        const double centre = GlyphViewBox / 2;
        var rotation = new RotateTransform(glyph.RotationDegrees, centre, centre);
        if (!glyph.MirroredTopToBottom)
        {
            return rotation;
        }

        var mirror = new MatrixTransform(new Matrix(1, 0, 0, -1, 0, GlyphViewBox));
        return new TransformGroup { Children = { mirror, rotation } };
    }

    /// <summary>Whether <paramref name="item"/> is a quick-command strip.</summary>
    public static bool IsStrip(MenuItem item) => item.Classes.Contains(StripClass);

    /// <summary>The strip's buttons, row one then row two; each button's <see cref="Control.Tag"/> is its catalog entry's id.</summary>
    public static IReadOnlyList<Button> Buttons(MenuItem strip) =>
        (strip.Header is StackPanel rows) ? [.. rows.Children.OfType<StackPanel>().SelectMany(row => row.Children.OfType<Button>())] : [];

    /// <summary>The colour a glyph of <paramref name="family"/> is drawn in.</summary>
    public static Color FamilyColor(QuickCommandGlyphFamily family) =>
        family switch
        {
            QuickCommandGlyphFamily.Tower => Color.Parse("#FFC062"),
            QuickCommandGlyphFamily.Ground => Color.Parse("#6EE0CE"),
            QuickCommandGlyphFamily.Flight => Color.Parse("#A8CAFF"),
            QuickCommandGlyphFamily.Pattern => Color.Parse("#DDBDFF"),
            QuickCommandGlyphFamily.ScopeAndSim => Color.Parse("#DCE2E8"),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown quick-command glyph family."),
        };

    private static string EntryLabel(MenuCatalogEntry entry, MenuItem built) => built.Header as string ?? entry.Label;

    /// <summary>The one command <paramref name="built"/> sends, or null for a submenu or an item with no fixed command.</summary>
    private static string? FixedCommand(MenuItem built) =>
        ((built.Items.Count == 0) && (MenuCommandText.GetCommand(built) is { Length: > 0 } command)) ? command : null;

    /// <summary>
    /// The label <paramref name="built"/>'s button shows in the header line: its header and the command it sends
    /// (<c>Hold position — HOLD</c>), the header and <c> ›</c> for a submenu, or the header alone for an item that sends
    /// no fixed command.
    /// </summary>
    private static StripLabelText LabelText(MenuCatalogEntry entry, MenuItem built) =>
        (built.Items.Count > 0) ? new($"{EntryLabel(entry, built)} ›", null) : new(EntryLabel(entry, built), FixedCommand(built));

    private static Button BuildButton(ContextMenu menu, QuickCommandStripItem item, MenuItem built, StripLabelTracker tracker, StripFlyouts flyouts)
    {
        InertLabels.TryGetValue(built, out string? inertLabel);
        bool opensSubmenu = (inertLabel is null) && (built.Items.Count > 0);
        StripLabelText text = (inertLabel is null) ? LabelText(item.Entry, built) : new(inertLabel, null);
        var button = new Button
        {
            Content = GlyphCell(item.Glyph, opensSubmenu),
            Padding = new Thickness(0),
            Tag = item.Entry.Id,
            Classes = { ButtonClass },
        };
        button.PointerEntered += (_, _) => tracker.Point(text);
        button.GotFocus += (_, _) => tracker.Focus(button, text);
        button.LostFocus += (_, _) => tracker.Blur(button);
        if (inertLabel is not null)
        {
            button.Opacity = MenuGlyphRowTemplate.DimmedOpacity;
        }
        else if (opensSubmenu)
        {
            flyouts.Attach(button, SubmenuFlyout(menu, built));
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
    /// depth, closes the flyout and the menu after the item's own handler has run; a click on an item that stays open on
    /// click, or one its own handler marks handled, chooses nothing and leaves both open. A lazily built entry
    /// (<see cref="LazySubmenu"/>) fills the flyout when it first opens, as it fills its own submenu. It opens on the
    /// menu's right edge, or its left edge where the right would run off screen, as a submenu does, and does not close on
    /// a click outside it: <see cref="StripFlyouts"/> closes it, so a click in the parent menu reaches the menu.
    /// </summary>
    private static MenuFlyout SubmenuFlyout(ContextMenu menu, MenuItem built)
    {
        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.RightEdgeAlignedTop,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipX | PopupPositionerConstraintAdjustment.SlideY,
        };
        flyout.Popup.IsLightDismissEnabled = false;
        List<object?> moved = [.. built.Items];
        built.Items.Clear();
        foreach (object? child in moved)
        {
            flyout.Items.Add(child);
            CloseOnChoice(child, flyout, menu);
        }

        flyout.Opened += (_, _) => CloseOnChoices(LazySubmenu.Fill(built, flyout.Items), flyout, menu);
        if (FlyoutContents.TryGetValue(built, out IStripFlyoutContent? content))
        {
            Tell(content, flyout, menu);
        }

        return flyout;
    }

    /// <summary>
    /// Tells <paramref name="content"/> when <paramref name="flyout"/> opens and closes, and when the pointer enters and
    /// leaves the flyout's popup, watched from the first opening on, when the popup has its presenter.
    /// </summary>
    private static void Tell(IStripFlyoutContent content, MenuFlyout flyout, ContextMenu menu)
    {
        Control? watched = null;
        flyout.Opened += (_, _) =>
        {
            content.Opened(
                flyout.Items,
                () =>
                {
                    flyout.Hide();
                    menu.Close();
                }
            );
            if ((flyout.Popup.Child is Control root) && !ReferenceEquals(root, watched))
            {
                watched = root;
                root.PointerEntered += (_, _) => content.PointerInside(true);
                root.PointerExited += (_, _) => content.PointerInside(false);
            }
        };
        flyout.Closed += (_, _) => content.Closed();
    }

    /// <summary>
    /// Has the flyout a strip button opens over <paramref name="entry"/>'s items tell <paramref name="content"/> when it
    /// opens and closes, so items that follow live data keep following it once the strip has moved them into the flyout,
    /// where <see cref="MenuItem.IsSubMenuOpen"/> never changes.
    /// </summary>
    /// <param name="entry">The submenu a catalog entry built.</param>
    /// <param name="content">What keeps the entry's items current.</param>
    public static void FollowInFlyout(MenuItem entry, IStripFlyoutContent content) => FlyoutContents.AddOrUpdate(entry, content);

    /// <summary>
    /// Has the strip button standing for <paramref name="entry"/> stay in place but faded, opening and sending nothing,
    /// its label in the header line reading <paramref name="label"/> (<c>Follow · no other aircraft on the ground</c>).
    /// </summary>
    /// <param name="entry">The item a catalog entry built that has nothing to offer now.</param>
    /// <param name="label">The label naming the entry and why it offers nothing.</param>
    public static void MarkInert(MenuItem entry, string label) => InertLabels.AddOrUpdate(entry, label);

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
            // An item that stays open on its click (a row whose box is not filled yet), or whose own handler stopped the
            // click, has chosen nothing.
            menuItem.Click += (_, e) =>
            {
                if (e.Handled || menuItem.StaysOpenOnClick)
                {
                    return;
                }

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
    /// A strip button's look, set on the button template's content presenter: a dark well at rest, a lighter one under the
    /// pointer and a darker one while pressed, so the glyph keeps its contrast in every state; and, added last so it wins,
    /// the latched look while its flyout is open: a blue background in every one of those states, so hovering or pressing
    /// the latched button still shows it latched.
    /// </summary>
    private static Style[] ButtonStyles() =>
        [
            PresenterStyle(x => StripButton(x), WellBackground),
            PresenterStyle(x => StripButton(x).Class(":pointerover"), WellPointerOverBackground),
            PresenterStyle(x => StripButton(x).Class(":pressed"), WellPressedBackground),
            new(x =>
                Selectors.Or(
                    Presenter(StripButton(x).Class(StickyClass)),
                    Presenter(StripButton(x).Class(StickyClass).Class(":pointerover")),
                    Presenter(StripButton(x).Class(StickyClass).Class(":pressed"))
                )
            )
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(StickyBackground)) },
            },
        ];

    private static Style PresenterStyle(Func<Selector?, Selector> button, Color background) =>
        new(x => Presenter(button(x))) { Setters = { new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(background)) } };

    private static Selector StripButton(Selector? x) => x.OfType<Button>().Class(ButtonClass);

    private static Selector Presenter(Selector button) => button.Template().OfType<ContentPresenter>().Name(ButtonPresenterPart);

    /// <summary>
    /// The strip's submenu flyouts, of which at most one is open. A click on a button opens its flyout beside the menu,
    /// its top edge on the strip's row, replacing any other, or closes it when it is the open one; the open flyout also
    /// closes when a parent-menu row opens its own submenu or the menu closes. Its button carries <see cref="StickyClass"/>
    /// while it is open. When a flyout holding keyboard focus closes by itself (Escape) with the menu still open, focus
    /// goes back to its button, so a second Escape closes the menu; a flyout this class hides leaves focus where it is, on
    /// the row whose submenu just opened or the button just clicked.
    /// </summary>
    private sealed class StripFlyouts
    {
        private readonly ContextMenu _menu;
        private readonly MenuItem _strip;
        private MenuFlyout? _open;
        private bool _hiding;

        public StripFlyouts(ContextMenu menu, MenuItem strip)
        {
            _menu = menu;
            _strip = strip;
            menu.AddHandler(MenuItem.SubmenuOpenedEvent, OnSubmenuOpened);
            menu.Closed += (_, _) => HideOpen();
        }

        public void Attach(Button button, MenuFlyout flyout)
        {
            FlyoutBase.SetAttachedFlyout(button, flyout);
            button.Click += (_, _) => Toggle(flyout);
            flyout.Opened += (_, _) => button.Classes.Add(StickyClass);
            bool hadFocus = false;
            flyout.Closing += (_, _) => hadFocus = (!_hiding) && (flyout.Popup.Child?.IsKeyboardFocusWithin == true);
            flyout.Closed += (_, _) =>
            {
                button.Classes.Remove(StickyClass);
                bool refocus = hadFocus && _menu.IsOpen;
                hadFocus = false;
                if (refocus)
                {
                    button.Focus();
                }
            };
        }

        private void Toggle(MenuFlyout flyout)
        {
            if (flyout.IsOpen)
            {
                Hide(flyout);
                return;
            }

            HideOpen();
            flyout.VerticalOffset = StripTop();
            flyout.ShowAt(_menu);
            _open = flyout;
        }

        /// <summary>
        /// The strip's top within the menu; before the menu is laid out (a <see cref="OpenSubmenu"/> call too early) the
        /// strip has no place in it yet, and the flyout's top meets the menu's.
        /// </summary>
        private double StripTop()
        {
            if (_strip.TranslatePoint(default, _menu) is { } top)
            {
                return top.Y;
            }

            Log.LogWarning(
                "Quick-command strip flyout opened before the menu was laid out: the strip has no place in the menu yet, "
                    + "so the flyout's top meets the menu's top"
            );
            return 0;
        }

        private void HideOpen()
        {
            if (_open is { } open)
            {
                Hide(open);
            }

            _open = null;
        }

        private void Hide(MenuFlyout flyout)
        {
            _hiding = true;
            try
            {
                flyout.Hide();
            }
            finally
            {
                _hiding = false;
            }
        }

        private void OnSubmenuOpened(object? sender, RoutedEventArgs e)
        {
            if ((e.Source is MenuItem row) && IsParentMenuRow(row))
            {
                HideOpen();
            }
        }

        /// <summary>Whether <paramref name="row"/> is a row of the menu or of one of its submenus, outside the strip and its flyouts.</summary>
        private bool IsParentMenuRow(MenuItem row)
        {
            StyledElement? current = row;
            while (current is MenuItem item)
            {
                if (ReferenceEquals(item, _strip))
                {
                    return false;
                }

                current = item.Parent;
            }

            return ReferenceEquals(current, _menu);
        }
    }

    /// <summary>
    /// Which button's label the header line shows: the button under the pointer, else the one holding keyboard focus,
    /// else none, and the line shows its rest text. The pointer counts as on a button from the moment it enters one until
    /// it leaves the strip, so crossing the gap between two buttons keeps the label up.
    /// </summary>
    private sealed class StripLabelTracker(QuickCommandLabelLine? line)
    {
        private StripLabelText? _pointed;
        private (Button Button, StripLabelText Text)? _focused;

        public void Point(StripLabelText text)
        {
            _pointed = text;
            Show();
        }

        public void LeaveStrip()
        {
            _pointed = null;
            Show();
        }

        public void Focus(Button button, StripLabelText text)
        {
            _focused = (button, text);
            Show();
        }

        public void Blur(Button button)
        {
            if ((_focused is { } focused) && ReferenceEquals(focused.Button, button))
            {
                _focused = null;
                Show();
            }
        }

        private void Show()
        {
            if ((_pointed ?? _focused?.Text) is { } text)
            {
                line?.ShowLabel(text.Name, text.Command);
            }
            else
            {
                line?.ShowRest();
            }
        }
    }

    /// <summary>What a button's label reads: the entry's name, and the command it sends when it sends one fixed command.</summary>
    private sealed record StripLabelText(string Name, string? Command);

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

/// <summary>
/// The header line under a menu's title that the quick-command strip writes its label into: a disabled, dimmed row
/// whose header is its rest text (the aircraft's state line, or empty for the blank line a menu without one keeps) while
/// no strip button is pointed at or focused, and the button's label while one is. The label is the entry's name, then
/// <c> — </c> and the command in the monospace font when it sends one fixed command. It takes the rest text's place in
/// the same row and is sized by the rest text, so the menu keeps its width and height as the label comes and goes; a
/// label wider than the row waits 800 ms, scrolls left at 60 px/s until its end is in view, waits 800 ms, scrolls back
/// and repeats, starting again from the left when the label changes. A label that fits does not move.
/// </summary>
public sealed class QuickCommandLabelLine : MenuItem
{
    private const double RestOpacity = 0.8;
    private const double StateLineFontSize = 11;

    private readonly string _rest;
    private readonly Marquee _marquee;

    /// <summary>A header line showing <paramref name="rest"/> at rest.</summary>
    /// <param name="rest">The state line's text, or empty for a blank line.</param>
    public QuickCommandLabelLine(string rest)
    {
        _rest = rest;
        _marquee = new Marquee(rest, this);
        Header = rest;
        IsEnabled = false;
        FontSize = StateLineFontSize;
        Opacity = RestOpacity;
    }

    protected override Type StyleKeyOverride => typeof(MenuItem);

    /// <summary>The clock the marquee's pauses and scrolling run on; the system clock unless a test sets its own.</summary>
    public TimeProvider MarqueeClock
    {
        get => _marquee.Clock;
        set => _marquee.Clock = value;
    }

    /// <summary>The label the line shows (<c>Hold position — HOLD</c>, <c>Follow ›</c>), or null while it shows its rest text.</summary>
    public string? Label { get; private set; }

    /// <summary>How far, in px, the label has scrolled left; 0 at rest, while it waits, and for a label that fits.</summary>
    public double MarqueeOffset => _marquee.Offset;

    /// <summary>The header line of <paramref name="menu"/>, or null when it has none.</summary>
    public static QuickCommandLabelLine? In(ContextMenu menu) => menu.Items.OfType<QuickCommandLabelLine>().FirstOrDefault();

    /// <summary>
    /// The header line of <paramref name="menu"/>: its state line, else a blank line put in under its title (the first
    /// item), so a menu without a state line still has a row for the label. Null for a menu with no items.
    /// </summary>
    public static QuickCommandLabelLine? Ensure(ContextMenu menu)
    {
        if (In(menu) is { } line)
        {
            return line;
        }

        if (menu.Items.Count == 0)
        {
            return null;
        }

        var blank = new QuickCommandLabelLine("");
        menu.Items.Insert(1, blank);
        return blank;
    }

    /// <summary>Shows <paramref name="name"/> and, when there is one, <paramref name="command"/> in place of the rest text.</summary>
    internal void ShowLabel(string name, string? command)
    {
        string label = (command is null) ? name : $"{name} — {command}";
        if (label == Label)
        {
            return;
        }

        Label = label;
        _marquee.Start(name, command);
        Header = _marquee;
        Opacity = 1;
    }

    /// <summary>Puts the rest text back.</summary>
    internal void ShowRest()
    {
        if (Label is null)
        {
            return;
        }

        Label = null;
        _marquee.Stop();
        Header = _rest;
        Opacity = RestOpacity;
    }

    /// <summary>
    /// How far a label <paramref name="overflow"/> px wider than its line has scrolled <paramref name="elapsed"/> after it
    /// was shown: still for the pause, then left at the scroll speed to <paramref name="overflow"/>, still for the pause,
    /// back at the same speed to 0, and round again. Always 0 for a label that fits.
    /// </summary>
    internal static double MarqueeOffsetAt(TimeSpan elapsed, double overflow)
    {
        const double pauseSeconds = 0.8;
        const double pixelsPerSecond = 60;
        if (overflow <= 0)
        {
            return 0;
        }

        double travel = overflow / pixelsPerSecond;
        double t = elapsed.TotalSeconds % (2 * (pauseSeconds + travel));
        if (t < pauseSeconds)
        {
            return 0;
        }

        t -= pauseSeconds;
        if (t < travel)
        {
            return t * pixelsPerSecond;
        }

        t -= travel + pauseSeconds;
        return (t < 0) ? overflow : overflow - (t * pixelsPerSecond);
    }

    /// <summary>
    /// The label's view: sized by an invisible copy of the rest text, the label drawn on one line over it, centred, shifted
    /// left by the marquee offset and clipped at the line's left and right edges. While it shows on screen, a 60 Hz timer
    /// on the clock moves it; the timer stops once the laid-out label fits, when the label goes and when the menu closes.
    /// </summary>
    private sealed class Marquee : Control
    {
        private const double NameFontSize = 12;
        private const double CommandFontSize = 11;
        private static readonly IImmutableSolidColorBrush NameBrush = new ImmutableSolidColorBrush(Color.Parse("#EDEDED"));
        private static readonly IImmutableSolidColorBrush DashBrush = new ImmutableSolidColorBrush(Color.Parse("#9A9A9A"));
        private static readonly IImmutableSolidColorBrush CommandBrush = new ImmutableSolidColorBrush(Color.Parse("#B8B8B8"));

        private readonly TextBlock _sizer;
        private readonly TextBlock _text = new() { TextWrapping = TextWrapping.NoWrap };
        private readonly Run _name = new() { FontSize = NameFontSize, Foreground = NameBrush };
        private readonly Run _dash = new() { FontSize = NameFontSize, Foreground = DashBrush };
        private readonly Run _command = new() { FontSize = CommandFontSize, Foreground = CommandBrush };
        private static readonly TimeSpan FramePeriod = TimeSpan.FromSeconds(1.0 / 60);

        private readonly TranslateTransform _shift = new();
        private long _start;
        private bool _running;
        private bool _attached;
        private ITimer? _frames;

        public Marquee(string rest, MenuItem line)
        {
            _sizer = new TextBlock
            {
                Text = rest,
                Opacity = 0,
                IsHitTestVisible = false,
            };
            // The rest text is drawn in the line's own font, which a theme's TextBlock style would otherwise override here;
            // set locally, it wins, so the copy of the rest text measures as the rest text does.
            foreach (TextBlock block in (TextBlock[])[_sizer, _text])
            {
                block.Bind(TextBlock.FontSizeProperty, line.GetObservable(TemplatedControl.FontSizeProperty));
                block.Bind(TextBlock.FontFamilyProperty, line.GetObservable(TemplatedControl.FontFamilyProperty));
            }

            _text.Inlines = [_name, _dash, _command];
            _text.RenderTransform = _shift;
            _command.Bind(TextElement.FontFamilyProperty, _text.GetResourceObservable(QuickCommandStrip.MonoFontKey));
            VisualChildren.Add(_sizer);
            VisualChildren.Add(_text);
            LogicalChildren.Add(_sizer);
            LogicalChildren.Add(_text);
        }

        public TimeProvider Clock { get; set; } = TimeProvider.System;

        public double Offset { get; private set; }

        public void Start(string name, string? command)
        {
            _name.Text = name;
            _dash.Text = (command is null) ? "" : " — ";
            _command.Text = command ?? "";
            _start = Clock.GetTimestamp();
            _running = true;
            SetOffset(0);
            StopFrames();
            StartFrames();
        }

        public void Stop()
        {
            _running = false;
            StopFrames();
            SetOffset(0);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _sizer.Measure(availableSize);
            _text.Measure(Size.Infinity);
            return _sizer.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _sizer.Arrange(new Rect(finalSize));
            Size text = _text.DesiredSize;
            _text.Arrange(new Rect(0, (finalSize.Height - text.Height) / 2, text.Width, text.Height));
            Clip = new RectangleGeometry(new Rect(0, -finalSize.Height, finalSize.Width, 3 * finalSize.Height));
            StartFrames();
            return finalSize;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            StartFrames();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _attached = false;
            StopFrames();
        }

        /// <summary>
        /// Starts the frame timer on <see cref="Clock"/> while a label shows on screen and none runs: each tick moves the
        /// label on the UI thread.
        /// </summary>
        private void StartFrames()
        {
            if (_running && _attached && (_frames is null))
            {
                _frames = Clock.CreateTimer(_ => Dispatcher.UIThread.Post(OnFrame), null, TimeSpan.Zero, FramePeriod);
            }
        }

        private void StopFrames()
        {
            _frames?.Dispose();
            _frames = null;
        }

        /// <summary>Moves the label to where the clock says it is; once laid out, a label that fits stops the frames.</summary>
        private void OnFrame()
        {
            if (!_running)
            {
                return;
            }

            double overflow = Math.Max(0, _text.DesiredSize.Width - Bounds.Width);
            if ((overflow <= 0) && (Bounds.Width > 0))
            {
                StopFrames();
            }

            SetOffset(MarqueeOffsetAt(Clock.GetElapsedTime(_start), overflow));
        }

        private void SetOffset(double offset)
        {
            Offset = offset;
            _shift.X = -offset;
        }
    }
}
