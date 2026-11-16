using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>
/// The Scope window's toolbar: a bar of tiles in the profile's order (<see cref="ScopeViewModel.ToolbarButtons"/>), and
/// under it the tray of the open menu's children (<see cref="ScopeViewModel.TrayChildren"/>). On the bar a button that
/// shows a value (PREF always, with or without a set) is a full-height tile, consecutive buttons without one stack in
/// half-height pairs, and one left without a partner is a full tile; the bar is laid out again when a button's value
/// appears or goes. The tray holds half-height tiles in columns of two, wrapped into further two-row blocks when they
/// don't fit the window. The bar scrolls sideways while it is wider than the window, with a chevron at each end. Each
/// button (<see cref="ScopeButtonControl"/>) draws its label, value and active state from its <see cref="ScopeViewModel"/>.
/// </summary>
public partial class ScopeToolbar : UserControl
{
    private static readonly IBrush TrayDivider = new ImmutableSolidColorBrush(Color.Parse("#22313b"));

    // Lists, not maps by id: the MAPS tray's toggles share one id.
    private readonly List<ScopeButtonControl> _bar = [];
    private readonly List<ScopeButtonControl> _tray = [];

    // The bar's buttons, and which of them were half tiles when the bar was laid out.
    private IReadOnlyList<ScopeButton> _barButtons = [];
    private bool[] _barShape = [];
    private ScopeViewModel? _viewModel;

    public ScopeToolbar()
    {
        InitializeComponent();
        AutomationProperties.SetName(ScrollLeft, "Scroll toolbar left");
        AutomationProperties.SetName(ScrollRight, "Scroll toolbar right");
        MainScroll.PropertyChanged += OnScrollerPropertyChanged;
        // The scroller reports its viewport while the row is still being measured, so the row's own width is only known
        // once the layout pass is over.
        MainScroll.LayoutUpdated += OnLayoutUpdated;
        MainRow.AddHandler(PointerWheelChangedEvent, OnBarWheel, RoutingStrategies.Bubble);
        MainRow.AddHandler(GotFocusEvent, OnBarGotFocus, RoutingStrategies.Bubble);
        BuildMainRow(ScopeToolbarButtons.All);
        RefreshButtons();
        UpdateChevrons();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as ScopeViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        BuildMainRow(_viewModel?.ToolbarButtons ?? ScopeToolbarButtons.All);
        RebuildTray();
        RefreshButtons();
    }

    /// <summary>
    /// Fills the bar with <paramref name="buttons"/>, left to right, in the shapes <see cref="HalfTiles"/> gives them: a
    /// full tile alone, two half tiles as a pair, top over bottom.
    /// </summary>
    private void BuildMainRow(IReadOnlyList<ScopeButton> buttons)
    {
        _bar.Clear();
        MainRow.Children.Clear();
        _barButtons = buttons;
        _barShape = ShapeOf(buttons);
        int i = 0;
        while (i < buttons.Count)
        {
            if (!_barShape[i])
            {
                MainRow.Children.Add(BuildButton(_bar, buttons[i], isHalf: false));
                i++;
                continue;
            }

            var pair = new StackPanel { Spacing = 1 };
            pair.Children.Add(BuildButton(_bar, buttons[i], isHalf: true));
            pair.Children.Add(BuildButton(_bar, buttons[i + 1], isHalf: true));
            MainRow.Children.Add(pair);
            i += 2;
        }
    }

    /// <summary>Which of <paramref name="buttons"/> are half tiles now (<see cref="HalfTiles"/>).</summary>
    private bool[] ShapeOf(IReadOnlyList<ScopeButton> buttons) => HalfTiles(buttons, b => ShowsValue(_viewModel, b));

    /// <summary>
    /// Whether <paramref name="button"/> counts as showing a value on <paramref name="viewModel"/>'s bar: PREF always, with
    /// or without an active set, so loading or clearing one moves no tile; PILOT LENS always, as it shows a mode's lit
    /// state; any other button while it has a value.
    /// </summary>
    private static bool ShowsValue(ScopeViewModel? viewModel, ScopeButton button) =>
        (button.Id is ScopeButtonId.Pref or ScopeButtonId.Lens) || (viewModel?.SpinnerValue(button) is not null);

    /// <summary>
    /// Which of the bar's <paramref name="buttons"/> are half tiles: a button that shows a value
    /// (<paramref name="showsValue"/>) is a full tile, two consecutive buttons without one pair up as half tiles, in bar
    /// order, and one left without a partner is a full tile. Half tiles always come two by two, a pair's top first.
    /// </summary>
    public static bool[] HalfTiles(IReadOnlyList<ScopeButton> buttons, Func<ScopeButton, bool> showsValue)
    {
        bool[] values = [.. buttons.Select(showsValue)];
        bool[] half = new bool[buttons.Count];
        int i = 0;
        while (i + 1 < buttons.Count)
        {
            bool pairs = !values[i] && !values[i + 1];
            half[i] = pairs;
            half[i + 1] = pairs;
            i += pairs ? 2 : 1;
        }

        return half;
    }

    /// <summary>
    /// Whether <paramref name="model"/>'s tile is a half one where it lives: a tray child's always, a bar button's as the
    /// bar of <paramref name="viewModel"/> pairs it now (<see cref="HalfTiles"/>).
    /// </summary>
    public static bool IsHalfTile(ScopeViewModel viewModel, ScopeButton model)
    {
        IReadOnlyList<ScopeButton> bar = viewModel.ToolbarButtons;
        int index = bar.Select(b => b.Id).ToList().IndexOf(model.Id);
        return (index < 0) || HalfTiles(bar, b => ShowsValue(viewModel, b))[index];
    }

    private static ScopeButtonControl BuildButton(List<ScopeButtonControl> list, ScopeButton model, bool isHalf)
    {
        var button = new ScopeButtonControl(model, isTearoff: false) { IsHalf = isHalf };
        list.Add(button);
        return button;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(ScopeViewModel.ToolbarButtons)) && (_viewModel is { } vm))
        {
            BuildMainRow(vm.ToolbarButtons);
            RefreshButtons();
            return;
        }

        // A rebuilt tray's new buttons are drawn by the refresh that follows.
        bool trayChanged = TrayContentChanged(e.PropertyName);
        if (trayChanged)
        {
            RebuildTray();
        }

        // A value appearing or going can change which buttons pair up; the bar is laid out again when it does.
        if (IsDrawnState(e.PropertyName) && !ShapeOf(_barButtons).SequenceEqual(_barShape))
        {
            BuildMainRow(_barButtons);
        }

        if (trayChanged || IsDrawnState(e.PropertyName))
        {
            RefreshButtons();
        }
    }

    /// <summary>
    /// Whether the view-model property <paramref name="propertyName"/> changes the buttons of the open tray: the menu
    /// opening or closing, or the map toggles or the pref sets changing while their own tray is the open one.
    /// </summary>
    private bool TrayContentChanged(string? propertyName)
    {
        bool mapsChanged = (propertyName == nameof(ScopeViewModel.MapToggles)) && (_viewModel?.OpenMenu == ScopeButtonId.Maps);
        bool prefSetsChanged =
            (propertyName is nameof(ScopeViewModel.PrefSetNames) or nameof(ScopeViewModel.ActivePrefSet))
            && (_viewModel?.OpenMenu == ScopeButtonId.Pref);
        return (propertyName == nameof(ScopeViewModel.OpenMenu)) || mapsChanged || prefSetsChanged;
    }

    /// <summary>Whether the view-model property <paramref name="propertyName"/> is one a button draws.</summary>
    internal static bool IsDrawnState(string? propertyName) =>
        propertyName
            is nameof(ScopeViewModel.OpenMenu)
                or nameof(ScopeViewModel.TemplateId)
                or nameof(ScopeViewModel.ProfileKind)
                or nameof(ScopeViewModel.RangeNm)
                or nameof(ScopeViewModel.Pins)
                or nameof(ScopeViewModel.EffectiveBand)
                or nameof(ScopeViewModel.VectorOwnership)
                or nameof(ScopeViewModel.IsTdm)
                or nameof(ScopeViewModel.Lens)
                or nameof(ScopeViewModel.OpenViews)
                or nameof(ScopeViewModel.Brightness)
                or nameof(ScopeViewModel.DbSettings)
                or nameof(ScopeViewModel.ActivePrefSet)
                or nameof(ScopeViewModel.IsDeletingTearoffs)
                or nameof(ScopeViewModel.ArmedRingTool)
                or nameof(ScopeViewModel.RingSizeNm);

    /// <summary>
    /// Fills the tray with the open menu's tray children (<see cref="ScopeViewModel.TrayChildren"/>) as half tiles, two to
    /// a column, filled column by column, or hides it when no menu is open.
    /// </summary>
    private void RebuildTray()
    {
        _tray.Clear();
        TrayRow.Children.Clear();
        ScopeButton? menu = _viewModel?.OpenMenu is { } open ? ScopeToolbarButtons.Find(open) : null;
        if ((menu is not null) && (_viewModel is { } vm))
        {
            IReadOnlyList<ScopeButton> children = vm.TrayChildren(menu);
            for (int i = 0; i < children.Count; i += 2)
            {
                // The column's divider colour shows through its 1 DIP gap, under an empty bottom cell and down its right edge.
                var column = new StackPanel { Spacing = 1 };
                foreach (ScopeButton child in children.Skip(i).Take(2))
                {
                    column.Children.Add(BuildButton(_tray, child, isHalf: true));
                }

                TrayRow.Children.Add(
                    new Border
                    {
                        Background = TrayDivider,
                        BorderBrush = TrayDivider,
                        BorderThickness = new Thickness(0, 0, 1, 0),
                        Child = column,
                    }
                );
            }
        }

        Tray.IsVisible = menu is not null;
    }

    private void RefreshButtons()
    {
        foreach (ScopeButtonControl button in _bar.Concat(_tray))
        {
            button.ViewModel = _viewModel;
            button.Refresh();
        }
    }

    /// <summary>The most the bar scrolls sideways: its content past its viewport.</summary>
    private double MaxOffset => Math.Max(MainScroll.Extent.Width - MainScroll.Viewport.Width, 0);

    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateChevrons();

    private void OnScrollerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (
            (e.Property == ScrollViewer.ExtentProperty)
            || (e.Property == ScrollViewer.ViewportProperty)
            || (e.Property == ScrollViewer.OffsetProperty)
        )
        {
            UpdateChevrons();
        }
    }

    /// <summary>
    /// Shows the chevrons while the bar is wider than the row that holds it, and enables one only while it can still move
    /// the bar: <see cref="ScrollLeft"/> is disabled at the bar's left end and <see cref="ScrollRight"/> at its right. A bar
    /// that fits goes back to its left end, whatever it was scrolled to before.
    /// </summary>
    private void UpdateChevrons()
    {
        // The row's own width, not the scroller's viewport: the chevrons narrow the viewport, so a bar only just wider
        // than the viewport would keep them up for ever.
        bool overflows = MainScroll.Extent.Width > (BarRow.Bounds.Width + 0.5);
        ScrollLeft.IsVisible = overflows;
        ScrollRight.IsVisible = overflows;
        if (!overflows)
        {
            if (MainScroll.Offset.X != 0)
            {
                MainScroll.Offset = MainScroll.Offset.WithX(0);
            }

            ScrollLeft.IsEnabled = false;
            ScrollRight.IsEnabled = false;
            return;
        }

        ScrollLeft.IsEnabled = MainScroll.Offset.X > 0;
        ScrollRight.IsEnabled = MainScroll.Offset.X < MaxOffset;
    }

    private void OnScrollLeftClick(object? sender, RoutedEventArgs e) => ScrollBar(-1);

    private void OnScrollRightClick(object? sender, RoutedEventArgs e) => ScrollBar(1);

    /// <summary>Scrolls the bar three quarters of a viewport toward <paramref name="direction"/>, clamped to its ends.</summary>
    private void ScrollBar(int direction)
    {
        double target = MainScroll.Offset.X + (direction * 0.75 * MainScroll.Viewport.Width);
        MainScroll.Offset = MainScroll.Offset.WithX(Math.Clamp(target, 0, MaxOffset));
        UpdateChevrons();
    }

    /// <summary>
    /// The wheel never scrolls the bar: <see cref="MainRow"/> fills the whole row while the bar is wider than the window,
    /// so every wheel anywhere on the bar, the dividers between tiles included, reaches this handler and is marked handled
    /// before the scroller sees it. A spinner has already stepped itself on its button by then.
    /// </summary>
    private void OnBarWheel(object? sender, PointerWheelEventArgs e) => e.Handled = true;

    /// <summary>
    /// Brings a tile the keyboard focused into view. The scroller does not follow focus itself, so a tile a press focused
    /// stays where it is under the pointer.
    /// </summary>
    private void OnBarGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if ((e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional) && (e.Source is Control focused))
        {
            focused.BringIntoView();
        }
    }
}
