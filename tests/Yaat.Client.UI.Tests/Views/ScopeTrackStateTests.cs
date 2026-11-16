using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Pins that a scenario unload, a restart and a rewind, by another client or by this one, drop the Scope's whole per-track
/// view state: its data-block state (dwell locks, block offsets), its rings and its PTLs.
/// </summary>
public class ScopeTrackStateTests
{
    [AvaloniaFact]
    public async Task Unload_ClearsDwellLocksOffsetsRingsAndPtl()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            SeedPerTrackState(main, scope);

            main.ClearScenarioState();

            AssertTrackStateCleared(scope);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Restart_ClearsDwellLocksOffsetsRingsAndPtl()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            SeedPerTrackState(main, scope);

            main.ApplyScenarioRestart([], null);

            AssertTrackStateCleared(scope);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RewindByAnotherClient_ClearsDwellLocksOffsetsRingsAndPtl()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            SeedPerTrackState(main, scope);

            main.ApplyScenarioRewind([]);

            AssertTrackStateCleared(scope);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task OwnRewind_ClearsDwellLocksOffsetsRingsAndPtl()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            SeedPerTrackState(main, scope);

            main.ApplyRewindResult(new RewindResultDto(true, null, [], [], Aircraft: [], SessionStartUtc: null), 0, "Rewound");

            AssertTrackStateCleared(scope);
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static void SeedPerTrackState(MainViewModel main, ScopeViewModel scope)
    {
        AddAircraft(main, "UAL1", 5000);
        scope.DataBlockState.ToggleDwellLock("UAL1");
        scope.DataBlockState.SetManualOffset("UAL1", new SKPoint(10, 20));
        main.RunScopeCommand(".scope rings dri5 UAL1");
        main.RunScopeCommand(".scope ptl UAL1");
        Assert.Single(scope.DataBlockState.DwellLockedCallsigns);
        Assert.Single(scope.DataBlockState.ManualOffsets);
        Assert.Single(scope.LocalRings);
        Assert.Single(scope.LocalPtlCallsigns);
    }

    private static void AssertTrackStateCleared(ScopeViewModel scope)
    {
        Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
        Assert.Empty(scope.DataBlockState.ManualOffsets);
        Assert.Empty(scope.LocalRings);
        Assert.Empty(scope.LocalPtlCallsigns);
    }
}
