using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Scope;
using static Yaat.Client.UI.Tests.Helpers.ScopeStoreFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// A layout carries the Scope: saving one records whether the primary Scope is popped out and which extra Scope windows
/// are open; applying it pops the Scope in or out and opens or closes extra Scope windows, each with its own saved state
/// and the layout's geometry. A layout saved before the Scope was a layout part leaves the Scopes alone, and no layout
/// opens a Radar window.
/// </summary>
public class ScopeLayoutTests
{
    private static readonly HashSet<string> ScopeRowsOnly = [ViewSettingsCopyCatalog.LayoutScopeKey, ViewSettingsCopyCatalog.LayoutExtraScopeKey];

    [AvaloniaFact]
    public void CaptureCurrent_RecordsScopePopOutAndExtras()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.IsScopePoppedOut = true;
            vm.IsRadarViewPoppedOut = true;
            vm.OpenExtraScopeView();
            vm.OpenExtraScopeView();
            MainWindowHost.Pump();

            SavedLayout layout = new LayoutService(vm.Preferences).CaptureCurrent("scopes", vm);

            Assert.Equal(true, layout.IsScopePoppedOut);
            Assert.Equal([new SavedExtraView(2, ""), new SavedExtraView(3, "")], layout.ExtraScopeViews);
            Assert.Contains("ScopeView", layout.WindowGeometries.Keys);
            Assert.Contains("ScopeView#2", layout.WindowGeometries.Keys);
            Assert.Contains("ScopeView#3", layout.WindowGeometries.Keys);
            Assert.False(layout.IsRadarViewPoppedOut);
            Assert.Empty(layout.ExtraRadarViews);
        }
        finally
        {
            TearDown(main, vm);
        }
    }

    [AvaloniaFact]
    public async Task ApplyLayout_ReopensExtraScopeWindows_WithTheirGeometry()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            var layout = new SavedLayout
            {
                Name = "two-scopes",
                ExtraScopeViews = [new SavedExtraView(2, ""), new SavedExtraView(3, "")],
                WindowGeometries = new() { ["ScopeView#2"] = Geometry(900, 700), ["ScopeView#3"] = Geometry(1000, 640) },
            };

            await main.ApplyLayoutAsync(vm, layout, ViewSettingsCopyCatalog.AllLayoutKeys(layout));
            MainWindowHost.Pump();

            List<ScopeWindow> windows = [.. main.ExtraScopeWindows.OrderBy(p => p.Key.Ordinal).Select(p => p.Value)];
            Assert.Equal(["YAAT Scope #2", "YAAT Scope #3"], windows.Select(w => w.Title));
            Assert.Equal([900.0, 1000.0], windows.Select(w => w.Width));
            Assert.Equal([2, 3], vm.ExtraScopeViews.Select(i => i.Ordinal));
        }
        finally
        {
            TearDown(main, vm);
        }
    }

    [AvaloniaFact]
    public async Task ApplyLayout_ClosesExtrasTheLayoutDoesNotList_AndKeepsSurvivors()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            ScopeViewInstance second = vm.OpenExtraScopeView();
            ScopeViewInstance third = vm.OpenExtraScopeView();
            MainWindowHost.Pump();

            await main.ApplyLayoutAsync(vm, new SavedLayout { Name = "one", ExtraScopeViews = [new SavedExtraView(2, "")] }, ScopeRowsOnly);
            MainWindowHost.Pump();

            Assert.Same(second, Assert.Single(vm.ExtraScopeViews));
            Assert.Same(second, Assert.Single(main.ExtraScopeWindows).Key);
            Assert.True(third.Vm.IsDisposed);
            Assert.False(second.Vm.IsDisposed);
        }
        finally
        {
            TearDown(main, vm);
        }
    }

    [AvaloniaFact]
    public async Task ApplyLayout_FlipsScopePopOut_BothWays()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.IsScopePoppedOut = false;
            MainWindowHost.Pump();

            await main.ApplyLayoutAsync(vm, new SavedLayout { Name = "out", IsScopePoppedOut = true }, ScopeRowsOnly);
            MainWindowHost.Pump();

            Assert.True(vm.IsScopePoppedOut);
            Assert.NotNull(main.ScopeWindow);

            await main.ApplyLayoutAsync(vm, new SavedLayout { Name = "in", IsScopePoppedOut = false }, ScopeRowsOnly);
            MainWindowHost.Pump();

            Assert.False(vm.IsScopePoppedOut);
            Assert.Null(main.ScopeWindow);
        }
        finally
        {
            TearDown(main, vm);
        }
    }

    [AvaloniaFact]
    public async Task ApplyLayout_WithNullScopeFields_LeavesScopesAlone()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            vm.IsScopePoppedOut = true;
            ScopeViewInstance extra = vm.OpenExtraScopeView();
            MainWindowHost.Pump();
            var old = new SavedLayout { Name = "old" };

            await main.ApplyLayoutAsync(vm, old, ViewSettingsCopyCatalog.AllLayoutKeys(old));
            MainWindowHost.Pump();

            Assert.True(vm.IsScopePoppedOut);
            Assert.NotNull(main.ScopeWindow);
            Assert.Same(extra, Assert.Single(vm.ExtraScopeViews));
            Assert.False(extra.Vm.IsDisposed);
        }
        finally
        {
            TearDown(main, vm);
        }
    }

    [AvaloniaFact]
    public async Task ApplyLayout_NeverOpensARadarWindow()
    {
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            var layout = new SavedLayout
            {
                Name = "radar",
                IsRadarViewPoppedOut = true,
                ExtraRadarViews = [new SavedExtraView(2, "OAK")],
            };

            await main.ApplyLayoutAsync(vm, layout, ViewSettingsCopyCatalog.AllLayoutKeys(layout));
            MainWindowHost.Pump();

            Assert.False(vm.IsRadarViewPoppedOut);
        }
        finally
        {
            TearDown(main, vm);
        }
    }

    [AvaloniaFact]
    public async Task ReopenedExtra_OpensWithItsOwnSavedState()
    {
        string root = NewStoreRoot();
        using IDisposable store = UseStore(root);
        (MainWindow main, MainViewModel vm) = MainWindowHost.Boot();
        try
        {
            await OnSector44Async(vm);
            SeedExtraState(vm, root, 2, 77);
            MainWindowHost.Pump();
            vm.PrimaryScope.RangeNm = 33;

            await main.ApplyLayoutAsync(vm, new SavedLayout { Name = "two", ExtraScopeViews = [new SavedExtraView(2, "")] }, ScopeRowsOnly);
            MainWindowHost.Pump();

            ScopeViewInstance reopened = Assert.Single(vm.ExtraScopeViews);
            Assert.Equal(2, reopened.Ordinal);
            // Headless, the map source never seeds, so the saved range is held for the seed: the state the Scope holds.
            Assert.Equal(77, reopened.Vm.CapturePrefSet(null).RangeNm);
            Assert.Equal(33, vm.PrimaryScope.RangeNm);
        }
        finally
        {
            TearDown(main, vm);
            DeleteStoreRoot(root);
        }
    }

    private static SavedWindowGeometry Geometry(double width, double height) =>
        new()
        {
            X = 40,
            Y = 40,
            Width = width,
            Height = height,
        };

    private static void TearDown(MainWindow main, MainViewModel vm)
    {
        foreach (ScopeWindow window in main.ExtraScopeWindows.Values.ToList())
        {
            window.Close();
        }

        vm.IsScopePoppedOut = false;
        vm.IsRadarViewPoppedOut = false;
        MainWindowHost.Pump();
        MainWindowHost.CloseAll(main);
    }
}
