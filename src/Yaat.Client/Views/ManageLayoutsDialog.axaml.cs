using Avalonia.Controls;
using Avalonia.Interactivity;
using MsBox.Avalonia.Enums;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public enum ManageLayoutsAction
{
    None,
    Apply,
    UpdateFromCurrent,
}

/// <summary>
/// Lets the user inspect, rename, delete, apply, or update saved window
/// layouts. Rename and Delete are handled inline against
/// <see cref="UserPreferences"/>; Apply and Update close the dialog with an
/// <see cref="Action"/> value the caller (MainWindow) inspects to do the work
/// that needs MainWindow-level orchestration (DataGrid layout, pop-out toggles).
/// </summary>
public partial class ManageLayoutsDialog : Window
{
    private readonly UserPreferences _preferences;
    private readonly WindowGeometryHelper _geometryHelper;

    public ManageLayoutsAction Action { get; private set; } = ManageLayoutsAction.None;
    public string? SelectedLayoutName { get; private set; }

    // Parameterless ctor required for Avalonia designer / XamlLoader. Should not be used at runtime.
    public ManageLayoutsDialog()
        : this(new UserPreferences()) { }

    public ManageLayoutsDialog(UserPreferences preferences)
    {
        InitializeComponent();
        _preferences = preferences;
        _geometryHelper = new WindowGeometryHelper(this, preferences, "ManageLayouts", 500, 380);
        _geometryHelper.Restore();

        Button? apply = this.FindControl<Button>("ApplyButton");
        Button? update = this.FindControl<Button>("UpdateButton");
        Button? rename = this.FindControl<Button>("RenameButton");
        Button? delete = this.FindControl<Button>("DeleteButton");
        Button? close = this.FindControl<Button>("CloseButton");
        ListBox? list = this.FindControl<ListBox>("LayoutsList");

        apply?.Click += OnApplyClick;
        update?.Click += OnUpdateClick;
        rename?.Click += OnRenameClick;
        delete?.Click += OnDeleteClick;
        close?.Click += (_, _) => Close();
        list?.DoubleTapped += (_, _) => OnApplyClick(null, new RoutedEventArgs());

        Populate();
    }

    private void Populate()
    {
        ListBox? list = this.FindControl<ListBox>("LayoutsList");
        if (list is null)
        {
            return;
        }
        list.ItemsSource = _preferences.Layouts.Select(p => p.Name).ToList();
    }

    private string? GetSelectedName()
    {
        ListBox? list = this.FindControl<ListBox>("LayoutsList");
        return list?.SelectedItem as string;
    }

    private void SetStatus(string? message)
    {
        TextBlock? status = this.FindControl<TextBlock>("StatusText");
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
        string? name = GetSelectedName();
        if (name is null)
        {
            SetStatus("Select a layout first.");
            return;
        }
        Action = ManageLayoutsAction.Apply;
        SelectedLayoutName = name;
        Close();
    }

    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        string? name = GetSelectedName();
        if (name is null)
        {
            SetStatus("Select a layout first.");
            return;
        }
        Action = ManageLayoutsAction.UpdateFromCurrent;
        SelectedLayoutName = name;
        Close();
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        string? oldName = GetSelectedName();
        if (oldName is null)
        {
            SetStatus("Select a layout first.");
            return;
        }

        IEnumerable<string> others = _preferences
            .Layouts.Where(p => !string.Equals(p.Name, oldName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name);
        var dlg = new SaveLayoutDialog(others, oldName) { Title = "Rename Layout" };
        await DialogPresenter.ShowModalAsync(dlg, this);

        if (dlg.LayoutName is null || string.Equals(dlg.LayoutName, oldName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_preferences.RenameLayout(oldName, dlg.LayoutName))
        {
            SetStatus($"Could not rename to \"{dlg.LayoutName}\".");
            return;
        }

        SetStatus(null);
        Populate();
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        string? name = GetSelectedName();
        if (name is null)
        {
            SetStatus("Select a layout first.");
            return;
        }

        ButtonResult result = await MessageBoxPresenter.ShowStandardAsync(this, "Delete layout?", $"Delete layout \"{name}\"?", ButtonEnum.YesNo);
        if (result != ButtonResult.Yes)
        {
            return;
        }

        _preferences.DeleteLayout(name);
        SetStatus(null);
        Populate();
    }
}
