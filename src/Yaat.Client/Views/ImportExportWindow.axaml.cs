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

    private readonly ImportExportViewModel? _viewModel;

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
        _viewModel = new ImportExportViewModel(
            target,
            source,
            preselected,
            initialTab,
            BuildInfo.Version,
            path => File.Open(path, FileMode.CreateNew)
        );
        DataContext = _viewModel;
        InitializeComponent();
        _filePicker = FilePickerFactory.Create(this);
        new WindowGeometryHelper(this, preferences, "ImportExport", 760, 560).Restore();

        this.FindControl<Button>("ExportButton")!.Click += OnExportClick;
        this.FindControl<Button>("ChooseFileButton")!.Click += OnChooseFileClick;
        this.FindControl<Button>("ImportButton")!.Click += OnImportClick;
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }

    /// <summary>
    /// Every item type an import in this window applied, whichever file it came from (<see cref="ImportExportViewModel.AppliedItemTypes"/>);
    /// empty while nothing has been imported.
    /// </summary>
    public IReadOnlySet<SettingsItemType> ImportedItems
    {
        get
        {
            HashSet<SettingsItemType> items = (_viewModel is { } vm) ? [.. vm.AppliedItemTypes] : [];
            return items;
        }
    }

    /// <summary>
    /// Opens the hub over <paramref name="owner"/> on the user's preferences and favorites store, so an import applies at
    /// once, and afterwards tells the live views what it applied (<see cref="MainViewModel.NotifySettingsImported"/>).
    /// </summary>
    /// <param name="owner">The window the hub is modal over.</param>
    /// <param name="vm">The main view model whose preferences and favorites the hub reads and writes.</param>
    /// <param name="preselected">The item types ticked when the hub opens.</param>
    /// <param name="initialTab">The tab shown first.</param>
    public static async Task ShowLiveAsync(Window owner, MainViewModel vm, IReadOnlySet<SettingsItemType> preselected, ImportExportTab initialTab)
    {
        var hub = new ImportExportWindow(
            vm.Preferences,
            new UserPreferencesImportTarget(vm.Preferences, vm.FavoriteStore),
            new UserPreferencesExportSource(vm.Preferences, vm.FavoriteStore),
            preselected,
            initialTab
        );
        vm.NotifySettingsImported(await hub.ShowOverAsync(owner));
    }

    /// <summary>Shows the hub modally over <paramref name="owner"/>.</summary>
    /// <returns>The item types an import applied before the window closed.</returns>
    public async Task<IReadOnlySet<SettingsItemType>> ShowOverAsync(Window owner)
    {
        await DialogPresenter.ShowModalAsync(this, owner);
        return ImportedItems;
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if ((_viewModel is not { } vm) || (_filePicker is null))
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

    // An import that replaces an item first offers to back up its current values (ImportExportViewModel.ImportAsync).
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if ((_viewModel is not { } vm) || (_filePicker is not { } picker))
        {
            return;
        }

        try
        {
            await vm.ImportAsync(
                message => ReplaceBackupDialog.AskAsync(this, message),
                plan =>
                    picker.SaveFileAsync(
                        new SaveFileOptions(
                            Title: "Back Up Settings",
                            SuggestedFileName: plan.SuggestedFileName,
                            Filters: [new FilePickerFilter("YAAT settings file", ["*" + plan.Extension])],
                            DefaultExtension: plan.Extension.TrimStart('.')
                        )
                    )
            );
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Settings import failed");
            vm.ImportError = $"Import failed: {ex.Message}";
        }
    }

    private async void OnChooseFileClick(object? sender, RoutedEventArgs e)
    {
        if ((_viewModel is not { } vm) || (_filePicker is null))
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
