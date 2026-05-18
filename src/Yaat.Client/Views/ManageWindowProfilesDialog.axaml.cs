using Avalonia.Controls;
using Avalonia.Interactivity;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public enum ManageWindowProfilesAction
{
    None,
    Apply,
    UpdateFromCurrent,
}

/// <summary>
/// Lets the user inspect, rename, delete, apply, or update saved window
/// profiles. Rename and Delete are handled inline against
/// <see cref="UserPreferences"/>; Apply and Update close the dialog with an
/// <see cref="Action"/> value the caller (MainWindow) inspects to do the work
/// that needs MainWindow-level orchestration (DataGrid layout, pop-out toggles).
/// </summary>
public partial class ManageWindowProfilesDialog : Window
{
    private readonly UserPreferences _preferences;
    private readonly WindowGeometryHelper _geometryHelper;

    public ManageWindowProfilesAction Action { get; private set; } = ManageWindowProfilesAction.None;
    public string? SelectedProfileName { get; private set; }

    // Parameterless ctor required for Avalonia designer / XamlLoader. Should not be used at runtime.
    public ManageWindowProfilesDialog()
        : this(new UserPreferences()) { }

    public ManageWindowProfilesDialog(UserPreferences preferences)
    {
        InitializeComponent();
        _preferences = preferences;
        _geometryHelper = new WindowGeometryHelper(this, preferences, "ManageWindowProfiles", 500, 380);
        _geometryHelper.Restore();

        var apply = this.FindControl<Button>("ApplyButton");
        var update = this.FindControl<Button>("UpdateButton");
        var rename = this.FindControl<Button>("RenameButton");
        var delete = this.FindControl<Button>("DeleteButton");
        var close = this.FindControl<Button>("CloseButton");
        var list = this.FindControl<ListBox>("ProfilesList");

        if (apply is not null)
        {
            apply.Click += OnApplyClick;
        }
        if (update is not null)
        {
            update.Click += OnUpdateClick;
        }
        if (rename is not null)
        {
            rename.Click += OnRenameClick;
        }
        if (delete is not null)
        {
            delete.Click += OnDeleteClick;
        }
        if (close is not null)
        {
            close.Click += (_, _) => Close();
        }
        if (list is not null)
        {
            list.DoubleTapped += (_, _) => OnApplyClick(null, new RoutedEventArgs());
        }

        Populate();
    }

    private void Populate()
    {
        var list = this.FindControl<ListBox>("ProfilesList");
        if (list is null)
        {
            return;
        }
        list.ItemsSource = _preferences.WindowProfiles.Select(p => p.Name).ToList();
    }

    private string? GetSelectedName()
    {
        var list = this.FindControl<ListBox>("ProfilesList");
        return list?.SelectedItem as string;
    }

    private void SetStatus(string? message)
    {
        var status = this.FindControl<TextBlock>("StatusText");
        if (status is null)
        {
            return;
        }
        if (string.IsNullOrEmpty(message))
        {
            status.IsVisible = false;
            return;
        }
        status.Text = message;
        status.IsVisible = true;
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        var name = GetSelectedName();
        if (name is null)
        {
            SetStatus("Select a profile first.");
            return;
        }
        Action = ManageWindowProfilesAction.Apply;
        SelectedProfileName = name;
        Close();
    }

    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        var name = GetSelectedName();
        if (name is null)
        {
            SetStatus("Select a profile first.");
            return;
        }
        Action = ManageWindowProfilesAction.UpdateFromCurrent;
        SelectedProfileName = name;
        Close();
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        var oldName = GetSelectedName();
        if (oldName is null)
        {
            SetStatus("Select a profile first.");
            return;
        }

        var others = _preferences.WindowProfiles.Where(p => !string.Equals(p.Name, oldName, StringComparison.OrdinalIgnoreCase)).Select(p => p.Name);
        var dlg = new SaveWindowProfileDialog(others, oldName) { Title = "Rename Window Profile" };
        await dlg.ShowDialog(this);

        if (dlg.ProfileName is null || string.Equals(dlg.ProfileName, oldName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_preferences.RenameWindowProfile(oldName, dlg.ProfileName))
        {
            SetStatus($"Could not rename to \"{dlg.ProfileName}\".");
            return;
        }

        SetStatus(null);
        Populate();
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        var name = GetSelectedName();
        if (name is null)
        {
            SetStatus("Select a profile first.");
            return;
        }

        var box = MessageBoxManager.GetMessageBoxStandard("Delete profile?", $"Delete window profile \"{name}\"?", ButtonEnum.YesNo);
        var result = await box.ShowWindowDialogAsync(this);
        if (result != ButtonResult.Yes)
        {
            return;
        }

        _preferences.DeleteWindowProfile(name);
        SetStatus(null);
        Populate();
    }
}
