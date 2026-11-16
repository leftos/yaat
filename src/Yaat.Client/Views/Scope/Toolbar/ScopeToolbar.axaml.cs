using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>
/// The Scope window's toolbar: one row of buttons in the profile's order (<see cref="ScopeViewModel.ToolbarButtons"/>), and under it the tray of
/// the open menu's children (<see cref="ScopeViewModel.TrayChildren"/>), wrapped onto more rows when they don't
/// fit. The bar scrolls sideways while it is wider than the window, with a chevron at each end. Each button
/// (<see cref="ScopeButtonControl"/>) draws its label, value and active state from its <see cref="ScopeViewModel"/>.
/// </summary>
public partial class ScopeToolbar : UserControl
{
    // A list, not a map by id: the MAPS tray's toggles share one id.
    private readonly List<ScopeButtonControl> _buttons = [];
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

    /// <summary>Fills the bar with <paramref name="buttons"/>, left to right, a separator between each two.</summary>
    private void BuildMainRow(IReadOnlyList<ScopeButton> buttons)
    {
        _buttons.RemoveAll(MainRow.Children.Contains);
        MainRow.Children.Clear();
        for (int i = 0; i < buttons.Count; i++)
        {
            if (i > 0)
            {
                MainRow.Children.Add(new Border { Classes = { "scopeSeparator" }, VerticalAlignment = VerticalAlignment.Center });
            }

            ScopeButtonControl control = BuildButton(buttons[i]);
            // The row fills the bar so a wheel over the gaps between buttons lands on it; its buttons centre in it.
            control.VerticalAlignment = VerticalAlignment.Center;
            MainRow.Children.Add(control);
        }
    }

    private ScopeButtonControl BuildButton(ScopeButton model)
    {
        var button = new ScopeButtonControl(model, isTearoff: false);
        _buttons.Add(button);
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
                or nameof(ScopeViewModel.OpenViews)
                or nameof(ScopeViewModel.Brightness)
                or nameof(ScopeViewModel.DbSettings)
                or nameof(ScopeViewModel.ActivePrefSet)
                or nameof(ScopeViewModel.IsDeletingTearoffs)
                or nameof(ScopeViewModel.ArmedRingTool)
                or nameof(ScopeViewModel.RingSizeNm);

    /// <summary>
    /// Fills the tray with the open menu's tray children (<see cref="ScopeViewModel.TrayChildren"/>), or hides it when no
    /// menu is open.
    /// </summary>
    private void RebuildTray()
    {
        _buttons.RemoveAll(TrayRow.Children.Contains);
        TrayRow.Children.Clear();
        ScopeButton? menu = _viewModel?.OpenMenu is { } open ? ScopeToolbarButtons.Find(open) : null;
        if ((menu is not null) && (_viewModel is { } vm))
        {
            foreach (ScopeButton child in vm.TrayChildren(menu))
            {
                TrayRow.Children.Add(BuildButton(child));
            }
        }

        Tray.IsVisible = menu is not null;
    }

    private void RefreshButtons()
    {
        foreach (ScopeButtonControl button in _buttons)
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
    /// The wheel never scrolls the bar: <see cref="MainRow"/> fills the whole row, so every wheel anywhere on the bar,
    /// gaps between buttons included, reaches this handler and is marked handled before the scroller sees it. A spinner
    /// has already stepped itself on its button by then.
    /// </summary>
    private void OnBarWheel(object? sender, PointerWheelEventArgs e) => e.Handled = true;
}
