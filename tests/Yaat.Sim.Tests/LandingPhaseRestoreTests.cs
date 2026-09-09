using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// A <see cref="LandingPhase"/> that comes back from a snapshot must fly the landing it was in the middle of, on
/// the numbers it was already flying.
///
/// <para>
/// The plan round-trips whole — geometry and category constants — because Vref carries the gust additive from the
/// weather at the original OnStart and the plan names the assigned runway: recomputing either at restore time
/// would let a rewind touch down differently from the live run, and recordings must replay exactly. A snapshot
/// written before the constants round-tripped still restores: <see cref="PhaseRunner"/> only calls <c>OnStart</c>
/// on a Pending phase, so a restored Active phase rebuilds them from the category table on its own first tick.
/// </para>
/// </summary>
public sealed class LandingPhaseRestoreTests
{
    public LandingPhaseRestoreTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>Well under the jet touchdown speed the restore path used to fabricate (135 kt).</summary>
    private const double PistonTouchdownCeilingKts = 100.0;

    private static RunwayInfo Oak28R() =>
        TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: 280,
            elevationFt: 9
        );

    /// <summary>A C172 on a stabilized short final: 25 ft AGL, on centerline, aligned with the runway.</summary>
    private static AircraftState MakePistonOnShortFinal(RunwayInfo runway) =>
        new()
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 25,
            IndicatedAirspeed = 80,
            IsOnGround = false,
        };

    private static PhaseContext MakeContext(AircraftState ac, RunwayInfo runway, WeatherProfile? weather) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            Weather = weather,
            ScenarioElapsedSeconds = 120.0,
        };

    /// <summary>27010G20 — a 10 kt gust additive on Vref, so a recomputed piston Vref would read 85, not 75.</summary>
    private static WeatherProfile GustyWeather() =>
        new()
        {
            WindLayers =
            [
                new WindLayer
                {
                    Direction = 270,
                    Speed = 10,
                    Gusts = 20,
                },
            ],
        };

    /// <summary>Starts a landing phase on a fresh piston aircraft in calm air and returns it, Active.</summary>
    private static LandingPhase StartLanding(RunwayInfo runway)
    {
        var ac = MakePistonOnShortFinal(runway);
        var phase = new LandingPhase();
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Add(phase);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        phase.OnStart(MakeContext(ac, runway, weather: null));
        phase.Status = PhaseStatus.Active;
        return phase;
    }

    /// <summary>
    /// A context whose phase list carries no assigned runway, so anything the restored phase reports about the
    /// runway has to have come out of the snapshot.
    /// </summary>
    private static PhaseContext MakeRestoredContext(RunwayInfo runway, WeatherProfile? weather)
    {
        var ac = MakePistonOnShortFinal(runway);
        ac.Phases = new PhaseList();
        return MakeContext(ac, runway, weather);
    }

    /// <summary>A snapshot from before the plan's constants round-tripped: geometry only.</summary>
    private static LandingPhaseDto SnapshotWithoutConstants(RunwayInfo runway, PhaseStatus status) =>
        new()
        {
            Status = (int)status,
            ElapsedSeconds = 3.0,
            FieldElevation = runway.ElevationFt,
            RunwayHeadingDeg = runway.TrueHeading.Degrees,
            ThresholdLat = runway.ThresholdLatitude,
            ThresholdLon = runway.ThresholdLongitude,
            TouchedDown = false,
            CanGoAround = false,
            LahsoHoldShortDistNm = 0,
            HasLahso = false,
            StoppedForLahso = false,
            CurrentStateValue = (int)LandingPhase.State.StabilizedApproach,
        };

    private static string Serialize(PhaseDto dto) => JsonSerializer.Serialize(dto);

    [Fact]
    public void Snapshot_BeforeTheFirstTick_IsLossless()
    {
        var runway = Oak28R();
        var twin = StartLanding(runway);

        var first = Assert.IsType<LandingPhaseDto>(twin.ToSnapshot());
        var restored = LandingPhase.FromSnapshot(first, groundLayout: null);
        var second = Assert.IsType<LandingPhaseDto>(restored.ToSnapshot());

        // Geometry: a lost threshold leaves the next restore with a null plan, which the phase runner reads as a
        // finished landing and follows with a runway exit.
        Assert.Equal(first.FieldElevation, second.FieldElevation, 6);
        Assert.Equal(first.RunwayHeadingDeg, second.RunwayHeadingDeg, 6);
        Assert.Equal(first.ThresholdLat, second.ThresholdLat, 6);
        Assert.Equal(first.ThresholdLon, second.ThresholdLon, 6);

        // Constants, including the runway id and the weather-derived Vref.
        Assert.Equal(first.RunwayId, second.RunwayId);
        Assert.Equal(first.Vref, second.Vref);
        Assert.Equal(first.Vtd, second.Vtd);

        // Everything else, field by field, so a field added to the DTO without a restore path fails here.
        Assert.Equal(Serialize(first), Serialize(second));
    }

    [Fact]
    public void RestoredPlan_MatchesTheLivePlanExactly()
    {
        var runway = Oak28R();
        var twin = StartLanding(runway);
        Assert.Equal(LandingPhase.State.StabilizedApproach, twin.CurrentState);
        var expected = twin.Plan;
        Assert.NotNull(expected);
        Assert.Equal("28R", expected.RunwayId);

        var dto = Assert.IsType<LandingPhaseDto>(twin.ToSnapshot());
        var restored = LandingPhase.FromSnapshot(dto, groundLayout: null);

        // Gustier air and no assigned runway in the restored context: a plan rebuilt from the live context would
        // read Vref 85 and a null runway id instead of the 75 and "28R" the aircraft has been flying.
        restored.OnTick(MakeRestoredContext(runway, GustyWeather()));

        var plan = restored.Plan;
        Assert.NotNull(plan);
        Assert.Equal(expected, plan);
        Assert.Equal("28R", plan.RunwayId);
        Assert.Equal(expected.Vref, plan.Vref, 3);
        Assert.True(plan.Vtd < PistonTouchdownCeilingKts, $"piston touchdown speed should be well below 135 kt, was {plan.Vtd:F1}");
    }

    [Fact]
    public void LegacySnapshot_WithoutConstants_RebuildsFromCategory()
    {
        var runway = Oak28R();
        var dto = SnapshotWithoutConstants(runway, PhaseStatus.Active);
        var restored = LandingPhase.FromSnapshot(dto, groundLayout: null);
        Assert.Null(restored.Plan);

        // Re-snapshotting inside the un-ticked window is still lossless: the geometry has nowhere to live but
        // the restored copy, and losing it would leave the next restore with a null plan.
        var resnapshot = Assert.IsType<LandingPhaseDto>(restored.ToSnapshot());
        Assert.Equal(dto.FieldElevation, resnapshot.FieldElevation, 6);
        Assert.Equal(dto.RunwayHeadingDeg, resnapshot.RunwayHeadingDeg, 6);
        Assert.Equal(dto.ThresholdLat, resnapshot.ThresholdLat, 6);
        Assert.Equal(dto.ThresholdLon, resnapshot.ThresholdLon, 6);
        Assert.Equal(Serialize(dto), Serialize(resnapshot));

        var ac = MakePistonOnShortFinal(runway);
        ac.Phases = new PhaseList { AssignedRunway = runway };
        restored.OnTick(MakeContext(ac, runway, weather: null));

        var plan = restored.Plan;
        Assert.NotNull(plan);
        Assert.Equal(CategoryPerformance.FlareAltitude(AircraftCategory.Piston), plan.FlareEntryAgl, 3);
        Assert.Equal(CategoryPerformance.FlareDescentRate(AircraftCategory.Piston), plan.FlareFpm, 3);
        Assert.Equal(CategoryPerformance.ApproachSpeed(AircraftCategory.Piston), plan.Vref, 3);
        Assert.Equal(CategoryPerformance.RolloutCoastSpeed(AircraftCategory.Piston), plan.CoastSpeed, 3);
        Assert.Equal(CategoryPerformance.RolloutDecelRate(AircraftCategory.Piston), plan.DefaultDecel, 3);
        Assert.Equal(AircraftPerformance.TouchdownSpeed("C172", AircraftCategory.Piston), plan.Vtd, 3);
        Assert.True(plan.Vtd < PistonTouchdownCeilingKts, $"piston touchdown speed should be well below 135 kt, was {plan.Vtd:F1}");

        // Geometry came from the snapshot; the runway id is the fallback's only live read.
        Assert.Equal(runway.ThresholdLatitude, plan.ThresholdLat, 6);
        Assert.Equal("28R", plan.RunwayId);
    }

    [Fact]
    public void RestoredPendingLanding_HasNoPlanUntilOnStart()
    {
        var runway = Oak28R();
        var dto = SnapshotWithoutConstants(runway, PhaseStatus.Pending);
        var restored = LandingPhase.FromSnapshot(dto, groundLayout: null);

        Assert.Null(restored.Plan);

        var ac = MakePistonOnShortFinal(runway);
        ac.Phases = new PhaseList { AssignedRunway = runway };
        var ctx = MakeContext(ac, runway, weather: null);

        // A tick alone must not fill it in: only OnStart plans a phase that had not started.
        restored.OnTick(ctx);
        Assert.Null(restored.Plan);

        restored.OnStart(ctx);
        var plan = restored.Plan;
        Assert.NotNull(plan);
        Assert.Equal(CategoryPerformance.FlareAltitude(AircraftCategory.Piston), plan.FlareEntryAgl, 3);
    }
}
