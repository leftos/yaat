using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// The Import / Export hub: an Export tab that writes the ticked item types to one file, and an Import tab that previews
/// a file and applies the ticked entries. The window owns the file pickers; <see cref="ImportExportViewModel"/> does the rest.
/// </summary>
public partial class ImportExportWindow : Window
{
    private static readonly FilePickerFilter SettingsFileType = new(
        "YAAT settings files",
        [
            "*" + SettingsBundleFile.Extension,
            "*" + SettingsBundleFormats.MacrosExtension,
            "*" + CommandSchemeFile.Extension,
            "*" + FavoriteExport.SetExportExtension,
            "*" + FavoriteExport.LibraryExportExtension,
            "*" + SettingsBundleFormats.GridLayoutExtension,
            "*.json",
        ]
    );

    private static readonly ILogger Log = AppLog.CreateLogger<ImportExportWindow>();

    private readonly IFilePickerService? _filePicker;

    // Parameterless ctor required for the Avalonia designer / XamlLoader. Not used at runtime.
    public ImportExportWindow()
    {
        InitializeComponent();
    }

    /// <summary>Builds the hub.</summary>
    /// <param name="preferences">The preferences the window's geometry is saved in.</param>
    /// <param name="target">Where an import reads the current state from and writes to.</param>
    /// <param name="source">Where an export reads each item from.</param>
    /// <param name="preselected">The item types ticked on the Export tab, and in an import preview when not empty.</param>
    /// <param name="initialTab">The tab shown first.</param>
    public ImportExportWindow(
        UserPreferences preferences,
        ISettingsImportTarget target,
        ISettingsExportSource source,
        IReadOnlySet<SettingsItemType> preselected,
        ImportExportTab initialTab
    )
    {
        DataContext = new ImportExportViewModel(target, source, preselected, initialTab, BuildInfo.Version);
        InitializeComponent();
        _filePicker = FilePickerFactory.Create(this);
        new WindowGeometryHelper(this, preferences, "ImportExport", 760, 560).Restore();

        this.FindControl<Button>("ExportButton")!.Click += OnExportClick;
        this.FindControl<Button>("ChooseFileButton")!.Click += OnChooseFileClick;
        this.FindControl<Button>("ImportButton")!.Click += (_, _) => (DataContext as ImportExportViewModel)?.Import();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if ((DataContext is not ImportExportViewModel vm) || (_filePicker is null))
        {
            return;
        }

        try
        {
            if (vm.PrepareExport() is not { } plan)
            {
                return;
            }

            string? path = await _filePicker.SaveFileAsync(
                new SaveFileOptions(
                    Title: "Export Settings",
                    SuggestedFileName: plan.SuggestedFileName,
                    Filters: [new FilePickerFilter("YAAT settings file", ["*" + plan.Extension])],
                    DefaultExtension: plan.Extension.TrimStart('.')
                )
            );
            if (path is not null)
            {
                vm.WriteExport(plan, path);
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Settings export failed");
            vm.ExportStatus = $"Export failed: {ex.Message}";
        }
    }

    private async void OnChooseFileClick(object? sender, RoutedEventArgs e)
    {
        if ((DataContext is not ImportExportViewModel vm) || (_filePicker is null))
        {
            return;
        }

        try
        {
            string? path = await _filePicker.OpenFileAsync(new OpenFileOptions("Import Settings", [SettingsFileType]));
            if (path is not null)
            {
                vm.OpenImportFile(path);
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Choosing a settings file to import failed");
            vm.ImportError = $"Could not open the file: {ex.Message}";
        }
    }
}
