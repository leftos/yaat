using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>
/// The Scope window's toolbar: one row of buttons from <see cref="ScopeToolbarButtons.All"/>, and under it the tray of
/// the open menu's children (<see cref="ScopeViewModel.TrayChildren"/>), wrapped onto more rows when they don't
/// fit. Each button's label, value and active state follow its <see cref="ScopeViewModel"/>.
/// </summary>
public partial class ScopeToolbar : UserControl
{
    // A list, not a map by id: the MAPS tray's toggles share one id.
    private readonly List<(Button Button, ScopeButton Model)> _buttons = [];

    // One accumulator per spinner, so a part-step on one never carries to the next.
    private readonly Dictionary<ScopeButtonId, ScrollStepAccumulator> _spinnerScroll = [];
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

    private Button BuildButton(ScopeButton model)
    {
        var button = new Button { Tag = model.Id, Classes = { "scopeButton" } };
        button.Click += (_, _) => _viewModel?.Press(model);
        if (model.Kind == ScopeButtonKind.Spinner)
        {
            button.PointerWheelChanged += OnSpinnerWheel;
        }

        _buttons.Add((button, model));
        return button;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A rebuilt tray's new buttons are drawn by the refresh that follows. The map toggles change the MAPS tray's buttons.
        bool mapsChanged = (e.PropertyName == nameof(ScopeViewModel.MapToggles)) && (_viewModel?.OpenMenu == ScopeButtonId.Maps);
        if ((e.PropertyName == nameof(ScopeViewModel.OpenMenu)) || mapsChanged)
        {
            RebuildTray();
        }

        if (
            mapsChanged
            || e.PropertyName
                is nameof(ScopeViewModel.OpenMenu)
                    or nameof(ScopeViewModel.TemplateId)
                    or nameof(ScopeViewModel.RangeNm)
                    or nameof(ScopeViewModel.OpenViews)
                    or nameof(ScopeViewModel.Brightness)
                    or nameof(ScopeViewModel.DbSettings)
        )
        {
            RefreshButtons();
        }
    }

    /// <summary>
    /// Fills the tray with the open menu's tray children (<see cref="ScopeViewModel.TrayChildren"/>), or hides it when no
    /// menu is open.
    /// </summary>
    private void RebuildTray()
    {
        _buttons.RemoveAll(entry => TrayRow.Children.Contains(entry.Button));
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
        foreach ((Button button, ScopeButton model) in _buttons)
        {
            Refresh(button, model);
        }
    }

    /// <summary>Draws <paramref name="button"/>: its label, a menu's triangle, a spinner's bold value, and the active state.</summary>
    private void Refresh(Button button, ScopeButton model)
    {
        string? value = _viewModel?.SpinnerValue(model);
        var inlines = new InlineCollection { new Run(model.Label) };
        if (model.Kind == ScopeButtonKind.Menu)
        {
            inlines.Add(new Run(" ▸") { FontSize = 9 });
        }

        if (value is not null)
        {
            inlines.Add(new Run(" "));
            inlines.Add(new Run(value) { FontWeight = FontWeight.Bold });
        }

        button.Content = new TextBlock { Inlines = inlines, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        AutomationProperties.SetName(button, value is null ? model.Label : $"{model.Label} {value}");
        button.Classes.Set("active", _viewModel?.IsActive(model) ?? false);
    }

    /// <summary>
    /// The wheel over a spinner steps the spinner it is over (the sender's <see cref="ScopeButtonId"/> tag) by whole steps,
    /// wheel up the positive direction (<see cref="ScopeViewModel.StepSpinner"/>).
    /// </summary>
    private void OnSpinnerWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((_viewModel is null) || (sender is not Control { Tag: ScopeButtonId id }))
        {
            return;
        }

        int direction = Math.Sign(e.Delta.Y);
        if (!_spinnerScroll.TryGetValue(id, out ScrollStepAccumulator? scroll))
        {
            scroll = new ScrollStepAccumulator();
            _spinnerScroll[id] = scroll;
        }

        int steps = scroll.Accumulate(direction, _viewModel.Preferences.ScrollSensitivity);
        if (steps != 0)
        {
            _viewModel.StepSpinner(id, steps);
        }

        e.Handled = true;
    }
}
