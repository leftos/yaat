using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Import / Export hub opens on the tab it was asked for, with the preselected items ticked, and shows an entry a
/// newer client wrote as a row that cannot be ticked.
/// </summary>
public class ImportExportWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-importexport-window-tests", Guid.NewGuid().ToString("N"));

    public ImportExportWindowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact]
    public void OpensOnExport_WithThePreselectionTicked()
    {
        using var scope = new PreferencesFileScope();
        ImportExportWindow window = Open([SettingsItemType.Favorites], ImportExportTab.Export);
        try
        {
            TabControl? tabs = window.FindControl<TabControl>("Tabs");
            Assert.NotNull(tabs);
            Assert.Equal(0, tabs.SelectedIndex);

            ImportExportViewModel vm = Assert.IsType<ImportExportViewModel>(window.DataContext);
            Assert.Equal([SettingsItemType.Favorites], vm.ExportItems.Where(r => r.IsSelected).Select(r => r.ItemType));

            ItemsControl? list = window.FindControl<ItemsControl>("ExportList");
            Assert.NotNull(list);
            for (int i = 0; i < vm.ExportItems.Count; i++)
            {
                CheckBox box = RowCheckBox(list, i);
                Assert.Equal(vm.ExportItems[i].ItemType == SettingsItemType.Favorites, box.IsChecked);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void OpensOnImport_WhenAskedFor()
    {
        using var scope = new PreferencesFileScope();
        ImportExportWindow window = Open([SettingsItemType.Macros], ImportExportTab.Import);
        try
        {
            TabControl? tabs = window.FindControl<TabControl>("Tabs");
            Assert.NotNull(tabs);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal(ImportExportTab.Import, Assert.IsType<ImportExportViewModel>(window.DataContext).SelectedTab);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NewerClientBundle_ShowsTheUnknownEntryAsARowThatCannotBeTicked()
    {
        using var scope = new PreferencesFileScope();
        string path = WriteNewerClientBundle();
        ImportExportWindow window = Open([], ImportExportTab.Import);
        try
        {
            ImportExportViewModel vm = Assert.IsType<ImportExportViewModel>(window.DataContext);
            vm.OpenImportFile(path);
            Dispatcher.UIThread.RunJobs();

            ItemsControl? list = window.FindControl<ItemsControl>("ImportList");
            Assert.NotNull(list);
            Assert.Equal(2, vm.ImportItems.Count);
            for (int i = 0; i < vm.ImportItems.Count; i++)
            {
                ImportItemRow row = vm.ImportItems[i];
                CheckBox box = RowCheckBox(list, i);
                bool known = row.ItemType == SettingsItemType.Macros;
                Assert.Equal(known, box.IsEnabled);
                Assert.Equal(known, box.IsChecked);
            }

            ImportItemRow skipped = vm.ImportItems.Single(r => r.ItemType is null);
            Assert.Equal(0.5, skipped.RowOpacity);
            Assert.Contains("does not know the item type 'hotkeys'", skipped.ClashSummary, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    private static CheckBox RowCheckBox(ItemsControl list, int index)
    {
        Control? container = list.ContainerFromIndex(index);
        Assert.NotNull(container);
        return container.GetVisualDescendants().OfType<CheckBox>().First();
    }

    private string WriteNewerClientBundle()
    {
        string path = Path.Combine(_root, "newer" + SettingsBundleFile.Extension);
        const string manifest = """
            {
              "bundleVersion": 2,
              "writtenBy": "9.9.9",
              "entries": [
                { "itemType": "macros", "format": "macros-json", "fileName": "macros.yaat-macros.json" },
                { "itemType": "hotkeys", "format": "hotkeys-json", "fileName": "hotkeys.json" }
              ]
            }
            """;
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteZipEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest));
        WriteZipEntry(zip, "macros.yaat-macros.json", SettingsBundleItems.Macros([new SavedMacro { Name = "DEP", Expansion = "CTO" }]).Content);
        WriteZipEntry(zip, "hotkeys.json", Encoding.UTF8.GetBytes("{}"));
        return path;
    }

    private static void WriteZipEntry(ZipArchive zip, string name, byte[] content)
    {
        using Stream stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }

    private ImportExportWindow Open(SettingsItemType[] preselected, ImportExportTab tab)
    {
        var preferences = new UserPreferences();
        var favorites = new FavoriteStore(Path.Combine(_root, "favorites"));
        var window = new ImportExportWindow(
            preferences,
            new UserPreferencesImportTarget(preferences, favorites),
            new UserPreferencesExportSource(preferences, favorites),
            new HashSet<SettingsItemType>(preselected),
            tab
        );
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
