using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A pursuit that left a pattern leg and ends with the follower between its circuit's base turn point and the threshold on
/// the approach side continues the circuit forward from where the follower is, never reversing it: the final when the
/// follower passes the final join's gates, else a base from here when it is on the pattern side heading toward the final with
/// room for the final turn, else the downwind when it flies the downwind's way, else the upwind. A spacing loss behind a lead
/// on that final never takes the base or final join. Real OAK navdata, C172s on 28R right traffic.
/// </summary>
public class FollowReturnEntryTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
    }

    private static RunwayInfo Oak(string designator) =>
        NavigationDatabase.Instance.GetRunway("KOAK", designator) ?? throw new InvalidOperationException($"KOAK {designator} missing from navdata");

    private static LatLon Threshold(RunwayInfo rwy) => new(rwy.ThresholdLatitude, rwy.ThresholdLongitude);

    /// <summary>Point <paramref name="alongNm"/> out the final and <paramref name="crossNm"/> to the right of the landing direction.</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double crossNm)
    {
        LatLon onCenterline = GeoMath.ProjectPoint(Threshold(rwy), rwy.TrueHeading.ToReciprocal(), alongNm);
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }

        TrueHeading perpendicular = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perpendicular, Math.Abs(crossNm));
    }

    private static double ApproachSpeedKt => AircraftPerformance.ApproachSpeed("C172", AircraftCategory.Piston);

    private static double PatternAltitudeFt(RunwayInfo rwy) =>
        rwy.AirportElevationFt + CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Piston);

    private static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = ApproachSpeedKt,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 110,
            },
        };

    /// <summary>
    /// A follower pursuing <see cref="Leader"/> from its 28R right-traffic base, at <paramref name="alongNm"/> out and
    /// <paramref name="crossNm"/> right of the centerline, flying <paramref name="heading"/>, cleared to land.
    /// </summary>
    private static AircraftState AddPursuerFromBase(SimulationEngine engine, double alongNm, double crossNm, TrueHeading heading, double altitudeFt)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, alongNm, crossNm), heading, altitudeFt);
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList { LandingClearance = ClearanceType.ClearedToLand, ClearedRunwayId = rwy.Designator };
        follower.Approach.HasReportedTrafficInSight = true;
        CommandDispatcher.InstallVfrFollowPhase(
            follower,
            Leader,
            new FollowPatternReturn(rwy, PatternDirection.Right, PatternAltitudeFt(rwy), FromBase: true),
            climbOutGate: null
        );
        return follower;
    }

    /// <summary>
    /// <see cref="AddPursuerFromBase"/> behind a lead far off the circuit that then despawns, so the next tick ends the follow
    /// and the follower re-enters its circuit from where it is.
    /// </summary>
    private static AircraftState PursuerWhoseLeadDespawns(
        SimulationEngine engine,
        double alongNm,
        double crossNm,
        TrueHeading heading,
        double altitudeFt
    )
    {
        RunwayInfo rwy = Oak("28R");
        engine.World.AddAircraft(MakeVfr(Leader, OffFinal(rwy, 10.0, 6.0), rwy.TrueHeading, 2000));
        AircraftState follower = AddPursuerFromBase(engine, alongNm, crossNm, heading, altitudeFt);
        engine.World.RemoveAircraft(Leader);
        return follower;
    }

    /// <summary>The lead on the 28R right-traffic final <paramref name="alongNm"/> out, on the glidepath, cleared to land.</summary>
    private static AircraftState AddFinalLead(SimulationEngine engine, double alongNm)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, alongNm, 0.0), rwy.TrueHeading, rwy.ElevationFt + (318.0 * alongNm));
        engine.World.AddAircraft(lead);
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = rwy,
                Category = AircraftCategory.Piston,
                AircraftType = "C172",
                WindSpeedKt = 0,
                Direction = PatternDirection.Right,
                EntryLeg = PatternEntryLeg.Final,
                TouchAndGo = false,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = NavigationDatabase.Instance.GetRunways(rwy.AirportId),
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        lead.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = PatternDirection.Right,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = rwy.Designator,
        };
        foreach (Phase phase in circuit)
        {
            lead.Phases.Add(phase);
        }

        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead, ActiveRunways.Empty));
        Assert.IsType<FinalApproachPhase>(lead.Phases.CurrentPhase);
        return lead;
    }

    private void TraceReturn(AircraftState follower)
    {
        output.WriteLine(
            $"now {follower.Phases?.CurrentPhase?.GetType().Name ?? "none"}; list "
                + string.Join(", ", follower.Phases?.Phases.Select(p => p is PatternEntryPhase e ? $"Entry({e.Kind})" : p.GetType().Name) ?? [])
        );
        output.WriteLine(string.Join(Environment.NewLine, follower.PendingWarnings));
    }

    /// <summary>The follow is over, the follower is on 28R's circuit and its clearance to land came with it.</summary>
    private static void AssertReturnedCleared(AircraftState follower)
    {
        Assert.IsNotType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
    }

    /// <summary>The rebuilt list opens with a base turned from here or a final join: the base and final joins the return builds.</summary>
    private static bool StartsWithBaseOrFinalJoin(AircraftState follower) =>
        follower.Phases!.Phases.First() is BasePhase or PatternEntryPhase { Kind: PatternEntryKind.Final };

    private static int IndexOf<T>(AircraftState follower)
        where T : Phase => follower.Phases!.Phases.ToList().FindIndex(phase => phase is T);

    // ─── Base ───

    /// <summary>
    /// 1.5 nm out on the base heading toward the final when the lead despawns: the follower turns base from where it is, never
    /// back to a downwind entry, and its heading never swings more than 90° off the heading it was flying.
    /// </summary>
    [Fact]
    public void OnBaseHeadingTowardTheFinal_LeadDespawns_ContinuesTheBase()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        TrueHeading baseHeading = rwy.TrueHeading - 90.0;
        AircraftState follower = PursuerWhoseLeadDespawns(engine, 1.5, 0.8, baseHeading, 900);

        engine.TickPhysics(0.25);
        TraceReturn(follower);

        AssertReturnedCleared(follower);
        Assert.IsType<BasePhase>(follower.Phases!.CurrentPhase);
        Assert.DoesNotContain(follower.Phases.Phases, phase => phase is PatternEntryPhase or DownwindPhase);

        for (int i = 0; (i < 90 * 4) && (follower.Phases?.CurrentPhase is BasePhase); i++)
        {
            engine.TickPhysics(0.25);
            Assert.True(
                follower.TrueHeading.AbsAngleTo(baseHeading) <= 90.0,
                $"t={i / 4.0:F1}s heading {follower.TrueHeading.Degrees:F0} swung more than 90° off the base heading {baseHeading.Degrees:F0}"
            );
        }

        Assert.IsType<FinalApproachPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases!.LandingClearance);
    }

    /// <summary>
    /// The base gate's case table on the pattern side: outside (0.8 nm) or inside (0.1 nm) one final-turn radius of the
    /// centerline (a C172's, about 0.2 nm at the piston's 5°/s pattern turn rate), on the base heading (toward the final)
    /// or the downwind heading (90° off it, outside the gate), and 1.5 nm
    /// out (room for the final turn) or 0.3 nm out (none). Only outside the radius, on the base heading and with room is it
    /// a base; inside the radius the turn to final would overshoot onto the non-pattern side (toward 28L's final), so it falls
    /// through to the downwind (on the downwind heading) or the upwind.
    /// </summary>
    [Theory]
    [InlineData(1.5, 0.8, true, PatternEntryLeg.Base)]
    [InlineData(0.3, 0.8, true, PatternEntryLeg.Upwind)]
    [InlineData(1.5, 0.8, false, PatternEntryLeg.Downwind)]
    [InlineData(0.3, 0.8, false, PatternEntryLeg.Downwind)]
    [InlineData(1.5, 0.1, true, PatternEntryLeg.Upwind)]
    [InlineData(0.3, 0.1, true, PatternEntryLeg.Upwind)]
    [InlineData(1.5, 0.1, false, PatternEntryLeg.Downwind)]
    [InlineData(0.3, 0.1, false, PatternEntryLeg.Downwind)]
    public void BaseGate_TurnsBaseOnlyOutsideOneRadiusHeadingInWithRoom(double alongNm, double crossNm, bool onBaseHeading, PatternEntryLeg expected)
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        TrueHeading heading = onBaseHeading ? rwy.TrueHeading - 90.0 : rwy.TrueHeading.ToReciprocal();
        AircraftState follower = PursuerWhoseLeadDespawns(engine, alongNm, crossNm, heading, 1000);

        engine.TickPhysics(0.25);
        TraceReturn(follower);

        AssertReturnedCleared(follower);
        int upwind = IndexOf<UpwindPhase>(follower);
        int downwind = IndexOf<DownwindPhase>(follower);
        switch (expected)
        {
            case PatternEntryLeg.Base:
                Assert.IsType<BasePhase>(follower.Phases!.Phases.First());
                break;
            case PatternEntryLeg.Upwind:
                Assert.False(StartsWithBaseOrFinalJoin(follower), "the return joined the base or the final");
                Assert.True((upwind >= 0) && (upwind < downwind), $"upwind at {upwind}, downwind at {downwind}: no upwind entry");
                break;
            default:
                Assert.False(StartsWithBaseOrFinalJoin(follower), "the return joined the base or the final");
                Assert.True((upwind < 0) && (downwind >= 0), $"upwind at {upwind}, downwind at {downwind}: no downwind entry");
                break;
        }
    }

    // ─── Final ───

    /// <summary>Aligned on the final 2 nm out when the lead despawns: the follower joins the final.</summary>
    [Fact]
    public void AlignedOnTheFinal_LeadDespawns_JoinsTheFinal()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = PursuerWhoseLeadDespawns(engine, 2.0, 0.0, rwy.TrueHeading, 700);

        engine.TickPhysics(0.25);
        TraceReturn(follower);

        AssertReturnedCleared(follower);
        Assert.Contains(follower.Phases!.Phases, phase => phase is PatternEntryPhase { Kind: PatternEntryKind.Final });
        Assert.DoesNotContain(follower.Phases.Phases, phase => phase is DownwindPhase or BasePhase);
    }

    // ─── Upwind ───

    /// <summary>
    /// Just short of abeam the threshold, 0.3 nm out on the pattern side, inbound on the base heading: too close in for a base, so the
    /// follower takes the upwind entry (upwind, crosswind, downwind), never a base or final join.
    /// </summary>
    [Fact]
    public void InboundAbeamTheThreshold_LeadDespawns_TakesTheUpwind()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        // 0.1 nm out keeps it clear of the threshold's own line, where the past-threshold rule decides.
        AircraftState follower = PursuerWhoseLeadDespawns(engine, 0.1, 0.3, rwy.TrueHeading - 90.0, 1000);

        engine.TickPhysics(0.25);
        TraceReturn(follower);

        AssertReturnedCleared(follower);
        Assert.False(StartsWithBaseOrFinalJoin(follower), "the return joined the base or the final");
        int upwind = IndexOf<UpwindPhase>(follower);
        Assert.True(upwind >= 0, "no upwind leg");
        Assert.True(upwind < IndexOf<DownwindPhase>(follower), "the downwind comes before the upwind");
    }

    // ─── Downwind ───

    /// <summary>Regression guard: on the pattern side abeam the threshold, flying the downwind heading, the downwind entry.</summary>
    [Fact]
    public void FlyingTheDownwindHeadingAbeamTheThreshold_LeadDespawns_EntersTheDownwind()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = PursuerWhoseLeadDespawns(engine, 0.0, 0.8, rwy.TrueHeading.ToReciprocal(), 1000);

        engine.TickPhysics(0.25);
        TraceReturn(follower);

        AssertReturnedCleared(follower);
        Assert.False(StartsWithBaseOrFinalJoin(follower), "the return joined the base or the final");
        Assert.DoesNotContain(follower.Phases!.Phases, phase => phase is UpwindPhase);
        Assert.True(IndexOf<DownwindPhase>(follower) >= 0, "no downwind leg");
    }

    // ─── Non-pattern side ───

    /// <summary>Regression guard: on the non-pattern side heading for the final, the wrong-side re-entry, the midfield crossing.</summary>
    [Fact]
    public void OnTheNonPatternSide_LeadDespawns_CrossesMidfield()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = PursuerWhoseLeadDespawns(engine, 1.0, -0.8, rwy.TrueHeading + 90.0, 1000);

        engine.TickPhysics(0.25);
        TraceReturn(follower);

        AssertReturnedCleared(follower);
        Assert.False(StartsWithBaseOrFinalJoin(follower), "the return joined the base or the final");
        Assert.Contains(follower.Phases!.Phases, phase => phase is MidfieldCrossingPhase);
    }

    // ─── Spacing loss ───

    /// <summary>
    /// From base, too close behind a lead on the same final to keep the spacing at the speed floor: the follow breaks off as
    /// before and never takes a base or final join, which would put the follower back onto the lead's final.
    /// </summary>
    [Fact]
    public void SpacingLossBehindALeadOnTheSameFinal_NeverJoinsTheBaseOrFinal()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddFinalLead(engine, 1.6);
        AircraftState follower = AddPursuerFromBase(engine, 1.8, 0.25, rwy.TrueHeading - 90.0, 700);

        for (int i = 0; (i < 5 * 4) && (follower.Phases?.CurrentPhase is VfrFollowPhase); i++)
        {
            engine.TickPhysics(0.25);
        }

        TraceReturn(follower);
        Assert.Contains(follower.PendingWarnings, w => w.Contains("unable to maintain separation", StringComparison.Ordinal));
        Assert.IsNotType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.IsNotType<BasePhase>(follower.Phases.CurrentPhase);
        Assert.False(StartsWithBaseOrFinalJoin(follower), "the return joined the base or the final");
        Assert.True(IndexOf<UpwindPhase>(follower) < 0, "the spacing-loss return took the upwind leg");
        Assert.True(IndexOf<DownwindPhase>(follower) >= 0, "no downwind entry after the spacing loss");
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
    }
}
