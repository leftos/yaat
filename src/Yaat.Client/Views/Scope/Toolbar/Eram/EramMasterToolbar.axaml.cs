using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>
/// CRC's ERAM Master Toolbar: its move-down pick area down the left edge, then its 2x9 table of buttons over the bar's
/// grey across the full width of whatever holds it. The owner listens on <see cref="Menu"/>, <see cref="Toggle"/>,
/// <see cref="Command"/> and <see cref="IncDec"/>, and marks the open menu with <see cref="OpenMenu"/>.
/// </summary>
public partial class EramMasterToolbar : UserControl
{
    private const string MoveDownGlyph = "▼";

    private readonly List<EramToolbarButton> _buttons = [];
    private EramButtonType? _openMenu;

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
        AddButtons((EramMasterLayout.PickAreaChars * charWidth) + EramMasterLayout.PickAreaExtraWidth);
    }

    /// <summary>Raised when a menu button is pressed: its menu should open or close.</summary>
    public event EventHandler<EramButtonEventArgs>? Menu;

    /// <summary>Raised when a toggle button is pressed, carrying the state it asks to be put in.</summary>
    public event EventHandler<EramButtonEventArgs>? Toggle;

    /// <summary>Raised when a command button is pressed.</summary>
    public event EventHandler<EramButtonEventArgs>? Command;

    /// <summary>Raised when an increment/decrement button is pressed, carrying +1 for the middle button and -1 for the left.</summary>
    public event EventHandler<EramButtonEventArgs>? IncDec;

    /// <summary>The bar's buttons, top row first, each row left to right.</summary>
    public IReadOnlyList<EramToolbarButton> Buttons => _buttons;

    /// <summary>The menu button whose menu is open, drawn in CRC's BurntCoral; null when none is.</summary>
    public EramButtonType? OpenMenu
    {
        get => _openMenu;
        set
        {
            _openMenu = value;
            foreach (EramToolbarButton button in _buttons)
            {
                button.IsOpenMenu = (button.Type == value);
            }
        }
    }

    /// <summary>The button for <paramref name="type"/>, or null when the bar holds none.</summary>
    /// <param name="type">The button asked for.</param>
    public EramToolbarButton? Find(EramButtonType type) => _buttons.Find(button => button.Type == type);

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

    private void AddButtons(double pickAreaWidth)
    {
        foreach (EramButtonPlacement placement in EramMasterLayout.Placements)
        {
            var button = new EramToolbarButton(placement.Type, pickAreaWidth);
            button.Invoked += OnButtonInvoked;
            Grid.SetRow(button, placement.Row);
            Grid.SetColumn(button, placement.Column);
            Cells.Children.Add(button);
            _buttons.Add(button);
        }
    }

    private void OnButtonInvoked(object? sender, EramButtonEventArgs e) => HandlerFor(e.Type.Kind())?.Invoke(this, e);

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
