using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The primary Scope lives with the main view model: it is the main window's tab at index 2, pops out into its own window
/// (View › Windows and the pop-out chord) and docks back when that window closes, the same view model and state throughout.
/// </summary>
public class ScopeTabHostingTests
{
    private const int ScopeTabIndex = 2;

    private static TabControl Tabs(MainWindow main) => main.GetLogicalDescendants().OfType<TabControl>().Single(t => t.Name == "MainTabControl");

    private static TabItem ScopeTab(MainWindow main) => Tabs(main).Items.OfType<TabItem>().ElementAt(ScopeTabIndex);

    [AvaloniaFact]
    public void PrimaryScope_ExistsOnceTheViewModelIsBuilt_AndTheVerbsActOnIt()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        Assert.NotNull(vm.PrimaryScope);
        Assert.Same(vm.PrimaryScope, vm.OpenScope);
        Assert.Equal([vm.PrimaryScope], vm.AllScopeViews);
        Assert.False(vm.IsScopePoppedOut);
    }

    [AvaloniaFact]
    public void ScopeTab_IsTabTwo_ShowingThePrimaryScope()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.IsScopePoppedOut = false;
            vm.SelectedTabIndex = ScopeTabIndex;
            MainWindowHost.Pump();

            TabItem tab = ScopeTab(main);
            Assert.Equal(MainWindow.ScopeTabHeader, tab.Header);
            Assert.True(tab.IsVisible);
            ScopeHost host = Assert.IsType<ScopeHost>(tab.Content);
            Assert.Same(vm.PrimaryScope, host.DataContext);
            Assert.Single(main.GetVisualDescendants().OfType<ScopeView>());
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void PoppingOut_HidesTheTabAndOpensOneScopeWindow()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.IsScopePoppedOut = false;
            vm.SelectedTabIndex = ScopeTabIndex;
            MainWindowHost.Pump();
            Assert.Null(main.ScopeWindow);

            vm.IsScopePoppedOut = true;
            MainWindowHost.Pump();

            ScopeWindow window = Assert.IsType<ScopeWindow>(main.ScopeWindow);
            Assert.True(window.IsVisible);
            Assert.Same(vm.PrimaryScope, window.DataContext);
            Assert.False(ScopeTab(main).IsVisible);
            Assert.NotEqual(ScopeTabIndex, vm.SelectedTabIndex);
            Assert.True(vm.Preferences.IsScopePoppedOut);

            vm.IsScopePoppedOut = false;
            MainWindowHost.Pump();

            Assert.Null(main.ScopeWindow);
            Assert.False(window.IsVisible);
            Assert.True(ScopeTab(main).IsVisible);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void PopOutThenTitleBarClose_DocksBack_UnbindsTheWindow_AndKeepsTheState() => AssertDocksBack(main => main.ScopeWindow!.Close());

    [AvaloniaFact]
    public void PopOutThenDockByTheMenuItem_DocksBack_UnbindsTheWindow_AndKeepsTheState() =>
        AssertDocksBack(main => main.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Name == "PopOutScopeMenuItem").IsChecked = false);

    [AvaloniaFact]
    public void PopOutThenTheChordInsideThePopOut_DocksBack_UnbindsTheWindow_AndKeepsTheState() =>
        AssertDocksBack(main => main.ScopeWindow!.DispatchKey(Key.R, RawInputModifiers.Control | RawInputModifiers.Shift));

    // Pops the primary Scope out, docks it back through dockBack, and checks the tab is back with the same Scope and its
    // state, and the closed window holds no binding to it.
    private static void AssertDocksBack(Action<MainWindow> dockBack)
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            ScopeViewModel primary = vm.PrimaryScope;
            vm.IsScopePoppedOut = false;
            MainWindowHost.Pump();
            vm.IsScopePoppedOut = true;
            MainWindowHost.Pump();
            ScopeWindow window = main.ScopeWindow!;
            primary.RangeNm = 77;

            dockBack(main);
            MainWindowHost.Pump();

            Assert.False(vm.IsScopePoppedOut);
            Assert.Null(main.ScopeWindow);
            Assert.False(window.IsVisible);
            Assert.Null(window.DataContext);
            Assert.Null(window.ScopeHost.ScopeView.DataContext);
            Assert.True(ScopeTab(main).IsVisible);
            Assert.Same(primary, vm.PrimaryScope);
            Assert.Equal(77, primary.RangeNm);

            // Still following the main view model: a closed pop-out did not dispose the docked Scope.
            var ac = new AircraftModel { Callsign = "UAL1" };
            vm.Aircraft.Add(ac);
            vm.SelectedAircraft = ac;
            Assert.Same(ac, primary.SelectedAircraft);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void PopOutChord_TogglesTheScopeBetweenItsWindowAndTheTab()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.IsScopePoppedOut = false;
            MainWindowHost.Pump();
            Assert.Equal("Ctrl+Shift+R", vm.Preferences.PopOutRadarViewKey);
            RawInputModifiers chord = RawInputModifiers.Control | RawInputModifiers.Shift;

            main.DispatchKey(Key.R, chord);
            MainWindowHost.Pump();

            Assert.True(vm.IsScopePoppedOut);
            Assert.NotNull(main.ScopeWindow);

            main.DispatchKey(Key.R, chord);
            MainWindowHost.Pump();

            Assert.False(vm.IsScopePoppedOut);
            Assert.Null(main.ScopeWindow);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task ClosingThePopOut_SavesThePrimaryScopesState()
    {
        string root = NewStoreRoot();
        Func<(ScopePrefSetStore?, ScopeProfileStore?)> previous = MainViewModel.ScopeStoreFactory;
        MainViewModel.ScopeStoreFactory = () => (Store(root), null);
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            await OnSector44Async(vm);
            vm.IsScopePoppedOut = false;
            vm.IsScopePoppedOut = true;
            MainWindowHost.Pump();
            vm.PrimaryScope.RangeNm = 33;

            main.ScopeWindow!.Close();
            MainWindowHost.Pump();

            Assert.Equal(33, Store(root).LoadCurrent("ZOA", ScopePrefSetStore.PrimarySlot)!.RangeNm);
        }
        finally
        {
            MainViewModel.ScopeStoreFactory = previous;
            MainWindowHost.CloseAll(main);
            DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task AppExit_SavesThePrimaryScopesState()
    {
        string root = NewStoreRoot();
        Func<(ScopePrefSetStore?, ScopeProfileStore?)> previous = MainViewModel.ScopeStoreFactory;
        MainViewModel.ScopeStoreFactory = () => (Store(root), null);
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            await OnSector44Async(vm);
            vm.PrimaryScope.RangeNm = 44;

            MainWindowHost.CloseAll(main);

            Assert.Equal(44, Store(root).LoadCurrent("ZOA", ScopePrefSetStore.PrimarySlot)!.RangeNm);
        }
        finally
        {
            MainViewModel.ScopeStoreFactory = previous;
            if (main.IsVisible)
            {
                MainWindowHost.CloseAll(main);
            }

            DeleteStoreRoot(root);
        }
    }

    private static readonly ActivePositionDto Sector44 = new(
        Callsign: "OAK_44_CTR",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    // The primary Scope's state is saved per ERAM facility, so it needs a viewer on one: ZOA's sector 44.
    private static async Task OnSector44Async(MainViewModel vm)
    {
        await vm.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        vm.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], Sector44.HandoffCode, Sector44, 3, NoStarsDisplay: true));
        Assert.Equal("ZOA", vm.PrimaryScope.Viewer.EramFacilityId);
    }

    private static string NewStoreRoot() => Path.Combine(Path.GetTempPath(), "yaat-scope-hosting-tests", Guid.NewGuid().ToString("N"));

    private static ScopePrefSetStore Store(string root) => new(root, AppLog.CreateLogger<ScopePrefSetStore>());

    private static void DeleteStoreRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
