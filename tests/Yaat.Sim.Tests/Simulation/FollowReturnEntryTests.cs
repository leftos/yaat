using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Helpers.FollowTestHelpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A pursuit that left a pattern leg and ends with the follower between its circuit's base turn point and the threshold on
/// the approach side continues the circuit forward from where the follower is, never reversing it: the final when the
/// follower passes the final join's gates, else a base from here when it is on the pattern side heading toward the final with
/// room for the final turn, else the downwind when it flies the downwind's way, else the upwind. Aligned on the final beyond
/// that window, it joins the final. A spacing loss behind a lead on that final breaks off outward through the turn-out within
/// the turn-out's range, re-enters by the downwind beyond it, and never takes the base or final join. Real OAK navdata, C172s
/// on 28R right traffic.
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

    private static double ApproachSpeedKt => AircraftPerformance.ApproachSpeed("C172", AircraftCategory.Piston);

    private static double PatternAltitudeFt(RunwayInfo rwy) =>
        rwy.AirportElevationFt + CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Piston);

    private static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitude) =>
        FollowTestHelpers.MakeVfr(callsign, position, heading, altitude, ApproachSpeedKt);

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

    /// <summary>
    /// Aligned on the final 5 nm out, beyond the forward window (the circuit's base turn point plus
    /// <see cref="VfrFollowPhase.BaseExtensionLimitNm"/>, about 3 nm here), when the lead despawns: the follower joins the final
    /// rather than a downwind entry that would turn it back.
    /// </summary>
    [Fact]
    public void AlignedOnTheFinalBeyondTheWindow_LeadDespawns_JoinsTheFinal()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = PursuerWhoseLeadDespawns(engine, 5.0, 0.0, rwy.TrueHeading, 1000);

        engine.TickPhysics(0.25);
        double edgeNm = ForwardWindowEdgeNm(rwy);
        output.WriteLine($"forward window edge {edgeNm:F2} nm along the final; follower 5.00 nm out");
        TraceReturn(follower);

        Assert.True(edgeNm < 5.0, $"the forward window edge {edgeNm:F2} nm is not inside the follower's 5.00 nm");
        AssertReturnedCleared(follower);
        Assert.Contains(follower.Phases!.Phases, phase => phase is PatternEntryPhase { Kind: PatternEntryKind.Final });
        Assert.DoesNotContain(follower.Phases.Phases, phase => phase is DownwindPhase or BasePhase);
    }

    /// <summary>
    /// The along-final distance of the return's forward window edge on 28R right traffic: the circuit's base turn point plus
    /// <see cref="VfrFollowPhase.BaseExtensionLimitNm"/>, from the same geometry the return computes (no authored layout).
    /// </summary>
    private static double ForwardWindowEdgeNm(RunwayInfo rwy)
    {
        PatternWaypoints wp = PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, AircraftCategory.Piston, "C172", 0, PatternDirection.Right)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = PatternAltitudeFt(rwy),
                AirportRunways = NavigationDatabase.Instance.GetRunways(rwy.AirportId),
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        double baseTurnAlongNm = GeoMath.AlongTrackDistanceNm(
            new LatLon(wp.BaseTurnLat, wp.BaseTurnLon),
            Threshold(rwy),
            rwy.TrueHeading.ToReciprocal()
        );
        return baseTurnAlongNm + VfrFollowPhase.BaseExtensionLimitNm;
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
    /// From base 0.25 nm right of the centerline (inside one turn radius), too close behind a lead on the same final to keep
    /// the spacing at the speed floor: the follow breaks off through the turn-out, its reversal turned away from the lead and
    /// the field, at <see cref="VfrFollowPhase.BreakOffBankDegrees"/> of bank. Through the turn-out (up to the sample the lead
    /// has passed at) it may cross 28R's final but stays at least 0.02 nm short of half the 28L/28R centerline spacing
    /// (AIM 4-3-3 FIG 4-3-3 key 7) and takes no base or final join; then one base behind the lead. It never closes on the
    /// lead, and its clearance to land stands.
    /// </summary>
    [Fact]
    public void SpacingLossBehindALeadOnTheSameFinal_BreaksOffOutwardThroughTheTurnOut()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        double halfSpacingNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(Threshold(Oak("28L")), Threshold(rwy), rwy.TrueHeading)) / 2.0;
        AircraftState lead = AddFinalLead(engine, 1.6);
        AircraftState follower = AddPursuerFromBase(engine, 1.8, 0.25, rwy.TrueHeading - 90.0, 700);

        BreakOffTrace trace = FlyTheBreakOff(engine, lead, follower, rwy);

        double bankRate = VfrFollowPhase.BreakOffTurnRateDegPerSec(trace.GroundSpeedAtWarningKt);
        output.WriteLine(
            $"30° rate {bankRate:F3} deg/s at {trace.GroundSpeedAtWarningKt:F1} kt; first-10-s overrides "
                + string.Join(", ", trace.EarlyTurnRateOverrides.Select(r => r?.ToString("F3") ?? "none"))
        );
        Assert.NotEmpty(trace.EarlyTurnRateOverrides);
        Assert.All(trace.EarlyTurnRateOverrides, rate => Assert.True((rate is { } r) && (Math.Abs(r - bankRate) <= 0.01), $"override {rate}"));
        output.WriteLine(
            $"28L/28R centerline spacing {2.0 * halfSpacingNm:F3} nm; minimum pattern-side cross-track through the lead passing "
                + $"{trace.MinSideThroughPassedNm:F3} nm"
        );
        Assert.True(
            trace.MinSideThroughPassedNm >= -halfSpacingNm + 0.02,
            $"pattern-side cross-track fell to {trace.MinSideThroughPassedNm:F3} nm by the time the lead passed, "
                + $"within 0.02 nm of half the parallel spacing ({-halfSpacingNm:F3} nm)"
        );
    }

    /// <summary>
    /// The break-off bank and the turn-rate override it replaced survive a snapshot round-trip; an older snapshot has none.
    /// </summary>
    [Fact]
    public void BreakOffBank_SurvivesSnapshotRoundTrip()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddFinalLead(engine, 1.6);
        AircraftState follower = AddPursuerFromBase(engine, 1.8, 0.25, rwy.TrueHeading - 90.0, 700);
        // 3°/s, a turn radius (0.33 nm at 62.6 kt) wider than the follower's 0.246 nm offset, so the break-off bank fires.
        follower.Targets.TurnRateOverride = 3.0;
        for (int i = 0; (i < 20 * 4) && !HasSpacingLossWarning(follower); i++)
        {
            engine.TickPhysics(0.25);
        }

        VfrFollowPhase phase = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        var original = (VfrFollowPhaseDto)phase.ToSnapshot();
        string json = JsonSerializer.Serialize<PhaseDto>(original, RecordingJsonOptions.Default);
        VfrFollowPhaseDto dto = Assert.IsType<VfrFollowPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        var copy = (VfrFollowPhaseDto)VfrFollowPhase.FromSnapshot(dto).ToSnapshot();

        Assert.True(original.BreakOffBankFlown);
        Assert.Equal(3.0, original.BreakOffReplacedTurnRateOverride);
        Assert.True(copy.BreakOffBankFlown);
        Assert.Equal(3.0, copy.BreakOffReplacedTurnRateOverride);
        var older = new VfrFollowPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 1.0,
            TargetCallsign = Leader,
        };
        Assert.Null(((VfrFollowPhaseDto)VfrFollowPhase.FromSnapshot(older).ToSnapshot()).BreakOffBankFlown);
    }

    /// <summary>
    /// <see cref="SpacingLossBehindALeadOnTheSameFinal_BreaksOffOutwardThroughTheTurnOut"/> from 0.8 nm right of the centerline:
    /// the turn-out stays on the pattern side until the lead has passed.
    /// </summary>
    [Fact]
    public void SpacingLossFarOnThePatternSide_TurnOutStaysOnThePatternSide()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddFinalLead(engine, 1.6);
        AircraftState follower = AddPursuerFromBase(engine, 1.8, 0.8, rwy.TrueHeading - 90.0, 700);

        BreakOffTrace trace = FlyTheBreakOff(engine, lead, follower, rwy);

        output.WriteLine($"minimum pattern-side cross-track before the lead passed {trace.MinSideBeforePassedNm:F3} nm");
        Assert.True(trace.MinSideBeforePassedNm > 0.0, $"the turn-out crossed 28R's final ({trace.MinSideBeforePassedNm:F3} nm)");
        Assert.DoesNotContain(trace.EarlyTurnRateOverrides, rate => IsBreakOffRate(rate, trace));
    }

    /// <summary>
    /// From base 6.0 nm out and 0.25 nm right of the centerline, beyond <see cref="VfrFollowPhase.TurnOutRangeNm"/> of the
    /// threshold, too close behind a lead 5.8 nm out on the same final: no turn-out; the follow ends into the return's downwind
    /// entry, with no "no room to turn base" note.
    /// </summary>
    [Fact]
    public void SpacingLossBeyondTheTurnOutRange_ReentersByTheDownwind()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddFinalLead(engine, 5.8);
        AircraftState follower = AddPursuerFromBase(engine, 6.0, 0.25, rwy.TrueHeading - 90.0, 1000);
        Assert.True(GeoMath.DistanceNm(follower.Position, Threshold(rwy)) > VfrFollowPhase.TurnOutRangeNm, "the follower starts within range");

        for (int i = 1; (i <= 20 * 4) && (follower.Phases?.CurrentPhase is VfrFollowPhase); i++)
        {
            engine.TickPhysics(0.25);
            Assert.False(follower.Phases?.CurrentPhase is VfrFollowPhase { TurningOut: true }, $"t={i / 4.0:F2}s turned out beyond the range");
        }

        TraceReturn(follower);
        Assert.True(HasSpacingLossWarning(follower), "no \"unable to maintain separation\" warning within 20 s");
        Assert.IsNotType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.True(IndexOf<DownwindPhase>(follower) >= 0, "no downwind entry after the spacing loss");
        Assert.DoesNotContain(follower.PendingWarnings, w => w.Contains("no room to turn base", StringComparison.Ordinal));
    }

    /// <summary>
    /// <see cref="SpacingLossBehindALeadOnTheSameFinal_BreaksOffOutwardThroughTheTurnOut"/>'s break-off with TRATE 5 issued
    /// during the turn-out: the controller's turn rate survives the turn-out's end.
    /// </summary>
    [Fact]
    public void BreakOff_TrateDuringTurnOut_SurvivesTheTurnOutEnd()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddFinalLead(engine, 1.6);
        AircraftState follower = AddPursuerFromBase(engine, 1.8, 0.25, rwy.TrueHeading - 90.0, 700);
        for (int i = 0; (i < 20 * 4) && !HasSpacingLossWarning(follower); i++)
        {
            engine.TickPhysics(0.25);
        }

        Assert.True(follower.Phases?.CurrentPhase is VfrFollowPhase { TurningOut: true }, "the spacing loss did not turn out");
        CommandResult result = CommandDispatcher.Dispatch(new SetTurnRateCommand(5.0), follower, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success, result.Message);
        for (int i = 0; (i < 90 * 4) && (follower.Phases?.CurrentPhase is VfrFollowPhase { TurningOut: true }); i++)
        {
            engine.TickPhysics(0.25);
        }

        TraceReturn(follower);
        Assert.False(follower.Phases?.CurrentPhase is VfrFollowPhase { TurningOut: true }, "the turn-out never ended");
        Assert.Equal(5.0, follower.Targets.TurnRateOverride);
        Assert.True(follower.Targets.HasExplicitTurnRate, "the controller's turn rate was dropped");
    }

    /// <summary>What <see cref="FlyTheBreakOff"/> measured.</summary>
    private sealed class BreakOffTrace(double gapAtWarningNm, double groundSpeedAtWarningKt)
    {
        public double GapAtWarningNm { get; } = gapAtWarningNm;
        public double GroundSpeedAtWarningKt { get; } = groundSpeedAtWarningKt;
        public double GapNm { get; set; } = gapAtWarningNm;
        public double MinGapNm { get; set; } = gapAtWarningNm;

        /// <summary>The least pattern-side cross-track (nm) through the sample the lead has passed at: the turn-out itself.</summary>
        public double MinSideThroughPassedNm { get; set; } = double.MaxValue;
        public double MinSideBeforePassedNm { get; set; } = double.MaxValue;
        public int? PassedAtSecond { get; set; }

        /// <summary>The follower's turn-rate override at each of the first 10 samples it is turning out.</summary>
        public List<double?> EarlyTurnRateOverrides { get; } = [];
    }

    /// <summary>
    /// Ticks until the spacing-loss warning (failing past 20 s), then samples once a second for 60 s (or until the lead lands
    /// or despawns) with <see cref="SampleBreakOff"/>, and asserts the gap at the end is larger than at the warning.
    /// </summary>
    private BreakOffTrace FlyTheBreakOff(SimulationEngine engine, AircraftState lead, AircraftState follower, RunwayInfo rwy)
    {
        for (int i = 0; (i < 20 * 4) && !HasSpacingLossWarning(follower); i++)
        {
            engine.TickPhysics(0.25);
        }

        TraceReturn(follower);
        Assert.True(HasSpacingLossWarning(follower), "no \"unable to maintain separation\" warning within 20 s");
        var trace = new BreakOffTrace(GeoMath.DistanceNm(follower.Position, lead.Position), follower.GroundSpeed);
        output.WriteLine(
            $"at the warning: gap {trace.GapAtWarningNm:F3} nm, pattern-side cross-track {PatternSideCrossTrackNm(follower, rwy):F3} nm"
        );

        for (int second = 1; second <= 60; second++)
        {
            TickSeconds(engine, 1);
            if (engine.World.FindAircraft(Leader) is null)
            {
                output.WriteLine($"t={second}s lead despawned, sampling stopped");
                break;
            }

            SampleBreakOff(trace, second, lead, follower, rwy);
            if (lead.IsOnGround)
            {
                output.WriteLine($"t={second}s lead landed, sampling stopped");
                break;
            }
        }

        output.WriteLine(
            $"at the end: gap {trace.GapNm:F3} nm (least {trace.MinGapNm:F3} nm); lead passed at {trace.PassedAtSecond?.ToString() ?? "never"} s"
        );
        Assert.True(trace.GapNm > trace.GapAtWarningNm, $"gap {trace.GapNm:F3} nm did not open from {trace.GapAtWarningNm:F3} nm");
        return trace;
    }

    /// <summary>
    /// One sample of the break-off: in a turn-out at the first sample; the gap never more than 0.10 nm under the gap at the
    /// warning (the reversal from a base heading swings through the lead's final direction before it opens); the clearance to
    /// land kept; no base or final join and no upwind until the turn-out ends (the lead has passed), and then a base behind
    /// the lead with the gap at or above the gap at the warning and no turn-rate override left at the break-off bank's rate.
    /// The pattern-side cross-track counts toward <see cref="BreakOffTrace.MinSideThroughPassedNm"/> only up to and including
    /// the sample the lead has passed at: the turn-out itself, not the base turned behind the lead after it.
    /// </summary>
    private void SampleBreakOff(BreakOffTrace trace, int second, AircraftState lead, AircraftState follower, RunwayInfo rwy)
    {
        trace.GapNm = GeoMath.DistanceNm(follower.Position, lead.Position);
        trace.MinGapNm = Math.Min(trace.MinGapNm, trace.GapNm);
        double sideNm = PatternSideCrossTrackNm(follower, rwy);
        bool turningOut = follower.Phases?.CurrentPhase is VfrFollowPhase { TurningOut: true };
        LogBreakOffSample(trace, second, follower, sideNm, turningOut);
        if (second == 1)
        {
            Assert.True(turningOut, "the spacing loss did not turn out");
        }

        if (turningOut && (second <= 10))
        {
            trace.EarlyTurnRateOverrides.Add(follower.Targets.TurnRateOverride);
        }

        Assert.True(
            trace.GapNm >= trace.GapAtWarningNm - 0.10,
            $"t={second}s gap {trace.GapNm:F3} nm closed on the lead from {trace.GapAtWarningNm:F3} nm"
        );
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases!.LandingClearance);
        if (trace.PassedAtSecond is not null)
        {
            return;
        }

        trace.MinSideThroughPassedNm = Math.Min(trace.MinSideThroughPassedNm, sideNm);
        if (turningOut)
        {
            trace.MinSideBeforePassedNm = Math.Min(trace.MinSideBeforePassedNm, sideNm);
            Assert.False(StartsWithBaseOrFinalJoin(follower), $"t={second}s the break-off joined the base or the final");
            Assert.True(IndexOf<UpwindPhase>(follower) < 0, $"t={second}s the break-off took the upwind leg");
            return;
        }

        trace.PassedAtSecond = second;
        Assert.IsType<BasePhase>(follower.Phases.CurrentPhase);
        Assert.True(trace.GapNm >= trace.GapAtWarningNm, $"t={second}s base turned {trace.GapNm:F3} nm behind the lead");
        Assert.False(
            IsBreakOffRate(follower.Targets.TurnRateOverride, trace),
            $"t={second}s the turn-out ended with the break-off bank's rate left ({follower.Targets.TurnRateOverride:F3} deg/s)"
        );
    }

    /// <summary>One <see cref="SampleBreakOff"/> line: phase, turn-out state, gap, cross-track, heading and turn-rate override.</summary>
    private void LogBreakOffSample(BreakOffTrace trace, int second, AircraftState follower, double sideNm, bool turningOut) =>
        output.WriteLine(
            $"t={second}s {follower.Phases?.CurrentPhase?.GetType().Name ?? "none"} turningOut {turningOut} gap {trace.GapNm:F3} "
                + $"side {sideNm:F3} hdg {follower.TrueHeading.Degrees:F0} trate {follower.Targets.TurnRateOverride?.ToString("F2") ?? "none"}"
        );

    /// <summary>
    /// True when <paramref name="rate"/> (deg/s) is within 0.01 of the <see cref="VfrFollowPhase.BreakOffBankDegrees"/> rate at
    /// the ground speed at the warning.
    /// </summary>
    private static bool IsBreakOffRate(double? rate, BreakOffTrace trace) =>
        (rate is { } r) && (Math.Abs(r - VfrFollowPhase.BreakOffTurnRateDegPerSec(trace.GroundSpeedAtWarningKt)) <= 0.01);

    private static bool HasSpacingLossWarning(AircraftState follower) =>
        follower.PendingWarnings.Exists(w => w.Contains("unable to maintain separation", StringComparison.Ordinal));

    /// <summary>The follower's cross-track from 28R's extended centerline, positive toward the right-traffic pattern side.</summary>
    private static double PatternSideCrossTrackNm(AircraftState follower, RunwayInfo rwy) =>
        GeoMath.SignedCrossTrackDistanceNm(follower.Position, Threshold(rwy), rwy.TrueHeading);
}
