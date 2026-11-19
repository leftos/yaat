using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// View › Windows › "New Radar window…" opens another Scope of the same facility in its own window, "YAAT Radar #n", with
/// its own view model and its own saved state; its canvas taking the focus makes it the Scope the typed verbs act on, and
/// closing the window disposes it.
/// </summary>
public class ScopeExtraWindowTests
{
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
            Assert.Equal("YAAT Radar #2", window.Title);
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

    // Each window builds its own canvas, and closing it disposes that canvas once the window's render work has detached;
    // a second dispose is harmless.
    [AvaloniaFact]
    public void ClosingAnExtraScopeWindow_DisposesItsCanvas()
    {
        (MainWindow main, MainViewModel _) = MainWindowHost.Boot();
        try
        {
            ScopeWindow window = ClickNewScopeWindow(main);
            ScopeCanvas canvas = window.ScopeHost.ScopeView.Canvas;
            Assert.False(canvas.IsDisposed);

            window.Close();
            MainWindowHost.Pump();

            Assert.True(canvas.IsDisposed);
            canvas.Dispose();
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
    public void AnAnchorAndALinePickedOnScopeTwo_DrawOnScopeTwoOnly()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.IsScopePoppedOut = false;
            vm.SelectedTabIndex = MainWindow.ScopeTabIndex;
            MainWindowHost.Pump();
            TabItem scopeTab = main.GetLogicalDescendants().OfType<TabItem>().Single(t => t.Name == "ScopeTab");
            ScopeCanvas primary = Assert.IsType<ScopeHost>(scopeTab.Content).ScopeView.Canvas;
            ScopeWindow window = ClickNewScopeWindow(main);
            ScopeViewModel extra = Assert.Single(vm.ExtraScopeViews).Vm;
            ScopeCanvas second = window.ScopeHost.ScopeView.Canvas;
            primary.MeasureCursor = new Point(300, 300);
            second.MeasureCursor = new Point(300, 300);

            extra.PickMeasurePoint(RblEndpoint.AtPoint(new LatLon(37.7, -122.2), ""));
            MainWindowHost.Pump();

            Assert.NotNull(second.RenderSnapshotForTesting().PendingRangeBearingLine);
            Assert.Null(primary.RenderSnapshotForTesting().PendingRangeBearingLine);

            extra.PickMeasurePoint(RblEndpoint.AtPoint(new LatLon(37.6, -122.1), ""));
            MainWindowHost.Pump();

            Assert.Single(second.RenderSnapshotForTesting().RangeBearingLines);
            Assert.Empty(primary.RenderSnapshotForTesting().RangeBearingLines);
        }
        finally
        {
            CloseExtras(main);
            MainWindowHost.CloseAll(main);
        }
    }

    // Closing an extra Scope drops the lines it placed and cancels the anchor it holds, so neither keeps a slot nor completes
    // on another Scope.
    [AvaloniaFact]
    public void ClosingAnExtraScope_DropsItsLinesAndCancelsItsAnchor()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            ScopeWindow window = ClickNewScopeWindow(main);
            ScopeViewModel extra = Assert.Single(vm.ExtraScopeViews).Vm;
            extra.PickMeasurePoint(RblEndpoint.AtPoint(new LatLon(37.7, -122.2), ""));
            extra.PickMeasurePoint(RblEndpoint.AtPoint(new LatLon(37.6, -122.1), ""));
            extra.PickMeasurePoint(RblEndpoint.AtPoint(new LatLon(37.5, -122.0), ""));
            Assert.Single(vm.Measure.Lines);
            Assert.NotNull(vm.Measure.Anchor);

            window.Close();
            MainWindowHost.Pump();

            Assert.Empty(vm.Measure.Lines);
            Assert.Null(vm.Measure.Anchor);

            vm.PrimaryScope.PickMeasurePoint(RblEndpoint.AtPoint(new LatLon(37.4, -121.9), ""));

            Assert.Empty(vm.Measure.Lines);
            Assert.Same(vm.PrimaryScope, Assert.NotNull(vm.Measure.Anchor).Surface);
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
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        try
        {
            var vm = new MainViewModel(new Fakes.FakeFilePickerService());
            await ScopeStoreFixture.OnSector44Async(vm);
            ScopeViewInstance extra = vm.OpenExtraScopeView();
            Assert.Equal("ZOA", extra.Vm.Viewer.EramFacilityId);
            vm.PrimaryScope.RangeNm = 33;
            extra.Vm.RangeNm = 77;

            vm.CloseExtraScopeView(extra);

            Assert.Equal(77, ScopeStoreFixture.Store(root).LoadCurrent("ZOA", 2)!.RangeNm);
            Assert.Null(ScopeStoreFixture.Store(root).LoadCurrent("ZOA", ScopePrefSetStore.PrimarySlot));

            vm.PrimaryScope.SaveState();

            Assert.Equal(33, ScopeStoreFixture.Store(root).LoadCurrent("ZOA", ScopePrefSetStore.PrimarySlot)!.RangeNm);
            Assert.Equal(77, ScopeStoreFixture.Store(root).LoadCurrent("ZOA", 2)!.RangeNm);
            Assert.Null(ScopeStoreFixture.Store(root).LoadCurrent("ZOA", 3));
            vm.PrimaryScope.Dispose();
        }
        finally
        {
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }
}
