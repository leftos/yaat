using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// FOLLOW issued to an aircraft on a pattern leg when the lead has no current runway (free flight,
/// possibly with a pattern entry queued behind a DCT). The recorded case: N123AB on a long right base
/// to OAK 28R told to follow N314GT, which was on <c>DCT VPCBT; ERB 28R</c>. The old dispatcher took the
/// unknown lead runway to mean "same runway" and only retargeted the follow, so nothing moved.
///
/// Routing under test:
/// - lead queued for a different runway: re-sequence onto it from downwind/upwind/entry; refuse from base/final;
/// - otherwise (no queued entry, or one for the follower's runway): free pursuit with a pattern return,
///   except from final (refused) and from base when the lead is not ahead within ±60° of track (refused);
/// - the pursuit phase list keeps the follower's landing clearance.
/// </summary>
public class FollowRunwaylessLeadFromPatternTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-follow-base-freeflight-lead-recording.yaat-bug-report-bundle.zip";
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    private static RunwayInfo Oak(string designator) =>
        NavigationDatabase.Instance.GetRunway("KOAK", designator) ?? throw new InvalidOperationException($"KOAK {designator} missing from navdata");

    /// <summary>Point <paramref name="alongNm"/> out the final and <paramref name="crossNm"/> to the right of the landing direction.</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double crossNm)
    {
        LatLon onCenterline = GeoMath.ProjectPoint(
            new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            rwy.TrueHeading.ToReciprocal(),
            alongNm
        );
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }

        TrueHeading perpendicular = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perpendicular, Math.Abs(crossNm));
    }

    private static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = 90,
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

    /// <summary>Put <paramref name="ac"/> on the circuit for <paramref name="rwy"/> starting at <paramref name="leg"/>.</summary>
    private static void PutOnCircuit(AircraftState ac, RunwayInfo rwy, PatternDirection direction, PatternEntryLeg leg)
    {
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategorization.Categorize(ac.AircraftType),
            ac.AircraftType,
            windSpeedKt: 0,
            direction,
            leg,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            NavigationDatabase.Instance.GetRunways(rwy.AirportId),
            authoredRunway: null
        );
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = direction };
        foreach (Phase phase in circuit)
        {
            ac.Phases.Add(phase);
        }

        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
    }

    /// <summary>Follower on the right base to 28R, 2.5 nm out and 1.5 nm right of the centerline, heading for the final.</summary>
    private static AircraftState AddBaseFollower(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 2.5, 1.5), rwy.TrueHeading - 90.0, 1000);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        PutOnCircuit(follower, rwy, PatternDirection.Right, PatternEntryLeg.Base);
        Assert.IsType<BasePhase>(follower.Phases!.CurrentPhase);
        return follower;
    }

    private static AircraftState AddLead(SimulationEngine engine, LatLon position, string command)
    {
        AircraftState lead = MakeVfr(Leader, position, new TrueHeading(180), 1500);
        engine.World.AddAircraft(lead);
        CommandResult setup = engine.SendCommand(Leader, command);
        Assert.True(setup.Success, setup.Message);
        Assert.Null(lead.Phases?.AssignedRunway);
        return lead;
    }

    // ─── Recorded case ───

    [Fact]
    public void FollowFromBase_LeadNoCurrentRunwayQueuedSame_AheadInstallsPursuit()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            SessionRecording recording = archive.ToBaseSessionRecording();
            SimulationEngine engine = BuildEngine();

            engine.Replay(recording, 284);

            AircraftState? follower = engine.FindAircraft("N123AB");
            AircraftState? lead = engine.FindAircraft("N314GT");
            Assert.NotNull(follower);
            Assert.NotNull(lead);
            Assert.IsType<BasePhase>(follower.Phases?.CurrentPhase);
            Assert.Equal("28R", follower.Phases!.AssignedRunway?.Designator);
            Assert.Null(lead.Phases?.AssignedRunway);
            Assert.Equal(("28R", PatternDirection.Right), PatternCommandHandler.QueuedPatternEntry(lead));

            CommandResult result = engine.SendCommand("N123AB", "FOLLOW N314GT");
            output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

            Assert.True(result.Success, result.Message);
            VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases?.CurrentPhase);
            Assert.Equal("N314GT", pursuit.TargetCallsign);
            Assert.Equal("N314GT", follower.Approach.FollowingCallsign);
            Assert.NotNull(pursuit.PatternReturn);
            Assert.Equal("28R", pursuit.PatternReturn.Runway.Designator);
            Assert.Equal(PatternDirection.Right, pursuit.PatternReturn.Direction);
        }
    }

    // ─── Same-runway guard ───

    [Fact]
    public void FollowFromDownwind_LeadOnSameRunwayPattern_KeepsPhaseAndSetsFollowing()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, -1.0, 1.0), rwy.TrueHeading.ToReciprocal(), 1000);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        PutOnCircuit(follower, rwy, PatternDirection.Right, PatternEntryLeg.Downwind);
        Phase? before = follower.Phases!.CurrentPhase;

        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 2.0, 1.0), rwy.TrueHeading - 90.0, 1000);
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternDirection.Right, PatternEntryLeg.Base);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    // ─── Refusals ───

    [Fact]
    public void FollowFromBase_LeadQueuedOtherRunway_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState follower = AddBaseFollower(engine);
        Phase? before = follower.Phases!.CurrentPhase;
        AddLead(engine, OffFinal(Oak("28R"), 2.5, 0.5), "DCT VPCBT; ELD 28L");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.False(result.Success, result.Message);
        Assert.Contains("on base for runway 28R", result.Message);
        Assert.Contains("request vectors", result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowFromDownwind_LeadQueuedForSameDesignatorAtAnotherAirport_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        Phase? before = follower.Phases!.CurrentPhase;

        // Hayward also has 28L/28R: a lead queued for "ERB 28R" there is not landing OAK 28R.
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 3.0, 2.0), new TrueHeading(150), 1500);
        lead.FlightPlan.Destination = "KHWD";
        engine.World.AddAircraft(lead);
        CommandResult setup = engine.SendCommand(Leader, "DCT VPCBT; ERB 28R");
        Assert.True(setup.Success, setup.Message);
        Assert.Null(lead.Phases?.AssignedRunway);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.False(result.Success, result.Message);
        Assert.Contains("inbound to", result.Message);
        Assert.Contains("HWD", result.Message);
        Assert.Contains("request vectors", result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowFromDownwind_LeadOnGroundWithNoRunway_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        Phase? before = follower.Phases!.CurrentPhase;

        AircraftState lead = MakeVfr(Leader, new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading, rwy.ElevationFt);
        lead.IsOnGround = true;
        lead.IndicatedAirspeed = 0;
        engine.World.AddAircraft(lead);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.False(result.Success, result.Message);
        Assert.Equal($"Unable, {Leader} is on the ground", result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowFromBase_RunwaylessLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState follower = AddBaseFollower(engine);
        Phase? before = follower.Phases!.CurrentPhase;
        // Up the base leg behind the follower (farther from the centerline than it is).
        AddLead(engine, OffFinal(Oak("28R"), 2.5, 3.0), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.False(result.Success, result.Message);
        Assert.Contains("on base for runway 28R", result.Message);
        Assert.Contains($"{Leader} is not ahead", result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowFromFinal_LeadNoCurrentRunway_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 3.0, 0), rwy.TrueHeading, 900);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        PutOnCircuit(follower, rwy, PatternDirection.Right, PatternEntryLeg.Final);
        Phase? before = follower.Phases!.CurrentPhase;
        Assert.IsType<FinalApproachPhase>(before);

        // Dead ahead on the final course, but with no runway of its own: nothing to trail onto.
        AddLead(engine, OffFinal(rwy, 1.5, 0), "DCT VPCBT; ERB 28R");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.False(result.Success, result.Message);
        Assert.Contains("on final for runway 28R", result.Message);
        Assert.Contains($"request vectors to follow {Leader}", result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    // ─── Queued entry for another runway, from downwind ───

    /// <summary>Follower on the right downwind to <paramref name="rwy"/>, 1 nm abeam, flying the downwind heading.</summary>
    private static AircraftState AddDownwindFollower(SimulationEngine engine, RunwayInfo rwy)
    {
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, -1.0, 1.0), rwy.TrueHeading.ToReciprocal(), 1000);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        PutOnCircuit(follower, rwy, PatternDirection.Right, PatternEntryLeg.Downwind);
        return follower;
    }

    private void AssertEnteredQueuedRunway(AircraftState follower, CommandResult result, PatternDirection expectedDirection)
    {
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.Equal("28L", follower.Phases!.AssignedRunway?.Designator);
        Assert.Equal(expectedDirection, follower.Phases.TrafficDirection);
        Assert.True(
            follower.Phases.CurrentPhase is PatternEntryPhase or MidfieldCrossingPhase,
            $"expected a pattern entry to 28L, got {follower.Phases.CurrentPhase?.GetType().Name}"
        );
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowFromDownwind_LeadQueuedOtherRunway_BuildsEntryToQueuedRunway()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        // 28L's natural side is left (28R flies right traffic); the queued ERD names right, and wins.
        AddLead(engine, OffFinal(rwy, 3.0, 2.0), "DCT VPCBT; ERD 28L");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertEnteredQueuedRunway(follower, result, PatternDirection.Right);
    }

    [Fact]
    public void FollowFromDownwind_LeadQueuedOtherRunway_QueuedSideOverridesLeadStaleDirection()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        AircraftState lead = AddLead(engine, OffFinal(rwy, 3.0, 2.0), "DCT VPCBT; ERD 28L");
        lead.Phases ??= new PhaseList();
        lead.Phases.TrafficDirection = PatternDirection.Left;

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertEnteredQueuedRunway(follower, result, PatternDirection.Right);
    }

    [Fact]
    public void FollowFromDownwind_LeadQueuedStraightInOtherRunway_UsesLeadDirection()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        // EF names no side, so the lead's own circuit direction decides (over 28L's natural left).
        AircraftState lead = AddLead(engine, OffFinal(rwy, 3.0, 2.0), "DCT VPCBT; EF 28L");
        lead.Phases ??= new PhaseList();
        lead.Phases.TrafficDirection = PatternDirection.Right;

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertEnteredQueuedRunway(follower, result, PatternDirection.Right);
    }

    // ─── Pursuit ───

    [Fact]
    public void FollowFromDownwind_RunwaylessLeadNothingQueued_InstallsPursuitWithPatternReturn()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        double circuitAltitudeFt = ((DownwindPhase)follower.Phases!.CurrentPhase!).Waypoints!.PatternAltitude;
        AddLead(engine, OffFinal(rwy, -2.5, 1.5), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, pursuit.TargetCallsign);
        Assert.NotNull(pursuit.PatternReturn);
        Assert.Equal("28R", pursuit.PatternReturn.Runway.Designator);
        Assert.Equal(PatternDirection.Right, pursuit.PatternReturn.Direction);
        Assert.Equal(circuitAltitudeFt, pursuit.PatternReturn.PatternAltitudeFt);
    }

    [Fact]
    public void FollowFromStraightIn_RunwaylessLead_KeepsLandingClearanceWithoutPatternReturn()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 6.0, 0.8), rwy.TrueHeading, 1800);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = "28R",
        };
        follower.Phases.Add(
            new InterceptCoursePhase
            {
                FinalApproachCourse = rwy.TrueHeading,
                ThresholdLat = rwy.ThresholdLatitude,
                ThresholdLon = rwy.ThresholdLongitude,
                AssignedInterceptHeading = null,
            }
        );
        follower.Phases.Add(new FinalApproachPhase());
        follower.Phases.Add(new LandingPhase());
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower));
        AddLead(engine, OffFinal(rwy, 4.5, 0.5), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Null(pursuit.PatternReturn);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Equal("28R", follower.Phases.ClearedRunwayId);
        Assert.Contains(
            follower.PendingWarnings,
            w =>
                w.StartsWith($"{Follower} ", StringComparison.Ordinal)
                && w.EndsWith(" cancelled by FOLLOW, landing clearance kept", StringComparison.Ordinal)
        );
    }

    // ─── From an approach (intercept) ───

    [Fact]
    public void FollowFromIntercept_LeadInRunwayPattern_EntersPatternKeepingClearance()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 6.0, 0.8), rwy.TrueHeading, 1800);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = "28R",
        };
        follower.Phases.Add(
            new InterceptCoursePhase
            {
                FinalApproachCourse = rwy.TrueHeading,
                ThresholdLat = rwy.ThresholdLatitude,
                ThresholdLon = rwy.ThresholdLongitude,
                AssignedInterceptHeading = null,
            }
        );
        follower.Phases.Add(new FinalApproachPhase());
        follower.Phases.Add(new LandingPhase());
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower));
        follower.Targets.TurnRateOverride = 1.0;
        follower.Targets.HasExplicitTurnRate = true;

        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, -1.0, 1.0), rwy.TrueHeading.ToReciprocal(), 1000);
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternDirection.Right, PatternEntryLeg.Downwind);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}; now {follower.Phases?.CurrentPhase?.GetType().Name}");

        Assert.True(result.Success, result.Message);
        Assert.IsNotType<InterceptCoursePhase>(follower.Phases!.CurrentPhase);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Equal("28R", follower.Phases.ClearedRunwayId);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Null(follower.Targets.TurnRateOverride);
        Assert.False(follower.Targets.HasExplicitTurnRate);
        Assert.Contains(
            follower.PendingWarnings,
            w =>
                w.StartsWith($"{Follower} ", StringComparison.Ordinal)
                && w.EndsWith(" cancelled by FOLLOW, landing clearance kept", StringComparison.Ordinal)
        );
    }

    // ─── Ground and other-airport leads ───

    [Fact]
    public void FollowFromDownwind_LeadHoldingShortForTheParallel_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        Phase? before = follower.Phases!.CurrentPhase;

        RunwayInfo parallel = Oak("28L");
        AircraftState lead = MakeVfr(
            Leader,
            new LatLon(parallel.ThresholdLatitude, parallel.ThresholdLongitude),
            parallel.TrueHeading,
            parallel.ElevationFt
        );
        lead.IsOnGround = true;
        lead.IndicatedAirspeed = 0;
        lead.Phases = new PhaseList { AssignedRunway = parallel };
        engine.World.AddAircraft(lead);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.False(result.Success, result.Message);
        Assert.Equal($"Unable, {Leader} is on the ground", result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowFromDownwind_LeadInSameDesignatorPatternAtAnotherAirport_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        Phase? before = follower.Phases!.CurrentPhase;

        // Hayward's 28R shares OAK 28R's designator; a lead in its pattern is not on the follower's runway.
        RunwayInfo hayward = NavigationDatabase.Instance.GetRunway("KHWD", "28R") ?? throw new InvalidOperationException("KHWD 28R missing");
        AircraftState lead = MakeVfr(Leader, OffFinal(hayward, -1.0, -1.0), hayward.TrueHeading.ToReciprocal(), 1000);
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, hayward, PatternDirection.Left, PatternEntryLeg.Downwind);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.False(result.Success, result.Message);
        Assert.Contains("inbound to HWD", result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    // ─── Pattern return ───

    /// <summary>
    /// Base follower told to follow a runwayless lead well ahead of it (across the final, moving away), so the
    /// pursuit keeps its spacing and no join fires while the test ticks.
    /// </summary>
    private VfrFollowPhase FollowRunwaylessLeadFromBase(SimulationEngine engine, AircraftState follower)
    {
        AddLead(engine, OffFinal(Oak("28R"), 3.5, -1.5), "DCT VPCBT");
        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        return Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
    }

    [Fact]
    public void BaseFollower_InPursuit_HoldsPatternAltitude()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBaseFollower(engine);
        double startFt = follower.Altitude;
        Assert.True(follower.Targets.TargetAltitude < startFt, "the base leg should be descending toward the glideslope");

        VfrFollowPhase pursuit = FollowRunwaylessLeadFromBase(engine, follower);
        FollowPatternReturn patternReturn = Assert.IsType<FollowPatternReturn>(pursuit.PatternReturn);
        Assert.True(patternReturn.FromBase);
        double expectedFt = Math.Min(startFt, patternReturn.PatternAltitudeFt);

        Assert.Equal(expectedFt, follower.Targets.TargetAltitude);
        Assert.Null(follower.Targets.DesiredVerticalRate);

        TickSeconds(engine, 10);

        // Physics drops a reached altitude target once level, so the hold is read off the altitude itself.
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.True(follower.Altitude >= expectedFt - 20, $"follower descended to {follower.Altitude:F0} ft in pursuit");
    }

    [Fact]
    public void BaseFollower_AbovePatternAltitude_TargetsPatternAltitude()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBaseFollower(engine);
        follower.Altitude = 1600;

        VfrFollowPhase pursuit = FollowRunwaylessLeadFromBase(engine, follower);

        FollowPatternReturn patternReturn = Assert.IsType<FollowPatternReturn>(pursuit.PatternReturn);
        Assert.True(patternReturn.PatternAltitudeFt < 1600);
        Assert.Equal(patternReturn.PatternAltitudeFt, follower.Targets.TargetAltitude);
    }

    [Fact]
    public void UpwindFollower_ClimbsToPatternAltitude()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, -0.5, 0), rwy.TrueHeading, 500);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        PutOnCircuit(follower, rwy, PatternDirection.Right, PatternEntryLeg.Upwind);
        Assert.IsType<UpwindPhase>(follower.Phases!.CurrentPhase);
        // Runwayless lead well ahead on the upwind track (no command: no phases, no runway).
        engine.World.AddAircraft(MakeVfr(Leader, OffFinal(rwy, -3.5, 0.3), rwy.TrueHeading, 1500));

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        FollowPatternReturn patternReturn = Assert.IsType<FollowPatternReturn>(pursuit.PatternReturn);
        Assert.False(patternReturn.FromBase);
        Assert.True(patternReturn.PatternAltitudeFt > 500);
        Assert.Equal(patternReturn.PatternAltitudeFt, follower.Targets.TargetAltitude);
        Assert.Null(follower.Targets.DesiredVerticalRate);

        TickSeconds(engine, 5);

        Assert.True(follower.Altitude > 500, $"follower should keep climbing, at {follower.Altitude:F0} ft");
    }

    [Fact]
    public void PatternReturnPursuit_TooCloseWithinFiveMiles_WidensTowardThePatternSide()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        // Runwayless lead 1 nm ahead on the downwind track and slow (so the follower is at its speed floor), a
        // little farther from the runway: sitting on the runway side of its track, the follower would widen
        // toward the runway and 28L on its own. Right traffic to 28R holds it to the north (pattern) side.
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 0.0, 1.1), rwy.TrueHeading.ToReciprocal(), 1000);
        lead.IndicatedAirspeed = 40;
        engine.World.AddAircraft(lead);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TickSeconds(engine, 1);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        TrueHeading widenHeading = Assert.IsType<TrueHeading>(follower.Targets.TargetTrueHeading);
        int patternSide = AirborneFollowHelper.PatternSideWidenSide(lead.TrueTrack, rwy, PatternDirection.Right);
        Assert.Equal(-1, patternSide);
        Assert.True(
            lead.TrueTrack.SignedAngleTo(widenHeading) < -10,
            $"widen heading {widenHeading.Degrees:F1} vs lead track {lead.TrueTrack.Degrees:F1}"
        );
    }

    /// <summary>Runs the production physics + phase step at the engine's four sub-ticks a second (no scenario loaded).</summary>
    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            engine.TickPhysics(0.25);
        }
    }

    private static void AssertReenteredPattern(AircraftState follower)
    {
        Assert.NotNull(follower.Phases);
        Assert.IsNotType<VfrFollowPhase>(follower.Phases.CurrentPhase);
        Assert.NotNull(follower.Phases.CurrentPhase);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
        Assert.Equal(PatternDirection.Right, follower.Phases.TrafficDirection);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowEnds_LeadDespawns_ReentersThePattern()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBaseFollower(engine);
        FollowRunwaylessLeadFromBase(engine, follower);

        engine.World.RemoveAircraft(Leader);
        TickSeconds(engine, 2);

        output.WriteLine($"after despawn: {follower.Phases?.CurrentPhase?.GetType().Name}");
        AssertReenteredPattern(follower);
    }

    [Fact]
    public void FollowEnds_LeadLandsWithNoCapturedRunway_ReentersThePattern()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBaseFollower(engine);
        FollowRunwaylessLeadFromBase(engine, follower);

        AircraftState lead = engine.FindAircraft(Leader)!;
        lead.IsOnGround = true;
        lead.IndicatedAirspeed = 0;
        TickSeconds(engine, 2);

        output.WriteLine($"after lead landed: {follower.Phases?.CurrentPhase?.GetType().Name}");
        AssertReenteredPattern(follower);
    }

    [Fact]
    public void FollowEnds_SpacingLost_ReentersThePattern()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBaseFollower(engine);
        RunwayInfo rwy = Oak("28R");
        // Half a mile ahead on the base track and slow: at its speed floor the follower cannot open the gap, so the
        // free-flight speed loop gives up the follow ("unable to maintain separation").
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 2.5, 1.0), rwy.TrueHeading - 90.0, 1000);
        lead.IndicatedAirspeed = 40;
        engine.World.AddAircraft(lead);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);

        TickSeconds(engine, 2);

        output.WriteLine($"after spacing lost: {follower.Phases?.CurrentPhase?.GetType().Name}");
        AssertReenteredPattern(follower);
    }

    // ─── Clearance carry-over ───

    [Fact]
    public void FollowFromBase_RunwaylessLead_CarriesLandingClearance()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState follower = AddBaseFollower(engine);
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = "28R";
        double circuitAltitudeFt = ((BasePhase)follower.Phases.CurrentPhase!).Waypoints!.PatternAltitude;
        // On the base track ahead of the follower, 1 nm closer to the centerline.
        AddLead(engine, OffFinal(Oak("28R"), 2.5, 0.5), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Equal("28R", follower.Phases.ClearedRunwayId);
        Assert.NotNull(pursuit.PatternReturn);
        Assert.Equal("28R", pursuit.PatternReturn.Runway.Designator);
        Assert.Equal(PatternDirection.Right, pursuit.PatternReturn.Direction);
        Assert.Equal(circuitAltitudeFt, pursuit.PatternReturn.PatternAltitudeFt);
    }

    private static VfrFollowPhase RoundTrip(VfrFollowPhase phase)
    {
        string json = JsonSerializer.Serialize<PhaseDto>(phase.ToSnapshot(), RecordingJsonOptions.Default);
        VfrFollowPhaseDto dto = Assert.IsType<VfrFollowPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        return VfrFollowPhase.FromSnapshot(dto);
    }

    [Fact]
    public void PatternReturn_Present_SurvivesSnapshotRoundTrip()
    {
        TestVnasData.EnsureInitialized();
        var phase = new VfrFollowPhase(Leader, new FollowPatternReturn(Oak("28R"), PatternDirection.Right, 1009.5, FromBase: true));

        VfrFollowPhase restored = RoundTrip(phase);

        FollowPatternReturn patternReturn = Assert.IsType<FollowPatternReturn>(restored.PatternReturn);
        Assert.True(patternReturn.FromBase);
        Assert.Equal("28R", patternReturn.Runway.Designator);
        Assert.True(NavigationDatabase.AirportIdsMatch("KOAK", patternReturn.Runway.AirportId));
        Assert.Equal(PatternDirection.Right, patternReturn.Direction);
        Assert.Equal(1009.5, patternReturn.PatternAltitudeFt);
    }

    [Fact]
    public void PatternReturn_Absent_SurvivesSnapshotRoundTripAndOlderSnapshots()
    {
        Assert.Null(RoundTrip(new VfrFollowPhase(Leader, patternReturn: null)).PatternReturn);

        // A snapshot written before the field existed has no PatternReturn property at all.
        VfrFollowPhaseDto older = JsonSerializer.Deserialize<VfrFollowPhaseDto>(
            """{"TargetCallsign":"LEAD1","Status":1,"ElapsedSeconds":12.0,"WidenActive":false,"WidenSide":0}""",
            RecordingJsonOptions.Default
        )!;
        Assert.Null(VfrFollowPhase.FromSnapshot(older).PatternReturn);
    }

    [Fact]
    public void PatternReturn_WithoutFromBase_RestoresAsNotFromBase()
    {
        TestVnasData.EnsureInitialized();
        // A pattern return written before FromBase existed.
        var dto = new VfrFollowPhaseDto
        {
            Status = 1,
            ElapsedSeconds = 3.0,
            TargetCallsign = Leader,
            PatternReturn = new FollowPatternReturnDto
            {
                Runway = Oak("28R").ToSnapshot(),
                Direction = (int)PatternDirection.Right,
                PatternAltitudeFt = 1009,
            },
        };

        FollowPatternReturn patternReturn = Assert.IsType<FollowPatternReturn>(VfrFollowPhase.FromSnapshot(dto).PatternReturn);

        Assert.False(patternReturn.FromBase);
    }

    // ─── Widen held to the pattern side ───

    [Theory]
    [InlineData(PatternDirection.Right, "28R", false, 1)]
    [InlineData(PatternDirection.Right, "28R", true, -1)]
    [InlineData(PatternDirection.Left, "28L", false, -1)]
    [InlineData(PatternDirection.Left, "28L", true, 1)]
    public void PatternSideWidenSide_WidensTowardThePatternSideAwayFromTheParallel(
        PatternDirection direction,
        string designator,
        bool leadFlyingDownwind,
        int expectedSide
    )
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak(designator);
        TrueHeading leadTrack = leadFlyingDownwind ? rwy.TrueHeading.ToReciprocal() : rwy.TrueHeading;

        Assert.Equal(expectedSide, AirborneFollowHelper.PatternSideWidenSide(leadTrack, rwy, direction));
    }

    [Fact]
    public void PatternSideWidenClamp_AppliesOnlyWithinFiveMilesOfTheReturnThreshold()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        var patternReturn = new FollowPatternReturn(rwy, PatternDirection.Right, 1009, FromBase: false);

        Assert.Equal(1, VfrFollowPhase.PatternSideWidenClamp(OffFinal(rwy, 4.5, 0), rwy.TrueHeading, patternReturn));
        Assert.Null(VfrFollowPhase.PatternSideWidenClamp(OffFinal(rwy, 6.0, 0), rwy.TrueHeading, patternReturn));
        Assert.Null(VfrFollowPhase.PatternSideWidenClamp(OffFinal(rwy, 4.5, 0), rwy.TrueHeading, patternReturn: null));
    }

    [Fact]
    public void FreePursuitWiden_PreferredSide_OverridesTheFollowerOffsetSide()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 3.0, 0), rwy.TrueHeading, 1500);
        // Too close behind the lead and just left of its track: left on its own the widen would go left (toward 28L).
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 3.5, -0.1), rwy.TrueHeading, 1500);
        double desiredNm = AirborneFollowHelper.FreeFlightDistanceForLeader(AircraftCategory.Piston);

        TrueHeading unclamped = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, desiredNm, true, new FollowWidenState());
        TrueHeading clamped = AirborneFollowHelper.ComputeFreePursuitHeading(
            follower,
            lead,
            desiredNm,
            true,
            new FollowWidenState { PreferredSide = 1 }
        );

        Assert.True(rwy.TrueHeading.SignedAngleTo(unclamped) < 0, $"unclamped widen heading {unclamped.Degrees:F1}");
        Assert.True(rwy.TrueHeading.SignedAngleTo(clamped) > 0, $"clamped widen heading {clamped.Degrees:F1}");
    }

    // ─── QueuedPatternEntry ───

    [Fact]
    public void QueuedPatternEntry_UnfiredEntry_ReturnsRunwayAndDirection()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState lead = AddLead(engine, OffFinal(Oak("28R"), 6.0, 3.0), "DCT VPCBT; ERB 28R");

        Assert.Equal(("28R", PatternDirection.Right), PatternCommandHandler.QueuedPatternEntry(lead));
    }

    [Fact]
    public void QueuedPatternEntry_UnfiredFinalEntry_HasNoDirection()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState lead = AddLead(engine, OffFinal(Oak("28R"), 6.0, 3.0), "DCT VPCBT; EF 28R");

        Assert.Equal(("28R", (PatternDirection?)null), PatternCommandHandler.QueuedPatternEntry(lead));
    }

    [Fact]
    public void QueuedPatternEntry_BareEntry_ReturnsNull()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState lead = AddLead(engine, OffFinal(Oak("28R"), 6.0, 3.0), "DCT VPCBT; ERB");

        Assert.True(PatternCommandHandler.HasQueuedPatternEntry(lead));
        Assert.Null(PatternCommandHandler.QueuedPatternEntry(lead));
    }

    [Fact]
    public void QueuedPatternEntry_AppliedEntry_ReturnsNull()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState ac = MakeVfr(Leader, OffFinal(Oak("28R"), 4.0, 2.0), new TrueHeading(180), 1500);
        engine.World.AddAircraft(ac);
        CommandResult setup = engine.SendCommand(Leader, "ERB 28R");
        Assert.True(setup.Success, setup.Message);
        Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);

        Assert.Null(PatternCommandHandler.QueuedPatternEntry(ac));
    }

    [Fact]
    public void QueuedPatternEntry_RestoredFromSnapshot_ReparsesEntry()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState lead = AddLead(engine, OffFinal(Oak("28R"), 6.0, 3.0), "DCT VPCBT; ELB 28L");

        var restored = AircraftState.FromSnapshot(lead.ToSnapshot(), null);
        Assert.All(restored.Queue.Blocks, b => Assert.Null(b.ParsedCommands));

        Assert.Equal(("28L", PatternDirection.Left), PatternCommandHandler.QueuedPatternEntry(restored));
    }
}
