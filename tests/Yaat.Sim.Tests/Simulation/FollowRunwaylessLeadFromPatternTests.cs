using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
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

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

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
            SimulationEngine? engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
    }

    // ─── Clearance carry-over ───

    [Fact]
    public void FollowFromBase_RunwaylessLead_CarriesLandingClearance()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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

    // ─── QueuedPatternEntry ───

    [Fact]
    public void QueuedPatternEntry_UnfiredEntry_ReturnsRunwayAndDirection()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        AircraftState lead = AddLead(engine, OffFinal(Oak("28R"), 6.0, 3.0), "DCT VPCBT; ERB 28R");

        Assert.Equal(("28R", PatternDirection.Right), PatternCommandHandler.QueuedPatternEntry(lead));
    }

    [Fact]
    public void QueuedPatternEntry_UnfiredFinalEntry_HasNoDirection()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        AircraftState lead = AddLead(engine, OffFinal(Oak("28R"), 6.0, 3.0), "DCT VPCBT; EF 28R");

        Assert.Equal(("28R", (PatternDirection?)null), PatternCommandHandler.QueuedPatternEntry(lead));
    }

    [Fact]
    public void QueuedPatternEntry_BareEntry_ReturnsNull()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        AircraftState lead = AddLead(engine, OffFinal(Oak("28R"), 6.0, 3.0), "DCT VPCBT; ERB");

        Assert.True(PatternCommandHandler.HasQueuedPatternEntry(lead));
        Assert.Null(PatternCommandHandler.QueuedPatternEntry(lead));
    }

    [Fact]
    public void QueuedPatternEntry_AppliedEntry_ReturnsNull()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        AircraftState lead = AddLead(engine, OffFinal(Oak("28R"), 6.0, 3.0), "DCT VPCBT; ELB 28L");

        var restored = AircraftState.FromSnapshot(lead.ToSnapshot(), null);
        Assert.All(restored.Queue.Blocks, b => Assert.Null(b.ParsedCommands));

        Assert.Equal(("28L", PatternDirection.Left), PatternCommandHandler.QueuedPatternEntry(restored));
    }
}
