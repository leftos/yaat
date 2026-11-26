using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>
/// One Scope toolbar button, on the bar or in its tray, drawn as a tile: a full tile (the bar's
/// height) draws its label over its bold value, a half tile draws them on one line. PREF without an active set draws a
/// dim placeholder for the set's name, and its tile is always wide enough for a name of <see cref="ReservedNameChars"/>
/// characters, so loading or clearing a set moves nothing. A menu's corner mark lies over the tile at the top right. A
/// click presses the button (<see cref="ScopeViewModel.Press"/>) and the wheel steps a spinner.
/// </summary>
public sealed class ScopeButtonControl : Grid
{
    /// <summary>The height of a full tile: the bar's.</summary>
    private const double FullHeight = 45;

    /// <summary>The height of a half tile: two of them and a 1 DIP divider fill the bar.</summary>
    private const double HalfHeight = 22;

    /// <summary>How many characters of a pref set's name PREF's tile holds before it widens.</summary>
    private const int ReservedNameChars = 9;

    /// <summary>What PREF draws in place of the set's name while no set is active.</summary>
    private const string NoSetPlaceholder = "—";

    private static readonly IBrush PlaceholderBrush = new ImmutableSolidColorBrush(Color.Parse("#5f6b75"));

    private readonly TextBlock? _menuMark;
    private bool _isHalf;

    /// <summary>A control for <paramref name="model"/>.</summary>
    public ScopeButtonControl(ScopeButton model)
    {
        Model = model;
        Height = FullHeight;

        // The button is tagged with its id, so a lookup by id finds it.
        Button = new Button { Tag = model.Id, Classes = { "scopeButton" } };
        Button.Click += (_, _) => ViewModel?.Press(Model);
        if (model.Kind == ScopeButtonKind.Spinner)
        {
            Button.PointerWheelChanged += OnSpinnerWheel;
        }

        Children.Add(Button);
        if (model.Kind == ScopeButtonKind.Menu)
        {
            _menuMark = new TextBlock
            {
                Text = "▸",
                Classes = { "scopeMenuMark" },
                IsHitTestVisible = false,
            };
            Children.Add(_menuMark);
        }
    }

    /// <summary>
    /// Whether the control is a half tile (<see cref="HalfHeight"/>, its label and value on one line) rather than a full
    /// one (<see cref="FullHeight"/>, its label over its value). Set it before <see cref="Refresh"/>.
    /// </summary>
    public bool IsHalf
    {
        get => _isHalf;
        set
        {
            _isHalf = value;
            Height = value ? HalfHeight : FullHeight;
        }
    }

    /// <summary>The button the control stands for.</summary>
    public ScopeButton Model { get; }

    /// <summary>The button itself.</summary>
    public Button Button { get; }

    /// <summary>The Scope the button acts on; null draws the button's defaults and presses nothing.</summary>
    public ScopeViewModel? ViewModel { get; set; }

    /// <summary>
    /// Draws the button: its label, a spinner's bold value (PREF's dim placeholder without a set) and the active state.
    /// </summary>
    public void Refresh()
    {
        string? value = ViewModel?.SpinnerValue(Model);
        Button.Content = new TextBlock
        {
            Inlines = LinesOf(Model.Label, ValueRun(value), IsHalf),
            TextAlignment = TextAlignment.Center,
            LineHeight = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(Button, value is null ? Model.Label : $"{Model.Label} {value}");
        bool active = ViewModel?.IsActive(Model) ?? false;
        Button.Classes.Set("active", active);
        _menuMark?.Classes.Set("active", active);
    }

    /// <summary>
    /// The run a tile draws after its label: <paramref name="value"/> in bold, PREF's dim placeholder while no set is
    /// active, or null for a button with neither.
    /// </summary>
    private Run? ValueRun(string? value)
    {
        if (value is not null)
        {
            return new Run(value) { FontWeight = FontWeight.Bold };
        }

        return (Model.Id == ScopeButtonId.Pref) ? new Run(NoSetPlaceholder) { Foreground = PlaceholderBrush } : null;
    }

    /// <summary>
    /// The text of a tile labelled <paramref name="label"/> showing <paramref name="valueRun"/> last: one line on a half
    /// tile; on a full tile the label over the value, or a two-word label without a value over two lines.
    /// </summary>
    private static InlineCollection LinesOf(string label, Run? valueRun, bool isHalf)
    {
        int space = label.IndexOf(' ', StringComparison.Ordinal);
        if (!isHalf && (valueRun is null) && (space > 0))
        {
            return [new Run(label[..space]), new LineBreak(), new Run(label[(space + 1)..])];
        }

        var inlines = new InlineCollection { new Run(label) };
        if (valueRun is not null)
        {
            inlines.Add(isHalf ? new Run(" ") : new LineBreak());
            inlines.Add(valueRun);
        }

        return inlines;
    }

    /// <summary>PREF's tile takes its reserved width once it is styled and in the tree, where its font is known.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Model.Id == ScopeButtonId.Pref)
        {
            Button.MinWidth = ReservedWidth(Button);
        }
    }

    /// <summary>
    /// The width of a tile whose value line holds <see cref="ReservedNameChars"/> bold digits in <paramref name="button"/>'s
    /// font: the text's measured advance plus the tile's horizontal padding and border.
    /// </summary>
    private static double ReservedWidth(Button button)
    {
        var typeface = new Typeface(button.FontFamily, FontStyle.Normal, FontWeight.Bold);
        var text = new FormattedText(
            new string('0', ReservedNameChars),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            button.FontSize,
            null
        );
        Thickness padding = button.Padding;
        Thickness border = button.BorderThickness;
        return text.WidthIncludingTrailingWhitespace + padding.Left + padding.Right + border.Left + border.Right;
    }

    /// <summary>
    /// The wheel over a spinner steps it by whole steps, wheel up the positive direction (<see cref="ScopeViewModel.StepSpinner"/>).
    /// </summary>
    private void OnSpinnerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        int steps = vm.SpinnerScroll(Model.Id).Accumulate(Math.Sign(e.Delta.Y), vm.Preferences.ScrollSensitivity);
        if (steps != 0)
        {
            vm.StepSpinner(Model.Id, steps);
        }

        e.Handled = true;
    }
}
