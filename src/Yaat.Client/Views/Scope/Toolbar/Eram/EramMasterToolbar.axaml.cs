using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>
/// CRC's ERAM Master Toolbar: its move-down pick area down the left edge, then its 2x10 table of buttons (CRC's 2x9 plus
/// YAAT's tenth column) over the bar's grey across the full width of whatever holds it. The owner listens on
/// <see cref="Menu"/>, <see cref="Toggle"/>, <see cref="Command"/>, <see cref="IncDec"/> and <see cref="Wheel"/>, and
/// opens a menu with <see cref="OpenMenu"/> (a menu inside it with <see cref="OpenSubmenu"/>): the toolbar then expands that menu's buttons, from
/// <see cref="MenuSource"/>, as CRC's <c>MenuButton</c> does, right of its button inside CRC's 2 DIP BurntCoral outline,
/// and hides every other Master Toolbar button in place.
/// </summary>
public partial class EramMasterToolbar : UserControl
{
    private const string MoveDownGlyph = "▼";

    private const double ExpansionGap = 2;
    private const double ExpansionOutline = 2;

    /// <summary>The 2 in CRC's <c>2 - ToolbarHeight / 2</c>, how far a second-row button's two-row menu rises.</summary>
    private const double SecondRowRiseGap = 2;

    private readonly List<EramToolbarButton> _buttons = [];
    private readonly List<EramToolbarButton> _menuButtons = [];
    private readonly List<(Panel Parent, Canvas Anchor)> _anchors = [];
    private readonly double _pickAreaWidth;
    private EramButtonType? _openMenu;
    private EramButtonType? _openSubmenu;
    private Func<EramButtonType, IReadOnlyList<EramMenuButton>?> _menuSource = EramMenus.Fixed;

    /// <summary>The bar with its buttons in CRC's order.</summary>
    public EramMasterToolbar()
    {
        InitializeComponent();
        double charWidth = CharWidth(FontFamily, FontSize);
        Frame.Height = EramMasterLayout.BarHeight;
        Frame.Background = EramColors.Solid(EramColors.Toolbar);
        Frame.BorderBrush = EramColors.Solid(EramColors.Border);
        Padded.Padding = new Thickness(EramMasterLayout.HorizontalPadding, EramMasterLayout.VerticalPadding);
        BuildMoveDown(charWidth);
        Cells.RowSpacing = EramMasterLayout.RowGap;
        Cells.ColumnSpacing = EramMasterLayout.ColumnGap;
        AddDefinitions();
        _pickAreaWidth = PickAreaWidth(FontFamily, FontSize);
        AddButtons();
    }

    /// <summary>Raised when a menu button is pressed: its menu should open or close.</summary>
    public event EventHandler<EramButtonEventArgs>? Menu;

    /// <summary>Raised when a toggle button is pressed, carrying the state it asks to be put in.</summary>
    public event EventHandler<EramButtonEventArgs>? Toggle;

    /// <summary>Raised when a command button is pressed.</summary>
    public event EventHandler<EramButtonEventArgs>? Command;

    /// <summary>Raised when an increment/decrement button is pressed, carrying +1 for the middle button and -1 for the left.</summary>
    public event EventHandler<EramButtonEventArgs>? IncDec;

    /// <summary>Raised when the wheel turns over a live button, carrying +1 for a turn up and -1 for a turn down.</summary>
    public event EventHandler<EramButtonEventArgs>? Wheel;

    /// <summary>The bar's buttons, top row first, each row left to right.</summary>
    public IReadOnlyList<EramToolbarButton> Buttons => _buttons;

    /// <summary>The open menus' buttons: the open menu's in its table's order, then the open submenu's.</summary>
    public IReadOnlyList<EramToolbarButton> MenuButtons => _menuButtons;

    /// <summary>The open menu's expansion, its outline round its buttons; null while no menu is open.</summary>
    public Border? Expansion { get; private set; }

    /// <summary>The open submenu's expansion, its outline round its buttons; null while no submenu is open.</summary>
    public Border? SubmenuExpansion { get; private set; }

    /// <summary>
    /// The buttons a menu button's menu holds, or null for a button with none; <see cref="EramMenus.Fixed"/> until the
    /// owner gives the menus that follow its state.
    /// </summary>
    public Func<EramButtonType, IReadOnlyList<EramMenuButton>?> MenuSource
    {
        get => _menuSource;
        set
        {
            _menuSource = value;
            RefreshMenu();
        }
    }

    /// <summary>
    /// The Master Toolbar menu button whose menu is open, drawn in CRC's BurntCoral with its menu expanded and every other
    /// Master Toolbar button hidden in place; null when none is. Another menu closes the open submenu.
    /// </summary>
    public EramButtonType? OpenMenu
    {
        get => _openMenu;
        set
        {
            if (value != _openMenu)
            {
                _openSubmenu = null;
            }

            _openMenu = value;
            RefreshMenu();
        }
    }

    /// <summary>The menu button inside the open menu whose own menu is open (MAP BRIGHT inside BRIGHT); null when none is.</summary>
    public EramButtonType? OpenSubmenu
    {
        get => _openSubmenu;
        set
        {
            _openSubmenu = value;
            RefreshMenu();
        }
    }

    /// <summary>All the buttons shown: the bar's, then the open menus'.</summary>
    public IEnumerable<EramToolbarButton> AllButtons => _buttons.Concat(_menuButtons);

    /// <summary>The first shown button for <paramref name="type"/>, the bar's before the open menus', or null when none is shown.</summary>
    /// <param name="type">The button asked for.</param>
    public EramToolbarButton? Find(EramButtonType type) => AllButtons.FirstOrDefault(button => button.Type == type);

    /// <summary>Builds the open menus' expansions again from <see cref="MenuSource"/>, as their buttons change.</summary>
    public void RefreshMenu()
    {
        ClearExpansions();
        ApplyMenuStates(ExpandOpenMenus());
    }

    /// <summary>Expands the open menu beside its button and the open submenu beside its own; the open menu's button, or null.</summary>
    private EramToolbarButton? ExpandOpenMenus()
    {
        EramToolbarButton? owner = (_openMenu is { } menu) ? _buttons.Find(button => button.Type == menu) : null;
        Expansion = (owner is not null) ? Expand(owner, Cells, isSecondRow: (Grid.GetRow(owner) > 0)) : null;
        EramToolbarButton? subOwner = (_openSubmenu is { } sub) ? _menuButtons.Find(button => button.Type == sub) : null;
        SubmenuExpansion = (subOwner?.Parent is Panel parent) ? Expand(subOwner, parent, isSecondRow: (subOwner.CellRow > 0)) : null;
        return owner;
    }

    /// <summary>
    /// Draws the open menus' state: the outline on the innermost open expansion only, the open menu buttons in BurntCoral,
    /// and every Master Toolbar button but <paramref name="owner"/> hidden in place while it is open.
    /// </summary>
    private void ApplyMenuStates(EramToolbarButton? owner)
    {
        if ((Expansion is not null) && (SubmenuExpansion is not null))
        {
            Expansion.BorderBrush = Brushes.Transparent;
        }

        foreach (EramToolbarButton button in AllButtons)
        {
            button.IsOpenMenu = (button.Type == _openMenu) || (button.Type == _openSubmenu);
        }

        foreach (EramToolbarButton button in _buttons)
        {
            button.IsSuppressed = (owner is not null) && (button != owner);
        }
    }

    /// <summary>The width of one character of the toolbar font in DIP, which the pick areas' widths are measured from.</summary>
    private static double CharWidth(FontFamily fontFamily, double fontSize) =>
        new FormattedText(
            "0",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(fontFamily, FontStyle.Normal, FontWeight.Normal),
            fontSize,
            null
        ).WidthIncludingTrailingWhitespace;

    /// <summary>
    /// The width of a button's pick area in DIP, from the width of one character of <paramref name="font"/> at
    /// <paramref name="size"/>: the Master Toolbar's own, and the ERAM SHOW TOOLBAR tile's in the Scope host.
    /// </summary>
    internal static double PickAreaWidth(FontFamily font, double size) =>
        (EramMasterLayout.PickAreaChars * CharWidth(font, size)) + EramMasterLayout.PickAreaExtraWidth;

    private void BuildMoveDown(double charWidth)
    {
        MoveDown.Width = (EramMasterLayout.MoveDownChars * charWidth) + (2 * EramMasterLayout.MoveDownBorderWidth);
        MoveDown.Background = EramColors.Solid(EramColors.Toolbar);
        MoveDown.BorderBrush = EramColors.Solid(EramColors.Gray);
        MoveDown.BorderThickness = new Thickness(EramMasterLayout.MoveDownBorderWidth);
        MoveDown.Child = new TextBlock
        {
            Text = MoveDownGlyph,
            Foreground = EramColors.Solid(EramColors.Text),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
    }

    private void AddDefinitions()
    {
        for (int row = 0; row < EramMasterLayout.Rows; row++)
        {
            Cells.RowDefinitions.Add(new RowDefinition(new GridLength(EramMasterLayout.ButtonHeight)));
        }

        for (int column = 0; column < EramMasterLayout.Columns; column++)
        {
            Cells.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }
    }

    private void AddButtons()
    {
        foreach (EramButtonPlacement placement in EramMasterLayout.Placements)
        {
            EramToolbarButton button = NewButton(placement.Type, null, null, placement.Row);
            Grid.SetRow(button, placement.Row);
            Grid.SetColumn(button, placement.Column);
            Cells.Children.Add(button);
            _buttons.Add(button);
        }
    }

    private EramToolbarButton NewButton(EramButtonType type, (string Line1, string Line2)? lines, string? key, int cellRow)
    {
        var button = new EramToolbarButton(type, _pickAreaWidth, lines, key) { CellRow = cellRow };
        button.Invoked += OnButtonInvoked;
        button.Wheeled += OnButtonWheeled;
        return button;
    }

    private void ClearExpansions()
    {
        foreach ((Panel parent, Canvas anchor) in _anchors)
        {
            parent.Children.Remove(anchor);
        }

        foreach (EramToolbarButton button in _menuButtons)
        {
            button.Invoked -= OnButtonInvoked;
            button.Wheeled -= OnButtonWheeled;
        }

        _anchors.Clear();
        _menuButtons.Clear();
    }

    /// <summary>
    /// Expands <paramref name="owner"/>'s menu right of it, in a zero-width anchor beside it in <paramref name="parent"/>,
    /// as CRC's <c>MenuButton</c> does: one column per menu column in the order the menu first names it, each column's
    /// buttons top to bottom, a column with no top-row button set at the bottom, 2 DIP apart, inside a 2 DIP BurntCoral
    /// outline whose inside starts level with the button. A second-row button's menu that has a second row rises by
    /// CRC's <c>2 - ToolbarHeight / 2</c> so both its rows sit in the bar. Null when the button has no menu.
    /// </summary>
    private Border? Expand(EramToolbarButton owner, Panel parent, bool isSecondRow)
    {
        if (_menuSource(owner.Type) is not { Count: > 0 } items)
        {
            return null;
        }

        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = ExpansionGap };
        foreach (IGrouping<int, EramMenuButton> column in items.GroupBy(item => item.Column))
        {
            columns.Children.Add(BuildColumn(column));
        }

        var expansion = new Border
        {
            Background = EramColors.Solid(EramColors.Toolbar),
            BorderBrush = EramColors.Solid(EramColors.BurntCoral),
            BorderThickness = new Thickness(ExpansionOutline),
            Child = columns,
        };
        bool rises = isSecondRow && items.Any(item => item.Row > 0);
        Canvas.SetTop(expansion, -ExpansionOutline + (rises ? SecondRowRiseGap - (EramMasterLayout.BarHeight / 2) : 0));
        var anchor = new Canvas
        {
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            ZIndex = 1,
        };
        anchor.Children.Add(expansion);
        Grid.SetRow(anchor, Grid.GetRow(owner));
        Grid.SetColumn(anchor, Grid.GetColumn(owner));
        parent.Children.Add(anchor);
        _anchors.Add((parent, anchor));

        // A submenu's anchor sits in its button's column of the open menu; the menu's later columns would draw and take
        // the pointer over it, so its column rises above them. The menu is rebuilt on every change, so nothing resets.
        if ((parent != Cells) && (parent.Parent is Control ownerColumn))
        {
            ownerColumn.ZIndex = 1;
        }

        return expansion;
    }

    /// <summary>One expansion column: its buttons by row, each a menu button in a panel its own menu can anchor beside.</summary>
    private StackPanel BuildColumn(IEnumerable<EramMenuButton> column)
    {
        EramMenuButton[] cells = [.. column.OrderBy(item => item.Row)];
        var stack = new StackPanel { Spacing = ExpansionGap };
        if (!cells.Any(item => item.Row == 0))
        {
            stack.VerticalAlignment = VerticalAlignment.Bottom;
        }

        foreach (EramMenuButton item in cells)
        {
            EramToolbarButton button = NewButton(item.Type, item.Lines, item.Key, item.Row);
            _menuButtons.Add(button);
            stack.Children.Add((button.Kind == EramButtonKind.Menu) ? new Panel { Children = { button } } : button);
        }

        return stack;
    }

    private void OnButtonInvoked(object? sender, EramButtonEventArgs e) => HandlerFor(e.Type.Kind())?.Invoke(this, e);

    private void OnButtonWheeled(object? sender, EramButtonEventArgs e) => Wheel?.Invoke(this, e);

    private EventHandler<EramButtonEventArgs>? HandlerFor(EramButtonKind kind) =>
        kind switch
        {
            EramButtonKind.Menu => Menu,
            EramButtonKind.Toggle => Toggle,
            EramButtonKind.IncDec => IncDec,
            EramButtonKind.Command => Command,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown ERAM Master Toolbar button kind"),
        };
}
