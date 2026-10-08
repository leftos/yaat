using System.ComponentModel;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data;

namespace Yaat.Client.Views;

/// <summary>
/// Scenario › Active Runways…: the room's current list, one row per airport, edited with the text the load prompt and the
/// <c>ARWY</c> command take. Apply sends one <c>ARWY</c> per changed row through the connection and leaves the window open;
/// the view model asks the window to close when the scenario it was opened on goes away.
/// </summary>
public partial class ActiveRunwaysWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<ActiveRunwaysWindow>();

    private readonly ActiveRunwaysWindowViewModel _viewModel;
    private readonly Func<string, Task<CommandResultDto>> _send;

    // Parameterless ctor for the Avalonia designer / XamlLoader and for GuideCapture's offscreen render: a window with no
    // room behind it.
    public ActiveRunwaysWindow()
        : this(ActiveRunwaysWindowViewModel.CreateEmpty(), new UserPreferences(), _ => Task.FromResult(new CommandResultDto(false, null))) { }

    public ActiveRunwaysWindow(ActiveRunwaysWindowViewModel viewModel, UserPreferences preferences, Func<string, Task<CommandResultDto>> send)
    {
        _viewModel = viewModel;
        _send = send;
        DataContext = viewModel;
        InitializeComponent();
        new WindowGeometryHelper(this, preferences, "ActiveRunways", 460, 360).Restore();

        this.FindControl<Button>("ApplyButton")!.Click += OnApplyClick;
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
    }

    private async void OnApplyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            await _viewModel.ApplyAsync(NavigationDatabase.InstanceOrNull, _send);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Apply active runways error");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(ActiveRunwaysWindowViewModel.IsOpen)) && (!_viewModel.IsOpen))
        {
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Close();
    }
}
