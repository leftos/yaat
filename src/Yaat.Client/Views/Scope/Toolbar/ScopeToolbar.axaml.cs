using System.ComponentModel;
using Avalonia.Controls;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>
/// The Scope window's toolbar: one row of buttons from <see cref="ScopeToolbarButtons.All"/>, and under it the tray of
/// the open menu's children (<see cref="ScopeViewModel.TrayChildren"/>), wrapped onto more rows when they don't
/// fit. Each button (<see cref="ScopeButtonControl"/>) draws its label, value and active state from its
/// <see cref="ScopeViewModel"/>.
/// </summary>
public partial class ScopeToolbar : UserControl
{
    // A list, not a map by id: the MAPS tray's toggles share one id.
    private readonly List<ScopeButtonControl> _buttons = [];
    private ScopeViewModel? _viewModel;

    public ScopeToolbar()
    {
        InitializeComponent();
        for (int i = 0; i < ScopeToolbarButtons.All.Count; i++)
        {
            if (i > 0)
            {
                MainRow.Children.Add(new Border { Classes = { "scopeSeparator" } });
            }

            MainRow.Children.Add(BuildButton(ScopeToolbarButtons.All[i]));
        }

        RefreshButtons();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as ScopeViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        RebuildTray();
        RefreshButtons();
    }

    private ScopeButtonControl BuildButton(ScopeButton model)
    {
        var button = new ScopeButtonControl(model, isTearoff: false);
        _buttons.Add(button);
        return button;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
                or nameof(ScopeViewModel.RangeNm)
                or nameof(ScopeViewModel.OpenViews)
                or nameof(ScopeViewModel.Brightness)
                or nameof(ScopeViewModel.DbSettings)
                or nameof(ScopeViewModel.ActivePrefSet)
                or nameof(ScopeViewModel.IsDeletingTearoffs);

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
}
