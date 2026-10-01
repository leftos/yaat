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
/// - otherwise (no queued entry, or one for the follower's runway): free pursuit with a pattern return, except from
///   final (refused) and from any other pattern leg — entry, upwind, crosswind, downwind, base — when the lead is not
///   ahead within ±60° of the follower's track, or — from upwind and crosswind, whose next turn is onto the downwind —
///   inside the downwind box (the corridor along the downwind line from the downwind turn point to 3 nm past the base
///   turn, between half and the full downwind offset plus 1.5 nm of the extended runway centerline, tracking within ±90°
///   of the downwind heading), where the follower joins behind it by flying its own circuit;
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

    /// <summary>
    /// An aircraft on the 28R right circuit from <paramref name="leg"/>, then placed where <paramref name="place"/> puts
    /// it on that circuit's own geometry (the dispatcher reads position and track only, so no tick runs between).
    /// </summary>
    private static AircraftState AddOnCircuit(
        SimulationEngine engine,
        RunwayInfo rwy,
        PatternEntryLeg leg,
        Func<PatternWaypoints, (LatLon, TrueHeading)> place
    )
    {
        AircraftState ac = MakeVfr(Follower, OffFinal(rwy, 1.0, 1.0), rwy.TrueHeading, 1000);
        ac.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(ac);
        PutOnCircuit(ac, rwy, PatternDirection.Right, leg);
        PatternWaypoints wp = WaypointsOf(ac, leg);
        (LatLon position, TrueHeading heading) = place(wp);
        ac.Position = position;
        ac.TrueHeading = heading;
        ac.TrueTrack = heading;
        return ac;
    }

    private static PatternWaypoints WaypointsOf(AircraftState ac, PatternEntryLeg leg) =>
        AirborneFollowHelper.PatternWaypointsOf(ac.Phases!.CurrentPhase!) ?? throw new InvalidOperationException($"{leg} carries no waypoints");

    /// <summary>On the upwind, <paramref name="shortNm"/> before the crosswind-turn point (past it when negative).</summary>
    private static (LatLon, TrueHeading) UpwindShortOfCrosswindTurn(PatternWaypoints wp, double shortNm) =>
        (GeoMath.ProjectPoint(new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon), wp.UpwindHeading.ToReciprocal(), shortNm), wp.UpwindHeading);

    /// <summary>On the crosswind, <paramref name="outNm"/> out from the crosswind-turn point.</summary>
    private static (LatLon, TrueHeading) OutOnCrosswind(PatternWaypoints wp, double outNm) =>
        (GeoMath.ProjectPoint(new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon), wp.CrosswindHeading, outNm), wp.CrosswindHeading);

    /// <summary>Directly behind <paramref name="follower"/> on its own track, <paramref name="nm"/> back.</summary>
    private static LatLon Astern(AircraftState follower, double nm) => GeoMath.ProjectPoint(follower.Position, follower.TrueTrack.ToReciprocal(), nm);

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

    // ─── The ±60° cone on every pattern leg ───

    /// <summary>A position <paramref name="offTrackDeg"/> off <paramref name="follower"/>'s track at <paramref name="nm"/> nm.</summary>
    private static LatLon OffTrack(AircraftState follower, double offTrackDeg, double nm) =>
        GeoMath.ProjectPoint(follower.Position, follower.TrueTrack + offTrackDeg, nm);

    /// <summary>Gives <paramref name="lead"/> a ground track, as the box's third condition reads it.</summary>
    private static AircraftState WithTrack(AircraftState lead, TrueHeading track)
    {
        lead.TrueHeading = track;
        lead.TrueTrack = track;
        return lead;
    }

    /// <summary>A circuit point's along-track distance from the threshold, in the frame <see cref="OffFinal"/> uses.</summary>
    private static double AlongOf(RunwayInfo rwy, double lat, double lon) =>
        GeoMath.AlongTrackDistanceNm(new LatLon(lat, lon), new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal());

    /// <summary>The downwind turn point's along-track distance from the threshold: the crosswind turn, offset laterally.</summary>
    private static double DownwindTurnAlong(RunwayInfo rwy, PatternWaypoints wp) => AlongOf(rwy, wp.CrosswindTurnLat, wp.CrosswindTurnLon);

    /// <summary>The downwind heading's distance from the downwind turn point to the base turn, in nm.</summary>
    private static double BaseTurnS(RunwayInfo rwy, PatternWaypoints wp) => AlongOf(rwy, wp.BaseTurnLat, wp.BaseTurnLon) - DownwindTurnAlong(rwy, wp);

    /// <summary>The box's coordinates for <paramref name="lead"/>: s (along the downwind heading from the downwind turn
    /// point, positive toward the base turn), d (from the extended runway centerline, positive toward the circuit side)
    /// and how far the lead's track lies off the downwind heading.</summary>
    private static (double S, double D, double TrackOff) BoxCoordinates(RunwayInfo rwy, PatternWaypoints wp, AircraftState lead)
    {
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        var downwindTurn = new LatLon(wp.DownwindStartLat, wp.DownwindStartLon);
        double d = GeoMath.SignedCrossTrackDistanceNm(lead.Position, threshold, rwy.TrueHeading);
        return (
            GeoMath.AlongTrackDistanceNm(lead.Position, downwindTurn, wp.DownwindHeading),
            wp.Direction == PatternDirection.Left ? -d : d,
            wp.DownwindHeading.AbsAngleTo(lead.TrueTrack)
        );
    }

    /// <summary>Prints the lead's box coordinates and its angle off the follower's track.</summary>
    private void LogBoxCoordinates(RunwayInfo rwy, AircraftState follower, PatternWaypoints wp, AircraftState lead)
    {
        var bearing = new TrueHeading(GeoMath.BearingTo(follower.Position, lead.Position));
        (double s, double d, double trackOff) = BoxCoordinates(rwy, wp, lead);
        output.WriteLine(
            $"lead: s {s:F2} nm, d {d:F2} nm, track {trackOff:F1}° off the downwind heading, {follower.TrueTrack.AbsAngleTo(bearing):F1}° off the follower's track"
        );
    }

    /// <summary>
    /// Asserts the FOLLOW installed a pursuit on the strength of the downwind box: the lead is more than 60° off the
    /// follower's track (so the track cone alone would refuse it) yet inside the box, where a follower on its own circuit
    /// joins behind it. Prints the lead's s, d and track offset.
    /// </summary>
    private void AssertAcceptedByTheDownwindBox(
        SimulationEngine engine,
        RunwayInfo rwy,
        AircraftState follower,
        PatternWaypoints wp,
        CommandResult result
    )
    {
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");
        AircraftState lead = engine.FindAircraft(Leader) ?? throw new InvalidOperationException("lead missing");
        var bearing = new TrueHeading(GeoMath.BearingTo(follower.Position, lead.Position));
        double offTrack = follower.TrueTrack.AbsAngleTo(bearing);
        LogBoxCoordinates(rwy, follower, wp, lead);
        Assert.True(offTrack > 60.0, $"the track cone alone would accept a lead only {offTrack:F1}° off track");
        (double s, double d, double trackOff) = BoxCoordinates(rwy, wp, lead);
        Assert.True(s > 0.0, $"s {s:F2} nm, behind the downwind turn point");
        Assert.True(d >= (0.5 * wp.PatternSizeNm), $"d {d:F2} nm, inside the box's inner edge");
        Assert.True(trackOff <= 90.0, $"track {trackOff:F1}° off the downwind heading");
        Assert.True(result.Success, result.Message);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, pursuit.TargetCallsign);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    /// <summary>The cone refusal text for <paramref name="position"/>: the format the base leg already used.</summary>
    private static string NotAhead(string position) => $"Unable, on {position} for runway 28R, {Leader} is not ahead of us, request vectors";

    private void AssertRefusedUnchanged(AircraftState follower, Phase? before, CommandResult result, string position)
    {
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");
        Assert.False(result.Success, result.Message);
        Assert.Equal(NotAhead(position), result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowFromDownwind_RunwaylessLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        Phase? before = follower.Phases!.CurrentPhase;
        // Astern on the downwind line, beyond the departure end: following it takes a 360.
        AddLead(engine, OffFinal(rwy, -2.5, 1.0), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "downwind");
    }

    [Fact]
    public void FollowFromDownwind_RunwaylessLeadAbeam_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        Phase? before = follower.Phases!.CurrentPhase;
        // Abreast of the follower, 2.5 nm farther from the runway: 90° off its track.
        AddLead(engine, OffFinal(rwy, -1.0, 3.5), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "downwind");
    }

    [Fact]
    public void FollowFromUpwind_RunwaylessLeadDirectlyAstern_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddOnCircuit(engine, rwy, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 0.5));
        Assert.IsType<UpwindPhase>(follower.Phases!.CurrentPhase);
        Phase? before = follower.Phases.CurrentPhase;
        PatternWaypoints wp = WaypointsOf(follower, PatternEntryLeg.Upwind);
        // Directly astern on the runway line: nearer the centerline than the downwind box's inner edge, so it is traffic
        // between the follower and the runway rather than a lead on the downwind line.
        AircraftState lead = AddLead(engine, Astern(follower, 1.5), "DCT VPCBT");
        LogBoxCoordinates(rwy, follower, wp, lead);
        (_, double d, _) = BoxCoordinates(rwy, wp, lead);
        Assert.True(d < (0.5 * wp.PatternSizeNm), $"d {d:F2} nm is not inside the box's inner edge");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "upwind");
    }

    [Fact]
    public void FollowFromCrosswind_RunwaylessLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddOnCircuit(engine, rwy, PatternEntryLeg.Crosswind, wp => OutOnCrosswind(wp, 0.4));
        Assert.IsType<CrosswindPhase>(follower.Phases!.CurrentPhase);
        Phase? before = follower.Phases.CurrentPhase;
        AddLead(engine, Astern(follower, 1.0), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "crosswind");
    }

    [Fact]
    public void FollowFromPatternEntry_RunwaylessLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, -2.0, 4.0), new TrueHeading(180), 1500);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        CommandResult entry = engine.SendCommand(Follower, "ERD 28R");
        Assert.True(entry.Success, entry.Message);
        Assert.IsType<PatternEntryPhase>(follower.Phases!.CurrentPhase);
        Phase? before = follower.Phases.CurrentPhase;
        AddLead(engine, Astern(follower, 1.0), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "pattern entry");
    }

    [Fact]
    public void FollowForceFromDownwind_RunwaylessLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        Phase? before = follower.Phases!.CurrentPhase;
        AddLead(engine, OffFinal(rwy, -2.5, 1.0), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOWF {Leader}");

        AssertRefusedUnchanged(follower, before, result, "downwind");
    }

    /// <summary>Just inside the cone, to the runway side of the downwind track: still followed.</summary>
    [Fact]
    public void FollowFromDownwind_RunwaylessLeadJustInsideTheCone_InstallsPursuit()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        AddLead(engine, OffTrack(follower, 55.0, 1.5), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, pursuit.TargetCallsign);
        Assert.NotNull(pursuit.PatternReturn);
        Assert.Equal("28R", pursuit.PatternReturn.Runway.Designator);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    /// <summary>Just outside the cone on the same side as the accepted pair's lead: refused, at 65° off the track.</summary>
    [Fact]
    public void FollowFromDownwind_RunwaylessLeadJustOutsideTheCone_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        Phase? before = follower.Phases!.CurrentPhase;
        AddLead(engine, OffTrack(follower, 65.0, 1.5), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "downwind");
    }

    // ─── The downwind box from upwind and crosswind ───

    /// <summary>
    /// An upwind follower's next turn is onto the downwind, so a lead abeam the downwind turn point on the downwind
    /// line, tracking the downwind heading, is one it falls in behind by flying its own circuit.
    /// </summary>
    [Fact]
    public void FollowFromUpwind_RunwaylessLeadOnDownwindLineAbeamTurnPoint_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddOnCircuit(engine, rwy, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 0.5));
        Assert.IsType<UpwindPhase>(follower.Phases!.CurrentPhase);
        PatternWaypoints wp = WaypointsOf(follower, PatternEntryLeg.Upwind);
        WithTrack(AddLead(engine, OffFinal(rwy, DownwindTurnAlong(rwy, wp) + 0.1, 1.0), "DCT VPCBT"), wp.DownwindHeading);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertAcceptedByTheDownwindBox(engine, rwy, follower, wp, result);
    }

    /// <summary>A lead abeam midfield on the downwind line, tracking the downwind heading: inside the box.</summary>
    [Fact]
    public void FollowFromUpwind_RunwaylessLeadAbeamMidfield_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddOnCircuit(engine, rwy, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 0.2));
        Assert.IsType<UpwindPhase>(follower.Phases!.CurrentPhase);
        PatternWaypoints wp = WaypointsOf(follower, PatternEntryLeg.Upwind);
        WithTrack(AddLead(engine, OffFinal(rwy, PatternGeometry.MidfieldAlongTrackNm(wp), 1.0), "DCT VPCBT"), wp.DownwindHeading);
        output.WriteLine($"midfield along {PatternGeometry.MidfieldAlongTrackNm(wp):F2} nm; base turn s {BaseTurnS(rwy, wp):F2} nm");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertAcceptedByTheDownwindBox(engine, rwy, follower, wp, result);
    }

    /// <summary>The abeam-midfield position, but tracking the reciprocal: not flying the circuit, so outside the box.</summary>
    [Fact]
    public void FollowFromUpwind_RunwaylessLeadOnDownwindLineFlyingOpposite_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddOnCircuit(engine, rwy, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 0.2));
        Assert.IsType<UpwindPhase>(follower.Phases!.CurrentPhase);
        Phase? before = follower.Phases.CurrentPhase;
        PatternWaypoints wp = WaypointsOf(follower, PatternEntryLeg.Upwind);
        AircraftState lead = WithTrack(
            AddLead(engine, OffFinal(rwy, PatternGeometry.MidfieldAlongTrackNm(wp), 1.0), "DCT VPCBT"),
            wp.DownwindHeading.ToReciprocal()
        );
        LogBoxCoordinates(rwy, follower, wp, lead);
        (_, _, double trackOff) = BoxCoordinates(rwy, wp, lead);
        Assert.True(trackOff > 90.0, $"track {trackOff:F1}° off the downwind heading flies the circuit");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "upwind");
    }

    /// <summary>On the downwind line, but on the upwind side of the downwind turn point: behind the box's start.</summary>
    [Fact]
    public void FollowFromUpwind_RunwaylessLeadBeforeTheDownwindTurn_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddOnCircuit(engine, rwy, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 0.1));
        Assert.IsType<UpwindPhase>(follower.Phases!.CurrentPhase);
        Phase? before = follower.Phases.CurrentPhase;
        PatternWaypoints wp = WaypointsOf(follower, PatternEntryLeg.Upwind);
        AircraftState lead = WithTrack(AddLead(engine, OffFinal(rwy, DownwindTurnAlong(rwy, wp) - 0.25, 1.0), "DCT VPCBT"), wp.DownwindHeading);
        LogBoxCoordinates(rwy, follower, wp, lead);
        (double s, _, _) = BoxCoordinates(rwy, wp, lead);
        Assert.True(s < 0.0, $"s {s:F2} nm is past the downwind turn point");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "upwind");
    }

    /// <summary>From the crosswind, a lead 2 nm down the downwind line from the crosswind-turn point.</summary>
    [Fact]
    public void FollowFromCrosswind_RunwaylessLeadDownTheDownwindLine_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddOnCircuit(engine, rwy, PatternEntryLeg.Crosswind, wp => OutOnCrosswind(wp, 0.4));
        Assert.IsType<CrosswindPhase>(follower.Phases!.CurrentPhase);
        PatternWaypoints wp = WaypointsOf(follower, PatternEntryLeg.Crosswind);
        WithTrack(AddLead(engine, OffFinal(rwy, DownwindTurnAlong(rwy, wp) + 2.0, 1.0), "DCT VPCBT"), wp.DownwindHeading);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertAcceptedByTheDownwindBox(engine, rwy, follower, wp, result);
    }

    /// <summary>On the downwind line, but more than 3 nm beyond the base turn: past the box's far end.</summary>
    [Fact]
    public void FollowFromCrosswind_RunwaylessLeadBeyondExtendedDownwind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddOnCircuit(engine, rwy, PatternEntryLeg.Crosswind, wp => OutOnCrosswind(wp, 0.4));
        Assert.IsType<CrosswindPhase>(follower.Phases!.CurrentPhase);
        Phase? before = follower.Phases.CurrentPhase;
        PatternWaypoints wp = WaypointsOf(follower, PatternEntryLeg.Crosswind);
        AircraftState lead = WithTrack(
            AddLead(engine, OffFinal(rwy, DownwindTurnAlong(rwy, wp) + BaseTurnS(rwy, wp) + 3.5, 1.0), "DCT VPCBT"),
            wp.DownwindHeading
        );
        LogBoxCoordinates(rwy, follower, wp, lead);
        (double s, _, _) = BoxCoordinates(rwy, wp, lead);
        Assert.True(s > (BaseTurnS(rwy, wp) + 3.0), $"s {s:F2} nm is inside the box's far end");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "crosswind");
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

    // ─── FOLLOWF: FOLLOW with only the traffic-in-sight requirement bypassed ───

    [Fact]
    public void FollowForceFromBase_RunwaylessLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState follower = AddBaseFollower(engine);
        Phase? before = follower.Phases!.CurrentPhase;
        // Up the base leg behind the follower (farther from the centerline than it is).
        AddLead(engine, OffFinal(Oak("28R"), 2.5, 3.0), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOWF {Leader}");
        output.WriteLine($"FOLLOWF: success={result.Success} — {result.Message}");

        Assert.False(result.Success, result.Message);
        Assert.Contains("on base for runway 28R", result.Message);
        Assert.Contains($"{Leader} is not ahead", result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowForceFromFinal_KeepsPhaseAndLandingClearance()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 3.0, 0), rwy.TrueHeading, 900);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        PutOnCircuit(follower, rwy, PatternDirection.Right, PatternEntryLeg.Final);
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = "28R";
        Phase? before = follower.Phases.CurrentPhase;
        Assert.IsType<FinalApproachPhase>(before);

        // In-trail on the same runway: a follow the final can keep flying. The lead sits on the
        // 3° path for 1 nm (≈330 ft), not level with the follower.
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 1.0, 0), rwy.TrueHeading, 330);
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternDirection.Right, PatternEntryLeg.Final);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOWF {Leader}");
        output.WriteLine($"FOLLOWF: success={result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Equal("28R", follower.Phases.ClearedRunwayId);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowForceFromDownwind_LeadOnGround_IsRefused()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        Phase? before = follower.Phases!.CurrentPhase;

        AircraftState lead = MakeVfr(Leader, new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading, rwy.ElevationFt);
        lead.IsOnGround = true;
        lead.IndicatedAirspeed = 0;
        engine.World.AddAircraft(lead);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOWF {Leader}");
        output.WriteLine($"FOLLOWF: success={result.Success} — {result.Message}");

        Assert.False(result.Success, result.Message);
        Assert.Equal($"Unable, {Leader} is on the ground", result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowForce_NoTrafficInSight_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();

        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddDownwindFollower(engine, rwy);
        follower.Approach.HasReportedTrafficInSight = false;
        Phase? before = follower.Phases!.CurrentPhase;

        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 2.0, 1.0), rwy.TrueHeading - 90.0, 1000);
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternDirection.Right, PatternEntryLeg.Base);

        CommandResult plain = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={plain.Success} — {plain.Message}");
        Assert.False(plain.Success, plain.Message);
        Assert.Contains("Traffic not in sight", plain.Message);
        Assert.Null(follower.Approach.FollowingCallsign);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOWF {Leader}");
        output.WriteLine($"FOLLOWF: success={result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.True(follower.Approach.HasReportedTrafficInSight);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
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
        // Ahead on the downwind track, inside the ±60° cone (a lead behind it is refused).
        AddLead(engine, OffFinal(rwy, 1.5, 1.0), "DCT VPCBT");

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
    public void FollowFromIntercept_RunwaylessLeadInCone_KeepsApproachAndClearance()
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
        Phase? intercept = follower.Phases.CurrentPhase;
        AddLead(engine, OffFinal(rwy, 4.5, 0.5), "DCT VPCBT");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Same(intercept, follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Equal("28R", follower.Phases.ClearedRunwayId);
        Assert.DoesNotContain(follower.PendingWarnings, w => w.Contains("cancelled by FOLLOW", StringComparison.Ordinal));
    }

    // ─── From an approach (intercept) ───

    [Fact]
    public void FollowFromIntercept_LeadInRunwayPattern_KeepsApproachAndClearance()
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
        Assert.IsType<InterceptCoursePhase>(follower.Phases!.CurrentPhase);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Equal("28R", follower.Phases.ClearedRunwayId);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal(1.0, follower.Targets.TurnRateOverride);
        Assert.True(follower.Targets.HasExplicitTurnRate);
        Assert.DoesNotContain(follower.PendingWarnings, w => w.Contains("cancelled by FOLLOW", StringComparison.Ordinal));
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

    // ─── Approach and pursuit followers ───

    /// <summary>A runwayless VFR lead with no phase at all, so no assigned runway.</summary>
    private static AircraftState AddRunwaylessLead(SimulationEngine engine, LatLon position, TrueHeading heading)
    {
        AircraftState lead = MakeVfr(Leader, position, heading, 1000);
        engine.World.AddAircraft(lead);
        Assert.Null(lead.Phases?.AssignedRunway);
        return lead;
    }

    /// <summary>A follower pursuing <paramref name="target"/>, returning to the 28R right-traffic circuit.</summary>
    private static AircraftState AddPursuitFollower(SimulationEngine engine, string target)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 4.0, 1.0), rwy.TrueHeading, 1000);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        CommandDispatcher.InstallVfrFollowPhase(
            follower,
            target,
            new FollowPatternReturn(rwy, PatternDirection.Right, rwy.ElevationFt + 1000, false),
            climbOutGate: null
        );
        return follower;
    }

    [Fact]
    public void ApproachFollower_RunwaylessLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 2.5);
        Phase? before = follower.Phases!.CurrentPhase;
        AddRunwaylessLead(engine, Astern(follower, 3.0), follower.TrueHeading.ToReciprocal());

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "approach");
    }

    [Fact]
    public void ApproachNavigationFollower_RunwaylessLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachNavigationFollower(engine);
        Phase? before = follower.Phases!.CurrentPhase;
        AddRunwaylessLead(engine, Astern(follower, 3.0), follower.TrueHeading.ToReciprocal());

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "approach");
    }

    [Fact]
    public void ApproachFollower_RunwaylessLeadAhead_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 2.5);
        Phase? intercept = follower.Phases!.CurrentPhase;
        AddRunwaylessLead(engine, OffTrack(follower, 0.0, 2.0), follower.TrueHeading);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.Same(intercept, follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
    }

    /// <summary>
    /// An approach follower flying away from the field on its approach's outbound leg (an approach-navigation follower) has
    /// traffic ahead in sequence behind its track. A runwayless lead 3 nm out on the final, closer to the threshold in a
    /// straight line than the follower's remaining path (about 14 nm through the outbound fix and the FAF), is ahead of it,
    /// accepted, and the approach is kept.
    /// </summary>
    [Fact]
    public void ApproachFollower_ProcedureTurnOutbound_RunwaylessLeadCloserToThreshold_Accepted()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 6.0, 1.0), rwy.TrueHeading.ToReciprocal(), 2000);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        LatLon outbound = OffFinal(rwy, 10.0, 1.0);
        LatLon faf = OffFinal(rwy, 5.0, 0.0);
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = "28R",
        };
        follower.Phases.Add(
            new ApproachNavigationPhase { Fixes = [new ApproachFix("OUTBD", outbound.Lat, outbound.Lon), new ApproachFix("FAF", faf.Lat, faf.Lon)] }
        );
        follower.Phases.Add(new FinalApproachPhase());
        follower.Phases.Add(new LandingPhase());
        AircraftState lead = AddRunwaylessLead(engine, OffFinal(rwy, 3.0, 0.0), rwy.TrueHeading);
        Assert.False(AirborneFollowHelper.IsLeadAheadOfTrack(follower, lead), "the lead must be outside the follower's ±60° cone");
        Phase? navigation = follower.Phases.CurrentPhase;

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.Same(navigation, follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    /// <summary>An approach follower with no assigned runway has no approach to name: the cone refusal uses the pursuit wording.</summary>
    [Fact]
    public void ApproachFollower_NoAssignedRunway_RunwaylessLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 2.5);
        follower.Phases!.AssignedRunway = null;
        Phase? before = follower.Phases.CurrentPhase;
        AddRunwaylessLead(engine, Astern(follower, 3.0), follower.TrueHeading.ToReciprocal());

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");
        Assert.False(result.Success, result.Message);
        Assert.Equal($"Unable, {Leader} is not ahead of us, request vectors", result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void PursuitFollower_NewRunwaylessLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddPursuitFollower(engine, "OTHER1");
        Phase? before = follower.Phases!.CurrentPhase;
        AddRunwaylessLead(engine, Astern(follower, 3.0), follower.TrueHeading.ToReciprocal());

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.False(result.Success, result.Message);
        Assert.Equal($"Unable, {Leader} is not ahead of us, request vectors", result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Equal("OTHER1", follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void PursuitFollower_NewRunwaylessLeadAhead_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddPursuitFollower(engine, "OTHER1");
        VfrFollowPhase before = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        AddRunwaylessLead(engine, OffTrack(follower, 0.0, 3.0), follower.TrueHeading);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");
        output.WriteLine($"FOLLOW: success={result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, before.TargetCallsign);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
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
        int patternSide = AirborneFollowHelper.PatternOutsideWidenSide(follower.Position, lead.TrueTrack, rwy, PatternDirection.Right);
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

    // ─── Spacing excursion to the pattern's outside ───

    [Theory]
    [InlineData(PatternDirection.Right, "28R", false, 1)]
    [InlineData(PatternDirection.Right, "28R", true, -1)]
    [InlineData(PatternDirection.Left, "28L", false, -1)]
    [InlineData(PatternDirection.Left, "28L", true, 1)]
    public void PatternOutsideWidenSide_OnTheExtendedCenterline_WidensTowardThePatternSide(
        PatternDirection direction,
        string designator,
        bool leadFlyingDownwind,
        int expectedSide
    )
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak(designator);
        TrueHeading leadTrack = leadFlyingDownwind ? rwy.TrueHeading.ToReciprocal() : rwy.TrueHeading;

        Assert.Equal(expectedSide, AirborneFollowHelper.PatternOutsideWidenSide(OffFinal(rwy, 3.0, 0), leadTrack, rwy, direction));
    }

    /// <summary>
    /// Within 5 nm of the runway, on the pattern side, whatever track the lead flies, the excursion never leans toward the
    /// centerline — so never toward the final or the parallel's final beyond it — and, flying across the final's direction,
    /// turns away from the runway, for right and left traffic.
    /// </summary>
    [Theory]
    [InlineData(PatternDirection.Right, "28R")]
    [InlineData(PatternDirection.Left, "28L")]
    public void PatternOutsideWidenSide_WithinFiveMiles_NeverTowardTheRunwayOrTheFinal(PatternDirection direction, string designator)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak(designator);
        double sign = direction == PatternDirection.Right ? 1.0 : -1.0;
        TrueHeading patternSide = rwy.TrueHeading + (90.0 * sign);
        LatLon[] positions =
        [
            OffFinal(rwy, 4.5, 0.8 * sign),
            OffFinal(rwy, 3.0, 1.5 * sign),
            OffFinal(rwy, 1.0, 1.0 * sign),
            OffFinal(rwy, 0.5, 1.2 * sign),
        ];
        foreach (LatLon position in positions)
        {
            Assert.True(GeoMath.DistanceNm(position, new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude)) <= 5.0);
            for (int trackDeg = 0; trackDeg < 360; trackDeg += 15)
            {
                var track = new TrueHeading(trackDeg);
                int side = AirborneFollowHelper.PatternOutsideWidenSide(position, track, rwy, direction);
                TrueHeading excursionNormal = track + (90.0 * side);
                double towardPatternSide = Math.Cos(excursionNormal.AbsAngleTo(patternSide) * Math.PI / 180.0);
                Assert.True(
                    towardPatternSide >= -AirborneFollowHelper.OutsideSideTieCosine,
                    $"track {trackDeg}: excursion toward {excursionNormal.Degrees:F0} leans to the centerline"
                );
                double fromRunwayDeg = GeoMath.BearingTo(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), position);
                Assert.True(
                    (excursionNormal.AbsAngleTo(new TrueHeading(fromRunwayDeg)) <= 90.0)
                        || (towardPatternSide > AirborneFollowHelper.OutsideSideTieCosine),
                    $"track {trackDeg}: excursion toward {excursionNormal.Degrees:F0} turns toward the runway"
                );
            }
        }
    }

    /// <summary>A slow follower's excursion limits: a 1 nm offset cap, turning up to 45° off the lead's track.</summary>
    private static FollowExcursionLimits PistonLimits => new(1.0, AirborneFollowHelper.TrailWideExcursionDeg);

    [Fact]
    public void FreePursuitExcursion_KnownCircuit_TakesThePatternOutsideOverTheFollowerOffsetSide()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 3.0, 0.5), rwy.TrueHeading, 1500);
        // On the pattern side, too close behind the lead and just left of its track: left on its own the excursion would go
        // left, toward the final.
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 3.5, 0.4), rwy.TrueHeading, 1500);
        double desiredNm = AirborneFollowHelper.DesiredDistanceForLeader(AircraftCategory.Piston);

        TrueHeading free = AirborneFollowHelper.ComputeFreePursuitHeading(
            follower,
            lead,
            new FreePursuitSpacing(desiredNm, PistonLimits, null, null, null),
            new LeadPathTrail(),
            new FollowWidenState()
        );
        TrueHeading outside = AirborneFollowHelper.ComputeFreePursuitHeading(
            follower,
            lead,
            new FreePursuitSpacing(desiredNm, PistonLimits, new FollowCircuit(rwy, PatternDirection.Right), null, null),
            new LeadPathTrail(),
            new FollowWidenState()
        );

        Assert.True(rwy.TrueHeading.SignedAngleTo(free) < 0, $"offset-side excursion heading {free.Degrees:F1}");
        Assert.True(rwy.TrueHeading.SignedAngleTo(outside) > 0, $"pattern-outside excursion heading {outside.Degrees:F1}");
    }

    // ─── Joining the lead's base ───

    /// <summary>The lead's base start for the join tests: a wide right base to 28R, 4 nm out and 2.5 nm right of the centerline.</summary>
    private static LatLon LeadBaseStart => OffFinal(Oak("28R"), 4.0, 2.5);

    /// <summary>Lead on the right base to 28R, its base started at <see cref="LeadBaseStart"/>.</summary>
    private static AircraftState AddBaseLead(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, LeadBaseStart, rwy.TrueHeading - 90.0, 1000);
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternDirection.Right, PatternEntryLeg.Base);
        BasePhase leadBase = Assert.IsType<BasePhase>(lead.Phases!.CurrentPhase);
        Assert.Equal(LeadBaseStart, leadBase.StartPoint);
        return lead;
    }

    /// <summary>A follower already in free pursuit of <see cref="Leader"/> (no pattern return), at <paramref name="position"/>.</summary>
    private static AircraftState AddPursuingFollower(SimulationEngine engine, LatLon position, TrueHeading heading)
    {
        AircraftState follower = MakeVfr(Follower, position, heading, 1000);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        CommandDispatcher.InstallVfrFollowPhase(follower, Leader, patternReturn: null, climbOutGate: null);
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        return follower;
    }

    private static void AssertJoinedBaseAt(AircraftState follower, LatLon startPoint)
    {
        PatternEntryPhase entry = Assert.IsType<PatternEntryPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(PatternEntryKind.Base, entry.Kind);
        Assert.True(
            GeoMath.DistanceNm(new LatLon(entry.EntryLat, entry.EntryLon), startPoint) < 0.01,
            $"base entry at {entry.EntryLat:F5},{entry.EntryLon:F5}, lead's base began at {startPoint.Lat:F5},{startPoint.Lon:F5}"
        );
        Assert.IsType<BasePhase>(follower.Phases.Phases[follower.Phases.CurrentIndex + 1]);
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
        Assert.Equal(PatternDirection.Right, follower.Phases.TrafficDirection);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void TryJoinLeadBase_FliesToStartPointThenAlongTheLeadsBaseLine()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddBaseLead(engine);
        double leadPatternAltitudeFt = ((BasePhase)lead.Phases!.CurrentPhase!).Waypoints!.PatternAltitude;
        LatLon start = OffFinal(rwy, 5.0, 4.5);
        AircraftState follower = AddPursuingFollower(engine, start, new TrueHeading(GeoMath.BearingTo(start, LeadBaseStart)));
        // A touch-and-go cleared during the pursuit rides onto the joined circuit.
        follower.Phases!.LandingClearance = ClearanceType.ClearedTouchAndGo;
        follower.Phases.ClearedRunwayId = "28R";

        TickSeconds(engine, 1);

        AssertJoinedBaseAt(follower, LeadBaseStart);
        var entry = (PatternEntryPhase)follower.Phases!.CurrentPhase!;
        Assert.Equal(leadPatternAltitudeFt, entry.PatternAltitude);
        Assert.Equal(ClearanceType.ClearedTouchAndGo, follower.Phases.LandingClearance);
        Assert.Equal("28R", follower.Phases.ClearedRunwayId);
        Assert.IsType<TouchAndGoPhase>(follower.Phases.Phases[^1]);

        TrueHeading leadBaseHeading = ((BasePhase)lead.Phases!.CurrentPhase!).Waypoints!.BaseHeading;
        double? maxOffBaseLineNm = null;
        for (int s = 0; (s < 240) && (follower.Phases?.CurrentPhase is not FinalApproachPhase); s++)
        {
            TickSeconds(engine, 1);
            if (follower.Phases?.CurrentPhase is BasePhase followerBase)
            {
                double startOffNm = OffBaseLineNm(Assert.IsType<LatLon>(followerBase.StartPoint), LeadBaseStart, leadBaseHeading);
                double nowOffNm = OffBaseLineNm(follower.Position, LeadBaseStart, leadBaseHeading);
                maxOffBaseLineNm = Math.Max(maxOffBaseLineNm ?? 0, Math.Max(startOffNm, nowOffNm));
            }
        }

        Assert.NotNull(maxOffBaseLineNm);
        output.WriteLine($"follower's base track strayed up to {maxOffBaseLineNm:F2} nm from the lead's base line");
        Assert.True(maxOffBaseLineNm <= 0.3, $"follower's base track strayed {maxOffBaseLineNm:F2} nm from the lead's base line");
    }

    [Theory]
    [InlineData(ClearanceType.ClearedStopAndGo, typeof(StopAndGoPhase))]
    [InlineData(ClearanceType.ClearedLowApproach, typeof(LowApproachPhase))]
    [InlineData(ClearanceType.ClearedForOption, typeof(TouchAndGoPhase))]
    public void TryJoinLeadBase_CarriesTheArmedOptionFamilyClearance(ClearanceType clearance, Type terminal)
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddBaseLead(engine);
        LatLon start = OffFinal(rwy, 5.0, 4.5);
        AircraftState follower = AddPursuingFollower(engine, start, new TrueHeading(GeoMath.BearingTo(start, LeadBaseStart)));
        follower.Phases!.LandingClearance = clearance;
        follower.Phases.ClearedRunwayId = "28R";

        TickSeconds(engine, 1);

        AssertJoinedBaseAt(follower, LeadBaseStart);
        Assert.Equal(clearance, follower.Phases!.LandingClearance);
        Assert.Equal("28R", follower.Phases.ClearedRunwayId);
        Assert.IsType(terminal, follower.Phases.Phases[^1]);
    }

    /// <summary>Perpendicular distance (nm) from <paramref name="position"/> to the lead's base line: the line through
    /// the lead's recorded base start point along its base heading.</summary>
    private static double OffBaseLineNm(LatLon position, LatLon leadBaseStart, TrueHeading leadBaseHeading) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, leadBaseStart, leadBaseHeading));

    [Fact]
    public void TryJoinLeadBase_PastTheBaseLine_KeepsPursuing()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddBaseLead(engine);
        // A mile closer in along the final than the lead's base line, well outside it and the downwind.
        AircraftState follower = AddPursuingFollower(engine, OffFinal(rwy, 3.0, 4.5), rwy.TrueHeading - 90.0);

        TickSeconds(engine, 1);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void TryJoinLeadBase_Inboard_KeepsPursuing()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddBaseLead(engine);
        // On the lead's base line but between it and the final: reaching the start point means turning back outbound.
        AircraftState follower = AddPursuingFollower(engine, OffFinal(rwy, 4.0, 1.0), rwy.TrueHeading - 90.0);

        TickSeconds(engine, 1);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void TryJoinLeadBase_OnTheParallelSide_KeepsPursuing()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddBaseLead(engine);
        // South of 28L: joining the right base to 28R would cross 28L's final.
        AircraftState follower = AddPursuingFollower(engine, OffFinal(rwy, 4.5, -1.0), rwy.TrueHeading + 90.0);

        TickSeconds(engine, 1);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void TryJoinLeadBase_LeadAlreadyOnBase_UsesRecordedStartPoint()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddBaseLead(engine);
        TickSeconds(engine, 40);
        Assert.IsType<BasePhase>(lead.Phases!.CurrentPhase);
        double leadMovedNm = GeoMath.DistanceNm(lead.Position, LeadBaseStart);
        Assert.True(leadMovedNm > 0.5, $"lead moved only {leadMovedNm:F2} nm down its base");

        LatLon start = OffFinal(rwy, 5.5, 4.0);
        AircraftState follower = AddPursuingFollower(engine, start, new TrueHeading(GeoMath.BearingTo(start, LeadBaseStart)));
        TickSeconds(engine, 1);

        AssertJoinedBaseAt(follower, LeadBaseStart);
    }

    // ─── Building spacing laterally, following in a chain ───

    private static LatLon Vpcbt()
    {
        (double Lat, double Lon) fix =
            NavigationDatabase.Instance.GetFixPosition("VPCBT") ?? throw new InvalidOperationException("VPCBT missing from navdata");
        return new LatLon(fix.Lat, fix.Lon);
    }

    /// <summary>Lead flying <c>DCT VPCBT; ERB 28R</c> from <paramref name="start"/> at <paramref name="iasKt"/>: bound for the
    /// pattern, no runway yet.</summary>
    private static AircraftState AddVpcbtLead(SimulationEngine engine, LatLon start, double iasKt)
    {
        AircraftState lead = AddLead(engine, start, "DCT VPCBT; ERB 28R");
        var track = new TrueHeading(GeoMath.BearingTo(start, Vpcbt()));
        lead.TrueHeading = track;
        lead.TrueTrack = track;
        lead.IndicatedAirspeed = iasKt;
        lead.Targets.TargetSpeed = iasKt;
        return lead;
    }

    private static double C172FloorKt => AircraftPerformance.ApproachSpeed("C172", AircraftCategory.Piston);

    /// <summary>The along-path gap from <paramref name="follower"/> to <paramref name="lead"/>, recording the lead's position
    /// into <paramref name="path"/> first.</summary>
    private static double PathGapNm(LeadPathTrail path, AircraftState follower, AircraftState lead)
    {
        path.Record(lead.Position);
        return path.Project(follower.Position, lead.Position, lead.TrueTrack).GapNm;
    }

    [Fact]
    public void FreePursuit_TooCloseBehindPatternBoundLead_STurnsOutsideThenFollowsNoseOnInTrail()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        LatLon leadStart = OffFinal(rwy, 10.0, 6.0);
        // The lead a little faster than the follower's floor, slower than its cruise.
        AircraftState lead = AddVpcbtLead(engine, leadStart, C172FloorKt + 3.0);
        TrueHeading leg = lead.TrueTrack;
        AircraftState follower = AddPursuingFollower(engine, GeoMath.ProjectPoint(leadStart, leg.ToReciprocal(), 0.85), leg);
        follower.IndicatedAirspeed = C172FloorKt;
        var path = new LeadPathTrail();
        path.Record(lead.Position);

        TickSeconds(engine, 2);

        // 0.85 nm is short of the 1.1 nm goal (1.0 pattern spacing + 0.1) by 0.25 nm: a 30° S-turn, to the pattern side.
        TrueHeading excursion = Assert.IsType<TrueHeading>(follower.Targets.TargetTrueHeading);
        double offDeg = leg.SignedAngleTo(excursion);
        output.WriteLine($"leg {leg.Degrees:F0}, excursion heading {excursion.Degrees:F0} ({offDeg:F1}°)");
        Assert.InRange(Math.Abs(offDeg), 29.0, 31.0);
        TrueHeading excursionSide = leg + (90.0 * Math.Sign(offDeg));
        Assert.True(excursionSide.AbsAngleTo(rwy.TrueHeading + 90.0) < 90.0, $"S-turn toward {excursionSide.Degrees:F0} is not the pattern side");

        int? spacedAt = null;
        int noseChecks = 0;
        double minGapAfterNm = double.MaxValue;
        for (int s = 2; (s < 400) && (lead.Phases?.AssignedRunway is null); s++)
        {
            TickSeconds(engine, 1);
            if (follower.Phases?.CurrentPhase is not VfrFollowPhase)
            {
                break;
            }
            double gapNm = PathGapNm(path, follower, lead);
            if (s % 20 == 0)
            {
                output.WriteLine(
                    $"t={s}: gap {gapNm:F2} nm along the path, straight {GeoMath.DistanceNm(follower.Position, lead.Position):F2},"
                        + $" hdg {follower.TrueHeading.Degrees:F0} gs {follower.GroundSpeed:F0}/{lead.GroundSpeed:F0} kt"
                );
            }
            spacedAt ??= gapNm >= 1.1 ? s : null;
            if ((spacedAt is { } at) && (s >= at + 30))
            {
                var bearing = new TrueHeading(GeoMath.BearingTo(follower.Position, lead.Position));
                Assert.True(
                    follower.TrueHeading.AbsAngleTo(bearing) <= 10.0,
                    $"t={s}: heading {follower.TrueHeading.Degrees:F0}, lead bears {bearing.Degrees:F0}"
                );
                minGapAfterNm = Math.Min(minGapAfterNm, gapNm);
                noseChecks++;
            }
        }

        output.WriteLine($"spaced at t={spacedAt}, min gap after {minGapAfterNm:F2} nm over {noseChecks} s");
        Assert.NotNull(spacedAt);
        Assert.True(noseChecks > 30, $"only {noseChecks} s of chain following before the lead's entry");
        Assert.True(minGapAfterNm >= 1.0, $"gap fell to {minGapAfterNm:F2} nm after spacing was built");
    }

    [Fact]
    public void FreePursuit_ThroughLeadNinetyDegreeTurn_TurnsWhereTheLeadTurnedAndKeepsTheGap()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        LatLon leadStart = OffFinal(rwy, 20.0, 12.0);
        var north = new TrueHeading(0);
        AircraftState lead = MakeVfr(Leader, leadStart, north, 3000);
        engine.World.AddAircraft(lead);
        // Spaced at the free-flight goal behind a lead that is not in the pattern: 1.5 nm + 0.1.
        AircraftState follower = AddPursuingFollower(engine, GeoMath.ProjectPoint(leadStart, north.ToReciprocal(), 1.6), north);
        follower.Altitude = 3000;
        var path = new LeadPathTrail();
        path.Record(lead.Position);

        TickSeconds(engine, 30);
        lead.Targets.TargetTrueHeading = new TrueHeading(90);
        double startGapNm = PathGapNm(path, follower, lead);
        LatLon? leadTurnedAt = null;
        LatLon? followerTurnedAt = null;
        double minGapNm = startGapNm;
        for (int s = 0; s < 150; s++)
        {
            TickSeconds(engine, 1);
            minGapNm = Math.Min(minGapNm, PathGapNm(path, follower, lead));
            leadTurnedAt ??= lead.TrueHeading.AbsAngleTo(north) >= 3.0 ? lead.Position : null;
            followerTurnedAt ??= follower.TrueHeading.AbsAngleTo(north) >= 3.0 ? follower.Position : null;
        }

        LatLon leadTurn = Assert.IsType<LatLon>(leadTurnedAt);
        LatLon followerTurn = Assert.IsType<LatLon>(followerTurnedAt);
        double turnPointsApartNm = GeoMath.DistanceNm(leadTurn, followerTurn);
        output.WriteLine($"turn points {turnPointsApartNm:F2} nm apart; gap {startGapNm:F2} → min {minGapNm:F2} nm");
        // Pure pursuit would start turning the moment the lead did, 1.6 nm short of its turn point.
        Assert.True(turnPointsApartNm <= 0.3, $"follower turned {turnPointsApartNm:F2} nm from where the lead turned");
        Assert.True(minGapNm >= startGapNm - 0.05, $"gap shrank from {startGapNm:F2} to {minGapNm:F2} nm through the turn");
    }

    [Fact]
    public void FreePursuitExcursion_AtTheOffsetCap_HoldsParallelToTheLeadTrack()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 12.0, 8.0), new TrueHeading(0), 3000);
        // 0.5 nm behind and 1.2 nm right of the lead's track: past a 1.0 nm cap, still short of the gap.
        LatLon behind = GeoMath.ProjectPoint(lead.Position, new TrueHeading(180), 0.5);
        AircraftState follower = MakeVfr(Follower, GeoMath.ProjectPoint(behind, new TrueHeading(90), 1.2), new TrueHeading(30), 3000);
        var widen = new FollowWidenState { Active = true, Side = 1 };

        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(
            follower,
            lead,
            new FreePursuitSpacing(1.0, PistonLimits, null, null, null),
            new LeadPathTrail(),
            widen
        );

        Assert.True(widen.Active);
        Assert.True(heading.AbsAngleTo(lead.TrueTrack) < 0.01, $"heading {heading.Degrees:F1} at the cap");
    }

    [Fact]
    public void FreePursuit_ExtendingPastTheLeadsBaseTurn_KeepsTheLegAtTheFloorAndTurnsBaseOnceSpaced()
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState lead, AircraftState follower) = JetBaseLeadWithExtendedFollower(engine, 0.5);
        TrueHeading downwind = ((BasePhase)lead.Phases!.CurrentPhase!).Waypoints!.DownwindHeading;
        var path = new LeadPathTrail();
        path.Record(lead.Position);

        double? joinGapNm = null;
        int floorChecks = 0;
        for (int s = 0; (s < 300) && (joinGapNm is null); s++)
        {
            TickSeconds(engine, 1);
            double gapNm = PathGapNm(path, follower, lead);
            if (follower.Phases?.CurrentPhase is not VfrFollowPhase)
            {
                joinGapNm = gapNm;
                break;
            }

            // Short of the jet's 3 nm spacing, the follower keeps extending the leg (never turning with the lead onto its
            // base, 90° off) at its speed floor.
            Assert.True(follower.TrueHeading.AbsAngleTo(downwind) <= 50.0, $"t={s}: heading {follower.TrueHeading.Degrees:F0} left the leg");
            if (follower.Targets.TargetSpeed is { } targetKt)
            {
                Assert.Equal(C172FloorKt, targetKt);
                floorChecks++;
            }
        }

        Assert.True(floorChecks > 0, "the follower never flew a speed target while extending");
        BasePhase followerBase = Assert.IsType<BasePhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        LatLon baseStart = Assert.IsType<LatLon>(followerBase.StartPoint);
        double pastLeadStartNm = GeoMath.AlongTrackDistanceNm(baseStart, LeadBaseStart, downwind);
        output.WriteLine($"turned base {pastLeadStartNm:F2} nm past the lead's base turn point, {joinGapNm:F2} nm behind along its path");
        Assert.True(pastLeadStartNm > 0.5, $"turned base only {pastLeadStartNm:F2} nm past the lead's base turn point");
        Assert.True(pastLeadStartNm <= VfrFollowPhase.BaseExtensionLimitNm);
        Assert.True(joinGapNm >= 3.0, $"turned base {joinGapNm:F2} nm behind the lead along its path");
    }

    /// <summary>A jet lead on its right base to 28R, its base started at <see cref="LeadBaseStart"/>.</summary>
    private static AircraftState AddJetBaseLead(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, LeadBaseStart, rwy.TrueHeading - 90.0, 1500);
        lead.AircraftType = "B738";
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternDirection.Right, PatternEntryLeg.Base);
        return lead;
    }

    /// <summary>A jet lead on its right base to 28R (3 nm pattern spacing behind a jet), and a follower in pursuit on the lead's
    /// downwind line <paramref name="pastStartNm"/> past the point the lead's base began.</summary>
    private static (AircraftState Lead, AircraftState Follower) JetBaseLeadWithExtendedFollower(SimulationEngine engine, double pastStartNm)
    {
        AircraftState lead = AddJetBaseLead(engine);
        TrueHeading downwind = ((BasePhase)lead.Phases!.CurrentPhase!).Waypoints!.DownwindHeading;
        AircraftState follower = AddPursuingFollower(engine, GeoMath.ProjectPoint(LeadBaseStart, downwind, pastStartNm), downwind);
        return (lead, follower);
    }

    /// <summary>The RPO terminal form of the "unable to follow, extending downwind, request base turn" call, lead named.</summary>
    private const string UnableToFollowRequestBase = "unable to follow LEAD1, extending downwind, request base turn.";

    /// <summary>The follow has ended with the follower still flying <paramref name="leg"/>, never re-entering by a downwind
    /// entry, and it asked for a base turn exactly once.</summary>
    private static void AssertKeptTheLegAndRequestedABaseTurn(AircraftState follower, TrueHeading leg)
    {
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.IsNotType<VfrFollowPhase>(follower.Phases?.CurrentPhase);
        Assert.IsNotType<PatternEntryPhase>(follower.Phases?.CurrentPhase);
        Assert.True(follower.TrueHeading.AbsAngleTo(leg) <= 10.0, $"heading {follower.TrueHeading.Degrees:F0} left the leg {leg.Degrees:F0}");
        Assert.Single(follower.PendingWarnings, w => w.Contains(UnableToFollowRequestBase, StringComparison.Ordinal));
        Assert.DoesNotContain(follower.PendingWarnings, w => w.Contains("re-entering", StringComparison.Ordinal));
    }

    [Fact]
    public void FreePursuit_ExtendedPastTheLimitWithoutTheGap_KeepsTheLegAndRequestsABaseTurn()
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState lead, AircraftState follower) = JetBaseLeadWithExtendedFollower(engine, VfrFollowPhase.BaseExtensionLimitNm + 0.3);
        TrueHeading downwind = ((BasePhase)lead.Phases!.CurrentPhase!).Waypoints!.DownwindHeading;

        TickSeconds(engine, 1);
        Assert.Null(follower.Approach.FollowingCallsign);
        TickSeconds(engine, 30);

        AssertKeptTheLegAndRequestedABaseTurn(follower, downwind);
    }

    /// <summary>The <c>..._KeepsTheLegAndRequestsABaseTurn</c> set-up, flown to 30 s after the break-off.</summary>
    private AircraftState FollowerBrokenOffAtTheExtensionLimit(SimulationEngine engine)
    {
        (_, AircraftState follower) = JetBaseLeadWithExtendedFollower(engine, VfrFollowPhase.BaseExtensionLimitNm + 0.3);
        TickSeconds(engine, 31);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Single(follower.PendingWarnings, w => w.Contains(UnableToFollowRequestBase, StringComparison.Ordinal));
        output.WriteLine($"after the break-off: phase {follower.Phases?.CurrentPhase?.GetType().Name ?? "none"}");
        return follower;
    }

    /// <summary>The controller's answer is flown: the follower turns base to 28R in right traffic and goes on to final.</summary>
    private void AssertFliesBaseThenFinalTo28R(SimulationEngine engine, AircraftState follower)
    {
        bool sawBase = false;
        for (int s = 0; (s < 300) && (follower.Phases?.CurrentPhase is not FinalApproachPhase); s++)
        {
            TickSeconds(engine, 1);
            sawBase |= follower.Phases?.CurrentPhase is BasePhase;
        }

        output.WriteLine($"now {follower.Phases?.CurrentPhase?.GetType().Name ?? "none"}, saw base {sawBase}");
        Assert.True(sawBase, "the follower never flew a base leg");
        Assert.IsType<FinalApproachPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal("28R", follower.Phases?.AssignedRunway?.Designator);
        Assert.Equal(PatternDirection.Right, follower.Phases?.TrafficDirection);
    }

    [Fact]
    public void ExtensionBreakOff_HoldsAnExtendedDownwindUntilInstructed()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowerBrokenOffAtTheExtensionLimit(engine);
        DownwindPhase downwind = Assert.IsType<DownwindPhase>(follower.Phases?.CurrentPhase);
        Assert.True(downwind.IsExtended);
        Assert.Equal("28R", follower.Phases!.AssignedRunway?.Designator);
        Assert.Equal(PatternDirection.Right, follower.Phases.TrafficDirection);
        TrueHeading held = follower.TrueHeading;

        for (int s = 0; s < 60; s++)
        {
            TickSeconds(engine, 1);
            Assert.Same(downwind, follower.Phases?.CurrentPhase);
            Assert.True(follower.TrueHeading.AbsAngleTo(held) <= 5.0, $"t={s}: heading {follower.TrueHeading.Degrees:F0}, held {held.Degrees:F0}");
        }
    }

    [Fact]
    public void ExtensionBreakOff_ExtendedDownwindSurvivesSnapshotMidHold()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowerBrokenOffAtTheExtensionLimit(engine);
        SimulationEngine twinEngine = BuildEngine();
        AircraftState twin = FollowerBrokenOffAtTheExtensionLimit(twinEngine);
        DownwindPhase downwind = Assert.IsType<DownwindPhase>(follower.Phases?.CurrentPhase);

        string json = JsonSerializer.Serialize<PhaseDto>(downwind.ToSnapshot(), RecordingJsonOptions.Default);
        DownwindPhaseDto dto = Assert.IsType<DownwindPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        var restored = DownwindPhase.FromSnapshot(dto);
        Assert.True(restored.IsExtended);
        Assert.Equal(json, JsonSerializer.Serialize<PhaseDto>(restored.ToSnapshot(), RecordingJsonOptions.Default));

        // The restored hold flies exactly as the original: it replaces the twin world's phase and both worlds tick on.
        twin.Phases!.Phases[twin.Phases.CurrentIndex] = restored;
        for (int s = 0; s < 30; s++)
        {
            TickSeconds(engine, 1);
            TickSeconds(twinEngine, 1);
            Assert.Equal(follower.Targets.TargetTrueHeading, twin.Targets.TargetTrueHeading);
            Assert.Equal(follower.Targets.TargetAltitude, twin.Targets.TargetAltitude);
            Assert.Equal(follower.Targets.TargetSpeed, twin.Targets.TargetSpeed);
        }
        Assert.Same(restored, twin.Phases.CurrentPhase);
    }

    [Fact]
    public void ExtensionBreakOff_TurnBase_FliesBaseAndFinalToTheLeadsRunway()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowerBrokenOffAtTheExtensionLimit(engine);

        CommandResult result = engine.SendCommand(Follower, "TB");

        Assert.True(result.Success, result.Message);
        AssertFliesBaseThenFinalTo28R(engine, follower);
    }

    [Fact]
    public void ExtensionBreakOff_EnterRightBase_FliesBaseAndFinalToTheLeadsRunway()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowerBrokenOffAtTheExtensionLimit(engine);

        CommandResult result = engine.SendCommand(Follower, "ERB 28R");

        Assert.True(result.Success, result.Message);
        AssertFliesBaseThenFinalTo28R(engine, follower);
    }

    /// <summary>Fill <paramref name="ac"/>'s position history with points every 0.1 nm along <paramref name="trackInto"/>
    /// over the last <paramref name="backNm"/> before its present position, oldest first.</summary>
    private static void SeedPositionHistory(AircraftState ac, TrueHeading trackInto, double backNm)
    {
        for (int i = (int)Math.Round(backNm / 0.1); i >= 1; i--)
        {
            LatLon point = GeoMath.ProjectPoint(ac.Position, trackInto.ToReciprocal(), i * 0.1);
            ac.PositionHistory.Add((point.Lat, point.Lon));
        }
    }

    [Fact]
    public void FreePursuit_ExtendingALegThatNearsTheFinal_KeepsParallelAndRequestsABaseTurn()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        LatLon leadStart = OffFinal(rwy, 4.0, 1.3);
        AircraftState lead = MakeVfr(Leader, leadStart, rwy.TrueHeading - 90.0, 1000);
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternDirection.Right, PatternEntryLeg.Base);
        TrueHeading downwind = ((BasePhase)lead.Phases!.CurrentPhase!).Waypoints!.DownwindHeading;
        // The lead arrived at its base on a leg converging 30° on the final; 0.8 nm down that leg the follower is 0.9 nm
        // from the centerline, inside the 1 nm the extension keeps from the final.
        TrueHeading leg = downwind + 30.0;
        SeedPositionHistory(lead, leg, 0.5);
        AircraftState follower = AddPursuingFollower(engine, GeoMath.ProjectPoint(leadStart, leg, 0.8), leg);

        TickSeconds(engine, 1);
        Assert.Null(follower.Approach.FollowingCallsign);
        TickSeconds(engine, 30);

        AssertKeptTheLegAndRequestedABaseTurn(follower, downwind);
    }

    private static VfrFollowPhaseDto FollowDto(AircraftState follower) =>
        (VfrFollowPhaseDto)Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase).ToSnapshot();

    [Fact]
    public void FreePursuit_ExtendingWithinTheLimit_KeepsPursuingAndItsPathAndLeadBaseSurviveSnapshot()
    {
        SimulationEngine engine = BuildEngine();
        (_, AircraftState follower) = JetBaseLeadWithExtendedFollower(engine, 1.0);
        SimulationEngine twinEngine = BuildEngine();
        (_, AircraftState twin) = JetBaseLeadWithExtendedFollower(twinEngine, 1.0);

        TickSeconds(engine, 3);
        TickSeconds(twinEngine, 3);

        VfrFollowPhase phase = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        var dto = (VfrFollowPhaseDto)phase.ToSnapshot();
        Assert.NotNull(dto.LeadPath);
        Assert.True(dto.LeadPath.Length >= 4, $"lead path holds {dto.LeadPath.Length / 2} points");
        FollowLeadBaseDto leadBase = Assert.IsType<FollowLeadBaseDto>(dto.LeadBase);
        Assert.Equal(LeadBaseStart.Lat, leadBase.StartLat, 9);
        Assert.Equal(LeadBaseStart.Lon, leadBase.StartLon, 9);
        Assert.True(dto.WidenActive, "a follower 1 nm behind a jet should be building spacing");

        string json = JsonSerializer.Serialize<PhaseDto>(dto, RecordingJsonOptions.Default);
        VfrFollowPhase restored = RoundTrip(phase);
        Assert.Equal(json, JsonSerializer.Serialize<PhaseDto>(restored.ToSnapshot(), RecordingJsonOptions.Default));

        // The restored phase flies exactly as the original: it replaces the twin world's phase and both worlds tick on.
        twin.Phases!.Phases[twin.Phases.CurrentIndex] = restored;
        for (int s = 0; s < 20; s++)
        {
            TickSeconds(engine, 1);
            TickSeconds(twinEngine, 1);
            Assert.Equal(follower.Targets.TargetTrueHeading, twin.Targets.TargetTrueHeading);
            Assert.Equal(follower.Targets.TargetSpeed, twin.Targets.TargetSpeed);
        }
    }

    // ─── Review fixes: retarget, parallels, announcements, one gap measure, altitude, wake, seeding ───

    [Fact]
    public void Retarget_DropsTheOldLeadsPathAndBase()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddBaseLead(engine);
        // Beyond the base-join range: it pursues LEAD1 and remembers its base.
        LatLon start = OffFinal(rwy, 10.0, 6.0);
        AircraftState follower = AddPursuingFollower(engine, start, new TrueHeading(GeoMath.BearingTo(start, LeadBaseStart)));
        TickSeconds(engine, 5);
        Assert.NotNull(FollowDto(follower).LeadBase);

        // The new lead sits on the follower's own track: a runwayless lead outside the ±60° cone is refused from a
        // pursuit, so a retarget only happens when it is ahead.
        AircraftState other = MakeVfr("LEAD2", GeoMath.ProjectPoint(follower.Position, follower.TrueTrack, 4.0), new TrueHeading(0), 2000);
        engine.World.AddAircraft(other);
        CommandResult result = engine.SendCommand(Follower, "FOLLOW LEAD2");
        Assert.True(result.Success, result.Message);
        TickSeconds(engine, 1);

        VfrFollowPhaseDto dto = FollowDto(follower);
        Assert.Equal("LEAD2", dto.TargetCallsign);
        Assert.Null(dto.LeadBase);
        double[] path = Assert.IsType<double[]>(dto.LeadPath);
        for (int i = 0; i + 1 < path.Length; i += 2)
        {
            double fromOtherNm = GeoMath.DistanceNm(new LatLon(path[i], path[i + 1]), other.Position);
            Assert.True(fromOtherNm < 0.2, $"lead path point {i / 2} is {fromOtherNm:F2} nm from LEAD2");
        }
    }

    [Fact]
    public void FreePursuitExcursion_NoCircuitBehindAStraightInLead_NeverTowardTheParallelFinal()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 3.0, 0), rwy.TrueHeading, 1000);
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternDirection.Right, PatternEntryLeg.Final);
        Assert.IsType<FinalApproachPhase>(lead.Phases!.CurrentPhase);
        // 1.0 nm behind and 0.1 nm south of the 28R centerline, between it and 28L's.
        AircraftState follower = AddPursuingFollower(engine, OffFinal(rwy, 4.0, -0.1), rwy.TrueHeading);

        TickSeconds(engine, 1);

        VfrFollowPhaseDto dto = FollowDto(follower);
        TrueHeading heading = Assert.IsType<TrueHeading>(follower.Targets.TargetTrueHeading);
        output.WriteLine($"excursion active {dto.WidenActive}, side {dto.WidenSide}, heading {heading.Degrees:F0}");
        Assert.True(!dto.WidenActive || (dto.WidenSide > 0), $"excursion to side {dto.WidenSide}, toward 28L");
        Assert.True(rwy.TrueHeading.SignedAngleTo(heading) > -5.0, $"heading {heading.Degrees:F0} leans toward 28L");
    }

    [Fact]
    public void FreePursuitExcursion_OnTheNonPatternSideNearTheFinal_TurnsAwayFromTheFinal()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        // A runwayless lead half a mile ahead on the 28R final course, 0.4 nm south of it (south of 28L too), and the
        // follower on the same line: right traffic puts the pattern north, across both finals.
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 3.5, -0.4), rwy.TrueHeading, 1500);
        engine.World.AddAircraft(lead);
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 4.0, -0.4), rwy.TrueHeading, 1500);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        CommandDispatcher.InstallVfrFollowPhase(
            follower,
            Leader,
            new FollowPatternReturn(rwy, PatternDirection.Right, 1009, FromBase: false),
            climbOutGate: null
        );

        TickSeconds(engine, 1);

        TrueHeading heading = Assert.IsType<TrueHeading>(follower.Targets.TargetTrueHeading);
        Assert.True(rwy.TrueHeading.SignedAngleTo(heading) < -10.0, $"excursion heading {heading.Degrees:F0} turns toward the finals");
    }

    [Fact]
    public void FreePursuitExcursion_AnnouncesTheSTurnOncePerExcursion()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        LatLon leadStart = OffFinal(rwy, 10.0, 6.0);
        AircraftState lead = AddVpcbtLead(engine, leadStart, C172FloorKt + 3.0);
        LatLon behind = GeoMath.ProjectPoint(leadStart, lead.TrueTrack.ToReciprocal(), 0.85);
        AircraftState follower = AddPursuingFollower(engine, behind, lead.TrueTrack);
        const string STurnCall = "S-turning for spacing behind LEAD1.";

        TickSeconds(engine, 1);
        Assert.True(FollowDto(follower).WidenActive);
        Assert.Single(follower.PendingWarnings, w => w.Contains(STurnCall, StringComparison.Ordinal));

        TickSeconds(engine, 1);
        Assert.Single(follower.PendingWarnings, w => w.Contains(STurnCall, StringComparison.Ordinal));
    }

    [Fact]
    public void FreePursuit_PistonPairStartingTooClose_RunsOneExcursion()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        LatLon leadStart = OffFinal(rwy, 10.0, 6.0);
        AircraftState lead = AddVpcbtLead(engine, leadStart, C172FloorKt + 3.0);
        LatLon behind = GeoMath.ProjectPoint(leadStart, lead.TrueTrack.ToReciprocal(), 0.8);
        AircraftState follower = AddPursuingFollower(engine, behind, lead.TrueTrack);
        follower.IndicatedAirspeed = C172FloorKt;

        int excursions = 0;
        bool wasActive = false;
        for (int i = 0; (i < 400 * 4) && (lead.Phases?.AssignedRunway is null) && (follower.Phases?.CurrentPhase is VfrFollowPhase); i++)
        {
            engine.TickPhysics(0.25);
            if (follower.Phases?.CurrentPhase is not VfrFollowPhase)
            {
                break;
            }
            bool active = FollowDto(follower).WidenActive;
            excursions += (active && !wasActive) ? 1 : 0;
            wasActive = active;
        }

        Assert.Equal(1, excursions);
    }

    [Fact]
    public void FreePursuit_ExtendingBehindAPistonBase_TurnsBaseAtThePatternSpacing()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddBaseLead(engine);
        TrueHeading downwind = ((BasePhase)lead.Phases!.CurrentPhase!).Waypoints!.DownwindHeading;
        // 0.6 nm past the lead's base start: short of the 1.0 nm pattern spacing, clear of the 0.5 nm separation floor.
        AircraftState follower = AddPursuingFollower(engine, GeoMath.ProjectPoint(LeadBaseStart, downwind, 0.6), downwind);

        double leadPathNm = 0;
        LatLon leadWas = lead.Position;
        double? joinGapNm = null;
        for (int s = 0; (s < 300) && (joinGapNm is null); s++)
        {
            TickSeconds(engine, 1);
            leadPathNm += GeoMath.DistanceNm(leadWas, lead.Position);
            leadWas = lead.Position;
            if (follower.Phases?.CurrentPhase is not VfrFollowPhase)
            {
                joinGapNm = leadPathNm + GeoMath.AlongTrackDistanceNm(follower.Position, LeadBaseStart, downwind);
            }
        }

        Assert.IsType<BasePhase>(follower.Phases?.CurrentPhase);
        output.WriteLine($"turned base {joinGapNm:F2} nm behind along the lead's path plus the extension");
        Assert.InRange(Assert.IsType<double>(joinGapNm), 1.0, 1.35);
    }

    [Fact]
    public void TryJoinLeadBase_PistonBehindAJet_EntersAtItsOwnPatternAltitude()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddJetBaseLead(engine);
        double leadPatternAltitudeFt = ((BasePhase)lead.Phases!.CurrentPhase!).Waypoints!.PatternAltitude;
        // 4.7 nm from the lead's base start: past the jet's wake minimum, inside the base-join range.
        LatLon start = OffFinal(rwy, 8.0, 5.0);
        AircraftState follower = AddPursuingFollower(engine, start, new TrueHeading(GeoMath.BearingTo(start, LeadBaseStart)));

        TickSeconds(engine, 1);

        AssertJoinedBaseAt(follower, LeadBaseStart);
        var entry = (PatternEntryPhase)follower.Phases!.CurrentPhase!;
        double ownPatternAltitudeFt = rwy.AirportElevationFt + CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Piston);
        output.WriteLine($"entry at {entry.PatternAltitude:F0} ft; lead's pattern {leadPatternAltitudeFt:F0}, own {ownPatternAltitudeFt:F0}");
        Assert.True(ownPatternAltitudeFt < leadPatternAltitudeFt);
        Assert.True(entry.PatternAltitude <= ownPatternAltitudeFt + 1.0, $"entry climbs to {entry.PatternAltitude:F0} ft");
    }

    [Fact]
    public void TryJoinLeadBase_AssignedAltitude_IsKept()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddBaseLead(engine);
        LatLon start = OffFinal(rwy, 5.0, 4.5);
        AircraftState follower = AddPursuingFollower(engine, start, new TrueHeading(GeoMath.BearingTo(start, LeadBaseStart)));
        follower.Altitude = 2000;
        CommandResult cm = engine.SendCommand(Follower, "CM 20");
        Assert.True(cm.Success, cm.Message);
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);

        TickSeconds(engine, 1);

        AssertJoinedBaseAt(follower, LeadBaseStart);
        Assert.Equal(2000, ((PatternEntryPhase)follower.Phases!.CurrentPhase!).PatternAltitude);
        TickSeconds(engine, 5);
        Assert.InRange(follower.Altitude, 1990, 2010);
        Assert.True(follower.Targets.TargetAltitude is null or 2000, $"target altitude {follower.Targets.TargetAltitude}");
    }

    [Fact]
    public void TryJoinLeadBase_BehindAJet_WaitsForTheWakeMinimum()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AddJetBaseLead(engine);
        double patternNm = AirborneFollowHelper.DesiredDistanceForLeader(AircraftCategory.Jet);
        double wakeNm = WakeTurbulenceData.OnApproachWakeSeparationNm("B738", AircraftCategory.Jet, "C172", AircraftCategory.Piston);
        Assert.True(wakeNm > patternNm, $"wake minimum {wakeNm:F1} nm, pattern spacing {patternNm:F1} nm");
        LatLon start = OffFinal(rwy, 7.0, 4.0);
        double gapNm = GeoMath.DistanceNm(start, LeadBaseStart);
        Assert.InRange(gapNm, patternNm, wakeNm - 0.2);
        AircraftState follower = AddPursuingFollower(engine, start, new TrueHeading(GeoMath.BearingTo(start, LeadBaseStart)));

        TickSeconds(engine, 1);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FreePursuit_LeadAlreadyOnBase_SeedsItsPathFromThePositionHistory()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = AddBaseLead(engine);
        TrueHeading downwind = ((BasePhase)lead.Phases!.CurrentPhase!).Waypoints!.DownwindHeading;
        TrueHeading arrived = downwind + 25.0;
        SeedPositionHistory(lead, arrived, 0.5);
        LatLon start = OffFinal(rwy, 10.0, 6.0);
        AircraftState follower = AddPursuingFollower(engine, start, new TrueHeading(GeoMath.BearingTo(start, LeadBaseStart)));

        TickSeconds(engine, 1);

        FollowLeadBaseDto leadBase = Assert.IsType<FollowLeadBaseDto>(FollowDto(follower).LeadBase);
        var legTrack = new TrueHeading(leadBase.LegTrackDeg);
        Assert.True(legTrack.AbsAngleTo(arrived) < 2.0, $"leg track {legTrack.Degrees:F0}, the lead arrived on {arrived.Degrees:F0}");
    }

    // ─── Recorded case, flown on ───

    [Fact]
    public void FollowFromBase_RecordedCase_JoinsLeadBaseAndLandsInTrail()
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
            RunwayInfo rwy = Oak("28R");
            var trail = new RecordedTrail(rwy);

            for (int t = 285; t <= 500; t++)
            {
                engine.ReplayOneSecond();
                trail.Observe(t, engine.FindAircraft("N123AB"), engine.FindAircraft("N314GT"), output);
            }

            Assert.True(trail.FollowerPursued, "N123AB never went into pursuit of N314GT");
            Assert.NotNull(trail.LeadBaseStart);
            double maxOffBaseLineNm = Assert.IsType<double>(trail.MaxOffLeadBaseLineNm);
            output.WriteLine(
                $"N123AB's base up to {maxOffBaseLineNm:F2} nm off N314GT's base line; min final gap {trail.MinFinalGapNm:F2} nm;"
                    + $" max south of 28R {trail.MaxSouthNm:F2} nm"
            );
            Assert.True(maxOffBaseLineNm <= 0.3, $"N123AB's base track strayed {maxOffBaseLineNm:F2} nm from N314GT's base line");
            Assert.True(trail.BothOnFinalSeen, "N123AB and N314GT were never on final together");
            Assert.True(trail.MinFinalGapNm >= 1.0, $"along-final gap fell to {trail.MinFinalGapNm:F2} nm");
            Assert.True(trail.MaxSouthNm <= 0.1, $"N123AB went {trail.MaxSouthNm:F2} nm south of the 28R centerline before its final turn");
        }
    }

    /// <summary>What the recorded-case replay watches, second by second: the follower's pursuit, both base starts, the
    /// along-final gap once both are on final, and how far the follower strays south of the centerline before its final.</summary>
    private sealed class RecordedTrail(RunwayInfo runway)
    {
        private readonly LatLon _threshold = new(runway.ThresholdLatitude, runway.ThresholdLongitude);
        private bool _followerOnFinal;

        /// <summary>Position as (along the final from the threshold, right of the landing direction), in nm.</summary>
        private string Describe(LatLon position) =>
            $"({GeoMath.AlongTrackDistanceNm(position, _threshold, runway.TrueHeading.ToReciprocal()):F2},"
            + $" {GeoMath.SignedCrossTrackDistanceNm(position, _threshold, runway.TrueHeading):F2})";

        public bool FollowerPursued { get; private set; }
        public LatLon? LeadBaseStart { get; private set; }
        public double? MaxOffLeadBaseLineNm { get; private set; }

        private TrueHeading? _leadBaseHeading;

        /// <summary>The follower's joined base: its start point and present position, each measured off the lead's base line.</summary>
        private void ObserveFollowerBase(int t, AircraftState follower, BasePhase followerBase, ITestOutputHelper log)
        {
            if ((LeadBaseStart is not { } leadStart) || (_leadBaseHeading is not { } baseHeading) || (followerBase.StartPoint is not { } start))
            {
                return;
            }

            if (MaxOffLeadBaseLineNm is null)
            {
                log.WriteLine($"t={t} N123AB base began at {Describe(start)}");
            }
            double startOffNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(start, leadStart, baseHeading));
            double nowOffNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(follower.Position, leadStart, baseHeading));
            MaxOffLeadBaseLineNm = Math.Max(MaxOffLeadBaseLineNm ?? 0, Math.Max(startOffNm, nowOffNm));
        }

        public bool BothOnFinalSeen { get; private set; }
        public double MinFinalGapNm { get; private set; } = double.MaxValue;
        public double MaxSouthNm { get; private set; } = double.MinValue;

        public void Observe(int t, AircraftState? follower, AircraftState? lead, ITestOutputHelper log)
        {
            if ((follower is null) || (lead is null))
            {
                return;
            }

            Phase? followerPhase = follower.Phases?.CurrentPhase;
            Phase? leadPhase = lead.Phases?.CurrentPhase;
            FollowerPursued |= followerPhase is VfrFollowPhase;
            if ((leadPhase is BasePhase leadBase) && (leadBase.StartPoint is { } leadStart) && (LeadBaseStart is null))
            {
                LeadBaseStart = leadStart;
                _leadBaseHeading = leadBase.Waypoints?.BaseHeading;
                log.WriteLine($"t={t} N314GT base began at {Describe(leadStart)}");
            }
            if (FollowerPursued && (followerPhase is BasePhase followerBase))
            {
                ObserveFollowerBase(t, follower, followerBase, log);
            }

            _followerOnFinal |= FollowerPursued && (followerPhase is FinalApproachPhase);
            if (!_followerOnFinal)
            {
                MaxSouthNm = Math.Max(MaxSouthNm, -GeoMath.SignedCrossTrackDistanceNm(follower.Position, _threshold, runway.TrueHeading));
            }

            if (_followerOnFinal && (followerPhase is FinalApproachPhase) && (leadPhase is FinalApproachPhase or LandingPhase) && !lead.IsOnGround)
            {
                BothOnFinalSeen = true;
                TrueHeading outbound = runway.TrueHeading.ToReciprocal();
                double gapNm =
                    GeoMath.AlongTrackDistanceNm(follower.Position, _threshold, outbound)
                    - GeoMath.AlongTrackDistanceNm(lead.Position, _threshold, outbound);
                MinFinalGapNm = Math.Min(MinFinalGapNm, gapNm);
            }
        }
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
