using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
        : this(new UserPreferences()) { }

    public SettingsWindow(UserPreferences preferences)
    {
        InitializeComponent();

        var vm = new SettingsViewModel(preferences);
        DataContext = vm;

        new WindowGeometryHelper(this, preferences, "Settings", 560, 440).Restore();

        var saveBtn = this.FindControl<Button>("SaveButton");
        if (saveBtn is not null)
        {
            saveBtn.Click += OnSaveClick;
        }

        var cancelBtn = this.FindControl<Button>("CancelButton");
        if (cancelBtn is not null)
        {
            cancelBtn.Click += OnCancelClick;
        }
    }

    private void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.SaveCommand.Execute(null);
        }

        Close();
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
