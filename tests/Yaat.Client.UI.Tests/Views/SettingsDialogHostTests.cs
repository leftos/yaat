using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Radar;
using Yaat.Client.Views.Settings;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Settings window opened from the main window: Apply commits and refreshes the live views while the window
/// stays open, Cancel then puts back the state as of the last Apply (not as of opening), and OK commits and closes.
/// Tools › Settings opens on General and the pilot voice request on Speech; a second request while Settings is open
/// moves the open window to the requested section instead of opening another. Tools › Import / Export and the Import /
/// Export buttons in Settings open the hub on their item and tab, and an import from Tools reaches the live views.
/// </summary>
public class SettingsDialogHostTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public void Apply_RefreshesTheLiveViews_AndCancelRollsBackOnlyWhatCameAfterIt()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            SettingsWindow dialog = OpenSettings(main);
            SettingsViewModel settingsVm = dialog.ViewModel;
            bool showAllTaxiRoutes = !vm.Ground.ShowAllTaxiRoutes;
            int appliedFontSize = vm.Preferences.TerminalFontSize + 2;

            settingsVm.GroundShowAllTaxiRoutes = showAllTaxiRoutes;
            settingsVm.TerminalFontSize = appliedFontSize;
            Click(dialog, "ApplyButton");

            Assert.True(dialog.IsVisible, "Apply leaves the window open");
            Assert.Equal(showAllTaxiRoutes, vm.Ground.ShowAllTaxiRoutes);
            Assert.Equal(appliedFontSize, new UserPreferences().TerminalFontSize);

            settingsVm.TerminalFontSize = appliedFontSize + 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(appliedFontSize + 2, vm.TerminalFontSize);

            Click(dialog, "CancelButton");

            Assert.False(dialog.IsVisible);
            Assert.Equal(appliedFontSize, vm.TerminalFontSize);
            Assert.Equal(appliedFontSize, new UserPreferences().TerminalFontSize);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void Ok_CommitsAndCloses()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            SettingsWindow dialog = OpenSettings(main);
            bool showAllTaxiRoutes = !vm.Ground.ShowAllTaxiRoutes;

            dialog.ViewModel.GroundShowAllTaxiRoutes = showAllTaxiRoutes;
            Click(dialog, "OkButton");

            Assert.False(dialog.IsVisible);
            Assert.Equal(showAllTaxiRoutes, vm.Ground.ShowAllTaxiRoutes);
            Assert.Equal(showAllTaxiRoutes, new UserPreferences().GroundShowAllTaxiRoutes);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ToolsMenu_OpensOnGeneral()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, _) = MainWindowHost.Boot();
        try
        {
            SettingsWindow dialog = OpenSettings(main);

            Assert.IsType<GeneralSection>(ShownSection(dialog));
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ToolsSettingsItem_RaisesSettingsRequestedWithGeneral()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        List<SettingsSectionId?> requests = [];
        vm.SettingsRequested += requests.Add;

        try
        {
            main.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([SettingsSectionId.General], requests);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void PilotVoiceSettingsRequest_OpensOnSpeech()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.OpenPilotVoiceSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            SettingsWindow dialog = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
            Assert.IsType<SpeechSection>(ShownSection(dialog));
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void SpeechDebugRequest_WhileSettingsIsOpen_ShowsSpeechInTheOpenWindow()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            SettingsWindow first = OpenSettings(main);
            Assert.IsType<GeneralSection>(ShownSection(first));

            // The speech debug window's Settings button raises this request; Settings disables that window, so the
            // request comes from code here.
            vm.RequestSettings(SettingsSectionId.Speech);
            Dispatcher.UIThread.RunJobs();

            SettingsWindow shown = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
            Assert.Same(first, shown);
            Assert.IsType<SpeechSection>(ShownSection(shown));
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ViewMenuLink_WhileSettingsIsOpenAtSpeech_MovesToThatViewsSection()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            SettingsWindow dialog = OpenSettings(main);
            vm.RequestSettings(SettingsSectionId.Speech);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<SpeechSection>(ShownSection(dialog));

            // A view's "Settings for this view…" item raises this request; Settings disables the window it sits in, so
            // the request comes from code here.
            vm.RequestSettings(SettingsSectionId.Radar);
            Dispatcher.UIThread.RunJobs();

            SettingsWindow shown = Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
            Assert.Same(dialog, shown);
            Assert.IsType<RadarSection>(ShownSection(dialog));
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ToolsImportExport_OpensTheHubOnExportWithNothingTicked()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, _) = MainWindowHost.Boot();
        try
        {
            ImportExportWindow hub = OpenToolsImportExport(main);

            ImportExportViewModel hubVm = Assert.IsType<ImportExportViewModel>(hub.DataContext);
            Assert.Equal(ImportExportTab.Export, hubVm.SelectedTab);
            Assert.DoesNotContain(hubVm.ExportItems, r => r.IsSelected);
            hub.Close();
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ToolsImportExport_ImportedColumnLayoutAndDisplayPreference_ReachTheLiveViews()
    {
        using var scope = new PreferencesFileScope();
        string root = Path.Combine(Path.GetTempPath(), "yaat-settings-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            DataGrid grid = main.FindControl<DataGridView>("EmbeddedDataGridView")!.GetDataGrid()!;
            DataGridColumn column = grid.Columns.First(c => (c.Header is string) && c.IsVisible);
            bool showAllTaxiRoutes = !vm.Ground.ShowAllTaxiRoutes;
            List<SettingsBundleEntry> entries =
            [
                SettingsBundleItems.GridLayout(new SavedGridLayout { HiddenColumns = [(string)column.Header!] }),
                SettingsBundleItems.Preferences(vm.Preferences) with
                {
                    Content = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["groundShowAllTaxiRoutes"] = showAllTaxiRoutes }),
                },
            ];
            string path = Path.Combine(root, "settings" + SettingsBundleFile.Extension);
            using (FileStream stream = File.Create(path))
            {
                SettingsBundleFile.WriteExport(entries, "test", stream);
            }

            ImportExportWindow hub = OpenToolsImportExport(main);
            ImportExportViewModel hubVm = Assert.IsType<ImportExportViewModel>(hub.DataContext);
            hubVm.OpenImportFile(path);
            hubVm.Import();
            Assert.Null(hubVm.ImportError);
            hub.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.False(column.IsVisible);
            Assert.Equal(showAllTaxiRoutes, vm.Ground.ShowAllTaxiRoutes);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void SettingsGeneralHub_ImportedColumnLayout_HidesTheGridColumnOnApply()
    {
        using var scope = new PreferencesFileScope();
        string root = Path.Combine(Path.GetTempPath(), "yaat-settings-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        (MainWindow main, _) = MainWindowHost.Boot();
        try
        {
            DataGrid grid = main.FindControl<DataGridView>("EmbeddedDataGridView")!.GetDataGrid()!;
            DataGridColumn column = grid.Columns.First(c => (c.Header is string) && c.IsVisible);
            string path = Path.Combine(root, "aircraft-list" + SettingsBundleFormats.GridLayoutExtension);
            File.WriteAllBytes(path, SettingsBundleItems.GridLayout(new SavedGridLayout { HiddenColumns = [(string)column.Header!] }).Content);

            SettingsWindow dialog = OpenSettings(main);
            dialog
                .SectionView(SettingsSectionId.General)
                .FindControl<Button>("ImportExportButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ImportExportWindow hub = Assert.Single(dialog.OwnedWindows.OfType<ImportExportWindow>());
            ImportExportViewModel hubVm = Assert.IsType<ImportExportViewModel>(hub.DataContext);
            hubVm.OpenImportFile(path);
            hubVm.Import();
            Assert.Null(hubVm.ImportError);
            hub.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(column.IsVisible, "the import is staged until Apply");

            Click(dialog, "ApplyButton");

            Assert.False(column.IsVisible);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTheory(Timeout = 60_000)]
    [InlineData(SettingsSectionId.Macros, "ImportMacrosButton", SettingsItemType.Macros, ImportExportTab.Import)]
    [InlineData(SettingsSectionId.Macros, "ExportMacrosButton", SettingsItemType.Macros, ImportExportTab.Export)]
    [InlineData(SettingsSectionId.CommandVerbs, "ImportVerbsButton", SettingsItemType.Verbs, ImportExportTab.Import)]
    [InlineData(SettingsSectionId.CommandVerbs, "ExportVerbsButton", SettingsItemType.Verbs, ImportExportTab.Export)]
    [InlineData(SettingsSectionId.General, "ImportExportButton", null, ImportExportTab.Export)]
    public void SettingsImportExportButton_OpensTheHubWithItsItemTickedOnItsTab(
        SettingsSectionId section,
        string buttonName,
        SettingsItemType? ticked,
        ImportExportTab tab
    )
    {
        using var scope = new PreferencesFileScope();
        var dialog = new SettingsWindow();
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            dialog.SelectSection(section);
            dialog.SectionView(section).FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            ImportExportWindow hub = Assert.Single(dialog.OwnedWindows.OfType<ImportExportWindow>());
            ImportExportViewModel hubVm = Assert.IsType<ImportExportViewModel>(hub.DataContext);
            Assert.Equal(tab, hubVm.SelectedTab);
            SettingsItemType[] expected = ticked is { } item ? [item] : [];
            Assert.Equal(expected, hubVm.ExportItems.Where(r => r.IsSelected).Select(r => r.ItemType));
            hub.Close();
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void ExportSelectedMacros_ExportsOnlyTheSelectedRows_AndIsDisabledWhileNoneIsSelected()
    {
        using var scope = new PreferencesFileScope();
        var dialog = new SettingsWindow();
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            dialog.SelectSection(SettingsSectionId.Macros);
            dialog.ViewModel.StageMacros([.. Enumerable.Range(1, 5).Select(i => new SavedMacro { Name = $"M{i}", Expansion = $"FH {i}0" })]);
            Dispatcher.UIThread.RunJobs();
            Control section = dialog.SectionView(SettingsSectionId.Macros);
            DataGrid grid = section.FindControl<DataGrid>("MacroDataGrid")!;
            Button exportSelected = section.FindControl<Button>("ExportSelectedMacrosButton")!;
            Assert.False(exportSelected.IsEnabled, "Export Selected is disabled while no macro is selected");

            grid.SelectedItems.Add(dialog.ViewModel.MacroRows[3]);
            grid.SelectedItems.Add(dialog.ViewModel.MacroRows[1]);
            Dispatcher.UIThread.RunJobs();
            Assert.True(exportSelected.IsEnabled);
            exportSelected.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            ImportExportWindow hub = Assert.Single(dialog.OwnedWindows.OfType<ImportExportWindow>());
            ImportExportViewModel hubVm = Assert.IsType<ImportExportViewModel>(hub.DataContext);
            Assert.Equal(ImportExportTab.Export, hubVm.SelectedTab);
            Assert.Equal([SettingsItemType.Macros], hubVm.ExportItems.Where(r => r.IsSelected).Select(r => r.ItemType));
            Assert.Equal("The 2 selected macros", hubVm.ExportItems.Single(r => r.ItemType == SettingsItemType.Macros).Description);
            SettingsBundleEntry macros = Assert.Single(Assert.IsType<SettingsExportPlan>(hubVm.PrepareExport()).Entries);
            Assert.Equal(["M2", "M4"], SettingsBundleItems.ReadMacros(macros).Select(m => m.Name));
            hub.Close();
            Dispatcher.UIThread.RunJobs();

            grid.SelectedItems.Clear();
            Dispatcher.UIThread.RunJobs();
            Assert.False(exportSelected.IsEnabled);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaTheory(Timeout = 60_000)]
    [InlineData("CancelButton")]
    [InlineData("OkButton")]
    [InlineData(null)]
    public void OpenSettings_BlocksInputToOtherWindows_WithoutDisablingThem(string? closeButton)
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        var radar = new RadarViewWindow(vm.Preferences, "RadarView", "Radar View") { DataContext = vm };
        radar.ShowAndRunLayout();
        var probe = new CheckBox
        {
            Content = "Probe",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var other = new Window
        {
            Width = 200,
            Height = 100,
            Content = probe,
        };
        other.ShowAndRunLayout();
        // A gesture probe in a window of its own: Tapped/DoubleTapped are raised from the pointer-press route's end, as
        // bubble-only events, so a tunnelling pointer block alone lets them through. Its size differs from other's, so
        // the two windows' click points are apart and neither window's clicks count towards the other's double-click.
        int doubleTaps = 0;
        var gestureProbe = new Border { Background = Brushes.Gray };
        gestureProbe.DoubleTapped += (_, _) => doubleTaps++;
        var tapped = new Window
        {
            Width = 300,
            Height = 150,
            Content = gestureProbe,
        };
        tapped.ShowAndRunLayout();
        var firstDoubleClick = new Point(150, 75);
        var secondDoubleClick = new Point(170, 75);
        try
        {
            SettingsWindow dialog = OpenSettings(main);

            Assert.True(radar.IsEnabled, "a popped-out window keeps its look while Settings is open");
            Assert.True(main.IsEnabled);
            Assert.True(other.IsEnabled);
            ClickCenter(other);
            Assert.False(probe.IsChecked, "a click on another window does nothing while Settings is open");
            DoubleClick(tapped, firstDoubleClick);
            Assert.Equal(0, doubleTaps);
            Assert.True(dialog.IsEnabled);

            dialog
                .SectionView(SettingsSectionId.General)
                .FindControl<Button>("ImportExportButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            ImportExportWindow hub = Assert.Single(dialog.OwnedWindows.OfType<ImportExportWindow>());
            Assert.True(hub.IsEnabled, "a window Settings opens stays usable");
            hub.Close();
            Dispatcher.UIThread.RunJobs();

            if (closeButton is null)
            {
                dialog.Close();
                Dispatcher.UIThread.RunJobs();
            }
            else
            {
                Click(dialog, closeButton);
            }

            Assert.False(dialog.IsVisible);
            DoubleClick(tapped, secondDoubleClick);
            Assert.Equal(1, doubleTaps);
            ClickCenter(other);
            Assert.True(probe.IsChecked, "the same click works once Settings has closed");
        }
        finally
        {
            radar.Close();
            other.Close();
            tapped.Close();
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void CloseSettings_WithFocusOnAMenu_FocusesTheCommandInput()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, _) = MainWindowHost.Boot();
        try
        {
            TextBox input = main.FindControl<CommandInputView>("CommandInputView")!.FindControl<TextBox>("CommandInput")!;
            MenuItem tools = main.FindControl<MenuItem>("SettingsMenuItem")!.FindLogicalAncestorOfType<MenuItem>()!;
            tools.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(tools.IsFocused, "the Tools menu header has focus, as when Settings is opened from the menu");

            SettingsWindow dialog = OpenSettings(main);
            Click(dialog, "CancelButton");

            Assert.False(dialog.IsVisible);
            Assert.True(input.IsFocused, "focus does not go back to the menu; the command input gets it");
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    private static void DoubleClick(Window window, Point at)
    {
        for (int press = 0; press < 2; press++)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact(Timeout = 60_000)]
    public void CloseSettings_ReturnsFocusToTheCommandInput()
    {
        using var scope = new PreferencesFileScope();
        (MainWindow main, _) = MainWindowHost.Boot();
        try
        {
            TextBox input = main.FindControl<CommandInputView>("CommandInputView")!.FindControl<TextBox>("CommandInput")!;
            input.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(input.IsFocused);

            SettingsWindow dialog = OpenSettings(main);
            Click(dialog, "CancelButton");

            Assert.False(dialog.IsVisible);
            Assert.True(input.IsFocused, "the command input that had focus before Settings opened has it again");
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    private static void ClickCenter(Window window)
    {
        var center = new Point(window.Bounds.Width / 2, window.Bounds.Height / 2);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static ImportExportWindow OpenToolsImportExport(MainWindow main)
    {
        main.FindControl<MenuItem>("ImportExportMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        return Assert.Single(main.OwnedWindows.OfType<ImportExportWindow>());
    }

    private static SettingsWindow OpenSettings(MainWindow main)
    {
        MenuItem settingsItem = main.FindControl<MenuItem>("SettingsMenuItem")!;
        settingsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        return Assert.Single(main.OwnedWindows.OfType<SettingsWindow>());
    }

    private static object? ShownSection(SettingsWindow dialog) => dialog.FindControl<ContentControl>("SectionHost")!.Content;

    private static void Click(SettingsWindow dialog, string buttonName)
    {
        dialog.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
}
