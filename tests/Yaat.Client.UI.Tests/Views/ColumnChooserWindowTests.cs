using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The column chooser's Import and Export open the Import / Export hub with the column layout ticked, on the tab the button
/// names. A column layout imported there is staged in the chooser's rows: Cancel leaves the preferences and the live grid
/// as they were, and OK applies it to both.
/// </summary>
public class ColumnChooserWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-column-chooser-tests", Guid.NewGuid().ToString("N"));

    public ColumnChooserWindowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaTheory(Timeout = 60_000)]
    [InlineData("ImportButton", ImportExportTab.Import)]
    [InlineData("ExportButton", ImportExportTab.Export)]
    public void ImportExportButton_OpensTheHubWithTheColumnLayoutTicked(string buttonName, ImportExportTab tab)
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            ColumnChooserWindow chooser = OpenChooser(main, vm, EmbeddedGrid(main));
            ImportExportWindow hub = OpenHub(chooser, buttonName);

            ImportExportViewModel hubVm = Assert.IsType<ImportExportViewModel>(hub.DataContext);
            Assert.Equal(tab, hubVm.SelectedTab);
            Assert.Equal([SettingsItemType.GridLayout], hubVm.ExportItems.Where(r => r.IsSelected).Select(r => r.ItemType));

            hub.Close();
            chooser.Close();
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ImportedColumnLayout_CancelDropsIt_AndOkAppliesItToThePreferencesAndTheGrid()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            DataGrid grid = EmbeddedGrid(main);
            DataGridColumn column = grid.Columns.First(c => (c.Header is string) && c.IsVisible);
            string key = (string)column.Header!;
            string path = WriteGridLayoutFile(new SavedGridLayout { HiddenColumns = [key] });

            ColumnChooserWindow cancelled = OpenChooser(main, vm, grid);
            ImportInto(cancelled, path);
            Assert.False(cancelled.Entries.Single(e => e.Key == key).IsVisible);
            Click(cancelled, "CancelButton");

            Assert.DoesNotContain(key, new UserPreferences().GridLayout?.HiddenColumns ?? []);
            Assert.True(column.IsVisible);

            ColumnChooserWindow confirmed = OpenChooser(main, vm, grid);
            ImportInto(confirmed, path);
            Click(confirmed, "OkButton");

            Assert.Contains(key, new UserPreferences().GridLayout!.HiddenColumns!);
            Assert.False(column.IsVisible);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ExportAfterAStagedImport_CarriesTheStagedWidthsAndSort()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            DataGrid grid = EmbeddedGrid(main);
            string key = (string)grid.Columns.First(c => (c.Header is string) && c.IsVisible).Header!;
            string path = WriteGridLayoutFile(
                new SavedGridLayout
                {
                    ColumnWidths = new Dictionary<string, double> { [key] = 137 },
                    SortColumn = key,
                    SortDirection = System.ComponentModel.ListSortDirection.Descending,
                }
            );

            ColumnChooserWindow chooser = OpenChooser(main, vm, grid);
            ImportInto(chooser, path);
            ImportExportWindow hub = OpenHub(chooser, "ExportButton");
            ImportExportViewModel hubVm = Assert.IsType<ImportExportViewModel>(hub.DataContext);
            SettingsExportPlan plan = Assert.IsType<SettingsExportPlan>(hubVm.PrepareExport());
            SavedGridLayout exported = SettingsBundleItems.ReadGridLayout(Assert.Single(plan.Entries));
            hub.Close();
            chooser.Close();

            Assert.NotNull(exported.ColumnWidths);
            Assert.Equal(137, exported.ColumnWidths[key]);
            Assert.Equal(key, exported.SortColumn);
            Assert.Equal(System.ComponentModel.ListSortDirection.Descending, exported.SortDirection);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    private static DataGrid EmbeddedGrid(MainWindow main) => main.FindControl<DataGridView>("EmbeddedDataGridView")!.GetDataGrid()!;

    private static ColumnChooserWindow OpenChooser(MainWindow main, MainViewModel vm, DataGrid grid)
    {
        _ = main.ShowColumnChooserAsync(grid, vm);
        Dispatcher.UIThread.RunJobs();
        return Assert.Single(main.OwnedWindows.OfType<ColumnChooserWindow>());
    }

    private static ImportExportWindow OpenHub(ColumnChooserWindow chooser, string buttonName)
    {
        Click(chooser, buttonName);
        return Assert.Single(chooser.OwnedWindows.OfType<ImportExportWindow>());
    }

    private static void ImportInto(ColumnChooserWindow chooser, string path)
    {
        ImportExportWindow hub = OpenHub(chooser, "ImportButton");
        ImportExportViewModel hubVm = Assert.IsType<ImportExportViewModel>(hub.DataContext);
        hubVm.OpenImportFile(path);
        hubVm.Import();
        Assert.Null(hubVm.ImportError);
        hub.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, string buttonName)
    {
        window.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private string WriteGridLayoutFile(SavedGridLayout layout)
    {
        List<SettingsBundleEntry> entries = [SettingsBundleItems.GridLayout(layout)];
        string path = Path.Combine(_root, "layout" + SettingsBundleFile.ExportExtension(entries));
        using FileStream stream = File.Create(path);
        SettingsBundleFile.WriteExport(entries, "test", stream);
        return path;
    }
}
