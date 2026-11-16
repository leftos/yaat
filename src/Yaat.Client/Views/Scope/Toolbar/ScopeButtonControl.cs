using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>
/// One Scope toolbar button, on the bar, in its tray or torn off onto the scope, drawn as a tile: a full tile (the bar's
/// height) draws its label over its bold value, a half tile draws them on one line. Over the tile lie a menu's corner
/// mark at the top right and a tear-off pick strip on the left edge. A click presses the button
/// (<see cref="ScopeViewModel.Press"/>), the wheel steps a spinner, and a left press on the strip asks for a tear-off
/// (<see cref="TearoffRequestedEvent"/>). A torn-off copy draws its strip as a permanent accent bar and, while DEL
/// TEAROFF's mode is on, a left press anywhere on it marks or unmarks it.
/// </summary>
public sealed class ScopeButtonControl : Grid
{
    /// <summary>The height of a full tile: the bar's.</summary>
    private const double FullHeight = 45;

    /// <summary>The height of a half tile: two of them and a 1 DIP divider fill the bar.</summary>
    private const double HalfHeight = 22;

    /// <summary>Raised, bubbling, when a left press on the strip asks to tear the button off or to move its tear-off.</summary>
    public static readonly RoutedEvent<ScopeTearoffRequestedEventArgs> TearoffRequestedEvent = RoutedEvent.Register<
        ScopeButtonControl,
        ScopeTearoffRequestedEventArgs
    >("TearoffRequested", RoutingStrategies.Bubble);

    private readonly Border? _mark;
    private readonly TextBlock? _menuMark;
    private bool _isHalf;

    /// <summary>A control for <paramref name="model"/>: a torn-off copy when <paramref name="isTearoff"/>, else a bar button.</summary>
    public ScopeButtonControl(ScopeButton model, bool isTearoff)
    {
        Model = model;
        Key = ScopeToolbarButtons.KeyOf(model);
        IsTearoff = isTearoff;
        Height = FullHeight;

        // The bar's buttons are tagged with their id; a copy's with its key, so a lookup by id finds the bar's alone.
        Button = new Button { Tag = isTearoff ? Key : model.Id, Classes = { "scopeButton" } };
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

        // The strip lies over the tile's left edge, above the button, so a press on it never presses the button.
        if (ScopeToolbarButtons.CanTearOff(model.Id))
        {
            Strip = new Border { Classes = { "scopeStrip" } };
            Strip.Classes.Set("accent", isTearoff);
            Strip.PointerPressed += OnStripPressed;
            Children.Add(Strip);
        }

        if (isTearoff)
        {
            _mark = new Border
            {
                Classes = { "scopeMark" },
                IsHitTestVisible = false,
                IsVisible = false,
            };
            Children.Add(_mark);
            AddHandler(PointerPressedEvent, OnTearoffPressed, RoutingStrategies.Tunnel);
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

    /// <summary>The button's tear-off key.</summary>
    public ScopeButtonKey Key { get; }

    /// <summary>Whether the control is a torn-off copy.</summary>
    public bool IsTearoff { get; }

    /// <summary>The button itself.</summary>
    public Button Button { get; }

    /// <summary>
    /// The tear-off pick strip (a copy's accent bar), or null for a button that never tears off
    /// (<see cref="ScopeToolbarButtons.CanTearOff"/>).
    /// </summary>
    public Border? Strip { get; }

    /// <summary>Whether the copy's DEL TEAROFF mark is drawn. Test seam.</summary>
    internal bool IsMarkShown => _mark?.IsVisible ?? false;

    /// <summary>The Scope the button acts on; null draws the button's defaults and presses nothing.</summary>
    public ScopeViewModel? ViewModel { get; set; }

    /// <summary>Draws the button: its label, a spinner's bold value, the active state and a copy's mark.</summary>
    public void Refresh()
    {
        string? value = ViewModel?.SpinnerValue(Model);
        Button.Content = new TextBlock
        {
            Inlines = LinesOf(Model.Label, value, IsHalf),
            TextAlignment = TextAlignment.Center,
            LineHeight = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(Button, value is null ? Model.Label : $"{Model.Label} {value}");
        bool active = ViewModel?.IsActive(Model) ?? false;
        Button.Classes.Set("active", active);
        _menuMark?.Classes.Set("active", active);
        _mark?.IsVisible = (ViewModel is { IsDeletingTearoffs: true } vm) && vm.IsTearoffMarked(Key);
    }

    /// <summary>
    /// The text of a tile labelled <paramref name="label"/> showing <paramref name="value"/> (bold, last): one line on a
    /// half tile; on a full tile the label over the value, or a two-word label without a value over two lines.
    /// </summary>
    private static InlineCollection LinesOf(string label, string? value, bool isHalf)
    {
        int space = label.IndexOf(' ', StringComparison.Ordinal);
        if (!isHalf && (value is null) && (space > 0))
        {
            return [new Run(label[..space]), new LineBreak(), new Run(label[(space + 1)..])];
        }

        var inlines = new InlineCollection { new Run(label) };
        if (value is not null)
        {
            inlines.Add(isHalf ? new Run(" ") : new LineBreak());
            inlines.Add(new Run(value) { FontWeight = FontWeight.Bold });
        }

        return inlines;
    }

    private void OnStripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        RaiseEvent(new ScopeTearoffRequestedEventArgs(TearoffRequestedEvent, this, e));
    }

    // While DEL TEAROFF's mode is on, a left press on a copy marks it instead of pressing or moving it.
    private void OnTearoffPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((ViewModel is not { IsDeletingTearoffs: true } vm) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        vm.ToggleTearoffMark(Key);
        Refresh();
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

/// <summary>A left press on a button's tear-off strip: the button's control and the press.</summary>
public sealed class ScopeTearoffRequestedEventArgs(RoutedEvent routedEvent, ScopeButtonControl control, PointerPressedEventArgs press)
    : RoutedEventArgs(routedEvent, control)
{
    /// <summary>The control whose strip was pressed.</summary>
    public ScopeButtonControl Control { get; } = control;

    /// <summary>The press, for its position and its pointer.</summary>
    public PointerPressedEventArgs Press { get; } = press;
}
