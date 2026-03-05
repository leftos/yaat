using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class SettingsWindow : Window
{
    private static readonly FilePickerFileType MacroFileType = new("YAAT Macros")
    {
        Patterns = ["*.yaat-macros.json"],
        MimeTypes = ["application/json"],
    };

    private static readonly FilePickerFileType JsonFileType = new("JSON Files") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

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

        var importBtn = this.FindControl<Button>("ImportMacrosButton");
        if (importBtn is not null)
        {
            importBtn.Click += OnImportMacrosClick;
        }

        var exportSelectedBtn = this.FindControl<Button>("ExportSelectedMacrosButton");
        if (exportSelectedBtn is not null)
        {
            exportSelectedBtn.Click += OnExportSelectedClick;
        }

        var exportAllBtn = this.FindControl<Button>("ExportAllMacrosButton");
        if (exportAllBtn is not null)
        {
            exportAllBtn.Click += OnExportAllClick;
        }

        var keyBtn = this.FindControl<Button>("AircraftSelectKeyButton");
        if (keyBtn is not null)
        {
            keyBtn.KeyDown += OnKeyCaptureKeyDown;
            keyBtn.LostFocus += OnKeyCaptureLostFocus;
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

    private async void OnImportMacrosClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Import Macros",
                AllowMultiple = false,
                FileTypeFilter = [MacroFileType, JsonFileType],
            }
        );

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            var macros = await JsonSerializer.DeserializeAsync<List<SavedMacro>>(stream, UserPreferences.JsonOptions);
            if (macros is null || macros.Count == 0)
            {
                return;
            }

            var existingNames = new HashSet<string>(vm.MacroRows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            var importWindow = new MacroImportWindow(macros, existingNames);
            var result = await importWindow.ShowDialog<List<SavedMacro>?>(this);
            if (result is { Count: > 0 })
            {
                vm.ImportMacros(result);
            }
        }
        catch (JsonException)
        {
            // Invalid file format — silently ignore
        }
    }

    private async void OnExportSelectedClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        var grid = this.FindControl<DataGrid>("MacroDataGrid");
        if (grid is null)
        {
            return;
        }

        var selected = grid.SelectedItems.OfType<MacroRow>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        await ExportMacrosAsync(vm.ExportMacros(selected));
    }

    private async void OnExportAllClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        var all = vm.ExportMacros();
        if (all.Count == 0)
        {
            return;
        }

        await ExportMacrosAsync(all);
    }

    private void OnKeyCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.IsCapturingKey)
        {
            vm.CaptureKey(e.Key);
            e.Handled = true;
        }
    }

    private void OnKeyCaptureLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.CancelKeyCapture();
        }
    }

    private async Task ExportMacrosAsync(List<SavedMacro> macros)
    {
        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Export Macros",
                SuggestedFileName = "macros.yaat-macros.json",
                FileTypeChoices = [MacroFileType, JsonFileType],
            }
        );

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await JsonSerializer.SerializeAsync(stream, macros, UserPreferences.JsonOptions);
    }
}
