using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Phases.Ground;

/// <summary>
/// A snapshot taken while a cleared tow brakes to rest restores the release with the chain behind it still pending, and
/// the restored release finishes the brake in the distance the tug's rate gives from the saved speed.
/// </summary>
public class TugReleaseSnapshotTests(ITestOutputHelper output)
{
    /// <summary>
    /// How far the restored brake may stop from <c>v² / 2a</c>, feet: physics brakes before it moves each quarter-second
    /// sub-tick, which comes up about <c>v·dt / 2</c> (under a foot from 4 kt) short of the continuous figure.
    /// </summary>
    private const double StopToleranceFt = 2.0;

    [Fact]
    public void MidReleaseSnapshot_RestoresAndFinishesTheBrake()
    {
        if (new TugReleaseTests(output).PushToSpeed() is not var (ground, ac))
        {
            return;
        }

        Assert.True(ground.Engine.SendCommand(ac.Callsign, "TAXIAUTO $6B").Success);
        ground.Engine.TickOneSecond();
        TugReleasePhase release = Assert.IsType<TugReleasePhase>(ac.Phases?.CurrentPhase);

        string json = JsonSerializer.Serialize<PhaseDto>(release.ToSnapshot(), RecordingJsonOptions.Default);
        output.WriteLine(json);
        TugReleasePhaseDto readBack = Assert.IsType<TugReleasePhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        Assert.Equal(json, JsonSerializer.Serialize<PhaseDto>(TugReleasePhase.FromSnapshot(readBack).ToSnapshot(), RecordingJsonOptions.Default));

        PhaseListDto listDto = JsonSerializer.Deserialize<PhaseListDto>(
            JsonSerializer.Serialize(ac.Phases!.ToSnapshot(), RecordingJsonOptions.Default),
            RecordingJsonOptions.Default
        )!;
        var restored = PhaseList.FromSnapshot(listDto, ground.Layout);
        TugReleasePhase restoredRelease = Assert.IsType<TugReleasePhase>(restored.CurrentPhase);
        Assert.Equal(PushbackLegKind.Push, restoredRelease.Kind);
        Assert.Equal(PhaseStatus.Active, restoredRelease.Status);
        Assert.IsType<TaxiingPhase>(restored.Phases[restored.CurrentIndex + 1]);
        Assert.Equal(PhaseStatus.Pending, restored.Phases[restored.CurrentIndex + 1].Status);

        ac.Phases = restored;
        LatLon from = ac.Position;
        double speedKts = ac.GroundSpeed;
        LatLon? rest = null;
        SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            ac.Callsign,
            a => a.Phases?.CurrentPhase is not TugReleasePhase,
            10,
            a => rest ??= a.Phases?.CurrentPhase is TugReleasePhase ? null : a.Position
        );

        double decel = CategoryPerformance.TugDecelRate(AircraftCategory.Jet);
        double expectedFt = speedKts * speedKts / (2.0 * decel) * GeoMath.FeetPerNm / 3600.0;
        double stoppedFt = TugReleaseTests.FeetBetween(from, rest ?? throw new InvalidOperationException("the restored release never ended"));
        output.WriteLine($"restored at {speedKts:F2} kt: stopped {stoppedFt:F2} ft on, against {expectedFt:F2} ft");
        Assert.Equal(expectedFt, stoppedFt, StopToleranceFt);
        Assert.IsType<TaxiingPhase>(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Ground.TowbarTrueHeading);
    }
}
