using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// View › Windows › "New Scope window…" opens another Scope of the same facility in its own window, "YAAT Scope #n", with
/// its own view model and its own saved state; its canvas taking the focus makes it the Scope the typed verbs act on, and
/// closing the window disposes it.
/// </summary>
public class ScopeExtraWindowTests
{
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

    private static MenuItem NewScopeWindowItem(MainWindow main) =>
        main.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header is string s && s == MainWindow.NewScopeWindowMenuHeader);

    private static ScopeWindow ClickNewScopeWindow(MainWindow main)
    {
        NewScopeWindowItem(main).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        MainWindowHost.Pump();
        return main.ExtraScopeWindows.Values.Last();
    }

    private static void CloseExtras(MainWindow main)
    {
        foreach (ScopeWindow window in main.ExtraScopeWindows.Values.ToList())
        {
            window.Close();
        }

        MainWindowHost.Pump();
    }

    [AvaloniaFact]
    public void NewScopeWindow_OpensScopeNumberTwo_InItsOwnWindow()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            ScopeWindow window = ClickNewScopeWindow(main);

            ScopeViewInstance instance = Assert.Single(vm.ExtraScopeViews);
            Assert.Equal("YAAT Scope #2", window.Title);
            Assert.Equal("ScopeView#2", window.WindowGeometryKey);
            Assert.Same(instance.Vm, window.DataContext);
            Assert.NotSame(vm.PrimaryScope, instance.Vm);
            Assert.True(window.IsVisible);
        }
        finally
        {
            CloseExtras(main);
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void ClosingTheWindow_DisposesItsScope_AndDropsIt()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            ScopeWindow window = ClickNewScopeWindow(main);
            ScopeViewModel scope = Assert.Single(vm.ExtraScopeViews).Vm;

            window.Close();
            MainWindowHost.Pump();

            Assert.True(scope.IsDisposed);
            Assert.Empty(vm.ExtraScopeViews);
            Assert.Empty(main.ExtraScopeWindows);
            Assert.Equal([vm.PrimaryScope], vm.AllScopeViews);
        }
        finally
        {
            CloseExtras(main);
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public void FocusingAScopesCanvas_MakesItTheScopeTheVerbsActOn_AndClosingItHandsThemBack()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            ScopeWindow window = ClickNewScopeWindow(main);
            ScopeViewModel extra = Assert.Single(vm.ExtraScopeViews).Vm;
            vm.PrimaryScope.RangeNm = 40;

            Assert.True(window.ScopeHost.ScopeView.Canvas.Focus());
            Dispatcher.UIThread.RunJobs();
            vm.RunScopeCommand(".scope range 70");

            Assert.Same(extra, vm.ActiveScope);
            Assert.Equal(70, extra.RangeNm);
            Assert.Equal(40, vm.PrimaryScope.RangeNm);

            window.Close();
            MainWindowHost.Pump();
            vm.RunScopeCommand(".scope range 20");

            Assert.Same(vm.PrimaryScope, vm.OpenScope);
            Assert.Equal(20, vm.PrimaryScope.RangeNm);
        }
        finally
        {
            CloseExtras(main);
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task AnExtraScopesState_SavesInItsOwnSlot_ApartFromThePrimarys()
    {
        string root = Path.Combine(Path.GetTempPath(), "yaat-scope-extra-tests", Guid.NewGuid().ToString("N"));
        Func<(ScopePrefSetStore?, ScopeProfileStore?)> previous = MainViewModel.ScopeStoreFactory;
        MainViewModel.ScopeStoreFactory = () => (Store(root), null);
        try
        {
            var vm = new MainViewModel(new Fakes.FakeFilePickerService());
            await vm.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            vm.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], Sector44.HandoffCode, Sector44, 3, NoStarsDisplay: true));
            ScopeViewInstance extra = vm.OpenExtraScopeView();
            Assert.Equal("ZOA", extra.Vm.Viewer.EramFacilityId);
            vm.PrimaryScope.RangeNm = 33;
            extra.Vm.RangeNm = 77;

            vm.CloseExtraScopeView(extra);

            Assert.Equal(77, Store(root).LoadCurrent("ZOA", 2)!.RangeNm);
            Assert.Null(Store(root).LoadCurrent("ZOA", ScopePrefSetStore.PrimarySlot));

            vm.PrimaryScope.SaveState();

            Assert.Equal(33, Store(root).LoadCurrent("ZOA", ScopePrefSetStore.PrimarySlot)!.RangeNm);
            Assert.Equal(77, Store(root).LoadCurrent("ZOA", 2)!.RangeNm);
            Assert.Null(Store(root).LoadCurrent("ZOA", 3));
            vm.PrimaryScope.Dispose();
        }
        finally
        {
            MainViewModel.ScopeStoreFactory = previous;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ScopePrefSetStore Store(string root) => new(root, AppLog.CreateLogger<ScopePrefSetStore>());
}
