using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
/// <summary>
/// Tests that EF on a runway parallel to the active FinalApproachPhase performs a
/// 7110.65 §5-9-7 side-step retarget (no phase chain rebuild, no PatternEntry
/// teardrop) instead of the standard "fly to glideslope-TPA intercept" build.
///
/// Uses OAK 28L/28R: parallel pair, ~0.16 nm centerline separation, FAC 292°.
/// </summary>
public class EfParallelSidestepTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navDbScope;

    public EfParallelSidestepTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(MakeOak28R(), MakeOak28L()));
    }

    public void Dispose() => _navDbScope.Dispose();

    private static RunwayInfo MakeOak28R()
    {
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            thresholdLat: 37.72481,
            thresholdLon: -122.20472,
            endLat: 37.73046,
            endLon: -122.22220,
            heading: 292,
            elevationFt: 9,
            lengthFt: 10000,
            widthFt: 150
        );
    }

    private static RunwayInfo MakeOak28L()
    {
        return TestRunwayFactory.Make(
            designator: "28L",
            airportId: "KOAK",
            thresholdLat: 37.72227,
            thresholdLon: -122.20601,
            endLat: 37.72871,
            endLon: -122.22590,
            heading: 292,
            elevationFt: 9,
            lengthFt: 6213,
            widthFt: 150
        );
    }

    private static AircraftState MakeAircraftOnFinal28R(double altAgl, double distFromThresholdNm)
    {
        var rwy28R = MakeOak28R();
        // Project the aircraft along the reciprocal of 28R FAC (heading 112°) at the
        // requested distance from the 28R threshold. Aircraft is on the 28R extended
        // centerline, on FAC heading.
        var reciprocal = new TrueHeading((rwy28R.TrueHeading.Degrees + 180) % 360);
        var pos = GeoMath.ProjectPoint(rwy28R.ThresholdLatitude, rwy28R.ThresholdLongitude, reciprocal, distFromThresholdNm);

        return new AircraftState
        {
            Callsign = "N42416",
            AircraftType = "C172",
            Position = new LatLon(pos.Lat, pos.Lon),
            Altitude = rwy28R.ElevationFt + altAgl,
            TrueHeading = rwy28R.TrueHeading,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            Phases = new PhaseList { AssignedRunway = rwy28R },
        };
    }

    private static PhaseContext BuildCtx(AircraftState ac, RunwayInfo rwy)
    {
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
    }

    [Fact]
    public void EfOnParallel_OnFinal_SidestepsWithoutRebuilding()
    {
        var ac = MakeAircraftOnFinal28R(altAgl: 640, distFromThresholdNm: 1.5);

        // Install an active FinalApproachPhase + LandingPhase chain for 28R, mirroring
        // what an aircraft already on 28R short-final would have.
        var rwy28R = ac.Phases!.AssignedRunway!;
        var finalApproach = new FinalApproachPhase();
        var landing = new LandingPhase();
        ac.Phases.Add(finalApproach);
        ac.Phases.Add(landing);
        ac.Phases.LandingClearance = ClearanceType.ClearedToLand;
        ac.Phases.ClearedRunwayId = "28R";
        ac.Phases.Start(BuildCtx(ac, rwy28R));

        // Now issue EF 28L — parallel runway sidestep.
        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, runwayId: "28L", finalDistanceNm: null);

        Assert.True(result.Success, $"sidestep should succeed but got: {result.Message}");
        _output.WriteLine($"message: {result.Message}");

        // Phase chain must not be rebuilt — the same FinalApproachPhase instance is still active.
        Assert.Same(finalApproach, ac.Phases.CurrentPhase);
        Assert.Equal(2, ac.Phases.Phases.Count);

        // Assigned runway, destination, and clearance updated to 28L.
        Assert.Equal("28L", ac.Phases.AssignedRunway?.Designator);
        Assert.Equal("28L", ac.Procedure.DestinationRunway);
        Assert.Equal("28L", ac.Phases.ClearedRunwayId);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);

        // Message says "Sidestep" not "Enter final".
        Assert.Contains("Sidestep", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EfOnParallel_NotOnFinal_DoesNotSidestep()
    {
        // Aircraft cruising at 5 nm out, no active FinalApproachPhase. EF should
        // build the standard pattern entry, not sidestep.
        var ac = MakeAircraftOnFinal28R(altAgl: 2500, distFromThresholdNm: 8);

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, runwayId: "28L", finalDistanceNm: null);

        Assert.True(result.Success);
        Assert.DoesNotContain("Sidestep", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
        // Should have built a fresh chain that includes a PatternEntry phase.
        Assert.Contains(ac.Phases!.Phases, p => p is Yaat.Sim.Phases.Pattern.PatternEntryPhase);
    }

    [Fact]
    public void EfOnParallel_TooLowAgl_Rejected()
    {
        // Aircraft on 28R short final at 200 ft AGL — below the 500 ft sidestep floor.
        // Per AC 120-71, the aircraft is committed; sidestep would force an unstabilized
        // approach on the parallel centerline. Reject.
        var ac = MakeAircraftOnFinal28R(altAgl: 200, distFromThresholdNm: 0.5);
        var rwy28R = ac.Phases!.AssignedRunway!;
        var finalApproach = new FinalApproachPhase();
        ac.Phases.Add(finalApproach);
        ac.Phases.Start(BuildCtx(ac, rwy28R));

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, runwayId: "28L", finalDistanceNm: null);

        Assert.False(result.Success);
        Assert.Contains("too low", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
        // Phase chain unchanged on rejection — same active FinalApproachPhase instance.
        Assert.Same(finalApproach, ac.Phases.CurrentPhase);
    }

    [Fact]
    public void EfOnSameRunway_DoesNotSidestep()
    {
        // EF for the runway already assigned. Should fall through to standard logic
        // (no sidestep because we're not switching runways).
        var ac = MakeAircraftOnFinal28R(altAgl: 640, distFromThresholdNm: 1.5);
        var rwy28R = ac.Phases!.AssignedRunway!;
        var finalApproach = new FinalApproachPhase();
        ac.Phases.Add(finalApproach);
        ac.Phases.Start(BuildCtx(ac, rwy28R));

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, runwayId: "28R", finalDistanceNm: null);

        Assert.DoesNotContain("Sidestep", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
