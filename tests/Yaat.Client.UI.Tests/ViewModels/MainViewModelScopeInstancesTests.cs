using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope.Toolbar;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Extra Scope windows (#2, #3, …) beside the primary Scope: each has its own view model and state; a typed verb acts on the
/// Scope that last had the focus, else the primary; a scenario unload, restart or rewind, a dropped aircraft and the weather
/// push reach every Scope; the measuring surface is the Scope that took it; closing an extra disposes it.
/// </summary>
public class MainViewModelScopeInstancesTests
{
    private const string AunMetar = "METAR KAUN 230235Z AUTO 00000KT 10SM CLR 17/08 A3002";
    private const string SfoMetar = "METAR KSFO 230236Z 29012KT 10SM FEW008 16/12 A2998";

    [AvaloniaFact]
    public async Task OpenExtraScopeView_OpensNumberTwo_WithItsOwnState()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance extra = main.OpenExtraScopeView();
        try
        {
            Assert.Equal(2, extra.Ordinal);
            Assert.Equal("ScopeView#2", extra.GeometryKey);
            Assert.Equal("YAAT Scope #2", extra.Title);
            Assert.Equal(2, extra.Vm.Ordinal);
            Assert.Equal(1, main.PrimaryScope.Ordinal);
            Assert.NotSame(main.PrimaryScope, extra.Vm);
            Assert.NotSame(main.PrimaryScope.DataBlockState, extra.Vm.DataBlockState);
            Assert.Equal([main.PrimaryScope, extra.Vm], main.AllScopeViews);

            main.PrimaryScope.RangeNm = 30;
            extra.Vm.RangeNm = 70;

            Assert.Equal(30, main.PrimaryScope.RangeNm);
            Assert.Equal(70, extra.Vm.RangeNm);
        }
        finally
        {
            main.CloseExtraScopeView(extra);
        }
    }

    [AvaloniaFact]
    public async Task AClosedScopesNumber_IsReused()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance second = main.OpenExtraScopeView();
        ScopeViewInstance third = main.OpenExtraScopeView();
        main.CloseExtraScopeView(second);

        ScopeViewInstance reopened = main.OpenExtraScopeView();
        try
        {
            Assert.Equal(3, third.Ordinal);
            Assert.Equal(2, reopened.Ordinal);
        }
        finally
        {
            main.CloseExtraScopeView(third);
            main.CloseExtraScopeView(reopened);
        }
    }

    [AvaloniaFact]
    public async Task CloseExtraScopeView_DisposesIt_AndDropsItFromEveryScope()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance extra = main.OpenExtraScopeView();

        main.CloseExtraScopeView(extra);

        Assert.True(extra.Vm.IsDisposed);
        Assert.False(main.PrimaryScope.IsDisposed);
        Assert.Empty(main.ExtraScopeViews);
        Assert.Equal([main.PrimaryScope], main.AllScopeViews);
    }

    [AvaloniaFact]
    public async Task ATypedVerb_ActsOnTheFocusedScopeOnly()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance extra = main.OpenExtraScopeView();
        try
        {
            main.PrimaryScope.RangeNm = 40;
            extra.Vm.RangeNm = 40;

            main.ActiveScope = extra.Vm;
            main.RunScopeCommand(".scope range 70");

            Assert.Equal(70, extra.Vm.RangeNm);
            Assert.Equal(40, main.PrimaryScope.RangeNm);

            main.ActiveScope = main.PrimaryScope;
            main.RunScopeCommand(".scope range 20");

            Assert.Equal(20, main.PrimaryScope.RangeNm);
            Assert.Equal(70, extra.Vm.RangeNm);
        }
        finally
        {
            main.CloseExtraScopeView(extra);
        }
    }

    [AvaloniaFact]
    public async Task AToolbarOrCardAction_ActsOnItsOwnScope_WhicheverScopeHasTheFocus()
    {
        MainViewModel main = await MainAsync();
        main.ScopeMaps.ApplyVideoMapsDtoForTesting(
            new FacilityVideoMapsDto("ZOA", "NCT", [new StarsAreaDto("A", "Area A", 37.62, -122.38, 60, [])], [MapInfo(2), MapInfo(10)], []),
            null,
            null
        );
        ScopeViewInstance extra = main.OpenExtraScopeView();
        try
        {
            main.PrimaryScope.RangeNm = 40;
            extra.Vm.RangeNm = 40;
            main.PrimaryScope.SetViewOpen(ScopeToolbarButtons.ViewSsaName, true);
            extra.Vm.SetViewOpen(ScopeToolbarButtons.ViewSsaName, true);
            main.ActiveScope = main.PrimaryScope;

            extra.Vm.StepRange(+1);
            extra.Vm.Press(ScopeToolbarButtons.MapToggle(10, "MAP10"));
            extra.Vm.DockViews.Concat(extra.Vm.FloatingViews).Single(v => v.Name == ScopeToolbarButtons.ViewSsaName).CloseCommand.Execute(null);

            Assert.Equal(41, extra.Vm.RangeNm);
            Assert.True(extra.Vm.FindMapToggle("10") is { IsOn: true });
            Assert.False(extra.Vm.IsViewOpen(ScopeToolbarButtons.ViewSsaName));
            Assert.Equal(40, main.PrimaryScope.RangeNm);
            Assert.All(main.PrimaryScope.MapToggles, t => Assert.False(t.IsOn));
            Assert.True(main.PrimaryScope.IsViewOpen(ScopeToolbarButtons.ViewSsaName));
        }
        finally
        {
            main.CloseExtraScopeView(extra);
        }
    }

    private static VideoMapInfoDto MapInfo(int starsId) =>
        new($"m{starsId}", $"Map {starsId}", $"MAP{starsId}", [], "A", starsId, AlwaysVisible: false, TdmOnly: false);

    [AvaloniaFact]
    public async Task ClosingTheFocusedScope_HandsTheVerbsBackToThePrimary()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance extra = main.OpenExtraScopeView();
        main.ActiveScope = extra.Vm;

        main.CloseExtraScopeView(extra);
        main.RunScopeCommand(".scope range 55");

        Assert.Null(main.ActiveScope);
        Assert.Same(main.PrimaryScope, main.OpenScope);
        Assert.Equal(55, main.PrimaryScope.RangeNm);
    }

    [AvaloniaFact]
    public async Task Unload_ClearsEveryScopesTrackState()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance extra = main.OpenExtraScopeView();
        try
        {
            SeedBoth(main, extra.Vm);

            main.ClearScenarioState();

            AssertCleared(main.PrimaryScope);
            AssertCleared(extra.Vm);
        }
        finally
        {
            main.CloseExtraScopeView(extra);
        }
    }

    [AvaloniaFact]
    public async Task Restart_ClearsEveryScopesTrackState()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance extra = main.OpenExtraScopeView();
        try
        {
            SeedBoth(main, extra.Vm);

            main.ApplyScenarioRestart([], null);

            AssertCleared(main.PrimaryScope);
            AssertCleared(extra.Vm);
        }
        finally
        {
            main.CloseExtraScopeView(extra);
        }
    }

    [AvaloniaFact]
    public async Task Rewind_ClearsEveryScopesTrackState()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance extra = main.OpenExtraScopeView();
        try
        {
            SeedBoth(main, extra.Vm);

            main.ApplyScenarioRewind([]);

            AssertCleared(main.PrimaryScope);
            AssertCleared(extra.Vm);
        }
        finally
        {
            main.CloseExtraScopeView(extra);
        }
    }

    [AvaloniaFact]
    public async Task TheWeatherPush_ReachesEveryScope()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance extra = main.OpenExtraScopeView();
        try
        {
            main.ApplyWeatherChanged(new WeatherChangedDto("Live Weather", null, null, [AunMetar, SfoMetar], null, null, null));

            Assert.Equal(2, main.PrimaryScope.WeatherLines.Count);
            Assert.Equal(2, extra.Vm.WeatherLines.Count);
        }
        finally
        {
            main.CloseExtraScopeView(extra);
        }
    }

    [AvaloniaFact]
    public async Task TheMeasureSurface_IsTheScopeThatTookIt_WhicheverScopeHasTheFocus()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance second = main.OpenExtraScopeView();
        ScopeViewInstance third = main.OpenExtraScopeView();
        try
        {
            main.ActiveScope = third.Vm;
            second.Vm.ActivateMeasureSurface();

            Assert.Same(second.Vm, main.ActiveMeasureScope);
            Assert.True(second.Vm.IsActiveMeasureSurface);
            Assert.False(main.PrimaryScope.IsActiveMeasureSurface);
            Assert.False(third.Vm.IsActiveMeasureSurface);

            main.CloseExtraScopeView(third);

            Assert.Equal(RblView.Scope, main.ActiveMeasureView);
            Assert.Same(second.Vm, main.ActiveMeasureScope);
            Assert.True(second.Vm.IsActiveMeasureSurface);
        }
        finally
        {
            main.CloseExtraScopeView(second);
        }
    }

    [AvaloniaFact]
    public async Task ClosingTheScopeThatTookTheMeasureSurface_FallsBackToThePrimary()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance extra = main.OpenExtraScopeView();
        extra.Vm.ActivateMeasureSurface();

        main.CloseExtraScopeView(extra);

        Assert.Equal(RblView.Scope, main.ActiveMeasureView);
        Assert.Same(main.PrimaryScope, main.ActiveMeasureScope);
        Assert.Null(main.HeldMeasureScope);
        Assert.True(main.PrimaryScope.IsActiveMeasureSurface);
    }

    [AvaloniaFact]
    public async Task ADroppedAircraft_ClearsItsRingsAndPtlInEveryScope()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance extra = main.OpenExtraScopeView();
        try
        {
            SeedBoth(main, extra.Vm);

            main.OnAircraftDeleted("UAL1");
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(main.Aircraft, a => a.Callsign == "UAL1");
            foreach (ScopeViewModel scope in new[] { main.PrimaryScope, extra.Vm })
            {
                Assert.False(scope.LocalRings.ContainsKey("UAL1"));
                Assert.DoesNotContain("UAL1", scope.LocalPtlCallsigns);
            }
        }
        finally
        {
            main.CloseExtraScopeView(extra);
        }
    }

    [AvaloniaFact]
    public async Task ReconcileExtraScopeViews_OpensAListedOrdinal_ReadingItsOwnSlot()
    {
        string root = ScopeStoreFixture.NewStoreRoot();
        using IDisposable store = ScopeStoreFixture.UseStore(root);
        MainViewModel main = await MainAsync();
        try
        {
            await ScopeStoreFixture.OnSector44Async(main);
            ScopeStoreFixture.SeedExtraState(main, root, 3, 77);

            main.ReconcileExtraScopeViews([new SavedExtraView(3, "")]);

            ScopeViewInstance opened = Assert.Single(main.ExtraScopeViews);
            Assert.Equal(3, opened.Ordinal);
            Assert.Equal(3, opened.Vm.Ordinal);
            // Headless, the map source never seeds, so the saved range is held for the seed: the state the Scope holds.
            Assert.Equal(77, opened.Vm.CapturePrefSet(null).RangeNm);
        }
        finally
        {
            CloseEveryExtra(main);
            ScopeStoreFixture.DeleteStoreRoot(root);
        }
    }

    [AvaloniaFact]
    public async Task ReconcileExtraScopeViews_ClosesAnOpenScopeNotListed_DisposingIt()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance second = main.OpenExtraScopeView();
        ScopeViewInstance third = main.OpenExtraScopeView();
        try
        {
            main.ReconcileExtraScopeViews([new SavedExtraView(2, "")]);

            Assert.Equal([second], main.ExtraScopeViews);
            Assert.True(third.Vm.IsDisposed);
            Assert.Equal([main.PrimaryScope, second.Vm], main.AllScopeViews);
        }
        finally
        {
            CloseEveryExtra(main);
        }
    }

    [AvaloniaFact]
    public async Task ReconcileExtraScopeViews_LeavesAnOpenListedScopeUntouched()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance extra = main.OpenExtraScopeView();
        extra.Vm.RangeNm = 70;
        try
        {
            main.ReconcileExtraScopeViews([new SavedExtraView(2, "")]);

            Assert.Same(extra, Assert.Single(main.ExtraScopeViews));
            Assert.False(extra.Vm.IsDisposed);
            Assert.Equal(70, extra.Vm.RangeNm);
        }
        finally
        {
            CloseEveryExtra(main);
        }
    }

    [AvaloniaFact]
    public async Task ReconcileExtraScopeViews_IgnoresOrdinalsBelowTwo()
    {
        MainViewModel main = await MainAsync();
        try
        {
            main.ReconcileExtraScopeViews([new SavedExtraView(1, ""), new SavedExtraView(0, "")]);

            Assert.Empty(main.ExtraScopeViews);
            Assert.Equal([main.PrimaryScope], main.AllScopeViews);
        }
        finally
        {
            CloseEveryExtra(main);
        }
    }

    [AvaloniaFact]
    public async Task ReconcileExtraScopeViews_OpensAnOrdinalListedTwice_Once()
    {
        MainViewModel main = await MainAsync();
        try
        {
            main.ReconcileExtraScopeViews([new SavedExtraView(2, ""), new SavedExtraView(2, "")]);

            Assert.Equal([2], main.ExtraScopeViews.Select(i => i.Ordinal));
        }
        finally
        {
            CloseEveryExtra(main);
        }
    }

    [AvaloniaFact]
    public async Task ReconcileExtraScopeViews_AnEmptyList_ClosesEveryExtra()
    {
        MainViewModel main = await MainAsync();
        ScopeViewInstance second = main.OpenExtraScopeView();
        ScopeViewInstance third = main.OpenExtraScopeView();
        try
        {
            main.ReconcileExtraScopeViews([]);

            Assert.Empty(main.ExtraScopeViews);
            Assert.True(second.Vm.IsDisposed);
            Assert.True(third.Vm.IsDisposed);
            Assert.False(main.PrimaryScope.IsDisposed);
        }
        finally
        {
            CloseEveryExtra(main);
        }
    }

    private static void CloseEveryExtra(MainViewModel main)
    {
        foreach (ScopeViewInstance instance in main.ExtraScopeViews.ToList())
        {
            main.CloseExtraScopeView(instance);
        }
    }

    // Both Scopes get a dwell lock, a block offset, rings and a PTL on UAL1, each through its own typed verbs.
    private static void SeedBoth(MainViewModel main, ScopeViewModel extra)
    {
        AddAircraft(main, "UAL1", 5000);
        foreach (ScopeViewModel scope in new[] { main.PrimaryScope, extra })
        {
            main.ActiveScope = scope;
            scope.DataBlockState.ToggleDwellLock("UAL1");
            scope.DataBlockState.SetManualOffset("UAL1", new SKPoint(10, 20));
            main.RunScopeCommand(".scope rings dri5 UAL1");
            main.RunScopeCommand(".scope ptl UAL1");
            Assert.Single(scope.LocalRings);
            Assert.Single(scope.LocalPtlCallsigns);
        }
    }

    private static void AssertCleared(ScopeViewModel scope)
    {
        Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
        Assert.Empty(scope.DataBlockState.ManualOffsets);
        Assert.Empty(scope.LocalRings);
        Assert.Empty(scope.LocalPtlCallsigns);
    }
}
