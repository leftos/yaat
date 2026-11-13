using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Phases.Approach;

/// <summary>
/// YAAT-146 (b), (c): an approach fix keeps its CIFP speed type, and a published "at or below" speed is a ceiling, never a
/// target. A slower aircraft is never accelerated toward it, a faster one slows late to cross at or below it, and the speed
/// the approach phase writes for a fix is the type's own speed capped at the fix's, which is also the follower's spacing
/// ceiling and what the lead-landed restore goes back to (7110.65 5-7-1.d and its NOTE, 5-7-1.g NOTE 3; AIM 4-4-12.g).
/// Real KOAK 28R, KOAK ILS 28R and KCVG ILS 18L navdata.
/// </summary>
public class ApproachFixSpeedTypeTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Subject = "TEST1";

    private static SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        return new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
    }

    private static RunwayInfo Oak28R() =>
        NavigationDatabase.Instance.GetRunway("KOAK", "28R") ?? throw new InvalidOperationException("KOAK 28R missing from navdata");

    /// <summary>A point <paramref name="alongNm"/> out the 28R final on its centerline.</summary>
    private static LatLon OnFinal(double alongNm)
    {
        RunwayInfo rwy = Oak28R();
        return GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal(), alongNm);
    }

    private static AircraftState MakeAircraft(string callsign, string type, LatLon position, TrueHeading heading, double altitude, double ias) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
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
    /// <paramref name="type"/> 4 nm short of VPCOL on the 28R final course, heading for it, cleared for the KOAK ILS 28R direct
    /// VPCOL by the real CAPP chain. Its first approach fix carries the real 200 kt at-or-below limit.
    /// </summary>
    private static AircraftState ClearedI28R(string type, double ias)
    {
        (double Lat, double Lon) fix = NavigationDatabase.Instance.GetFixPosition("VPCOL") ?? throw new InvalidOperationException("VPCOL missing");
        var vpcol = new LatLon(fix.Lat, fix.Lon);
        LatLon position = GeoMath.ProjectPoint(vpcol, Oak28R().TrueHeading.ToReciprocal(), 4.0);
        AircraftState ac = MakeAircraft(Subject, type, position, new TrueHeading(GeoMath.BearingTo(position, vpcol)), 3000, ias);
        ac.Declination = 13.0;
        ac.Procedure = new AircraftProcedure { DestinationRunway = null };
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "VPCOL", Position = vpcol });
        var cmd = new ClearedApproachCommand(
            "I28R",
            "KOAK",
            Force: false,
            AtFix: null,
            AtFixLat: null,
            AtFixLon: null,
            DctFix: "VPCOL",
            DctFixLat: fix.Lat,
            DctFixLon: fix.Lon,
            CrossFixAltitude: null,
            CrossFixAltType: null
        );
        CommandResult capp = ApproachCommandHandler.TryClearedApproach(cmd, ac);
        Assert.True(capp.Success, capp.Message);
        return ac;
    }

    /// <summary>Starts <paramref name="ac"/>'s phase list and advances it to its <see cref="ApproachNavigationPhase"/>.</summary>
    private static ApproachNavigationPhase StartApproachNav(AircraftState ac)
    {
        PhaseList phases = ac.Phases!;
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty);
        if (phases.CurrentPhase is { Status: PhaseStatus.Pending })
        {
            phases.Start(ctx);
        }

        while ((phases.CurrentPhase is not null) && (phases.CurrentPhase is not ApproachNavigationPhase))
        {
            phases.AdvanceToNext(ctx);
        }

        return Assert.IsType<ApproachNavigationPhase>(phases.CurrentPhase);
    }

    private static ApproachFix FixOnFinal(string name, double alongNm, int? speedKts)
    {
        LatLon fix = OnFinal(alongNm);
        return new ApproachFix(name, fix.Lat, fix.Lon, null, speedKts);
    }

    /// <summary>
    /// A B738 at 5,000 ft <paramref name="alongNm"/> out the 28R final, beyond the SFO Class B, under the real KOAK ILS 28R
    /// clearance, flying fixes built by hand: ALPHA 34 nm out, BRAVO 33 nm out with a 210 kt at-or-below limit, CHRLY 25 nm out.
    /// The real approach has no 210 kt limit, so the fixes are built by hand on the real runway's final.
    /// </summary>
    private static AircraftState AddJetOnFinalFixes(SimulationEngine engine, double alongNm, double ias) =>
        AddJetOnFinalFixes(engine, alongNm, ias, [FixOnFinal("ALPHA", 34.0, null), FixOnFinal("BRAVO", 33.0, 210), FixOnFinal("CHRLY", 25.0, null)]);

    /// <summary>As <see cref="AddJetOnFinalFixes(SimulationEngine, double, double)"/>, flying <paramref name="fixes"/>.</summary>
    private static AircraftState AddJetOnFinalFixes(SimulationEngine engine, double alongNm, double ias, List<ApproachFix> fixes)
    {
        RunwayInfo rwy = Oak28R();
        AircraftState jet = MakeAircraft(Subject, "B738", OnFinal(alongNm), rwy.TrueHeading, 5000, ias);
        ApproachClearance clearance = ClearedI28R("C172", 110).Phases?.ActiveApproach ?? throw new InvalidOperationException("no clearance");
        engine.World.AddAircraft(jet);
        jet.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = "28R",
            ActiveApproach = clearance,
        };
        jet.Phases.Add(new ApproachNavigationPhase { Fixes = fixes });
        jet.Phases.Add(new FinalApproachPhase());
        jet.Phases.Add(new LandingPhase());
        jet.Phases.Start(CommandDispatcher.BuildMinimalContext(jet, ActiveRunways.Empty));
        Assert.Equal(250.0, FlightPhysics.RegulatorySpeedLimit(jet));
        return jet;
    }

    private void TickSeconds(SimulationEngine engine, AircraftState ac, double seconds)
    {
        for (int i = 0; i < (int)(seconds * 4); i++)
        {
            engine.TickPhysics(0.25);
        }

        output.WriteLine($"{ac.Phases?.CurrentPhase?.Name}: IAS={ac.IndicatedAirspeed:F1} target={ac.Targets.TargetSpeed:F1}");
    }

    private static LatLon FixPosition(string name) =>
        NavigationDatabase.Instance.GetFixPosition(name) is { } fix
            ? new LatLon(fix.Lat, fix.Lon)
            : throw new InvalidOperationException($"{name} missing");

    /// <summary>The first fix of <paramref name="transition"/> on the real approach, and the course from it to the second.</summary>
    private static (LatLon Fix, TrueHeading Inbound) TransitionEntry(string airport, string approach, string transition)
    {
        CifpApproachProcedure procedure =
            NavigationDatabase.Instance.GetApproach(airport, approach) ?? throw new InvalidOperationException($"{airport} {approach} missing");
        CifpTransition legs = procedure.Transitions[transition];
        LatLon first = FixPosition(legs.Legs[0].FixIdentifier);
        return (first, new TrueHeading(GeoMath.BearingTo(first, FixPosition(legs.Legs[1].FixIdentifier))));
    }

    /// <summary>
    /// <paramref name="type"/> at 5,000 ft 4 nm before <paramref name="transition"/>'s first fix, heading for it, cleared for the
    /// real approach direct that fix by the real CAPP chain.
    /// </summary>
    private static AircraftState ClearedDirectTransition(string airport, string approach, string transition, string type, double ias)
    {
        (LatLon fix, TrueHeading inbound) = TransitionEntry(airport, approach, transition);
        AircraftState ac = MakeAircraft(Subject, type, GeoMath.ProjectPoint(fix, inbound.ToReciprocal(), 4.0), inbound, 5000, ias);
        ac.Procedure = new AircraftProcedure { DestinationRunway = null };
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = transition, Position = fix });
        var cmd = new ClearedApproachCommand(approach, airport, false, null, null, null, transition, fix.Lat, fix.Lon, null, null);
        CommandResult capp = ApproachCommandHandler.TryClearedApproach(cmd, ac);
        Assert.True(capp.Success, capp.Message);
        return ac;
    }

    /// <summary>
    /// <paramref name="type"/> at 5,000 ft 12 nm before <paramref name="transition"/>'s first fix on a route through a waypoint to
    /// it, cleared for the real approach, which defers to that fix and appends the approach's fixes after it.
    /// </summary>
    private static AircraftState PendingCapp(string airport, string approach, string transition, string type, double ias)
    {
        (LatLon fix, TrueHeading inbound) = TransitionEntry(airport, approach, transition);
        AircraftState ac = MakeAircraft(Subject, type, GeoMath.ProjectPoint(fix, inbound.ToReciprocal(), 12.0), inbound, 5000, ias);
        ac.FlightPlan.Destination = airport;
        ac.Procedure = new AircraftProcedure { DestinationRunway = null };
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "WPT01", Position = GeoMath.ProjectPoint(fix, inbound.ToReciprocal(), 6.0) });
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = transition, Position = fix });
        ac.Approach.Expected = approach;
        var cmd = new ClearedApproachCommand(approach, airport, false, null, null, null, null, null, null, null, null);
        CommandResult capp = ApproachCommandHandler.TryClearedApproach(cmd, ac);
        Assert.True(capp.Success, capp.Message);
        Assert.Equal(transition, ac.Approach.PendingClearance?.ConnectingFix);
        return ac;
    }

    /// <summary>A C172 at 110 kt on 5,000 ft starting a phase whose single fix 10 nm out the 28R final publishes <paramref name="speed"/>.</summary>
    private static AircraftState C172StartingAtFix(CifpSpeedRestriction speed)
    {
        TestVnasData.EnsureInitialized();
        AircraftState ac = MakeAircraft(Subject, "C172", OnFinal(14.0), Oak28R().TrueHeading, 5000, 110);
        ApproachFix fix = FixOnFinal("ALPHA", 10.0, speed.SpeedKts) with { SpeedType = speed.Type };
        new ApproachNavigationPhase { Fixes = [fix] }.OnStart(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        return ac;
    }

    private static double? SpacingCeiling(ApproachNavigationPhase nav) => ((ApproachNavigationPhaseDto)nav.ToSnapshot()).SpacingCeilingKts;

    /// <summary>A C172 at 110 kt reaching KOAK I28R's 200 kt fix is told its own speed, never the 200 kt above its envelope.</summary>
    [Fact]
    public void KoakI28R_C172_TargetSpeedAtTheFixSpeedFix_IsCappedAtTheTypeSpeed()
    {
        TestVnasData.EnsureInitialized();
        AircraftState ac = ClearedI28R("C172", 110);

        List<ApproachFix> fixes = [.. StartApproachNav(ac).Fixes.SkipWhile(f => f.SpeedKts is null)];
        Assert.Equal(("GROVE", 200), (fixes[0].Name, fixes[0].SpeedKts));

        // The phase from GROVE on, so the 200 kt fix is the one being flown to.
        new ApproachNavigationPhase { Fixes = fixes }.OnStart(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));

        double? target = ac.Targets.TargetSpeed;
        Assert.True(target is <= 110.0, $"target {target:F1} kt: above the C172's 110 kt segment speed (fix limit 200 kt)");
    }

    /// <summary>A B738 at 250 kt into a 210 kt maximum holds its speed until its slowdown point and crosses at or below 220 kt.</summary>
    [Fact]
    public void Jet250_Into210MaxFix_CrossesAtOrBelow220_AndDeceleratesLate()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState jet = AddJetOnFinalFixes(engine, 52.0, 250);
        var nav = (ApproachNavigationPhase)jet.Phases!.CurrentPhase!;
        LatLon bravo = OnFinal(33.0);
        double decelRate = AircraftPerformance.DecelRate("B738", AircraftCategorization.Categorize("B738"));
        double? slowdownStartNm = null;
        double slowdownWindowNm = 0;

        for (int i = 0; (i < 4 * 600) && (nav.CurrentFixIndex < 2); i++)
        {
            engine.TickPhysics(0.25);
            if ((slowdownStartNm is null) && (jet.IndicatedAirspeed < 248.0))
            {
                slowdownStartNm = GeoMath.DistanceNm(jet.Position, bravo);
                slowdownWindowNm = (250.0 - 210.0) / decelRate * (jet.GroundSpeed / 3600.0) * 1.1;
            }
        }

        output.WriteLine($"slowdown at {slowdownStartNm:F2} nm (window {slowdownWindowNm:F2} nm), crossed at {jet.IndicatedAirspeed:F1} kt");
        Assert.Equal(2, nav.CurrentFixIndex);
        Assert.True(jet.IndicatedAirspeed <= 220.0, $"crossed BRAVO at {jet.IndicatedAirspeed:F1} kt");
        Assert.NotNull(slowdownStartNm);
        Assert.True(slowdownStartNm <= (2.0 * slowdownWindowNm) + 0.5, $"slowed {slowdownStartNm:F2} nm out, window {slowdownWindowNm:F2} nm");
    }

    /// <summary>A B738 at 180 kt ahead of a 210 kt maximum keeps its own 180 kt: the limit is a ceiling, not a cue to speed up.</summary>
    [Fact]
    public void Jet180_Before210MaxFix_NeverAccelerates()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState jet = AddJetOnFinalFixes(engine, 40.0, 180);
        var nav = (ApproachNavigationPhase)jet.Phases!.CurrentPhase!;

        for (int i = 0; (i < 4 * 600) && (nav.CurrentFixIndex < 2); i++)
        {
            engine.TickPhysics(0.25);
            double? target = jet.Targets.TargetSpeed;
            Assert.True(jet.IndicatedAirspeed <= 181.0, $"tick {i}: IAS {jet.IndicatedAirspeed:F1} kt, target {target:F1} kt");
            Assert.True(target is null or <= 180.0, $"tick {i}: target {target:F1} kt above the 180 kt flown");
        }

        Assert.Equal(2, nav.CurrentFixIndex);
    }

    /// <summary>
    /// KCVG ILS 18L via SPUDR publishes a mandatory 210 kt at BAIRE (no qualifier): the fix ahead carries it as "at", not
    /// relabelled "at or below", and a B738 at 180 kt starting at BAIRE is told 210 kt, which it can fly. No approach in the
    /// committed CIFP publishes an "at or above" speed.
    /// </summary>
    [Fact]
    public void CifpAtAndAtOrAboveTypes_ArePreserved_NotRelabelled()
    {
        TestVnasData.EnsureInitialized();
        CifpApproachProcedure procedure =
            NavigationDatabase.Instance.GetApproach("KCVG", "I18L") ?? throw new InvalidOperationException("KCVG I18L missing");
        Assert.True(procedure.Transitions.TryGetValue("SPUDR", out CifpTransition? spudr), "no SPUDR transition");
        var published = new CifpSpeedRestriction(210, CifpSpeedRestrictionType.Mandatory);
        Assert.Equal(published, spudr.Legs.First(l => l.FixIdentifier == "BAIRE").Speed);
        List<ApproachFix> fixes = ApproachCommandHandler.BuildApproachFixesWithTransition(spudr, procedure);
        ApproachFix first = fixes[0];
        AircraftState ac = MakeAircraft(Subject, "B738", new LatLon(first.Latitude, first.Longitude), new TrueHeading(180), 5000, 180);
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty);

        new ApproachNavigationPhase { Fixes = fixes }.OnStart(ctx);

        Assert.NotEqual("BAIRE", first.Name);
        Assert.Equal(published, ac.Targets.NavigationRoute.First(t => t.Name == "BAIRE").SpeedRestriction);

        ac.Targets.TargetSpeed = null;
        new ApproachNavigationPhase { Fixes = [.. fixes.SkipWhile(f => f.Name != "BAIRE")] }.OnStart(ctx);

        Assert.Equal(210.0, ac.Targets.TargetSpeed);
    }

    /// <summary>A following C172 whose lead lands goes back to its capped ceiling, its own speed, not the fix's 200 kt.</summary>
    [Fact]
    public void LeadLandedRestore_C172_ReturnsToTheCappedCeiling()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = ClearedI28R("C172", 110);
        engine.World.AddAircraft(follower);
        ApproachNavigationPhase nav = StartApproachNav(follower);
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Approach.FollowingCallsign = Leader;
        AircraftState lead = MakeAircraft(
            Leader,
            "C172",
            GeoMath.ProjectPoint(follower.Position, follower.TrueHeading, 0.6),
            follower.TrueHeading,
            2000,
            90
        );
        engine.World.AddAircraft(lead);
        TickSeconds(engine, follower, 1.0);
        double? ceiling = SpacingCeiling(nav);
        Assert.True(ceiling is <= 110.0, $"ceiling {ceiling:F1} kt: above the C172's 110 kt (fix limit 200 kt)");
        Assert.True(follower.Targets.TargetSpeed < ceiling - 1.0, $"spacing target {follower.Targets.TargetSpeed:F1} kt: spacing has not run");

        lead.IsOnGround = true;
        TickSeconds(engine, follower, 0.25);

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Same(nav, follower.Phases!.CurrentPhase);
        Assert.Equal(ceiling, follower.Targets.TargetSpeed);
    }

    /// <summary>
    /// A B738 at 180 kt following a slower lead on KCVG ILS 18L via SPUDR never targets above its spacing ceiling for the 210 kt
    /// "at" BAIRE ahead: in-trail spacing outranks a mandatory approach speed (AIM 4-4-14.b).
    /// </summary>
    [Fact]
    public void Follower_KcvgI18LViaSpudr_MandatoryFixAhead_NeverTargetsAboveTheSpacingCeiling()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = ClearedDirectTransition("KCVG", "I18L", "SPUDR", "B738", 180);
        engine.World.AddAircraft(follower);
        ApproachNavigationPhase nav = StartApproachNav(follower);
        Assert.Contains(
            nav.Fixes.Skip(nav.CurrentFixIndex + 1),
            f => f.SpeedRestriction == new CifpSpeedRestriction(210, CifpSpeedRestrictionType.Mandatory)
        );
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Approach.FollowingCallsign = Leader;
        engine.World.AddAircraft(
            MakeAircraft(Leader, "B738", GeoMath.ProjectPoint(follower.Position, follower.TrueHeading, 2.5), follower.TrueHeading, 5000, 150)
        );

        for (int i = 0; i < 4 * 20; i++)
        {
            engine.TickPhysics(0.25);
            double ceiling = SpacingCeiling(nav) ?? 180.0;
            double? target = follower.Targets.TargetSpeed;
            Assert.True(target is null || (target <= ceiling), $"tick {i}: target {target:F1} kt above the {ceiling:F1} kt spacing ceiling");
            Assert.True(follower.IndicatedAirspeed <= 181.0, $"tick {i}: IAS {follower.IndicatedAirspeed:F1} kt");
        }
    }

    /// <summary>
    /// A controller's crossing speed is an assignment, not a published maximum: a B738 at 180 kt with an approach clearance in
    /// force (as after a go-around) told to cross SUNOL at 230 kt speeds up to it.
    /// </summary>
    [Fact]
    public void ControllerCrossingSpeed_WithApproachClearance_IsTargeted()
    {
        SimulationEngine engine = BuildEngine();
        ApproachClearance clearance = ClearedI28R("C172", 110).Phases?.ActiveApproach ?? throw new InvalidOperationException("no clearance");
        LatLon sunol = FixPosition("SUNOL");
        LatLon start = GeoMath.ProjectPoint(sunol, new TrueHeading(90), 15.0);
        AircraftState ac = MakeAircraft(Subject, "B738", start, new TrueHeading(GeoMath.BearingTo(start, sunol)), 5000, 180);
        ac.Phases = new PhaseList { ActiveApproach = clearance };
        engine.World.AddAircraft(ac);

        CommandResult cfix = CommandDispatcher.Dispatch(
            new CrossFixCommand("SUNOL", sunol.Lat, sunol.Lon, 5000, CrossFixAltitudeType.At, 230),
            ac,
            TestDispatch.Context(Random.Shared)
        );
        Assert.True(cfix.Success, cfix.Message);
        TickSeconds(engine, ac, 1.0);

        Assert.Equal(230.0, ac.Targets.TargetSpeed);
    }

    /// <summary>
    /// A B738 at 180 kt before a 210 kt maximum and a 230 kt "at" beyond it never crosses the maximum above 220 kt: a later
    /// planned speed is capped at the lowest skipped maximum ahead of it.
    /// </summary>
    [Fact]
    public void Jet180_Before210MaxThenAt230_CrossesTheMaxAtOrBelow220()
    {
        SimulationEngine engine = BuildEngine();
        List<ApproachFix> fixes =
        [
            FixOnFinal("ALPHA", 33.3, null),
            FixOnFinal("BRAVO", 33.0, 210),
            FixOnFinal("CHRLY", 30.0, 230) with
            {
                SpeedType = CifpSpeedRestrictionType.Mandatory,
            },
            FixOnFinal("DELTA", 25.0, null),
        ];
        AircraftState jet = AddJetOnFinalFixes(engine, 40.0, 180, fixes);
        var nav = (ApproachNavigationPhase)jet.Phases!.CurrentPhase!;

        for (int i = 0; (i < 4 * 600) && (nav.CurrentFixIndex < 2); i++)
        {
            engine.TickPhysics(0.25);
        }

        Assert.Equal(2, nav.CurrentFixIndex);
        Assert.True(jet.IndicatedAirspeed <= 220.0, $"crossed BRAVO at {jet.IndicatedAirspeed:F1} kt");
    }

    /// <summary>
    /// A B738 at 180 kt cleared for KOAK ILS 28R ahead of MYSHN on its route: the approach defers to MYSHN, and its GROVE 200 kt
    /// maximum appended to the route is a ceiling, never a cue to speed up.
    /// </summary>
    [Fact]
    public void DeferredCapp_SlowerAircraft_NeverAcceleratesTowardAnAppendedMax()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = PendingCapp("KOAK", "I28R", "MYSHN", "B738", 180);
        Assert.Contains(ac.Targets.NavigationRoute, t => t.SpeedRestriction == new CifpSpeedRestriction(200, CifpSpeedRestrictionType.AtOrBelow));
        engine.World.AddAircraft(ac);

        for (int i = 0; i < 4 * 20; i++)
        {
            engine.TickPhysics(0.25);
            double? target = ac.Targets.TargetSpeed;
            Assert.True(target is null or <= 180.0, $"tick {i}: target {target:F1} kt above the 180 kt flown");
        }

        Assert.NotNull(ac.Approach.PendingClearance);
    }

    /// <summary>A fix's maximum above the airspeed pulls a higher target down to it at once and leaves a lower one alone.</summary>
    [Fact]
    public void ApplyFixConstraints_MaxAboveAirspeed_PullsAHigherTargetDownToIt()
    {
        TestVnasData.EnsureInitialized();
        AircraftState ac = ClearedI28R("B738", 180);
        var fix = new NavigationTarget
        {
            Name = "ALPHA",
            Position = OnFinal(20.0),
            SpeedRestriction = new CifpSpeedRestriction(210, CifpSpeedRestrictionType.AtOrBelow),
        };

        ac.Targets.TargetSpeed = 250;
        FlightPhysics.ApplyFixConstraints(ac, fix);
        Assert.Equal(210.0, ac.Targets.TargetSpeed);

        ac.Targets.TargetSpeed = 190;
        FlightPhysics.ApplyFixConstraints(ac, fix);
        Assert.Equal(190.0, ac.Targets.TargetSpeed);
    }

    /// <summary>A C172 told "at 210" by a fix is unable above its maximum operating speed and flies its own 110 kt (7110.65 5-7-1).</summary>
    [Fact]
    public void C172_At210Fix_FliesItsOwnSpeed()
    {
        TestVnasData.EnsureInitialized();
        Assert.True(AircraftPerformance.MaxOperatingIasKts("C172", 5000) < 210.0, "the C172 can fly 210 kt");

        AircraftState ac = C172StartingAtFix(new CifpSpeedRestriction(210, CifpSpeedRestrictionType.Mandatory));

        Assert.Equal(110.0, ac.Targets.TargetSpeed);
    }

    /// <summary>An "at or above" fix speed is a floor under the type's own speed: 120 kt lifts a C172 at 110 kt, 90 kt does not slow it.</summary>
    [Fact]
    public void AtOrAboveFix_IsAFloorUnderTheOwnSpeed()
    {
        AircraftState above = C172StartingAtFix(new CifpSpeedRestriction(120, CifpSpeedRestrictionType.AtOrAbove));
        AircraftState below = C172StartingAtFix(new CifpSpeedRestriction(90, CifpSpeedRestrictionType.AtOrAbove));

        Assert.Equal(120.0, above.Targets.TargetSpeed);
        Assert.Equal(110.0, below.Targets.TargetSpeed);
    }

    /// <summary>KMJD RNAV (GPS) Y 18's missed approach publishes 185 kt "at" CIQYI; the missed approach fix keeps the type.</summary>
    [Fact]
    public void MissedApproachFix_KeepsItsCifpSpeedType()
    {
        TestVnasData.EnsureInitialized();
        CifpApproachProcedure procedure =
            NavigationDatabase.Instance.GetApproach("KMJD", "R18-Y") ?? throw new InvalidOperationException("KMJD R18-Y missing");

        List<ApproachFix> missed = ApproachCommandHandler.BuildMissedApproachFixes(procedure);

        Assert.Equal(new CifpSpeedRestriction(185, CifpSpeedRestrictionType.Mandatory), missed.First(f => f.Name == "CIQYI").SpeedRestriction);
    }

    /// <summary>A deferred KCVG ILS 18L via SPUDR appends BAIRE to the route with its published 210 kt "at", not "at or below".</summary>
    [Fact]
    public void DeferredCapp_AppendedFix_KeepsItsCifpSpeedType()
    {
        TestVnasData.EnsureInitialized();
        AircraftState ac = PendingCapp("KCVG", "I18L", "SPUDR", "B738", 180);

        Assert.Equal(
            new CifpSpeedRestriction(210, CifpSpeedRestrictionType.Mandatory),
            ac.Targets.NavigationRoute.First(t => t.Name == "BAIRE").SpeedRestriction
        );
    }

    /// <summary>The cleared approach's published speeds a crossing restore reads back keep their CIFP type: BAIRE's 210 kt "at".</summary>
    [Fact]
    public void ClearedApproachSpeeds_KeepTheCifpType()
    {
        TestVnasData.EnsureInitialized();
        AircraftState ac = PendingCapp("KCVG", "I18L", "SPUDR", "B738", 180);

        Dictionary<string, CifpSpeedRestriction> speeds = NavigationCommandHandler.ClearedApproachSpeeds(ac);

        Assert.Equal(new CifpSpeedRestriction(210, CifpSpeedRestrictionType.Mandatory), speeds["BAIRE"]);
    }
}
