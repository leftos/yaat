using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// Scenario tests for <see cref="LineUpPhaseV2"/>. These use synthesised
/// PhaseContext fixtures (no real airport data) and drive the phase through
/// full scenarios with <see cref="FlightPhysics"/>, asserting the Design D
/// end-state contract at completion.
/// </summary>
public class LineUpPhaseV2Tests
{
    private readonly ITestOutputHelper _out;

    public LineUpPhaseV2Tests(ITestOutputHelper output)
    {
        _out = output;
    }

    /// <summary>
    /// Construct a minimal <see cref="PhaseContext"/> and <see cref="AircraftState"/>
    /// for V2 scenario tests. The runway is a KTEST strip with the given
    /// heading at a fixed anchor (37.0, -122.0); the aircraft is placed at
    /// (<paramref name="acLat"/>, <paramref name="acLon"/>) with the given
    /// heading and zero initial speed.
    /// </summary>
    private static (AircraftState Aircraft, PhaseContext Ctx) MakeFixture(
        double rwyHeadingDeg,
        double acLat,
        double acLon,
        double acHeadingDeg,
        AircraftCategory cat = AircraftCategory.Jet
    )
    {
        const double threshLat = 37.0;
        const double threshLon = -122.0;
        var (endLat, endLon) = GeoMath.ProjectPoint(threshLat, threshLon, new TrueHeading(rwyHeadingDeg), 2.0);
        var runway = TestRunwayFactory.Make(
            designator: "TST",
            thresholdLat: threshLat,
            thresholdLon: threshLon,
            endLat: endLat,
            endLon: endLon,
            heading: rwyHeadingDeg
        );

        var aircraft = new AircraftState
        {
            Callsign = "V2TEST",
            AircraftType = "B738",
            Latitude = acLat,
            Longitude = acLon,
            TrueHeading = new TrueHeading(acHeadingDeg),
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = cat,
            DeltaSeconds = 0.25,
            Runway = runway,
            FieldElevation = 0,
            GroundLayout = null,
            Logger = NullLogger.Instance,
        };

        return (aircraft, ctx);
    }

    /// <summary>
    /// Drive a V2 phase through <see cref="FlightPhysics"/> ticks until
    /// complete or budget exhausted. Returns (completed, finalCrossFt,
    /// finalHdgDiffDeg, finalGsKts, tickCount).
    /// </summary>
    private static (bool Completed, double CrossFt, double HdgDiffDeg, double GsKts, int Ticks) RunToCompletion(
        LineUpPhaseV2 phase,
        AircraftState aircraft,
        PhaseContext ctx,
        int maxTicks = 400
    )
    {
        phase.OnStart(ctx);

        for (int i = 0; i < maxTicks; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            if (phase.OnTick(ctx))
            {
                var cross =
                    Math.Abs(
                        GeoMath.SignedCrossTrackDistanceNm(
                            aircraft.Latitude,
                            aircraft.Longitude,
                            ctx.Runway!.ThresholdLatitude,
                            ctx.Runway.ThresholdLongitude,
                            ctx.Runway.TrueHeading
                        )
                    ) * GeoMath.FeetPerNm;
                double hdg = Math.Abs(ctx.Runway.TrueHeading.SignedAngleTo(aircraft.TrueHeading));
                return (true, cross, hdg, aircraft.IndicatedAirspeed, i + 1);
            }
        }

        var crossX =
            Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    aircraft.Latitude,
                    aircraft.Longitude,
                    ctx.Runway!.ThresholdLatitude,
                    ctx.Runway.ThresholdLongitude,
                    ctx.Runway.TrueHeading
                )
            ) * GeoMath.FeetPerNm;
        double hdgX = Math.Abs(ctx.Runway.TrueHeading.SignedAngleTo(aircraft.TrueHeading));
        return (false, crossX, hdgX, aircraft.IndicatedAirspeed, maxTicks);
    }

    // ---- Factory wiring ----

    [Fact]
    public void Factory_CreateV2_ReturnsLineUpPhaseV2()
    {
        var phase = LineUpPhaseFactory.Create(LineUpPhaseImpl.V2);
        Assert.IsType<LineUpPhaseV2>(phase);
        Assert.IsAssignableFrom<ILineUpPhase>(phase);
    }

    [Fact]
    public void Factory_FromSnapshot_V2_ReturnsLineUpPhaseV2()
    {
        var dto = new LineUpPhaseDto
        {
            ImplVersion = 2,
            Status = (int)PhaseStatus.Pending,
            ElapsedSeconds = 0,
            RunwayHeadingDeg = 90,
            Initialized = false,
            TimeSinceLastLog = 0,
            PerpHeadingDeg = 0,
            PerpAligned = false,
            OnCenterline = false,
        };

        var restored = LineUpPhaseFactory.FromSnapshot(dto);
        Assert.IsType<LineUpPhaseV2>(restored);
    }

    // ---- Faulted states ----

    [Fact]
    public void OnStart_NullRunway_EntersFaulted()
    {
        var (aircraft, ctx) = MakeFixture(90.0, 36.9965, -121.995, 0.0);
        var ctxNoRwy = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = ctx.Category,
            DeltaSeconds = ctx.DeltaSeconds,
            Runway = null,
            FieldElevation = 0,
            GroundLayout = null,
            Logger = NullLogger.Instance,
        };

        var phase = new LineUpPhaseV2();
        phase.OnStart(ctxNoRwy);

        Assert.Equal(LineUpPhaseV2.State.Faulted, phase.CurrentState);
        Assert.Null(phase.Plan);

        bool done = phase.OnTick(ctxNoRwy);
        Assert.True(done, "Faulted state should return true (phase done) on first tick");
    }

    [Fact]
    public void OnStart_InvalidGeometry_EntersFaulted()
    {
        // Aircraft 30 ft from centerline — radius (70 ft for Jet) doesn't fit
        // → LineUpPlanBuilder returns null → phase enters Faulted.
        double acLat = 37.0 - 30.0 / (GeoMath.FeetPerNm * 60.0);
        double acLon = -121.995;
        var (_, ctx) = MakeFixture(90.0, acLat, acLon, 0.0);

        var phase = new LineUpPhaseV2();
        phase.OnStart(ctx);

        Assert.Equal(LineUpPhaseV2.State.Faulted, phase.CurrentState);
        Assert.Null(phase.Plan);
    }

    // ---- End-state contract: perpendicular right turn ----

    [Fact]
    public void PerpendicularRightTurn_EndsOnCenterlineAlignedAndStopped()
    {
        double rwyHdg = 90.0;
        double acHdg = 0.0;
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0); // 200 ft south
        double acLon = -121.995;

        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, acLon, acHdg);
        var phase = new LineUpPhaseV2();
        var result = RunToCompletion(phase, aircraft, ctx);

        _out.WriteLine(
            $"PerpRight: completed={result.Completed} ticks={result.Ticks} "
                + $"cross={result.CrossFt:F2}ft hdgDiff={result.HdgDiffDeg:F2}° gs={result.GsKts:F2}kt"
        );

        Assert.True(result.Completed, "V2 did not complete perpendicular right-turn scenario");
        Assert.True(result.CrossFt < 3.0, $"cross-centerline {result.CrossFt:F2}ft exceeds 3 ft tolerance");
        Assert.True(result.HdgDiffDeg < 1.0, $"heading-diff {result.HdgDiffDeg:F2}° exceeds 1° tolerance");
        Assert.True(result.GsKts < 0.5, $"gs {result.GsKts:F2}kt exceeds 0.5 kt tolerance");
        Assert.Equal(LineUpPhaseV2.State.Stop, phase.CurrentState);
    }

    // ---- End-state contract: perpendicular left turn ----

    [Fact]
    public void PerpendicularLeftTurn_EndsOnCenterlineAlignedAndStopped()
    {
        double rwyHdg = 90.0;
        double acHdg = 180.0;
        double acLat = 37.0 + 200.0 / (GeoMath.FeetPerNm * 60.0); // 200 ft north
        double acLon = -121.995;

        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, acLon, acHdg);
        var phase = new LineUpPhaseV2();
        var result = RunToCompletion(phase, aircraft, ctx);

        _out.WriteLine(
            $"PerpLeft: completed={result.Completed} ticks={result.Ticks} "
                + $"cross={result.CrossFt:F2}ft hdgDiff={result.HdgDiffDeg:F2}° gs={result.GsKts:F2}kt"
        );

        Assert.True(result.Completed);
        Assert.True(result.CrossFt < 3.0, $"cross-centerline {result.CrossFt:F2}ft exceeds 3 ft tolerance");
        Assert.True(result.HdgDiffDeg < 1.0, $"heading-diff {result.HdgDiffDeg:F2}° exceeds 1° tolerance");
        Assert.True(result.GsKts < 0.5);
    }

    // ---- End-state contract: skewed (SFO 28R taxi E geometry) ----

    [Fact]
    public void SkewedTurn_SfoTaxiELike_EndsOnCenterlineAlignedAndStopped()
    {
        // Turn: 228.4° → 297.9° short way = +69.5° = right turn.
        double rwyHdg = 297.9;
        double acHdg = 228.4;

        // Place aircraft 200 ft perpendicular-right of the runway threshold,
        // 500 ft forward along runway. This is the same fixture as the
        // LineUpPlanBuilderTests skewed-turn test.
        double perpRightBearing = (rwyHdg + 90.0) % 360.0;
        var (acLat, acLon) = GeoMath.ProjectPoint(37.0, -122.0, new TrueHeading(perpRightBearing), 200.0 / GeoMath.FeetPerNm);
        (acLat, acLon) = GeoMath.ProjectPoint(acLat, acLon, new TrueHeading(rwyHdg), 500.0 / GeoMath.FeetPerNm);

        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, acLon, acHdg);
        var phase = new LineUpPhaseV2();
        var result = RunToCompletion(phase, aircraft, ctx);

        _out.WriteLine(
            $"Skewed: completed={result.Completed} ticks={result.Ticks} "
                + $"cross={result.CrossFt:F2}ft hdgDiff={result.HdgDiffDeg:F2}° gs={result.GsKts:F2}kt"
        );

        Assert.True(result.Completed);
        Assert.True(result.CrossFt < 3.0, $"cross-centerline {result.CrossFt:F2}ft exceeds 3 ft tolerance");
        Assert.True(result.HdgDiffDeg < 1.0, $"heading-diff {result.HdgDiffDeg:F2}° exceeds 1° tolerance");
        Assert.True(result.GsKts < 0.5);
    }

    // ---- State machine observable transitions ----

    [Fact]
    public void StateMachine_PerpendicularRightTurn_TransitionsThroughAllStates()
    {
        double rwyHdg = 90.0;
        double acHdg = 0.0;
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        double acLon = -121.995;

        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, acLon, acHdg);
        var phase = new LineUpPhaseV2();
        phase.OnStart(ctx);

        Assert.Equal(LineUpPhaseV2.State.NoseOut, phase.CurrentState);

        var seen = new HashSet<LineUpPhaseV2.State> { phase.CurrentState };
        for (int i = 0; i < 400; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            bool done = phase.OnTick(ctx);
            seen.Add(phase.CurrentState);
            if (done)
            {
                break;
            }
        }

        Assert.Contains(LineUpPhaseV2.State.NoseOut, seen);
        Assert.Contains(LineUpPhaseV2.State.Arc, seen);
        Assert.Contains(LineUpPhaseV2.State.Rollout, seen);
        Assert.Contains(LineUpPhaseV2.State.Stop, seen);
        Assert.DoesNotContain(LineUpPhaseV2.State.Faulted, seen);
    }

    // ---- Plan exposed via public getter ----

    [Fact]
    public void Plan_IsNullUntilOnStart_ThenNonNull()
    {
        var phase = new LineUpPhaseV2();
        Assert.Null(phase.Plan);

        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (_, ctx) = MakeFixture(90.0, acLat, -121.995, 0.0);
        phase.OnStart(ctx);

        Assert.NotNull(phase.Plan);
        Assert.False(phase.Plan.IsAlreadyAligned);
        Assert.True(phase.Plan.ArcSpeedKts > 0);
    }
}
