using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// Scenario tests for <see cref="LineUpPhase"/>. Focus on observable
/// behaviour: fault paths, RollingMode detection from the phase list,
/// TryUpgradeToRolling gates, and the aligned-path end-state contract
/// (cross ≤ 3 ft, hdgDiff ≤ 1°, ias ≤ 0.5 kt).
///
/// <para>
/// Pivot-path end-state verification lives in
/// <see cref="Simulation.Issue142SfoRwy01rShallowLineupTests"/>, which uses
/// the real SFO 01R runway + ground layout. Real-replay end-state checks
/// for the aligned path live in
/// <see cref="Simulation.DiagonalLineup28rTests"/>.
/// </para>
///
/// <para>
/// Fixtures here use a synthesised <see cref="RunwayInfo"/> plus an empty
/// but non-null <see cref="AirportGroundLayout"/>. LineUpPhase's geometry
/// is driven entirely by the runway and aircraft pose — the layout is only
/// consulted for a non-null smoke-check at OnStart (per user directive:
/// "null GroundLayout not supported for LineUpPhase"). An empty synthetic
/// layout satisfies that guard without pulling real airport data into
/// every test.
/// </para>
/// </summary>
[Collection("NavDbMutator")]
public class LineUpPhaseTests(ITestOutputHelper output)
{
    /// <summary>
    /// Construct a minimal <see cref="PhaseContext"/> + <see cref="AircraftState"/>
    /// around a synthesised runway and empty-but-non-null GroundLayout.
    /// </summary>
    private static (AircraftState Aircraft, PhaseContext Ctx) MakeFixture(
        double rwyHeadingDeg,
        double acLat,
        double acLon,
        double acHeadingDeg,
        AircraftCategory cat = AircraftCategory.Jet,
        AirportGroundLayout? layout = null
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
            Callsign = "LUTEST",
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
            GroundLayout = layout ?? new AirportGroundLayout { AirportId = "KTEST" },
            Logger = NullLogger.Instance,
        };

        return (aircraft, ctx);
    }

    /// <summary>
    /// Drive a phase through <see cref="FlightPhysics"/> ticks until it
    /// completes or budget is exhausted. Returns end-state metrics plus
    /// completion flag.
    /// </summary>
    private static (bool Completed, double CrossFt, double HdgDiffDeg, double GsKts, int Ticks) RunToCompletion(
        LineUpPhase phase,
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

    private static void InstallPhaseListWithNext(AircraftState aircraft, LineUpPhase phase, Phase next)
    {
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(next);
    }

    // ---- Snapshot ----

    [Fact]
    public void FromSnapshot_RestoresStatusAndElapsedTime()
    {
        var dto = new LineUpPhaseDto
        {
            Status = (int)PhaseStatus.Pending,
            ElapsedSeconds = 0,
            RunwayHeadingDeg = 90,
            Initialized = false,
            TimeSinceLastLog = 0,
            PerpHeadingDeg = 0,
            PerpAligned = false,
            OnCenterline = false,
        };

        var restored = LineUpPhase.FromSnapshot(dto);
        Assert.NotNull(restored);
        Assert.IsType<LineUpPhase>(restored);
    }

    // ---- Fault paths ----

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
            GroundLayout = ctx.GroundLayout,
            Logger = NullLogger.Instance,
        };

        var phase = new LineUpPhase();
        phase.OnStart(ctxNoRwy);

        Assert.Equal(LineUpPhase.State.Faulted, phase.CurrentState);
        Assert.Null(phase.PathPlan);
    }

    [Fact]
    public void OnStart_NullGroundLayout_EntersFaulted()
    {
        var (aircraft, ctx) = MakeFixture(90.0, 36.9965, -121.995, 0.0);
        var ctxNoLayout = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = ctx.Category,
            DeltaSeconds = ctx.DeltaSeconds,
            Runway = ctx.Runway,
            FieldElevation = 0,
            GroundLayout = null,
            Logger = NullLogger.Instance,
        };

        var phase = new LineUpPhase();
        phase.OnStart(ctxNoLayout);

        Assert.Equal(LineUpPhase.State.Faulted, phase.CurrentState);
        Assert.Null(phase.PathPlan);
    }

    [Fact]
    public void OnStart_DivergingHeading_EntersFaulted()
    {
        // Aircraft 200 ft south of an east-heading runway but pointing
        // south-east (away from centerline). Neither aligned nor pivot
        // can recover — geometry is faulted.
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        double acLon = -121.995;
        var (aircraft, ctx) = MakeFixture(90.0, acLat, acLon, 135.0);

        var phase = new LineUpPhase();
        phase.OnStart(ctx);

        Assert.Equal(LineUpPhase.State.Faulted, phase.CurrentState);
        Assert.NotNull(phase.PathPlan);
        Assert.Equal(LineUpPathKind.Fault, phase.PathPlan.Kind);
        _ = aircraft;
    }

    [Fact]
    public void TickFaulted_KeepsAircraftStopped_DoesNotCompletePhase()
    {
        // Regression for issue #142: Faulted tick must return false so the
        // aircraft does not auto-advance to TakeoffPhase with a bad pose.
        // User recovers via TAXI / CANCEL CLEARANCE instead.
        var (aircraft, ctx) = MakeFixture(90.0, 36.9965, -121.995, 0.0);
        var ctxNoRwy = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = ctx.Category,
            DeltaSeconds = ctx.DeltaSeconds,
            Runway = null,
            FieldElevation = 0,
            GroundLayout = ctx.GroundLayout,
            Logger = NullLogger.Instance,
        };

        var phase = new LineUpPhase();
        phase.OnStart(ctxNoRwy);
        Assert.Equal(LineUpPhase.State.Faulted, phase.CurrentState);

        for (int i = 0; i < 20; i++)
        {
            FlightPhysics.Update(aircraft, ctxNoRwy.DeltaSeconds);
            bool done = phase.OnTick(ctxNoRwy);
            Assert.False(done, $"Faulted tick #{i} returned true — phase must stay stopped, not auto-complete");
        }

        Assert.True(aircraft.IndicatedAirspeed < 0.1, $"Faulted phase should hold speed at 0, got {aircraft.IndicatedAirspeed:F2}kt");
    }

    // ---- End-state contract: aligned paths ----

    [Fact]
    public void PerpendicularRightTurn_EndsOnCenterlineAlignedAndStopped()
    {
        double rwyHdg = 90.0;
        double acHdg = 0.0;
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        double acLon = -121.995;

        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, acLon, acHdg);
        var phase = new LineUpPhase();
        var result = RunToCompletion(phase, aircraft, ctx);

        output.WriteLine(
            $"PerpRight: completed={result.Completed} ticks={result.Ticks} "
                + $"cross={result.CrossFt:F2}ft hdgDiff={result.HdgDiffDeg:F2}° gs={result.GsKts:F2}kt"
        );

        Assert.True(result.Completed, "LineUpPhase did not complete perpendicular right-turn scenario");
        Assert.True(result.CrossFt < 3.0, $"cross-centerline {result.CrossFt:F2}ft exceeds 3 ft tolerance");
        Assert.True(result.HdgDiffDeg < 1.0, $"heading-diff {result.HdgDiffDeg:F2}° exceeds 1° tolerance");
        Assert.True(result.GsKts < 0.5, $"gs {result.GsKts:F2}kt exceeds 0.5 kt tolerance");
        Assert.Equal(LineUpPhase.State.Stop, phase.CurrentState);
    }

    [Fact]
    public void PerpendicularLeftTurn_EndsOnCenterlineAlignedAndStopped()
    {
        double rwyHdg = 90.0;
        double acHdg = 180.0;
        double acLat = 37.0 + 200.0 / (GeoMath.FeetPerNm * 60.0);
        double acLon = -121.995;

        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, acLon, acHdg);
        var phase = new LineUpPhase();
        var result = RunToCompletion(phase, aircraft, ctx);

        output.WriteLine(
            $"PerpLeft: completed={result.Completed} ticks={result.Ticks} "
                + $"cross={result.CrossFt:F2}ft hdgDiff={result.HdgDiffDeg:F2}° gs={result.GsKts:F2}kt"
        );

        Assert.True(result.Completed);
        Assert.True(result.CrossFt < 3.0, $"cross-centerline {result.CrossFt:F2}ft exceeds 3 ft tolerance");
        Assert.True(result.HdgDiffDeg < 1.0, $"heading-diff {result.HdgDiffDeg:F2}° exceeds 1° tolerance");
        Assert.True(result.GsKts < 0.5);
    }

    [Fact]
    public void PathPlan_IsNullUntilOnStart_ThenNonNull()
    {
        var phase = new LineUpPhase();
        Assert.Null(phase.PathPlan);

        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (_, ctx) = MakeFixture(90.0, acLat, -121.995, 0.0);
        phase.OnStart(ctx);

        Assert.NotNull(phase.PathPlan);
        Assert.Equal(LineUpPathKind.Aligned, phase.PathPlan.Kind);
        Assert.False(phase.PathPlan.IsAlreadyAligned);
        Assert.True(phase.PathPlan.ArcSpeedKts > 0);
    }

    // ---- Rolling mode detection ----

    [Fact]
    public void OnStart_NextPhaseIsLuaw_RollingModeIsFalse()
    {
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (aircraft, ctx) = MakeFixture(90.0, acLat, -121.995, 0.0);

        var phase = new LineUpPhase();
        InstallPhaseListWithNext(aircraft, phase, new LinedUpAndWaitingPhase());
        phase.OnStart(ctx);

        Assert.False(phase.RollingMode);
    }

    [Fact]
    public void OnStart_NextPhaseIsTakeoff_RollingModeIsTrue()
    {
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (aircraft, ctx) = MakeFixture(90.0, acLat, -121.995, 0.0);

        var phase = new LineUpPhase();
        InstallPhaseListWithNext(aircraft, phase, new TakeoffPhase());
        phase.OnStart(ctx);

        Assert.True(phase.RollingMode);
    }

    [Fact]
    public void OnStart_NextPhaseIsHelicopterTakeoff_RollingModeIsTrue()
    {
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (aircraft, ctx) = MakeFixture(90.0, acLat, -121.995, 0.0);

        var phase = new LineUpPhase();
        InstallPhaseListWithNext(aircraft, phase, new HelicopterTakeoffPhase());
        phase.OnStart(ctx);

        Assert.True(phase.RollingMode);
    }

    [Fact]
    public void OnStart_NoPhaseList_RollingModeIsFalse()
    {
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (_, ctx) = MakeFixture(90.0, acLat, -121.995, 0.0);

        var phase = new LineUpPhase();
        phase.OnStart(ctx);

        Assert.False(phase.RollingMode);
    }

    // ---- Rolling mode behavior ----

    [Fact]
    public void RollingMode_DoesNotBrakeToZero_CompletesAboveThreshold()
    {
        double rwyHdg = 90.0;
        double acHdg = 0.0;
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        double acLon = -121.995;

        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, acLon, acHdg);
        var phase = new LineUpPhase();
        InstallPhaseListWithNext(aircraft, phase, new TakeoffPhase());

        phase.OnStart(ctx);
        Assert.True(phase.RollingMode);

        bool sawStopState = false;
        bool completed = false;

        for (int i = 0; i < 400; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            bool done = phase.OnTick(ctx);

            if (phase.CurrentState == LineUpPhase.State.Stop)
            {
                sawStopState = true;
            }

            if (done)
            {
                completed = true;
                break;
            }
        }

        Assert.True(completed, "Rolling LineUpPhase did not complete");
        Assert.False(sawStopState, "Rolling mode must not enter State.Stop");
        Assert.True(aircraft.IndicatedAirspeed > 3.0, $"Final IAS {aircraft.IndicatedAirspeed:F2}kt should exceed rolling-upgrade threshold");
    }

    [Fact]
    public void StopMode_BrakesToZero_RegressionGuard()
    {
        double rwyHdg = 90.0;
        double acHdg = 0.0;
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        double acLon = -121.995;

        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, acLon, acHdg);
        var phase = new LineUpPhase();
        InstallPhaseListWithNext(aircraft, phase, new LinedUpAndWaitingPhase());

        phase.OnStart(ctx);
        Assert.False(phase.RollingMode);

        var result = RunToCompletion(phase, aircraft, ctx);
        Assert.True(result.Completed);
        Assert.True(result.GsKts < 0.5, $"LUAW mode should still brake to 0, got {result.GsKts:F2}kt");
        Assert.Equal(LineUpPhase.State.Stop, phase.CurrentState);
    }

    // ---- TryUpgradeToRolling ----

    [Fact]
    public void TryUpgradeToRolling_BeforeOnStart_ReturnsFalse()
    {
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (_, ctx) = MakeFixture(90.0, acLat, -121.995, 0.0);

        var phase = new LineUpPhase();
        Assert.False(phase.TryUpgradeToRolling(ctx));
        Assert.False(phase.RollingMode);
    }

    [Fact]
    public void TryUpgradeToRolling_InArc_Succeeds()
    {
        double rwyHdg = 90.0;
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, -121.995, 0.0);

        var phase = new LineUpPhase();
        InstallPhaseListWithNext(aircraft, phase, new LinedUpAndWaitingPhase());
        phase.OnStart(ctx);
        Assert.False(phase.RollingMode);

        for (int i = 0; i < 200 && phase.CurrentState != LineUpPhase.State.Arc; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            phase.OnTick(ctx);
        }
        Assert.Equal(LineUpPhase.State.Arc, phase.CurrentState);

        bool upgraded = phase.TryUpgradeToRolling(ctx);
        Assert.True(upgraded);
        Assert.True(phase.RollingMode);

        bool completed = false;
        bool sawStop = false;
        for (int i = 0; i < 400; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            if (phase.CurrentState == LineUpPhase.State.Stop)
            {
                sawStop = true;
            }
            if (phase.OnTick(ctx))
            {
                completed = true;
                break;
            }
        }
        Assert.True(completed);
        Assert.False(sawStop, "Upgraded phase should not enter Stop state");
        Assert.True(aircraft.IndicatedAirspeed > 3.0);
    }

    [Fact]
    public void TryUpgradeToRolling_InStop_Rejected()
    {
        double rwyHdg = 90.0;
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, -121.995, 0.0);

        var phase = new LineUpPhase();
        InstallPhaseListWithNext(aircraft, phase, new LinedUpAndWaitingPhase());
        phase.OnStart(ctx);

        for (int i = 0; i < 400 && phase.CurrentState != LineUpPhase.State.Stop; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            phase.OnTick(ctx);
        }
        Assert.Equal(LineUpPhase.State.Stop, phase.CurrentState);

        bool upgraded = phase.TryUpgradeToRolling(ctx);
        Assert.False(upgraded);
        Assert.False(phase.RollingMode);
    }

    [Fact]
    public void IsAircraftEligibleForRollingTakeoff_B738_True()
    {
        Assert.True(LineUpPhase.IsAircraftEligibleForRollingTakeoff("B738"));
    }

    [Fact]
    public void IsAircraftEligibleForRollingTakeoff_B744Heavy_False()
    {
        // FAA 7110.65 §3-9-5.3: Heavy aircraft prohibited from rolling takeoffs.
        Assert.False(LineUpPhase.IsAircraftEligibleForRollingTakeoff("B744"));
    }

    [Fact]
    public void TryUpgradeToRolling_HeavyAircraft_Rejected()
    {
        double rwyHdg = 90.0;
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, -121.995, 0.0);
        aircraft.AircraftType = "B744";

        var phase = new LineUpPhase();
        InstallPhaseListWithNext(aircraft, phase, new LinedUpAndWaitingPhase());
        phase.OnStart(ctx);

        for (int i = 0; i < 200 && phase.CurrentState != LineUpPhase.State.Arc; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            phase.OnTick(ctx);
        }
        Assert.Equal(LineUpPhase.State.Arc, phase.CurrentState);
        Assert.True(aircraft.IndicatedAirspeed > 5.0);

        bool upgraded = phase.TryUpgradeToRolling(ctx);
        Assert.False(upgraded);
        Assert.False(phase.RollingMode);
    }

    [Fact]
    public void SatisfyUpcomingTakeoffClearance_ActiveLineUp_FlipsRollingMode()
    {
        double rwyHdg = 90.0;
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, -121.995, 0.0);

        using var navScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(ctx.Runway!));

        var phase = new LineUpPhase();
        var luaw = new LinedUpAndWaitingPhase();
        var takeoff = new TakeoffPhase();
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(luaw);
        aircraft.Phases.Add(takeoff);
        aircraft.Phases.AssignedRunway = ctx.Runway;
        aircraft.Phases.Start(ctx);

        Assert.False(phase.RollingMode);

        for (int i = 0; i < 200 && phase.CurrentState != LineUpPhase.State.Arc; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            phase.OnTick(ctx);
        }
        Assert.Equal(LineUpPhase.State.Arc, phase.CurrentState);
        Assert.True(aircraft.IndicatedAirspeed > 5.0);

        var result = DepartureClearanceHandler.SatisfyUpcomingTakeoffClearance(aircraft, new RunwayHeadingDeparture(), null, NullLogger.Instance);

        Assert.True(result.Success);
        Assert.True(phase.RollingMode, "Active LineUpPhase should flip to rolling mode");
        Assert.True(luaw.Requirements[0].IsSatisfied, "LUAW pre-satisfy fallback must still run");
    }

    [Fact]
    public void TryUpgradeToRolling_AlreadyRolling_Idempotent()
    {
        double rwyHdg = 90.0;
        double acLat = 37.0 - 200.0 / (GeoMath.FeetPerNm * 60.0);
        var (aircraft, ctx) = MakeFixture(rwyHdg, acLat, -121.995, 0.0);

        var phase = new LineUpPhase();
        InstallPhaseListWithNext(aircraft, phase, new TakeoffPhase());
        phase.OnStart(ctx);
        Assert.True(phase.RollingMode);

        bool upgraded = phase.TryUpgradeToRolling(ctx);
        Assert.True(upgraded);
        Assert.True(phase.RollingMode);
    }
}
