using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Proceed on course (<c>OC</c>), airborne: an IFR aircraft goes direct to the next fix ahead on its filed route and flies
/// the rest of it; a VFR aircraft, or one with no route fix ahead, goes direct to its destination. Refused on an approach,
/// with a pending approach clearance, on a military route and on the ground. Real navdata throughout.
/// </summary>
[Collection("NavDbMutator")]
public sealed class ProceedOnCourseTests
{
    private const string KaseRoute = "LINDZ1 LINDZ JNC BEVRR KATTS INYOE DYAMD5";

    public ProceedOnCourseTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static LatLon Fix(string name)
    {
        (double Lat, double Lon)? pos = NavigationDatabase.Instance.ResolveFixOrFrd(name);
        Assert.NotNull(pos);
        return new LatLon(pos.Value.Lat, pos.Value.Lon);
    }

    private static LatLon Point(ResolvedFix fix) => new(fix.Lat, fix.Lon);

    /// <summary>An IFR aircraft <paramref name="fraction"/> of the way along a leg, tracking toward its end.</summary>
    private static AircraftState OnLeg(LatLon from, LatLon to, double fraction, AircraftFlightPlan plan)
    {
        var bearing = new TrueHeading(GeoMath.BearingTo(from, to));
        return Airborne(GeoMath.ProjectPoint(from, bearing, GeoMath.DistanceNm(from, to) * fraction), bearing, plan);
    }

    /// <summary>An IFR aircraft <paramref name="beyondNm"/> past <paramref name="to"/>, still tracking away from <paramref name="from"/>.</summary>
    private static AircraftState PastFix(LatLon from, LatLon to, double beyondNm, AircraftFlightPlan plan)
    {
        var bearing = new TrueHeading(GeoMath.BearingTo(from, to));
        return Airborne(GeoMath.ProjectPoint(to, bearing, beyondNm), bearing, plan);
    }

    private static AircraftState Airborne(LatLon position, TrueHeading track, AircraftFlightPlan plan)
    {
        var ac = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = position,
            TrueHeading = track,
            TrueTrack = track,
            Altitude = 15_000,
            IndicatedAirspeed = 280,
            FlightPlan = plan,
        };
        ac.Targets.TargetTrueHeading = track;
        ac.Targets.TargetAltitude = 15_000;
        ac.Targets.AssignedAltitude = 15_000;
        return ac;
    }

    private static AircraftFlightPlan OakToRdd() =>
        new()
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KRDD",
            Route = "OAK SAC RBL",
        };

    private static AircraftFlightPlan AseToSfo(string route = KaseRoute) =>
        new()
        {
            HasFlightPlan = true,
            Departure = "KASE",
            Destination = "KSFO",
            Route = route,
        };

    private static AircraftState OakSacLeg() => OnLeg(Fix("OAK"), Fix("SAC"), 0.4, OakToRdd());

    /// <summary>On the OAK–SAC leg, tracking <paramref name="offDeg"/> right of the leg's course: a vector off the route.</summary>
    private static AircraftState OakSacLegVectored(double offDeg)
    {
        AircraftState ac = OakSacLeg();
        var track = new TrueHeading(ac.TrueTrack.Degrees + offDeg);
        ac.TrueHeading = track;
        ac.TrueTrack = track;
        ac.Targets.TargetTrueHeading = track;
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(track.Degrees);
        return ac;
    }

    private static CommandResult Send(AircraftState ac, string input)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new Random(1)));
    }

    private static List<string> RouteNames(AircraftState ac) => [.. ac.Targets.NavigationRoute.Select(t => t.Name)];

    private static List<ResolvedFix> Expanded(AircraftFlightPlan plan) =>
        LiveTrafficAssumer.FindRouteRejoin(Airborne(Fix("INYOE"), new TrueHeading(0), plan)).Resolved;

    private static ApproachClearance Ils28R() =>
        new()
        {
            TransitionName = null,
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = new TrueHeading(280),
        };

    private static HoldingPatternPhase Hold(bool inLieu) =>
        new()
        {
            FixName = "SAC",
            FixLat = Fix("SAC").Lat,
            FixLon = Fix("SAC").Lon,
            InboundCourse = 360,
            LegLength = 1,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
            IsHoldInLieu = inLieu,
        };

    private static void StartIn(AircraftState ac, Phase phase, PhaseList? list = null)
    {
        ac.Phases = list ?? new PhaseList();
        ac.Phases.Add(phase);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        Assert.Same(phase, ac.Phases.CurrentPhase);
    }

    [Fact]
    public void Parse_Oc_IsProceedOnCourse()
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse("OC");

        Assert.True(parsed.IsSuccess, parsed.Reason);
        Assert.IsType<ProceedOnCourseCommand>(parsed.Value);
    }

    [Fact]
    public void IfrOnHeadingVector_RouteAhead_RejoinsAtTheNextFixAndClearsTheHeading()
    {
        AircraftState ac = OakSacLeg();
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(10);
        ac.Targets.PreferredTurnDirection = TurnDirection.Left;

        CommandResult result = FlightCommandHandler.ApplyOnCourse(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["SAC", "RBL"], RouteNames(ac));
        Assert.Null(ac.Targets.AssignedMagneticHeading);
        Assert.Null(ac.Targets.PreferredTurnDirection);
        Assert.Equal(15_000, ac.Targets.AssignedAltitude);
        Assert.Equal("on course, direct SAC", result.PilotReadback?.Speech.Terminal);
    }

    /// <summary>A 60° vector off the route: the rejoin point is the leg's end fix, whatever the track.</summary>
    [Fact]
    public void Ifr60DegreeVectorOffTheRoute_RejoinsTheNextFixAlongTheRoute()
    {
        AircraftState ac = OakSacLegVectored(60);

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal(["SAC", "RBL"], RouteNames(ac));
    }

    [Fact]
    public void Ifr90DegreeVectorOffTheRoute_RejoinsTheNextFixAlongTheRoute()
    {
        AircraftState ac = OakSacLegVectored(90);

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal(["SAC", "RBL"], RouteNames(ac));
    }

    [Fact]
    public void IfrPastTheLastRouteFix_GoesToTheClearanceLimitDirect()
    {
        AircraftState ac = PastFix(Fix("SAC"), Fix("RBL"), 20, OakToRdd());

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        NavigationTarget target = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("KRDD", target.Name);
        Assert.True(target.IsClearanceLimitDirect);
    }

    [Fact]
    public void IfrNoRouteAhead_ClearedDestinationSet_GoesToTheClearedDestination()
    {
        AircraftState ac = PastFix(Fix("SAC"), Fix("RBL"), 20, OakToRdd());
        ac.ClearedDestination = "KSMF";

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KSMF", Assert.Single(ac.Targets.NavigationRoute).Name);
    }

    /// <summary>
    /// No cleared destination: the rejoined route ends where the no-route fallback would go, the filed destination, through
    /// the route-end clearance-limit direct.
    /// </summary>
    [Fact]
    public void IfrClearedDestinationNull_RouteRejoined_PastTheLastFixGoesToTheFiledDestination()
    {
        AircraftState ac = OnLeg(Fix("SAC"), Fix("RBL"), 0.97, OakToRdd());
        ac.ClearedDestination = "";

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal(["RBL"], RouteNames(ac));
        Assert.Equal("KRDD", ac.ClearedDestination);
        for (int t = 0; (t < 300) && (ac.Targets.NavigationRoute is [{ Name: "RBL" }]); t++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        NavigationTarget direct = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("KRDD", direct.Name);
        Assert.True(direct.IsClearanceLimitDirect);
    }

    [Fact]
    public void IfrFiledStarInTheRemainder_ActivatesTheStarWithoutDescendVia()
    {
        AircraftState ac = OnLeg(Fix("KATTS"), Fix("INYOE"), 0.5, AseToSfo());
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(250);

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal("INYOE", ac.Targets.NavigationRoute[0].Name);
        Assert.Equal("DYAMD5", ac.Procedure.ActiveStarId);
        Assert.False(ac.Procedure.StarViaMode);
        Assert.Equal(15_000, ac.Targets.AssignedAltitude);
    }

    /// <summary>Every STAR fix is behind and the route goes on past it: the STAR stays inactive.</summary>
    [Fact]
    public void IfrNextFixPastTheStarsLastFix_StarStaysInactive()
    {
        AircraftFlightPlan plan = AseToSfo($"{KaseRoute} OAK SAC");
        List<ResolvedFix> resolved = Expanded(plan);
        Assert.Equal(["OAK", "SAC"], resolved.TakeLast(2).Select(f => f.Name));
        AircraftState ac = OnLeg(Point(resolved[^2]), Point(resolved[^1]), 0.5, plan);

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Procedure.ActiveStarId);
        Assert.Equal(["SAC"], RouteNames(ac));
    }

    /// <summary>On a STAR under a descend via, not vectored: the STAR and its via mode stay, the route from the next fix.</summary>
    [Fact]
    public void IfrOnStarWithDescendViaNoHeading_StarViaModeStays()
    {
        List<ResolvedFix> resolved = Expanded(AseToSfo());
        int inyoe = resolved.FindIndex(f => f.Name == "INYOE");
        Assert.InRange(inyoe, 0, resolved.Count - 2);
        AircraftState ac = OnLeg(Point(resolved[inyoe]), Point(resolved[inyoe + 1]), 0.3, AseToSfo());
        ac.Procedure.ActiveStarId = "DYAMD5";
        ac.Procedure.StarViaMode = true;

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Procedure.StarViaMode);
        Assert.Equal("DYAMD5", ac.Procedure.ActiveStarId);
        Assert.Equal(resolved[inyoe + 1].Name, ac.Targets.NavigationRoute[0].Name);

        // The rebuilt route carries the STAR's published restrictions again, as a descend via puts them on it.
        AircraftState probe = OnLeg(Point(resolved[inyoe]), Point(resolved[inyoe + 1]), 0.3, AseToSfo());
        LiveTrafficAssumer.InstallRouteFrom(probe, resolved, inyoe + 1);
        ArrivalRouteResolver.ApplyAltitudeProfile(probe, probe.FlightPlan.Route, []);
        AssertSameRestrictions(probe, ac);
    }

    /// <summary>
    /// On a STAR with no SID active, not vectored: the STAR stays, and so does the last published speed it crossed, which a
    /// later cancelled speed falls back to (AIM 5-4-1 NOTE 2; 7110.65 5-7-1.g NOTE 3). No SID ends, so none takes it.
    /// </summary>
    [Fact]
    public void IfrOnStarNoSidNoHeading_StarKept_LastCrossedPublishedSpeedKept()
    {
        List<ResolvedFix> resolved = Expanded(AseToSfo());
        int inyoe = resolved.FindIndex(f => f.Name == "INYOE");
        Assert.InRange(inyoe, 0, resolved.Count - 2);
        AircraftState ac = OnLeg(Point(resolved[inyoe]), Point(resolved[inyoe + 1]), 0.3, AseToSfo());
        ac.Procedure.ActiveStarId = "DYAMD5";
        ac.Procedure.LastCrossedPublishedSpeedKts = 230;
        Assert.Null(ac.Procedure.ActiveSidId);
        Assert.Null(ac.Targets.AssignedMagneticHeading);

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal("DYAMD5", ac.Procedure.ActiveStarId);
        Assert.Equal(230, ac.Procedure.LastCrossedPublishedSpeedKts);
    }

    /// <summary>
    /// On a STAR with a SID also active, not vectored: the SID ends and the STAR stays, and so does the last published speed
    /// the STAR crossed (AIM 5-4-1 NOTE 2; 7110.65 5-7-1.g NOTE 3). Ending the SID does not take it.
    /// </summary>
    [Fact]
    public void IfrOnStarSidEnded_StarKept_LastCrossedPublishedSpeedKept()
    {
        List<ResolvedFix> resolved = Expanded(AseToSfo());
        int inyoe = resolved.FindIndex(f => f.Name == "INYOE");
        Assert.InRange(inyoe, 0, resolved.Count - 2);
        AircraftState ac = OnLeg(Point(resolved[inyoe]), Point(resolved[inyoe + 1]), 0.3, AseToSfo());
        ac.Procedure.ActiveSidId = "LINDZ1";
        ac.Procedure.ActiveStarId = "DYAMD5";
        ac.Procedure.LastCrossedPublishedSpeedKts = 230;
        Assert.Null(ac.Targets.AssignedMagneticHeading);

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Procedure.ActiveSidId);
        Assert.Equal("DYAMD5", ac.Procedure.ActiveStarId);
        Assert.Equal(230, ac.Procedure.LastCrossedPublishedSpeedKts);
    }

    /// <summary>
    /// The route of <paramref name="actual"/> carries exactly the altitude and speed restrictions of
    /// <paramref name="expected"/>, fix by fix, and at least one of them.
    /// </summary>
    private static void AssertSameRestrictions(AircraftState expected, AircraftState actual)
    {
        static List<(string, CifpAltitudeRestriction?, CifpSpeedRestriction?)> Restrictions(AircraftState ac) =>
            [.. ac.Targets.NavigationRoute.Select(t => (t.Name, t.AltitudeRestriction, t.SpeedRestriction))];

        Assert.Contains(Restrictions(expected), r => (r.Item2 is not null) || (r.Item3 is not null));
        Assert.Equal(Restrictions(expected), Restrictions(actual));
    }

    /// <summary>Both procedures active under via clearances, at the case's position, with the case's heading.</summary>
    private static AircraftState BothProceduresActive(AircraftState ac, bool vectored, string sid = "LINDZ1", string star = "DYAMD5")
    {
        ac.Procedure.ActiveSidId = sid;
        ac.Procedure.SidViaMode = true;
        ac.Procedure.ClimbViaTargetFt = 19_000;
        ac.Procedure.ActiveStarId = star;
        ac.Procedure.StarViaMode = true;
        ac.Targets.AssignedAltitude = null;
        if (vectored)
        {
            ac.Targets.AssignedMagneticHeading = new MagneticHeading(270);
        }

        return ac;
    }

    /// <summary>Both active, the next fix on the STAR, not vectored: the STAR and its descend via stay, the SID ends.</summary>
    [Fact]
    public void BothActive_FixOnTheStar_NoHeading_SidClearedStarKept()
    {
        AircraftState ac = BothProceduresActive(OnLeg(Fix("KATTS"), Fix("INYOE"), 0.5, AseToSfo()), vectored: false);

        Assert.True(Send(ac, "OC").Success);

        Assert.Null(ac.Procedure.ActiveSidId);
        Assert.Equal("DYAMD5", ac.Procedure.ActiveStarId);
        Assert.True(ac.Procedure.StarViaMode);
    }

    /// <summary>Both active, the next fix on the SID, not vectored: the SID stays, the STAR's descend via is not kept.</summary>
    [Fact]
    public void BothActive_FixOnTheSid_NoHeading_SidKeptStarViaNotKept()
    {
        (AircraftState ac, _) = OnCoast9Sid();
        BothProceduresActive(ac, vectored: false, "COAST9", "COMIX2");

        Assert.True(Send(ac, "OC").Success);

        Assert.Equal("COAST9", ac.Procedure.ActiveSidId);
        Assert.True(ac.Procedure.SidViaMode);
        Assert.False(ac.Procedure.StarViaMode);
    }

    [Fact]
    public void BothActive_FixOnTheStar_Vectored_BothViasEnd()
    {
        AircraftState ac = BothProceduresActive(OnLeg(Fix("KATTS"), Fix("INYOE"), 0.5, AseToSfo()), vectored: true);

        Assert.True(Send(ac, "OC").Success);

        Assert.Null(ac.Procedure.ActiveSidId);
        Assert.False(ac.Procedure.SidViaMode);
        Assert.False(ac.Procedure.StarViaMode);
    }

    [Fact]
    public void BothActive_FixOnTheSid_Vectored_BothViasEnd()
    {
        (AircraftState ac, _) = OnCoast9Sid();
        BothProceduresActive(ac, vectored: true, "COAST9", "COMIX2");

        Assert.True(Send(ac, "OC").Success);

        Assert.Null(ac.Procedure.ActiveSidId);
        Assert.False(ac.Procedure.SidViaMode);
        Assert.False(ac.Procedure.StarViaMode);
    }

    /// <summary>STAR only under a descend via, vectored: the vector cancelled the STAR, so OC resumes it without the via.</summary>
    [Fact]
    public void StarOnly_Vectored_StarResumedWithoutDescendVia()
    {
        List<ResolvedFix> resolved = Expanded(AseToSfo());
        int inyoe = resolved.FindIndex(f => f.Name == "INYOE");
        AircraftState ac = OnLeg(Point(resolved[inyoe]), Point(resolved[inyoe + 1]), 0.3, AseToSfo());
        ac.Procedure.ActiveStarId = "DYAMD5";
        ac.Procedure.StarViaMode = true;
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(250);

        Assert.True(Send(ac, "OC").Success);

        Assert.Equal("DYAMD5", ac.Procedure.ActiveStarId);
        Assert.False(ac.Procedure.StarViaMode);
    }

    /// <summary>
    /// Under a climb via with no altitude assigned, OC to a fix past the SID ends the SID but not the climb: the top
    /// altitude becomes the assigned one, with no level-off and no vectoring warning.
    /// </summary>
    [Fact]
    public void ClimbViaInForce_NextFixPastTheSid_ClimbsToTheTopAltitudeWithoutLevelOff()
    {
        AircraftState ac = OnLeg(Fix("KATTS"), Fix("INYOE"), 0.5, AseToSfo());
        ac.Procedure.ActiveSidId = "LINDZ1";
        ac.Procedure.SidViaMode = true;
        ac.Procedure.ClimbViaTargetFt = 19_000;
        ac.Targets.AssignedAltitude = null;
        ac.Targets.TargetAltitude = 19_000;

        Assert.True(Send(ac, "OC").Success);

        Assert.Null(ac.Procedure.ActiveSidId);
        Assert.Empty(ac.PendingWarnings);
        Assert.Equal(19_000, ac.Targets.TargetAltitude);
        Assert.Equal(19_000, ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// The destination runway survives the rejoin, so the STAR's speeds are read with that runway's transition, not the
    /// first one NavData lists.
    /// </summary>
    [Fact]
    public void IfrStarRejoined_DestinationRunwayKept_ItsTransitionsSpeeds()
    {
        AircraftState ac = OnLeg(Fix("KATTS"), Fix("INYOE"), 0.5, AseToSfo());
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(250);
        ac.Procedure.DestinationRunway = "28L";

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal("28L", ac.Procedure.DestinationRunway);
        Assert.Equal(StarSpeeds("28L"), ac.Targets.NavigationRoute.Select(t => t.SpeedRestriction));
    }

    /// <summary>The published speeds the filed STAR puts on the route from INYOE for <paramref name="runway"/>.</summary>
    private static List<CifpSpeedRestriction?> StarSpeeds(string? runway)
    {
        AircraftState probe = OnLeg(Fix("KATTS"), Fix("INYOE"), 0.5, AseToSfo());
        List<ResolvedFix> resolved = Expanded(AseToSfo());
        LiveTrafficAssumer.InstallRouteFrom(probe, resolved, resolved.FindIndex(f => f.Name == "INYOE"));
        probe.Procedure.DestinationRunway = runway;
        ArrivalRouteResolver.ApplyStarSpeedRestrictions(probe, probe.FlightPlan.Route, []);
        return [.. probe.Targets.NavigationRoute.Select(t => t.SpeedRestriction)];
    }

    /// <summary>
    /// On the SID and not vectored: the SID and its climb via stay in force, the route from the next fix, and the filed STAR
    /// ahead is active with its speeds.
    /// </summary>
    [Fact]
    public void IfrOnSidNoHeading_ClimbViaInForce_SidAndClimbViaUnchanged()
    {
        (AircraftState ac, string next) = OnCoast9Sid();
        ac.Procedure.SidViaMode = true;
        ac.Procedure.ClimbViaTargetFt = 19_000;

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal("COAST9", ac.Procedure.ActiveSidId);
        Assert.True(ac.Procedure.SidViaMode);
        Assert.Equal(19_000, ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(next, ac.Targets.NavigationRoute[0].Name);
        Assert.NotNull(ac.Procedure.ActiveStarId);
    }

    /// <summary>The SID is kept by the next fix being one of its fixes, even when the live route no longer holds that fix.</summary>
    [Fact]
    public void IfrOnSidNoHeading_LiveRouteLacksTheFix_SidKept()
    {
        (AircraftFlightPlan plan, List<ResolvedFix> resolved, int next, string sid) = FindSidRestrictedPastItsFirstFix();
        AircraftState ac = OnLeg(Point(resolved[next - 1]), Point(resolved[next]), 0.3, plan);
        ac.Procedure.ActiveSidId = sid;
        ac.Procedure.SidViaMode = true;

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal(sid, ac.Procedure.ActiveSidId);
        Assert.True(ac.Procedure.SidViaMode);
        Assert.Equal(resolved[next].Name, ac.Targets.NavigationRoute[0].Name);

        // The rebuilt route carries the SID's published restrictions again, as the SID's activation overlays them.
        AssertSameRestrictions(SidOverlaidFrom(plan, resolved, next), ac);
    }

    /// <summary>The filed route from <paramref name="index"/> on with the filed SID's published restrictions overlaid.</summary>
    private static AircraftState SidOverlaidFrom(AircraftFlightPlan plan, List<ResolvedFix> resolved, int index)
    {
        AircraftState probe = OnLeg(Point(resolved[index - 1]), Point(resolved[index]), 0.3, plan);
        LiveTrafficAssumer.InstallRouteFrom(probe, resolved, index);
        NavigationCommandHandler.TryActivateFiledSid(probe, transmissionId: 0);
        return probe;
    }

    private static readonly string[] SidAirports = ["KSFO", "KOAK", "KSJC", "KLAX", "KLAS", "KDEN", "KSEA", "KPHX", "KATL", "KORD"];

    /// <summary>
    /// The first SID in NavData, searched in a fixed order, whose filed expansion has a fix past its first (a fix OC can
    /// rejoin at, on a leg long enough to rejoin it from 30% along) from which the overlaid route still carries a published
    /// restriction; with that fix's index.
    /// </summary>
    private static (AircraftFlightPlan Plan, List<ResolvedFix> Resolved, int Next, string Sid) FindSidRestrictedPastItsFirstFix() =>
        FindSidRestrictedPastItsFirstFix(HasRestrictions);

    private static (AircraftFlightPlan Plan, List<ResolvedFix> Resolved, int Next, string Sid) FindSidRestrictedPastItsFirstFix(
        Func<AircraftState, bool> restricted
    )
    {
        foreach (string airport in SidAirports)
        {
            foreach (CifpSidProcedure sid in NavigationDatabase.Instance.GetSids(airport))
            {
                var plan = new AircraftFlightPlan
                {
                    HasFlightPlan = true,
                    Departure = airport,
                    Destination = "KRNO",
                    Route = sid.ProcedureId,
                };
                List<ResolvedFix> resolved = Expanded(plan);
                int next = Enumerable
                    .Range(1, Math.Max(0, resolved.Count - 1))
                    .FirstOrDefault(i =>
                        (GeoMath.DistanceNm(Point(resolved[i - 1]), Point(resolved[i])) >= 6) && restricted(SidOverlaidFrom(plan, resolved, i))
                    );
                if (next > 0)
                {
                    return (plan, resolved, next, sid.ProcedureId);
                }
            }
        }

        Assert.Fail("no SID publishes a restriction past its first fix");
        return default;
    }

    private static bool HasRestrictions(AircraftState ac) =>
        ac.Targets.NavigationRoute.Exists(t => (t.AltitudeRestriction is not null) || (t.SpeedRestriction is not null));

    /// <summary>
    /// A kept SID with no climb via ("maintain" in force): its rebuilt route gets the published speeds back, its published
    /// altitudes stay out of force, and the SID id and the last published speed crossed are as they were.
    /// </summary>
    [Fact]
    public void SidKeptWithoutClimbVia_RouteRebuilt_PublishedSpeedsBackAltitudesNotInForce()
    {
        (AircraftFlightPlan plan, List<ResolvedFix> resolved, int next, string sid) = FindSidRestrictedPastItsFirstFix(p =>
            p.Targets.NavigationRoute.Exists(t => t.SpeedRestriction is not null)
        );
        AircraftState ac = OnLeg(Point(resolved[next - 1]), Point(resolved[next]), 0.3, plan);
        ac.Procedure.ActiveSidId = sid;
        ac.Procedure.SidViaMode = false;
        ac.Procedure.LastCrossedPublishedSpeedKts = 230;

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal(sid, ac.Procedure.ActiveSidId);
        Assert.Equal(230, ac.Procedure.LastCrossedPublishedSpeedKts);
        Assert.Equal(
            SidOverlaidFrom(plan, resolved, next).Targets.NavigationRoute.Select(t => (t.Name, t.SpeedRestriction)),
            ac.Targets.NavigationRoute.Select(t => (t.Name, t.SpeedRestriction))
        );
        Assert.False(AltitudeRestrictionInForce.ProcedureAltitudesInForce(ac));
        Assert.Equal(15_000, ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// Under a climb via, with the filed route ending on the SID and no fix ahead: OC goes to the destination but keeps
    /// climbing to the via's top, with no level-off and no vectoring warning.
    /// </summary>
    [Fact]
    public void ClimbViaInForce_NoFixAhead_ClimbsToTheTopAltitudeWithoutLevelOff()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KSAN",
            Route = "COAST9",
        };
        List<ResolvedFix> resolved = Expanded(plan);
        Assert.True(resolved.Count >= 2, "COAST9 expands to a leg");
        AircraftState ac = PastFix(Point(resolved[^2]), Point(resolved[^1]), 20, plan);
        ac.Procedure.ActiveSidId = "COAST9";
        ac.Procedure.SidViaMode = true;
        ac.Procedure.ClimbViaTargetFt = 19_000;
        ac.Targets.AssignedAltitude = null;
        ac.Targets.TargetAltitude = 19_000;

        Assert.True(Send(ac, "OC").Success);

        Assert.True(Assert.Single(ac.Targets.NavigationRoute).IsClearanceLimitDirect);
        Assert.Empty(ac.PendingWarnings);
        Assert.Equal(19_000, ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// The filed route joins the STAR's transition at a fix past the transition's first: the fix ahead is on the STAR as the
    /// route expands it, so the STAR and its descend via stay, with no vectoring warning.
    /// </summary>
    [Fact]
    public void StarJoinedMidTransition_DescendViaNoHeading_StarKept()
    {
        // The first KSFO STAR transition, in NavData order, with a fix other than its name that has a fix after it.
        (string star, string join, string next) = NavigationDatabase
            .Instance.GetStars("KSFO")
            .SelectMany(s =>
                (NavigationDatabase.Instance.GetStarTransitions(s.ProcedureId) ?? []).SelectMany(t =>
                    Enumerable
                        .Range(0, Math.Max(0, t.Fixes.Count - 1))
                        .Where(j => !t.Fixes[j].Equals(t.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(j => (s.ProcedureId, t.Fixes[j], t.Fixes[j + 1]))
                )
            )
            .First();
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KASE",
            Destination = "KSFO",
            Route = $"{join} {star}",
        };
        List<ResolvedFix> resolved = Expanded(plan);
        Assert.Equal([join, next], resolved.Take(2).Select(f => f.Name));
        AircraftState ac = OnLeg(Point(resolved[0]), Point(resolved[1]), 0.3, plan);
        ac.Procedure.ActiveStarId = star;
        ac.Procedure.StarViaMode = true;

        Assert.True(Send(ac, "OC").Success);

        Assert.Equal(star, ac.Procedure.ActiveStarId);
        Assert.True(ac.Procedure.StarViaMode);
        Assert.Equal(next, ac.Targets.NavigationRoute[0].Name);
        Assert.Empty(ac.PendingWarnings);
    }

    /// <summary>The next fix ahead is past the SID: the SID ends, the filed route from that fix, the STAR step after it.</summary>
    [Fact]
    public void IfrNextFixPastTheSid_SidClearedStarResumed()
    {
        AircraftState ac = OnLeg(Fix("KATTS"), Fix("INYOE"), 0.5, AseToSfo());
        ac.Procedure.ActiveSidId = "LINDZ1";
        ac.Procedure.SidViaMode = true;

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Procedure.ActiveSidId);
        Assert.False(ac.Procedure.SidViaMode);
        Assert.Equal("INYOE", ac.Targets.NavigationRoute[0].Name);
        Assert.Equal("DYAMD5", ac.Procedure.ActiveStarId);
    }

    /// <summary>Vectored off the SID: OC rejoins laterally only, with no climb via and the altitude kept.</summary>
    [Fact]
    public void IfrVectoredOffSid_RejoinsLaterallyOnly()
    {
        (AircraftState ac, string next) = OnCoast9Sid();
        ac.Procedure.SidViaMode = true;
        ac.Procedure.ClimbViaTargetFt = 19_000;
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(270);

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.False(ac.Procedure.SidViaMode);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Procedure.ActiveSidId);
        Assert.Equal(15_000, ac.Targets.AssignedAltitude);
        Assert.Equal(next, ac.Targets.NavigationRoute[0].Name);
    }

    private static AircraftFlightPlan OakToSan() =>
        new()
        {
            HasFlightPlan = true,
            Departure = "KOAK",
            Destination = "KSAN",
            Route = "COAST9 MCKEY LAX COMIX2",
        };

    /// <summary>An aircraft on the COAST9 SID's first leg between fixes, the SID active and its filed route programmed.</summary>
    private static (AircraftState Aircraft, string Next) OnCoast9Sid()
    {
        List<ResolvedFix> resolved = Expanded(OakToSan());
        int segul = 1;
        Assert.True(resolved.Count > 2, "COAST9 expands to fixes past its first");
        AircraftState ac = OnLeg(Point(resolved[segul - 1]), Point(resolved[segul]), 0.3, OakToSan());
        ac.Procedure.ActiveSidId = "COAST9";
        LiveTrafficAssumer.InstallRouteFrom(ac, resolved, segul - 1);
        return (ac, resolved[segul].Name);
    }

    /// <summary>
    /// A COAST9 departure off OAK 30 (coded legs) cleared for takeoff and airborne below the 400 ft turn gate in its initial
    /// climb, with the context its phases tick in.
    /// </summary>
    private static (AircraftState Aircraft, PhaseContext Context) Coast9BelowTheGate()
    {
        RunwayInfo rwy = NavigationDatabase.Instance.GetRunway("OAK", "30")!;
        var ac = new AircraftState
        {
            Callsign = "SWA1822",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KOAK",
                Destination = "KSAN",
                Route = "COAST9 MCKEY LAX COMIX2",
                Altitude = PlannedAltitude.Ifr(39000),
                FlightRules = "IFR",
            },
        };
        var holding = new HoldingInPositionPhase();
        StartIn(ac, holding, new PhaseList { AssignedRunway = rwy });
        CommandResult cto = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            assignedAltitude: null,
            requestedRunway: null,
            TestDispatch.Context(new Random(1)),
            TestDispatch.EmptyTaxi,
            NullLogger.Instance
        );
        Assert.True(cto.Success, cto.Message);

        ac.IsOnGround = false;
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.EndLatitude, rwy.EndLongitude), rwy.TrueHeading, 0.2);
        ac.Altitude = rwy.ElevationFt + 200;
        ac.IndicatedAirspeed = 170;
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
        ac.Phases!.SkipTo<InitialClimbPhase>(ctx);
        Assert.NotNull(ac.Procedure.ActiveSidId);
        return (ac, ctx);
    }

    private static void FlyUntil(AircraftState ac, PhaseContext ctx, Func<bool> done, int maxSeconds)
    {
        for (int t = 0; (t < maxSeconds) && !done(); t++)
        {
            PhaseRunner.Tick(ac, ctx);
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(done(), $"the condition did not hold within {maxSeconds} s");
    }

    /// <summary>Below the 400 ft gate on the SID, not vectored: OC is already on course, so the gate and the pending route stay.</summary>
    [Fact]
    public void OcInInitialClimbBelowTheGate_GateKeptDeferredTurnNotReleased()
    {
        (AircraftState ac, _) = Coast9BelowTheGate();
        InitialClimbPhase climb = Assert.IsType<InitialClimbPhase>(ac.Phases?.CurrentPhase);
        List<NavigationTarget>? pending = climb.PendingRoute;
        Assert.NotNull(pending);
        string? sid = ac.Procedure.ActiveSidId;
        List<string> route = RouteNames(ac);

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Same(climb, ac.Phases?.CurrentPhase);
        Assert.Same(pending, climb.PendingRoute);
        Assert.Equal(sid, ac.Procedure.ActiveSidId);
        Assert.Equal(route, RouteNames(ac));
        Assert.Equal("on course", result.PilotReadback?.Speech.Terminal);
    }

    /// <summary>
    /// On the SID's coded legs, not vectored: OC is already on course. The phase, the SID, the climb via and the pending
    /// post-route stay, and the phase goes on sequencing its legs with the post-route still pending for its hand-over.
    /// </summary>
    [Fact]
    public void OcInDepartureProcedurePhaseOnCodedLegs_PhaseSidClimbViaAndPendingRouteKept()
    {
        (AircraftState ac, PhaseContext ctx) = Coast9BelowTheGate();
        FlyUntil(ac, ctx, () => ac.Phases?.CurrentPhase is DepartureProcedurePhase, 180);
        DepartureProcedurePhase procedure = Assert.IsType<DepartureProcedurePhase>(ac.Phases?.CurrentPhase);
        Assert.NotNull(procedure.PendingRoute);
        string? sid = ac.Procedure.ActiveSidId;
        double? climbVia = ac.Procedure.ClimbViaTargetFt;

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.Same(procedure.PostRoute, procedure.PendingRoute);
        Assert.Equal(sid, ac.Procedure.ActiveSidId);
        Assert.Equal(climbVia, ac.Procedure.ClimbViaTargetFt);

        FlyUntil(ac, ctx, () => procedure.ActiveLegIndex > 0, 300);
        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.Same(procedure.PostRoute, procedure.PendingRoute);
    }

    [Fact]
    public void VfrAirborne_GoesDirectToTheDestinationAsAPlainFix()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = "VFR",
            Departure = "KOAK",
            Destination = "KSMF",
        };
        AircraftState ac = OnLeg(Fix("OAK"), Fix("SAC"), 0.4, plan);
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(30);

        CommandResult result = FlightCommandHandler.ApplyOnCourse(ac);

        Assert.True(result.Success, result.Message);
        NavigationTarget target = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal("KSMF", target.Name);
        Assert.False(target.IsClearanceLimitDirect);
        Assert.Null(ac.Targets.AssignedMagneticHeading);
        Assert.Equal("on course", result.PilotReadback?.Speech.Terminal);
    }

    [Fact]
    public void VfrAirborne_ClearedDestinationSet_GoesToTheClearedDestination()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = "VFR",
            Departure = "KOAK",
            Destination = "KSMF",
        };
        AircraftState ac = OnLeg(Fix("OAK"), Fix("SAC"), 0.4, plan);
        ac.ClearedDestination = "KSJC";

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KSJC", Assert.Single(ac.Targets.NavigationRoute).Name);
    }

    /// <summary>
    /// The sim half of the menu/sim agreement: a VFR aircraft flies OC to its destination, never its route, so one with a
    /// route and no destination is refused (the menu hides the row:
    /// <c>MenuCatalogCommandTests.ProceedOnCourse_VfrRouteNoDestination_NotOffered</c>).
    /// </summary>
    [Fact]
    public void VfrRouteNoDestination_RefusedForWantOfADestination()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            FlightRules = "VFR",
            Departure = "KOAK",
            Route = "SAC RBL",
        };

        CommandResult result = Send(OnLeg(Fix("OAK"), Fix("SAC"), 0.4, plan), "OC");

        Assert.False(result.Success);
        Assert.True(result.IsOperatorGuidance);
    }

    [Fact]
    public void NoDestinationAndNoRoute_IsRefusedAsOperatorGuidance()
    {
        var plan = new AircraftFlightPlan { HasFlightPlan = true, Departure = "KOAK" };
        AircraftState ac = OnLeg(Fix("OAK"), Fix("SAC"), 0.4, plan);
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(30);
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "SAC", Position = Fix("SAC") });

        CommandResult result = Send(ac, "OC");

        Assert.False(result.Success);
        Assert.True(result.IsOperatorGuidance);
        Assert.Equal("OC: UAL123 has no filed route fix ahead and no destination to proceed to", result.Message);
        Assert.Equal(["SAC"], RouteNames(ac));
        Assert.Equal(30, ac.Targets.AssignedMagneticHeading?.Degrees);
    }

    [Fact]
    public void ClearedForAnApproach_IsRefused_StateUnchanged()
    {
        AircraftState ac = OakSacLeg();
        ac.Phases = new PhaseList { ActiveApproach = Ils28R() };
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "OAK", Position = Fix("OAK") });

        CommandResult result = Send(ac, "OC");

        Assert.False(result.Success);
        Assert.Equal("Unable, cleared ILS runway 28R approach", result.Message);
        Assert.Equal("unable, cleared ILS runway 28R approach.", result.PilotUnable?.Terminal);
        Assert.Equal(["OAK"], RouteNames(ac));
    }

    /// <summary>Intercepting the final course with the approach clearance in force: the dispatcher's gate names the approach.</summary>
    [Fact]
    public void InterceptingTheFinalCourse_ClearedApproach_RefusedByName_PhaseKept()
    {
        AircraftState ac = OakSacLeg();
        var intercept = new InterceptCoursePhase
        {
            FinalApproachCourse = new TrueHeading(280),
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
            AssignedInterceptHeading = null,
        };
        StartIn(ac, intercept, new PhaseList { ActiveApproach = Ils28R() });

        CommandResult result = Send(ac, "OC");

        Assert.False(result.Success);
        Assert.Equal("Unable, cleared ILS runway 28R approach", result.Message);
        Assert.Equal("unable, cleared ILS runway 28R approach.", result.PilotUnable?.Terminal);
        Assert.Same(intercept, ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void PlainHold_PendingApproachClearance_Refused_HoldKept()
    {
        AircraftState ac = OakSacLeg();
        HoldingPatternPhase hold = Hold(inLieu: false);
        StartIn(ac, hold);
        ac.Approach.PendingClearance = new PendingApproachInfo
        {
            Clearance = Ils28R(),
            AssignedRunway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!,
            ConnectingFix = null,
        };

        CommandResult result = Send(ac, "OC");

        Assert.False(result.Success);
        Assert.Equal("Unable, cleared for the approach, say intentions", result.Message);
        Assert.Same(hold, ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void PendingApproachClearance_IsRefused_StateUnchanged()
    {
        AircraftState ac = OakSacLeg();
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        ac.Approach.PendingClearance = new PendingApproachInfo
        {
            Clearance = Ils28R(),
            AssignedRunway = runway,
            ConnectingFix = null,
        };

        CommandResult result = Send(ac, "OC");

        Assert.False(result.Success);
        Assert.Equal("Unable, cleared for the approach, say intentions", result.Message);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void OnFinalOrAnApproachLeg_PhaseRejects()
    {
        Assert.True(new FinalApproachPhase().CanAcceptCommand(CanonicalCommandType.OnCourse).IsRejected);
        Assert.True(
            new ApproachNavigationPhase { Fixes = [] }
                .CanAcceptCommand(CanonicalCommandType.OnCourse)
                .IsRejected
        );
        Assert.True(Hold(inLieu: true).CanAcceptCommand(CanonicalCommandType.OnCourse).IsRejected);
        Assert.True(Hold(inLieu: false).CanAcceptCommand(CanonicalCommandType.OnCourse).ClearsThePhase);
    }

    [Fact]
    public void InterceptCoursePhase_ThroughDispatch_UnableOnTheApproach()
    {
        AircraftState ac = OakSacLeg();
        var intercept = new InterceptCoursePhase
        {
            FinalApproachCourse = new TrueHeading(280),
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
            AssignedInterceptHeading = null,
        };
        StartIn(ac, intercept);

        CommandResult result = Send(ac, "OC");

        Assert.False(result.Success);
        Assert.Equal("Unable, on the approach", result.Message);
        Assert.Same(intercept, ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void ProcedureTurnPhase_ThroughDispatch_UnableOnTheApproach()
    {
        AircraftState ac = OakSacLeg();
        var procedureTurn = new ProcedureTurnPhase
        {
            FixName = "SAC",
            FixLat = Fix("SAC").Lat,
            FixLon = Fix("SAC").Lon,
            InboundCourseDeg = 180,
            PtOutboundCourseDeg = 315,
            MaxOutboundDistanceNm = 10,
            OneEightyTurnDirection = TurnDirection.Right,
            MinAltitudeFt = 3_000,
        };
        StartIn(ac, procedureTurn);

        CommandResult result = Send(ac, "OC");

        Assert.False(result.Success);
        Assert.Equal("Unable, on the approach", result.Message);
        Assert.Same(procedureTurn, ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void InitialClimbPhase_AcceptsOnCourse() => Assert.True(new InitialClimbPhase().CanAcceptCommand(CanonicalCommandType.OnCourse).IsAllowed);

    [Fact]
    public void PlainHold_EndsTheHoldAndRejoinsTheRoute()
    {
        AircraftState ac = OakSacLeg();
        StartIn(ac, Hold(inLieu: false));

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.IsNotType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(["SAC", "RBL"], RouteNames(ac));
    }

    /// <summary>On the hold's outbound leg, flying away from the fix: OC still rejoins the route at the fix ahead along it.</summary>
    [Fact]
    public void PlainHold_OnTheOutboundLeg_RejoinsTheRoute()
    {
        AircraftState ac = Airborne(GeoMath.ProjectPoint(Fix("SAC"), new TrueHeading(180), 4), new TrueHeading(180), OakToRdd());
        StartIn(ac, Hold(inLieu: false));

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.IsNotType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(["SAC", "RBL"], RouteNames(ac));
    }

    private static AircraftState VfrPatternAircraft(out RunwayInfo runway)
    {
        runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var plan = new AircraftFlightPlan
        {
            FlightRules = "VFR",
            Departure = "KOAK",
            Destination = "KSJC",
        };
        AircraftState ac = Airborne(GeoMath.ProjectPoint(threshold, new TrueHeading(10), 1.5), runway.TrueHeading.ToReciprocal(), plan);
        ac.AircraftType = "C172";
        ac.Altitude = 1_000;
        ac.IndicatedAirspeed = 90;
        return ac;
    }

    [Fact]
    public void VfrPatternDownwind_LeavesThePatternDirectToTheDestination()
    {
        AircraftState ac = VfrPatternAircraft(out RunwayInfo runway);
        StartIn(ac, new DownwindPhase(), new PhaseList { AssignedRunway = runway });

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.IsNotType<DownwindPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal("KSJC", Assert.Single(ac.Targets.NavigationRoute).Name);
    }

    /// <summary>On the pattern's final the aircraft is committed to land: OC gets the pattern's short-final refusal.</summary>
    [Fact]
    public void VfrPatternFinal_RefusedWithTheShortFinalWording()
    {
        AircraftState ac = VfrPatternAircraft(out RunwayInfo runway);
        ac.Position = GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading.ToReciprocal(), 1);
        ac.TrueHeading = runway.TrueHeading;
        ac.TrueTrack = runway.TrueHeading;
        var final = new FinalApproachPhase();
        StartIn(ac, final, new PhaseList { AssignedRunway = runway, TrafficDirection = PatternDirection.Left });

        CommandResult result = Send(ac, "OC");

        Assert.False(result.Success);
        Assert.Equal("Unable, short final", result.Message);
        Assert.Same(final, ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void OnTheGround_IsRefusedInTheTerminalOnly()
    {
        AircraftState ac = OakSacLeg();
        ac.IsOnGround = true;
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(30);
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "SAC", Position = Fix("SAC") });

        CommandResult result = Send(ac, "OC");

        Assert.False(result.Success);
        Assert.True(result.IsOperatorGuidance);
        Assert.Equal("OC is an airborne command: UAL123 is on the ground", result.Message);
        Assert.Equal(["SAC"], RouteNames(ac));
        Assert.Equal(30, ac.Targets.AssignedMagneticHeading?.Degrees);
    }

    [Fact]
    public void MilitaryRouteActive_IsRefusedOnTheRoute()
    {
        AircraftState ac = OakSacLeg();
        ac.MilitaryRoute.Designator = "IR200";
        ac.MilitaryRoute.Status = MilitaryRouteStatus.ClearedIn;

        CommandResult result = Send(ac, "OC");

        Assert.False(result.Success);
        Assert.Equal("Unable, on the route", result.Message);
    }

    /// <summary>A CFIX speed held on a fix the new route drops reverts as a DCT's does: the pending 300 is back.</summary>
    [Fact]
    public void CfixOnADroppedFix_ItsSpeedRevertApplies()
    {
        var plan = new AircraftFlightPlan
        {
            HasFlightPlan = true,
            Departure = "KPHX",
            Destination = "KLAX",
            Route = "HEC",
        };
        AircraftState ac = Airborne(new LatLon(34.771654, -114.542825), new TrueHeading(270), plan);
        Assert.True(Send(ac, "SPD 300").Success);
        Assert.True(Send(ac, "CFIX PMD 110 250").Success);
        Assert.Equal(250, ac.Targets.AssignedSpeed);

        CommandResult result = Send(ac, "OC");

        Assert.True(result.Success, result.Message);
        Assert.Equal(["HEC"], RouteNames(ac));
        Assert.Equal(300, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void QueuedBehindACondition_ReDispatchesWhenItFires()
    {
        AircraftState ac = OakSacLeg();
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(10);
        Assert.True(Send(ac, "LV 14000 OC").Success);
        Assert.NotEmpty(ac.Queue.Blocks);

        CommandResult fired = ac.Queue.Blocks[^1].ApplyAction!(ac, true);

        Assert.True(fired.Success, fired.Message);
        Assert.Equal(["SAC", "RBL"], RouteNames(ac));
        Assert.Null(ac.Targets.AssignedMagneticHeading);
    }
}
