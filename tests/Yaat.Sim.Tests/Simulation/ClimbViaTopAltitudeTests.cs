using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Speech;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A bare "climb via SID" cancels an earlier interim altitude and the aircraft climbs to the SID's top altitude
/// (YAAT-958; 7110.65 4-3-2, 4-5-7.h.10 Example 1, h.11 NOTE 1; AIM 5-2-9.h.8-9; P/CG TOP ALTITUDE). The top altitude is
/// the "except maintain" altitude; else the TDLS climb-via text's "EXC MAINT" altitude; else the TDLS initial altitude;
/// else the highest altitude the SID publishes on its flown legs. A TDLS value is never raised by a published altitude;
/// the top is never filed cruise, the CTO altitude or the TDLS initial-altitude menu, and never a descent. The climb goes
/// on to it past every at-or-above restriction, held only under a ceiling still ahead. CVIA also drops pending CFIX
/// reverts and restores the published restrictions, keeping a CFIX restated in its own transmission. Real KOAK / KSFO /
/// KSTS navdata and the committed ZOA ARTCC snapshot.
/// </summary>
public class ClimbViaTopAltitudeTests
{
    private const double LevelToleranceFt = 50.0;
    private const double WindowCeilingFt = 2000.0;

    /// <summary>Mirrors <c>FlightPhysics.ClimbViaTopReachedToleranceFt</c>: the band in which the climb via counts as reached.</summary>
    private const double ViaTopToleranceFt = 20.0;

    /// <summary>CNDEL5's last published ceiling is PORTE at or below 10,000; the fixture's climb via tops out at 12,000.</summary>
    private const int Cndel5ViaTopFt = 12000;

    /// <summary>COAST9's top altitude: every COAST9 transition's TDLS climb-via text at KOAK is "CLB VIA SID EXC MAINT 10000FT".</summary>
    private const int Coast9TopAltitudeFt = 10000;

    /// <summary>The first entry of KOAK's TDLS initial-altitude menu in the ZOA snapshot: what CTO stores for every OAK SID.</summary>
    private const int OakMenuInitialAltitudeFt = 2000;

    private static readonly LatLon KoakPosition = new(37.7212, -122.2208);
    private static readonly LatLon KsfoPosition = new(37.6189, -122.3750);

    private readonly ITestOutputHelper _output;

    public ClimbViaTopAltitudeTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private sealed record Departure(AircraftState Aircraft, PhaseContext Context, RunwayInfo Runway);

    /// <summary>
    /// A KOAK RWY30 departure <see cref="StartKoak30Departure"/> stages: filed <paramref name="Route"/> to
    /// <paramref name="Destination"/>, cleared for takeoff with <paramref name="Instruction"/> and <paramref name="CtoAltitude"/>
    /// or none (after a bare climb via in position with <paramref name="ClimbViaOnTheGround"/>), and put
    /// <paramref name="HeightAglFt"/> above the field.
    /// </summary>
    private sealed record KoakDeparture(
        string Destination,
        string Route,
        int? CtoAltitude,
        bool ClimbViaOnTheGround,
        DepartureInstruction Instruction,
        double HeightAglFt
    );

    private sealed record FlightResult(double MinAltitude, double MaxAltitude, bool SawDepartureProcedure);

    private sealed record Harness(SimulationEngine Engine, AircraftState Aircraft);

    [Fact]
    public void BareClimbVia_AfterInterimClimb_ClimbsToSidTopAltitude_NotCruise()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        int top = Coast9TopAltitudeFt;

        Dispatch(ac, "CM 080");
        double altitudeAtCvia = ac.Altitude;
        Dispatch(ac, "CVIA");

        int expected = Math.Max(top, RoundUpToHundred(altitudeAtCvia));
        Assert.Null(ac.Targets.AssignedAltitude);
        Assert.Equal(expected, ac.Procedure.ClimbViaTargetFt);

        FlightResult flight = FlyFor(departure, 600);
        _output.WriteLine($"top={top} expected={expected} maxAlt={flight.MaxAltitude:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(
            flight.MaxAltitude < expected + LevelToleranceFt,
            $"after a bare CVIA the aircraft kept climbing toward the interim 8,000 or cruise: reached {flight.MaxAltitude:F0}"
        );
        Assert.InRange(ac.Altitude, expected - LevelToleranceFt, expected + LevelToleranceFt);
        Assert.Null(ac.Targets.AssignedAltitude);
    }

    [Fact]
    public void ClimbViaExceptMaintain_LevelsAtThatAltitude()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;

        Dispatch(ac, "CVIA 120");
        Assert.Equal(12000, ac.Procedure.ClimbViaTargetFt);

        FlightResult flight = FlyFor(departure, 600);
        _output.WriteLine($"maxAlt={flight.MaxAltitude:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(flight.MaxAltitude < 12000 + LevelToleranceFt, $"climbed through the except-maintain 12,000: {flight.MaxAltitude:F0}");
        Assert.InRange(ac.Altitude, 12000 - LevelToleranceFt, 12000 + LevelToleranceFt);
        Assert.Null(ac.Targets.AssignedAltitude);
    }

    [Fact]
    public void ClimbVia_AboveTopAltitude_NeverDescends()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        int top = Coast9TopAltitudeFt;

        Dispatch(ac, "CM 140");
        FlyUntilAbove(departure, top + 1500, 600);
        double altitudeAtCvia = ac.Altitude;
        Dispatch(ac, "CVIA");

        int expected = RoundUpToHundred(altitudeAtCvia);
        Assert.Equal(expected, ac.Procedure.ClimbViaTargetFt);

        FlightResult flight = FlyFor(departure, 180);
        _output.WriteLine($"altAtCvia={altitudeAtCvia:F0} min={flight.MinAltitude:F0} max={flight.MaxAltitude:F0} final={ac.Altitude:F0}");

        Assert.True(flight.MinAltitude >= altitudeAtCvia - LevelToleranceFt, $"CVIA sent the aircraft down to {flight.MinAltitude:F0}");
        Assert.True(flight.MaxAltitude < expected + LevelToleranceFt, $"CVIA kept the aircraft climbing toward 14,000: {flight.MaxAltitude:F0}");
        Assert.InRange(ac.Altitude, expected - LevelToleranceFt, expected + LevelToleranceFt);
    }

    /// <summary>
    /// Santa Rosa (KSTS) has no TDLS configuration in ZOA, so CHRRO1 has no TDLS value: a bare CVIA climbs to the highest
    /// altitude CHRRO1 publishes on its flown legs (ENRQZ at or below 15,000 on the RBL transition), past the interim 10,000.
    /// </summary>
    [Fact]
    public void ClimbVia_SidWithoutTdlsValue_ClimbsToHighestPublishedAltitude()
    {
        ArtccConfigRoot zoa = LoadZoa();
        Assert.Null(zoa.GetSidTdlsTopAltitude("KSTS", "CHRRO1", "RBL"));
        CifpSidProcedure sid = Assert.IsType<CifpSidProcedure>(NavigationDatabase.Instance.GetSid("KSTS", "CHRRO1"));
        CifpTransition rbl = sid.EnrouteTransitions["RBL"];
        int highest = HighestPublishedFt(sid.RunwayTransitions.Values.SelectMany(t => t.Legs).Concat(sid.CommonLegs).Concat(rbl.Legs));
        Assert.True(highest > 10000, $"setup: CHRRO1's highest published altitude {highest} must sit above the interim 10,000");

        LatLon enrqz = FixPosition("ENRQZ");
        LatLon rblFix = FixPosition("RBL");
        var ac = new AircraftState
        {
            Callsign = "SKW3301",
            AircraftType = "B738",
            Position = enrqz,
            TrueHeading = new TrueHeading(GeoMath.BearingTo(enrqz, rblFix)),
            TrueTrack = new TrueHeading(GeoMath.BearingTo(enrqz, rblFix)),
            Altitude = 9000,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSTS",
                Destination = "KRDD",
                Route = "CHRRO1 RBL",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
        };
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "RBL", Position = rblFix });
        DispatchContext ctx = TestDispatch.Context(Random.Shared, artccConfig: zoa);

        DispatchLine(ac, "CM 100", ctx);
        DispatchLine(ac, "CVIA", ctx);

        Assert.Equal("CHRRO1", ac.Procedure.ActiveSidId);
        Assert.Equal(highest, ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Targets.AssignedAltitude);

        for (int t = 0; t < 600; t++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        _output.WriteLine($"highest={highest} finalAlt={ac.Altitude:F0}");
        Assert.InRange(ac.Altitude, highest - LevelToleranceFt, highest + LevelToleranceFt);
    }

    /// <summary>
    /// KIAH's FLYZA5 publishes up to 7,000 on its common route and 11,000 on its enroute transitions. Filed "FLYZA5" with no
    /// transition, and with no fix of a transition on its live route, the flown transition cannot be identified: a bare CVIA
    /// with no TDLS value climbs to the highest altitude the runway transitions and common route publish, never to a
    /// transition's altitude the aircraft is not flying.
    /// </summary>
    [Fact]
    public void ClimbVia_NoTransitionIdentified_ClimbsToTheCommonRouteTop_NotATransitionsTop()
    {
        CifpSidProcedure sid = Assert.IsType<CifpSidProcedure>(NavigationDatabase.Instance.GetSid("KIAH", "FLYZA5"));
        int commonTop = HighestPublishedFt(sid.RunwayTransitions.Values.SelectMany(t => t.Legs).Concat(sid.CommonLegs));
        int transitionTop = HighestPublishedFt(sid.EnrouteTransitions.Values.SelectMany(t => t.Legs));
        Assert.True(transitionTop > commonTop, $"setup: a FLYZA5 transition must publish above the common route ({transitionTop} vs {commonTop})");

        LatLon position = FixPosition(sid.CommonLegs.First(l => !string.IsNullOrEmpty(l.FixIdentifier)).FixIdentifier);
        var ac = new AircraftState
        {
            Callsign = "UAL1402",
            AircraftType = "B738",
            Position = position,
            Altitude = 3000,
            IndicatedAirspeed = 230,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KIAH",
                Destination = "KLAX",
                Route = "FLYZA5",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
        };
        ac.Procedure.ActiveSidId = sid.ProcedureId;
        ac.Procedure.SidViaMode = true;

        DispatchLine(ac, "CVIA", TestDispatch.Context(Random.Shared));

        _output.WriteLine($"commonTop={commonTop} transitionTop={transitionTop} climbViaTarget={ac.Procedure.ClimbViaTargetFt}");
        Assert.Equal(commonTop, ac.Procedure.ClimbViaTargetFt);
    }

    /// <summary>
    /// The filed NUEVO8 resolves to the current version in navdata, while the ZOA snapshot's TDLS entry is still NUEVO8: its
    /// SNS transition carries "CLB VIA SID EXC MAINT 10000FT" in each of KOAK's op configs, above anything the SID publishes.
    /// A bare CVIA climbs to it, not to the 2,000 CTO stored from KOAK's menu.
    /// </summary>
    [Fact]
    public void ClimbVia_OakSidWithTdlsValue_ClimbsToThatValue_NotTheMenuAltitude()
    {
        AircraftState ac = SidDeparture("KOAK", "NUEVO8", "RW30", "SNS", "SAPLY", 3000, sidAlreadyActive: false);
        int published = HighestPublishedFt(FlownLegs("KOAK", "NUEVO8", "RW30", "SNS"));
        Assert.True(published < 10000, $"setup: NUEVO8 SNS must publish nothing as high as its 10,000 TDLS value, got {published}");
        ac.Procedure.SidInitialAltitudeFt = OakMenuInitialAltitudeFt;

        DispatchLine(ac, "CVIA", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));

        Assert.Equal(10000, ac.Procedure.ClimbViaTargetFt);
    }

    /// <summary>
    /// CNDEL5's TDLS climb-via text at KOAK is a plain "CLIMB VIA SID", with no initial altitude: a bare CVIA climbs to the
    /// highest altitude its flown legs publish (PORTE at or below 10,000), not to the 2,000 at the top of KOAK's menu.
    /// </summary>
    [Fact]
    public void ClimbVia_OakSidWithoutTdlsValue_ClimbsToHighestPublished_NotTheMenuAltitude()
    {
        AircraftState ac = SidDeparture("KOAK", "CNDEL5", "RW30", "SUSEY", "LEJAY", 3000, sidAlreadyActive: false);
        int published = HighestPublishedFt(FlownLegs("KOAK", "CNDEL5", "RW30", "SUSEY"));
        Assert.Equal(10000, published);
        ac.Procedure.SidInitialAltitudeFt = OakMenuInitialAltitudeFt;

        DispatchLine(ac, "CVIA", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));

        Assert.Equal(published, ac.Procedure.ClimbViaTargetFt);
    }

    /// <summary>
    /// HUSSH2 TIPRE publishes REBAS at or above 8,000. A "CVIA 120" issued below it climbs through REBAS on to 12,000: an
    /// at-or-above is a floor the climb meets, never a level-off (7110.65 4-5-7.h.10 NOTEs; AIM 5-2-9 Examples 6-7).
    /// </summary>
    [Fact]
    public void ClimbViaExceptMaintain_PastAtOrAboveRestriction_LevelsAtTheExceptMaintainAltitude()
    {
        AircraftState ac = SidDeparture("KOAK", "HUSSH2", "RW30", "TIPRE", "HUSSH", 3000, sidAlreadyActive: false);
        DispatchLine(ac, "CVIA 120", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));
        Assert.Equal(new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 8000), RouteFix(ac, "REBAS").AltitudeRestriction);

        double atRebas = FlyPastFix(ac, "REBAS", 900);
        FlyPhysics(ac, 600);
        _output.WriteLine($"atRebas={atRebas:F0} finalAlt={ac.Altitude:F0}");

        Assert.InRange(ac.Altitude, 12000 - LevelToleranceFt, 12000 + LevelToleranceFt);
    }

    /// <summary>
    /// SFO's SEGUL1 from runways 28 publishes SEGUL at or above 16,000, and its YYUNG transition CYPRS at or above FL220; its
    /// TDLS climb-via text at KSFO is "CLB VIA SID EXC MAINT 10000FT". A bare CVIA climbs to 10,000 and levels there, short of
    /// SEGUL's and CYPRS' floors: a published floor above the top never raises it (AIM 5-2-9 Example 4 NOTE).
    /// </summary>
    [Fact]
    public void BareClimbVia_TdlsExceptMaintain_LevelsThereBelowAHigherPublishedFloor()
    {
        AircraftState ac = SidDeparture("KSFO", "SEGUL1", "RW28B", "YYUNG", "SENZY", 3000, sidAlreadyActive: false);
        Assert.Equal(22000, HighestPublishedFt(FlownLegs("KSFO", "SEGUL1", "RW28B", "YYUNG")));

        DispatchLine(ac, "CVIA", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));
        Assert.Equal(10000, ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 16000), RouteFix(ac, "SEGUL").AltitudeRestriction);
        Assert.Equal(new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 22000), RouteFix(ac, "CYPRS").AltitudeRestriction);

        double atSegul = FlyPastFix(ac, "SEGUL", 900);
        double atCyprs = FlyPastFix(ac, "CYPRS", 1800);
        _output.WriteLine($"atSegul={atSegul:F0} atCyprs={atCyprs:F0} finalAlt={ac.Altitude:F0}");

        Assert.InRange(atSegul, 10000 - LevelToleranceFt, 10000 + LevelToleranceFt);
        Assert.InRange(atCyprs, 10000 - LevelToleranceFt, 10000 + LevelToleranceFt);
    }

    /// <summary>
    /// SFO's MOLEN9 carries "CLB VIA SID EXC MAINT 15000FT" on its ENI transition and "... 3000FT" on its "- - - -"
    /// (no-transition) entry, in each op config of the ZOA snapshot: a bare CVIA climbs to the filed transition's value,
    /// else the no-transition entry's.
    /// </summary>
    [Theory]
    [InlineData("MOLEN9 ENI", 15000)]
    [InlineData("MOLEN9", 3000)]
    public void BareClimbVia_TdlsExceptMaintain_FiledTransitionElseNoTransitionEntry(string route, int expectedTop)
    {
        LatLon ksfo = KsfoPosition;
        var ac = new AircraftState
        {
            Callsign = "UAL1734",
            AircraftType = "B738",
            Position = ksfo,
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = 2000,
            IndicatedAirspeed = 200,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "KRNO",
                Route = route,
                Altitude = PlannedAltitude.Ifr(33000),
                FlightRules = "IFR",
            },
        };
        ac.Procedure.ActiveSidId = "MOLEN9";
        ac.Procedure.SidViaMode = true;

        DispatchLine(ac, "CVIA", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));

        Assert.Equal(expectedTop, ac.Procedure.ClimbViaTargetFt);
    }

    /// <summary>
    /// AIRAC 2611 renumbered KOAK's NUEVO SID to NUEVO9 while the ZOA snapshot and the filed route still name NUEVO8, and
    /// SFO's filed route still names MOLEN8 while the active SID and the snapshot name MOLEN9. A bare CVIA still climbs to the
    /// TDLS top: the SID's entry by exact name first, else the same base name without its trailing version digits.
    /// </summary>
    [Theory]
    [InlineData("KOAK", "NUEVO8 SNS", "NUEVO9", "KLAX", 10000)]
    [InlineData("KSFO", "MOLEN8 ENI", "MOLEN9", "KRNO", 15000)]
    public void BareClimbVia_ActiveSidVersionDiffersFromTheFiledAndTdlsName_ClimbsToTheTdlsValue(
        string departure,
        string route,
        string activeSidId,
        string destination,
        int expectedTop
    )
    {
        LatLon position = departure switch
        {
            "KOAK" => KoakPosition,
            "KSFO" => KsfoPosition,
            _ => throw new ArgumentOutOfRangeException(nameof(departure), departure, "no position for this departure airport"),
        };
        var ac = new AircraftState
        {
            Callsign = "UAL1734",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = 3000,
            IndicatedAirspeed = 200,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = departure,
                Destination = destination,
                Route = route,
                Altitude = PlannedAltitude.Ifr(33000),
                FlightRules = "IFR",
            },
        };
        ac.Procedure.ActiveSidId = activeSidId;
        ac.Procedure.SidViaMode = true;

        DispatchLine(ac, "CVIA", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));

        Assert.Equal(expectedTop, ac.Procedure.ClimbViaTargetFt);
    }

    /// <summary>
    /// CNDEL5 publishes PORTE at or below 10,000: a "CVIA 120" holds at or below 10,000 to PORTE, then climbs on to 12,000.
    /// </summary>
    [Fact]
    public void ClimbViaExceptMaintain_UnderCeilingAhead_HoldsItThenLevelsAtTheExceptMaintainAltitude()
    {
        AircraftState ac = SidDeparture("KOAK", "CNDEL5", "RW30", "SUSEY", "LEJAY", 3000, sidAlreadyActive: false);
        DispatchLine(ac, "CVIA 120", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));
        Assert.Equal(new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrBelow, 10000), RouteFix(ac, "PORTE").AltitudeRestriction);

        double atPorte = FlyPastFix(ac, "PORTE", 900);
        FlyPhysics(ac, 600);
        _output.WriteLine($"atPorte={atPorte:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(atPorte <= 10000 + LevelToleranceFt, $"crossed PORTE above its at-or-below 10,000: {atPorte:F0}");
        Assert.InRange(ac.Altitude, 12000 - LevelToleranceFt, 12000 + LevelToleranceFt);
    }

    /// <summary>
    /// A climb via whose route ends at the SID's last at-or-below ceiling stays in force past that fix: the ceiling applied
    /// only up to the fix, and the aircraft climbs on to the via's top altitude (AIM 5-2-9.h.8; P/CG TOP ALTITUDE;
    /// 7110.65 4-5-7.h). CNDEL5 publishes PORTE at or below 10,000 on its last constrained fix; the via tops out at 12,000.
    /// </summary>
    [Fact]
    public void ClimbVia_RouteEndsAtCeilingFix_ClimbsOnToTheViaTop()
    {
        AircraftState ac = Cndel5RouteEndingAtCeilingFix(out string ceilingFix, out int ceilingFt);
        DispatchLine(ac, "CVIA 120", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));
        int viaTop = Assert.NotNull(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(Cndel5ViaTopFt, viaTop);
        Assert.True(viaTop > ceilingFt, $"setup: the via top {viaTop:N0} must sit above {ceilingFix}'s {ceilingFt:N0} ceiling");
        Assert.Equal(new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrBelow, ceilingFt), RouteFix(ac, ceilingFix).AltitudeRestriction);

        double atFix = FlyPastFix(ac, ceilingFix, 900);
        _output.WriteLine($"at {ceilingFix}={atFix:F0} viaTop={viaTop}");
        Assert.True(atFix <= ceilingFt + LevelToleranceFt, $"crossed {ceilingFix} at {atFix:F0}, above its {ceilingFt:N0} ceiling");
        Assert.NotNull(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(Cndel5ProcedureId(), ac.Procedure.ActiveSidId);

        FlyPhysics(ac, 900);
        _output.WriteLine($"finalAlt={ac.Altitude:F0} via={ac.Procedure.ClimbViaTargetFt}");

        Assert.InRange(ac.Altitude, viaTop - ViaTopToleranceFt, viaTop + ViaTopToleranceFt);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.False(ac.Procedure.SidViaMode);
        Assert.Null(ac.Procedure.SidViaCeiling);
        Assert.Null(ac.Procedure.ActiveSidId);
        Assert.Equal(Cndel5ViaTopFt, ac.Targets.AssignedAltitude);

        FlyPhysics(ac, 300);
        _output.WriteLine($"held={ac.Altitude:F0}");
        Assert.InRange(ac.Altitude, viaTop - ViaTopToleranceFt, viaTop + ViaTopToleranceFt);
    }

    /// <summary>A "maintain" after the route's end replaces the climb via, so the aircraft levels at the assigned 8,000.</summary>
    [Fact]
    public void ClimbVia_RouteEndsAtCeilingFix_CmAfterTheFixHoldsIt()
    {
        AircraftState ac = Cndel5RouteEndingAtCeilingFix(out string ceilingFix, out _);
        DispatchContext ctx = TestDispatch.Context(Random.Shared, artccConfig: LoadZoa());
        DispatchLine(ac, "CVIA 120", ctx);
        Assert.NotNull(ac.Procedure.ClimbViaTargetFt);

        FlyPastFix(ac, ceilingFix, 900);
        Assert.NotNull(ac.Procedure.ClimbViaTargetFt);

        DispatchLine(ac, "CM 080", ctx);
        FlyPhysics(ac, 900);
        _output.WriteLine($"finalAlt={ac.Altitude:F0} assigned={ac.Targets.AssignedAltitude} via={ac.Procedure.ClimbViaTargetFt}");

        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.False(ac.Procedure.SidViaMode);
        Assert.Equal(8000, ac.Targets.AssignedAltitude);
        Assert.InRange(ac.Altitude, 8000 - ViaTopToleranceFt, 8000 + ViaTopToleranceFt);
    }

    /// <summary>
    /// A "maintain" with no climb via on the same route: with no climb via in force nothing is kept at the route's end —
    /// the no-via branch owns that, pinned by <c>ProcedureConstraintTests.ProcedureState_ClearedOnRouteCompletion</c> — and
    /// the aircraft levels at its assigned 5,000 rather than at the 10,000 ceiling.
    /// </summary>
    [Fact]
    public void CmWithoutClimbVia_RouteEndsAtCeilingFix_LevelsAtTheAssignedAltitude()
    {
        AircraftState ac = Cndel5RouteEndingAtCeilingFix(out string ceilingFix, out int ceilingFt);
        DispatchLine(ac, "CM 050", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));
        Assert.Null(ac.Procedure.ClimbViaTargetFt);

        double atFix = FlyPastFix(ac, ceilingFix, 1800);
        FlyPhysics(ac, 300);
        _output.WriteLine($"at {ceilingFix}={atFix:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(atFix <= ceilingFt + LevelToleranceFt, $"crossed {ceilingFix} at {atFix:F0}, above its {ceilingFt:N0} ceiling");
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(5000, ac.Targets.AssignedAltitude);
        Assert.InRange(ac.Altitude, 5000 - ViaTopToleranceFt, 5000 + ViaTopToleranceFt);
    }

    /// <summary>A snapshot taken mid-climb after the route's end keeps the climb via, and the restored aircraft levels at the top too.</summary>
    [Fact]
    public void ClimbVia_RouteEndsAtCeilingFix_SnapshotMidClimb_StillLevelsAtTheViaTop()
    {
        AircraftState ac = Cndel5RouteEndingAtCeilingFix(out string ceilingFix, out _);
        DispatchLine(ac, "CVIA 120", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));
        int viaTop = Assert.NotNull(ac.Procedure.ClimbViaTargetFt);

        FlyPastFix(ac, ceilingFix, 900);
        FlyPhysics(ac, 15);
        Assert.NotNull(ac.Procedure.ClimbViaTargetFt);
        Assert.True(ac.Altitude < viaTop - 500, $"setup: the snapshot must be taken mid-climb, at {ac.Altitude:F0} of {viaTop:N0}");

        AircraftSnapshotDto snapshot = ac.ToSnapshot();
        var restored = AircraftState.FromSnapshot(snapshot, groundLayout: null);
        Assert.Equal(Cndel5ViaTopFt, restored.Procedure.ClimbViaTargetFt);
        Assert.Equal(Cndel5ViaTopFt, restored.Procedure.SidViaCeiling);
        Assert.True(restored.Procedure.SidViaMode);

        FlyPhysics(ac, 900);
        FlyPhysics(restored, 900);
        _output.WriteLine($"live={ac.Altitude:F0} restored={restored.Altitude:F0} viaTop={viaTop}");

        Assert.InRange(ac.Altitude, viaTop - ViaTopToleranceFt, viaTop + ViaTopToleranceFt);
        Assert.InRange(restored.Altitude, viaTop - ViaTopToleranceFt, viaTop + ViaTopToleranceFt);
        Assert.Equal(ac.Altitude, restored.Altitude, 1.0);
        Assert.Null(restored.Procedure.ClimbViaTargetFt);
    }

    /// <summary>
    /// A vector without an altitude while the climb via is in force past the route's end takes the 7110.65 5-6-2 handling:
    /// "FH 270" levels the aircraft off where it is and raises the "vectored off procedure without an altitude assignment"
    /// warning, the climb via still being the aircraft's procedure.
    /// </summary>
    [Fact]
    public void ClimbVia_RouteEndsAtCeilingFix_VectorWithoutAltitude_LevelsOffAndWarns()
    {
        AircraftState ac = Cndel5RouteEndingAtCeilingFix(out string ceilingFix, out _);
        DispatchLine(ac, "CVIA 120", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));
        FlyPastFix(ac, ceilingFix, 900);
        Assert.NotNull(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(Cndel5ProcedureId(), ac.Procedure.ActiveSidId);

        double atVector = ac.Altitude;
        DispatchLine(ac, "FH 270", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));
        _output.WriteLine($"atVector={atVector:F0} warnings={string.Join("; ", ac.PendingWarnings)}");

        Assert.Contains("Vectored off procedure without an altitude assignment", ac.PendingWarnings);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Targets.TargetAltitude);

        FlyPhysics(ac, 120);
        _output.WriteLine($"level={ac.Altitude:F0}");
        Assert.InRange(ac.Altitude, atVector - LevelToleranceFt, atVector + LevelToleranceFt);
    }

    /// <summary>
    /// A check-in after the route's end and before the top still names the SID and the via's except-maintain altitude:
    /// "leaving X for 12,000, climbing via the CNDEL5 departure" (AIM 5-2-9.h.9 and initial-contact Example 2).
    /// </summary>
    [Fact]
    public void ClimbVia_RouteEndsAtCeilingFix_CheckIn_NamesTheSidAndTop()
    {
        AircraftState ac = Cndel5RouteEndingAtCeilingFix(out string ceilingFix, out _);
        DispatchLine(ac, "CVIA 120", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));
        FlyPastFix(ac, ceilingFix, 900);
        FlyPhysics(ac, 30);
        Assert.NotNull(ac.Procedure.ClimbViaTargetFt);
        Assert.True(ac.Altitude < Cndel5ViaTopFt, $"setup: the check-in must come before the top, at {ac.Altitude:F0}");

        PilotSpeechText line = Assert.IsType<PilotSpeechText>(PilotResponder.BuildAirborneCheckIn(ac, CheckInScenario(), KoakPosition));
        _output.WriteLine(line.Terminal);

        string sidId = Assert.IsType<string>(ac.Procedure.ActiveSidId);
        (string procTerm, string procTts) = PhraseologyVerbalizer.ProcedureName(sidId);
        Assert.Contains($" for {PhraseologyVerbalizer.CompactAltitude(Cndel5ViaTopFt)}", line.Terminal);
        Assert.Contains($"climbing via the {procTerm} departure", line.Terminal);
        Assert.Contains($" for {AtcNumberParser.AltitudeToWords(Cndel5ViaTopFt)}", line.Tts);
        Assert.Contains($"climbing via the {procTts} departure", line.Tts);
    }

    /// <summary>
    /// The route's end keeps the climb via only while the aircraft is still below its top: inside the 20 ft top-reached band
    /// everything clears at once, and a little further below the via stays in force.
    /// </summary>
    [Theory]
    [InlineData(15.0, false)]
    [InlineData(25.0, true)]
    public void ClimbVia_RouteEndsWithinTheTopBand_ClearsAtOnce(double belowTopFt, bool kept)
    {
        AircraftState ac = Cndel5RouteEndingAtCeilingFix(out string ceilingFix, out _);
        DispatchLine(ac, "CVIA 120", TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));
        ac.Altitude = Cndel5ViaTopFt - belowTopFt;
        double atFix = FlyPastFix(ac, ceilingFix, 600);
        _output.WriteLine($"belowTop={belowTopFt:F0} atFix={atFix:F0} alt={ac.Altitude:F0} via={ac.Procedure.ClimbViaTargetFt}");

        if (kept)
        {
            Assert.Equal(Cndel5ViaTopFt, ac.Procedure.ClimbViaTargetFt);
            Assert.Equal(Cndel5ViaTopFt, ac.Procedure.SidViaCeiling);
            Assert.True(ac.Procedure.SidViaMode);
            Assert.Equal(Cndel5ProcedureId(), ac.Procedure.ActiveSidId);
        }
        else
        {
            Assert.Null(ac.Procedure.ClimbViaTargetFt);
            Assert.Null(ac.Procedure.SidViaCeiling);
            Assert.False(ac.Procedure.SidViaMode);
            Assert.Null(ac.Procedure.ActiveSidId);
        }
    }

    /// <summary>A CTO with an altitude, then a bare CVIA: the CTO altitude held on the climb phases is an interim altitude too.</summary>
    [Fact]
    public void CtoWithAltitude_ThenBareClimbVia_ClimbsToTopAltitudeNotCtoAltitude()
    {
        Departure departure = StartCoast9Departure(LoadZoa(), ctoAltitude: 5000, climbViaOnTheGround: false);
        AircraftState ac = departure.Aircraft;
        int top = Coast9TopAltitudeFt;
        Assert.IsType<InitialClimbPhase>(Assert.IsType<PhaseList>(ac.Phases).CurrentPhase);

        Dispatch(ac, "CVIA");
        Assert.Null(ac.Targets.AssignedAltitude);

        FlightResult flight = FlyFor(departure, 600);
        _output.WriteLine($"top={top} maxAlt={flight.MaxAltitude:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(flight.SawDepartureProcedure, "the aircraft should keep flying the SID after the CVIA");
        Assert.True(flight.MaxAltitude < top + LevelToleranceFt, $"the CTO's 5,000 still drove the climb: reached {flight.MaxAltitude:F0}");
        Assert.InRange(ac.Altitude, top - LevelToleranceFt, top + LevelToleranceFt);
        Assert.Null(ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// A CVIA on the ground, then "CTO 050": the later "maintain" cancels the climb via's altitude (7110.65 4-5-7.h.11
    /// NOTE 1), so the departure levels at 5,000.
    /// </summary>
    [Fact]
    public void ClimbViaOnTheGround_ThenCtoWithAltitude_LevelsAtTheCtoAltitude()
    {
        Departure departure = StartCoast9Departure(LoadZoa(), ctoAltitude: 5000, climbViaOnTheGround: true);
        AircraftState ac = departure.Aircraft;

        FlightResult flight = FlyFor(departure, 420);
        _output.WriteLine($"maxAlt={flight.MaxAltitude:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(flight.MaxAltitude < 5000 + LevelToleranceFt, $"climbed through the CTO's 5,000: {flight.MaxAltitude:F0}");
        Assert.InRange(ac.Altitude, 5000 - LevelToleranceFt, 5000 + LevelToleranceFt);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
    }

    /// <summary>
    /// The check-in after a climb via names an ATC-assigned top: the "except maintain" altitude, "leaving 3,000 for 12,000,
    /// climbing via the CNDEL5 departure" (AIM 5-2-9.h.9 and initial contact Example 2), or the TDLS climb-via text's
    /// "EXC MAINT" altitude (NUEVO8 SNS's 10,000). After a bare climb via whose top is published (CNDEL5) it names none.
    /// </summary>
    [Theory]
    [InlineData("CNDEL5", "RW30", "SUSEY", "LEJAY", "CVIA 120", 12000)]
    [InlineData("CNDEL5", "RW30", "SUSEY", "LEJAY", "CVIA", null)]
    [InlineData("NUEVO8", "RW30", "SNS", "SAPLY", "CVIA", 10000)]
    public void ClimbVia_CheckIn_NamesOnlyAnAssignedTopAltitude(
        string sidId,
        string runwayTransition,
        string enrouteTransition,
        string startFix,
        string command,
        int? assignedTop
    )
    {
        AircraftState ac = SidDeparture("KOAK", sidId, runwayTransition, enrouteTransition, startFix, 3000, sidAlreadyActive: true);
        DispatchContext ctx = TestDispatch.Context(Random.Shared, artccConfig: LoadZoa());
        DispatchLine(ac, "CM 080", ctx);
        DispatchLine(ac, command, ctx);

        PilotSpeechText line = Assert.IsType<PilotSpeechText>(PilotResponder.BuildAirborneCheckIn(ac, CheckInScenario(), KoakPosition));
        _output.WriteLine(line.Terminal);

        (string procTerm, string procTts) = PhraseologyVerbalizer.ProcedureName(sidId);
        string forTerm = assignedTop is { } termAlt ? $" for {PhraseologyVerbalizer.CompactAltitude(termAlt)}" : "";
        string forTts = assignedTop is { } ttsAlt ? $" for {AtcNumberParser.AltitudeToWords(ttsAlt)}" : "";
        Assert.Contains($"leaving {PhraseologyVerbalizer.CompactAltitude(3000)}{forTerm}, climbing via the {procTerm} departure", line.Terminal);
        Assert.Contains($"leaving {AtcNumberParser.AltitudeToWords(3000)}{forTts}, climbing via the {procTts} departure", line.Tts);
    }

    /// <summary>
    /// A SID departure cleared "CTO 050" with no climb via checks in naming its assigned altitude, "leaving (altitude)
    /// climbing to 5,000", never the SID: the via form speaks only while a climb via is in force (AIM 5-2-9.h.9, 5-3-1.b.2.a).
    /// </summary>
    [Fact]
    public void SidDeparture_CtoWithAltitude_NoClimbVia_CheckInNamesTheAssignedAltitude()
    {
        Departure departure = StartCoast9Departure(LoadZoa(), ctoAltitude: 5000, climbViaOnTheGround: false);
        AircraftState ac = departure.Aircraft;
        FlyFor(departure, 30);
        Assert.True(ac.Procedure.SidViaMode, "setup: a SID departure flies its SID in via mode");
        Assert.Null(ac.Procedure.ClimbViaTargetFt);

        PilotSpeechText line = Assert.IsType<PilotSpeechText>(PilotResponder.BuildAirborneCheckIn(ac, CheckInScenario(), KoakPosition));
        _output.WriteLine(line.Terminal);

        Assert.Contains($"climbing to {PhraseologyVerbalizer.CompactAltitude(5000)}", line.Terminal);
        Assert.DoesNotContain("climbing via", line.Terminal);
        Assert.Contains($"climbing to {AtcNumberParser.AltitudeToWords(5000)}", line.Tts);
        Assert.DoesNotContain("climbing via", line.Tts);
    }

    /// <summary>
    /// A bare "CTO" on a SID whose TDLS initial altitude is the ARTCC's default (KSJC's SJC3: 5,000) checks in climbing to
    /// that altitude, never "level": the trend follows the altitude the aircraft is held to, not the step-climb planner's
    /// target on a low restricted leg (staged here as 100 ft above the present altitude), and a "level" report would keep
    /// the arrival ATIS (AIM 5-2-9.h.9, 5-3-1.b.2.a).
    /// </summary>
    [Fact]
    public void SidDeparture_TdlsDefaultInitialAltMaintain_CheckInNamesThatAltitude()
    {
        Departure departure = StartSjc3Departure(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        FlyFor(departure, 10);
        Assert.True(ac.Procedure.SidViaMode, "setup: a SID departure flies its SID in via mode");
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(5000, ac.Procedure.SidInitialAltitudeFt);
        Assert.Null(ac.Targets.AssignedAltitude);
        // Staged: the step-climb planner's target on a leg restricted below the 5,000 the aircraft is held to, within the
        // 200 ft the check-in treats as level.
        ac.Targets.TargetAltitude = ac.Altitude + 100;

        PilotSpeechText line = Assert.IsType<PilotSpeechText>(PilotResponder.BuildAirborneCheckIn(ac, CheckInScenario(), KoakPosition));
        _output.WriteLine($"{ac.Altitude:F0} ft, target {ac.Targets.TargetAltitude:F0}: {line.Terminal}");

        Assert.Contains($"climbing to {PhraseologyVerbalizer.CompactAltitude(5000)}", line.Terminal);
        Assert.DoesNotContain("level", line.Terminal);
        Assert.DoesNotContain("climbing via", line.Terminal);
        Assert.Contains($"climbing to {AtcNumberParser.AltitudeToWords(5000)}", line.Tts);
        Assert.DoesNotContain("climbing via", line.Tts);
    }

    /// <summary>
    /// A CNDEL6 departure cleared "CTO 050" with no climb via: the controller's altitude replaces the SID's published
    /// ones, so the aircraft climbs to and levels at 5,000, never on to PORTE's 10,000 ceiling (AIM 5-2-9.h.8 Example 2;
    /// 7110.65 4-3-2). Its route ends at PORTE, as on a filed route that ends at the SID.
    /// </summary>
    [Fact]
    public void Cndel6_CtoFifty_NoClimbVia_LevelsAtAssigned()
    {
        Departure departure = StartKoak30Departure(LoadZoa(), new KoakDeparture("KLAS", "CNDEL6 SUSEY", 5000, false, new DefaultDeparture(), 450));

        (double maxAltitude, double atPorte) = FlyPastPorteTrimmedThere(departure, _ => { });

        _output.WriteLine($"at PORTE={atPorte:F0} max={maxAltitude:F0} final={departure.Aircraft.Altitude:F0}");
        Assert.True(maxAltitude <= 5000 + ViaTopToleranceFt, $"the aircraft climbed to {maxAltitude:F0} ft, above its assigned 5,000");
        Assert.InRange(departure.Aircraft.Altitude, 5000 - ViaTopToleranceFt, 5000 + ViaTopToleranceFt);
    }

    /// <summary>
    /// The same "CTO 050" departure, then "CVIA 120" shortly after takeoff: the climb via brings the SID's restrictions ahead
    /// back, so PORTE's at or below 10,000 caps the climb up to PORTE (AIM 5-2-9.h.6, h.8).
    /// </summary>
    [Fact]
    public void Cndel6_CtoFifty_ThenClimbVia_RestrictionsAheadComeBack()
    {
        Departure departure = StartKoak30Departure(LoadZoa(), new KoakDeparture("KLAS", "CNDEL6 SUSEY", 5000, false, new DefaultDeparture(), 450));
        AircraftState ac = departure.Aircraft;

        int? viaTop = null;
        double beforePorte = 0;
        double? viaAltitudeBeforePorte = null;
        (_, double atPorte) = FlyPastPorteTrimmedThere(
            departure,
            t =>
            {
                if (t == 30)
                {
                    // Except maintain 12,000: the bare climb via's TDLS top is PORTE's own 10,000, which would leave the cap untested
                    Dispatch(ac, "CVIA 120");
                    viaTop = Assert.NotNull(ac.Procedure.ClimbViaTargetFt);
                }

                if ((t > 30) && ac.Targets.NavigationRoute.Exists(f => f.Name == "PORTE"))
                {
                    beforePorte = Math.Max(beforePorte, ac.Altitude);
                    viaAltitudeBeforePorte = FlightPhysics.ClimbViaAltitude(ac);
                }
            }
        );

        _output.WriteLine($"at PORTE={atPorte:F0} highest before={beforePorte:F0} top={viaTop} via before PORTE={viaAltitudeBeforePorte}");
        Assert.True(viaTop > 10000, $"setup: the climb via top {viaTop} must sit above PORTE's 10,000 ceiling");
        Assert.True(beforePorte <= 10000 + LevelToleranceFt, $"before PORTE the aircraft climbed to {beforePorte:F0}, above its 10,000 ceiling");
        Assert.Equal(10000, viaAltitudeBeforePorte);
        // Measured while PORTE is still on the route: PORTE sequences before the closest approach, and past it the climb
        // via goes on toward its 12,000 top.
        Assert.True(beforePorte > 5000 + 500, $"the climb via never took the aircraft above 5,000 before PORTE: {beforePorte:F0} ft");
    }

    /// <summary>
    /// A bare CTO under a TDLS "maintain" value (<see cref="AircraftProcedure.SidInitialAltitudeFt"/>, 5,000) is a controller
    /// altitude: the SID's published altitudes are not in force under it (AIM 5-2-9.h.8 Example 2), so the route planner never
    /// caps the climb at LEJAY's published ceiling below 5,000.
    /// </summary>
    [Fact]
    public void TdlsMaintain_SidPublishedAltitudesNotInForce()
    {
        List<NavigationTarget> resolved = DepartureClearanceHandler.ResolveLegsToTargets(FlownLegs("KOAK", "CNDEL5", "RW30", "SUSEY"));
        int lejay = resolved.FindIndex(t => t.Name == "LEJAY");
        Assert.True((lejay >= 0) && (lejay + 1 < resolved.Count), "setup: the flown CNDEL legs must pass LEJAY");
        CifpAltitudeRestriction ceiling = Assert.IsType<CifpAltitudeRestriction>(resolved[lejay].AltitudeRestriction);
        Assert.True(
            (ceiling.Type == CifpAltitudeRestrictionType.AtOrBelow) && (ceiling.Altitude1Ft < 5000),
            $"setup: LEJAY publishes {ceiling.Type} {ceiling.Altitude1Ft}, not a ceiling below 5,000"
        );

        LatLon fix = resolved[lejay].Position;
        double track = GeoMath.BearingTo(fix, resolved[lejay + 1].Position);
        LatLon start = GeoMath.ProjectPoint(fix, new TrueHeading(track + 180.0), 6.0);
        var ac = new AircraftState
        {
            Callsign = "SWA2210",
            AircraftType = "B738",
            Position = start,
            TrueHeading = new TrueHeading(GeoMath.BearingTo(start, fix)),
            TrueTrack = new TrueHeading(GeoMath.BearingTo(start, fix)),
            Altitude = 1500,
            IndicatedAirspeed = 230,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KLAS",
                Route = "CNDEL5 SUSEY",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
        };
        foreach (NavigationTarget target in resolved.Skip(lejay).DistinctBy(f => f.Name))
        {
            ac.Targets.NavigationRoute.Add(target);
        }

        ac.Procedure.ActiveSidId = Cndel5ProcedureId();
        ac.Procedure.DepartureRunway = "30";
        ac.Procedure.SidViaMode = true;
        ac.Procedure.SidInitialAltitudeFt = 5000;
        ac.Targets.TargetAltitude = 5000;

        double atLejay = FlyPastFix(ac, "LEJAY", 600);
        _output.WriteLine($"at LEJAY={atLejay:F0} target={ac.Targets.TargetAltitude}");

        Assert.True(
            atLejay > ceiling.Altitude1Ft + 200,
            $"crossed LEJAY at {atLejay:F0}: the planner capped the climb at its published {ceiling.Altitude1Ft:N0}"
        );
    }

    /// <summary>
    /// A CFIX on a SID fix, then the filed SID activated with no transmission (the TDLS climb via path): the published
    /// altitude the overlay restores is no longer the controller's, so the fix drops its crossing stamp with it.
    /// </summary>
    [Fact]
    public void Overlay_ClearsTheCrossingStampWhenRestoringThePublishedAltitude()
    {
        AircraftState ac = Cndel5Departure(sidAlreadyActive: false, out CifpAltitudeRestriction published);
        DispatchContext ctx = TestDispatch.Context(Random.Shared);
        DispatchLine(ac, "CM 140", ctx);
        DispatchLine(ac, "CFIX PORTE A120", ctx);
        NavigationTarget porte = RouteFix(ac, "PORTE");
        Assert.NotNull(porte.CrossingTransmissionId);

        Assert.True(NavigationCommandHandler.TryActivateFiledSid(ac, 0));

        Assert.Equal(published, porte.AltitudeRestriction);
        Assert.Null(porte.CrossingTransmissionId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClimbVia_AfterCrossFix_DropsTheRevertAndRestoresThePublishedRestriction(bool sidAlreadyActive)
    {
        AircraftState ac = Cndel5Departure(sidAlreadyActive, out CifpAltitudeRestriction published);
        DispatchContext ctx = TestDispatch.Context(Random.Shared);

        DispatchLine(ac, "CM 140", ctx);
        DispatchLine(ac, "CFIX PORTE A120", ctx);
        NavigationTarget porte = RouteFix(ac, "PORTE");
        Assert.Equal(14000, porte.RevertAssignedAltitude);

        DispatchLine(ac, "CVIA", ctx);

        Assert.Equal(published, porte.AltitudeRestriction);
        AssertNoReverts(porte);
        Assert.Null(porte.CrossingTransmissionId);
        Assert.Null(ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// A CVIA given inside the SID's climb window cancels a speed an earlier CFIX assigned, so it does not outlive the
    /// window (7110.65 5-7-1). The CFIX keeps the departure phase and stamps its fix on the pending route the phase hands
    /// over; the via clearance, in a later transmission, gives that pending fix its published speed back with no speed
    /// reverts and cancels the assigned speed, while the coded legs still steer.
    /// </summary>
    [Fact]
    public void ClimbVia_InTheClimbWindow_CancelsAnEarlierCrossFixSpeedPastTheWindow()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);

        NavigationTarget pending = procedure.PostRoute[0];
        CifpSpeedRestriction? published = pending.SpeedRestriction;
        _output.WriteLine($"fix={pending.Name} published={published}");

        Dispatch(ac, $"CFIX {pending.Name} A100 230");
        Assert.Equal(230, ac.Targets.AssignedSpeed);
        Assert.Equal(230, pending.SpeedRestriction?.SpeedKts);

        Dispatch(ac, "CVIA");

        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Equal(published, pending.SpeedRestriction);
        Assert.Null(pending.SpeedTransmissionId);
        Assert.Null(pending.RevertSpeed);
        Assert.Null(pending.RevertAssignedSpeed);
    }

    /// <summary>
    /// A CFIX in the SID's climb window keeps the departure phase (YAAT-1699): the coded legs keep steering, the live route
    /// stays empty, and the crossing stamps its fix on the pending route the phase hands over. A bare crossing gives no new
    /// altitude to maintain, so the assigned altitude stays as it was.
    /// </summary>
    [Fact]
    public void CrossFix_InClimbWindow_KeepsTheDeparturePhase()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        NavigationTarget segul = PendingFix(procedure, "SEGUL");
        double? assignedBefore = ac.Targets.AssignedAltitude;

        Dispatch(ac, "CFIX SEGUL A100");

        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Equal(AtOrAboveAtc(10000), segul.AltitudeRestriction);
        Assert.NotNull(segul.CrossingTransmissionId);
        Assert.Equal(assignedBefore, ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// A CFIX with an altitude and a speed on a pending fix beyond the coded legs survives the handover: the phase re-adds
    /// the stamped pending fix itself, so the live route's fix carries the crossing, its stamps and its speed reverts.
    /// </summary>
    [Fact]
    public void CrossFix_PendingFixBeyondCodedLegs_SurvivesTheHandover()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        NavigationTarget pending = FirstPendingFixBeyondCodedLegs(procedure);
        _output.WriteLine($"pending fix beyond the coded legs: {pending.Name}");

        Dispatch(ac, $"CFIX {pending.Name} A100 230");
        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.True(NavigationCommandHandler.HasSpeedRevert(pending), "setup: the crossing speed must hold a speed revert");

        RunFinish(departure, procedure);

        NavigationTarget live = RouteFix(ac, pending.Name);
        Assert.Equal(AtOrAboveAtc(10000), live.AltitudeRestriction);
        Assert.Equal(new CifpSpeedRestriction(230, CifpSpeedRestrictionType.AtOrBelow), live.SpeedRestriction);
        Assert.NotNull(live.CrossingTransmissionId);
        Assert.Equal(pending.CrossingTransmissionId, live.CrossingTransmissionId);
        Assert.Equal(pending.SpeedTransmissionId, live.SpeedTransmissionId);
        Assert.True(NavigationCommandHandler.HasSpeedRevert(live), $"{live.Name} lost its speed revert at the handover");
        Assert.Equal(pending.RevertSpeed, live.RevertSpeed);
        Assert.Equal(pending.RevertAssignedSpeed, live.RevertAssignedSpeed);
        Assert.Equal(pending.RevertAltitude, live.RevertAltitude);
        Assert.Equal(pending.RevertAssignedAltitude, live.RevertAssignedAltitude);
    }

    /// <summary>
    /// While a SID handover is pending, a CFIX on a fix that is on neither the pending route, the coded legs nor the filed
    /// route is refused as unable, and nothing changes: the departure phase and its pending route stay as they were.
    /// </summary>
    [Fact]
    public void CrossFix_OffRoute_InClimbWindow_IsRefusedAndSidKept()
    {
        const string offRoute = "SAC";
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        List<string> pendingBefore = [.. procedure.PostRoute.Select(f => f.Name)];
        _output.WriteLine($"pending route: {string.Join(" ", pendingBefore)}; filed: {ac.FlightPlan.Route}");
        Assert.DoesNotContain(offRoute, pendingBefore, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(procedure.Legs, l => string.Equals(l.FixName, offRoute, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(offRoute, ac.FlightPlan.Route.Split(' '), StringComparer.OrdinalIgnoreCase);

        CommandResult result = DispatchResult(ac, $"CFIX {offRoute} A100");

        Assert.False(result.Success, $"the off-route CFIX was accepted: {result.Message}");
        Assert.Equal($"unable, {PhraseologyVerbalizer.FixDisplayTextUpper(offRoute)} isn't on our route", result.Message);
        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.Equal(pendingBefore, procedure.PostRoute.Select(f => f.Name));
        Assert.All(procedure.PostRoute, f => Assert.Null(f.CrossingTransmissionId));
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    /// <summary>
    /// A CFIX with a speed on the coded legs' CF fix (COAST9: VD, VM, CF SEGUL) stamps that fix's pending entry, which the
    /// handover drops as flown: it applies the fix's speed revert once there, so the crossing speed is not left assigned
    /// past the coded legs.
    /// </summary>
    [Fact]
    public void CrossFix_SpeedOnCodedLegFix_RevertsAtTheHandover()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        _output.WriteLine($"legs={string.Join(",", procedure.Legs.Select(l => $"{l.Type}:{l.FixName}"))}");
        _output.WriteLine($"pending={string.Join(" ", procedure.PostRoute.Select(f => f.Name))}");
        Assert.Contains(procedure.Legs, l => l.FixName == "SEGUL");
        NavigationTarget segul = procedure.PostRoute[0];
        Assert.Equal("SEGUL", segul.Name);
        double? assignedBefore = ac.Targets.AssignedSpeed;

        Dispatch(ac, "CFIX SEGUL A100 230");
        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.Equal(230, ac.Targets.AssignedSpeed);
        double revertSpeed = Assert.NotNull(segul.RevertSpeed);
        Assert.Equal(assignedBefore, segul.RevertAssignedSpeed);

        RunFinish(departure, procedure);

        _output.WriteLine($"after the handover: assigned={ac.Targets.AssignedSpeed} target={ac.Targets.TargetSpeed} revert={revertSpeed}");
        Assert.Equal(assignedBefore, ac.Targets.AssignedSpeed);
        Assert.Equal(revertSpeed, ac.Targets.TargetSpeed);
        Assert.DoesNotContain(ac.Targets.NavigationRoute, f => f.Name == "SEGUL");
    }

    /// <summary>
    /// A CVIA in a later transmission than a CFIX on a pending fix beyond the coded legs cancels that crossing: the pending
    /// fix gets its published restriction and speed back, with no stamps and no reverts, and the assigned speed goes. Given
    /// in the same transmission as the CFIX, the via clearance restates the crossing, which keeps its stamp.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClimbVia_AfterAWindowCrossing_CancelsOnlyInALaterTransmission(bool sameTransmission)
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        NavigationTarget pending = FirstPendingFixBeyondCodedLegs(procedure);
        CifpAltitudeRestriction? publishedAltitude = pending.AltitudeRestriction;
        CifpSpeedRestriction? publishedSpeed = pending.SpeedRestriction;
        _output.WriteLine($"fix={pending.Name} published={publishedAltitude}/{publishedSpeed}");

        if (sameTransmission)
        {
            Dispatch(ac, $"CFIX {pending.Name} A100 230, CVIA");
        }
        else
        {
            Dispatch(ac, $"CFIX {pending.Name} A100 230");
            Dispatch(ac, "CVIA");
        }

        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        AssertNoReverts(pending);
        Assert.False(NavigationCommandHandler.HasSpeedRevert(pending), $"{pending.Name} kept a speed revert");
        if (sameTransmission)
        {
            Assert.Equal(AtOrAboveAtc(10000), pending.AltitudeRestriction);
            Assert.NotNull(pending.CrossingTransmissionId);
            Assert.Equal(new CifpSpeedRestriction(230, CifpSpeedRestrictionType.AtOrBelow), pending.SpeedRestriction);
            Assert.NotNull(pending.SpeedTransmissionId);
        }
        else
        {
            Assert.Equal(publishedAltitude, pending.AltitudeRestriction);
            Assert.Null(pending.CrossingTransmissionId);
            Assert.Equal(publishedSpeed, pending.SpeedRestriction);
            Assert.Null(pending.SpeedTransmissionId);
            Assert.Null(ac.Targets.AssignedSpeed);
        }
    }

    /// <summary>
    /// A CFIX given while an RV SID holds its heading keeps the initial climb: the heading hold and the deferred turn stay,
    /// and the crossing stamps the departure route the phase loads after the hand-off, so the live route then holds it.
    /// </summary>
    [Fact]
    public void InitialClimb_RvSidCfix_KeepsHeadingHoldAndStampsTheRoute()
    {
        Departure departure = StartKoak30Departure(LoadZoa(), RvSidNimi5(heightAglFt: 450));
        AircraftState ac = departure.Aircraft;
        InitialClimbPhase climb = Assert.IsType<InitialClimbPhase>(ac.Phases?.CurrentPhase);
        InitialClimbPhaseDto before = Assert.IsType<InitialClimbPhaseDto>(climb.ToSnapshot());
        _output.WriteLine(
            $"rv={before.RvSidActive} turnApplied={before.VfrTurnApplied} route={string.Join(" ", climb.DepartureRoute.Select(f => f.Name))}"
        );
        Assert.True(before.RvSidActive, "setup: the NIMI5 departure must hold its radar-vectors heading");
        Assert.Empty(ac.Targets.NavigationRoute);
        NavigationTarget pending = climb.DepartureRoute[0];
        TrueHeading? headingBefore = ac.Targets.TargetTrueHeading;

        Dispatch(ac, $"CFIX {pending.Name} A100");

        Assert.Same(climb, ac.Phases?.CurrentPhase);
        InitialClimbPhaseDto after = Assert.IsType<InitialClimbPhaseDto>(climb.ToSnapshot());
        Assert.Equal(before.RvSidActive, after.RvSidActive);
        Assert.Equal(before.VfrTurnApplied, after.VfrTurnApplied);
        Assert.Equal(headingBefore, ac.Targets.TargetTrueHeading);
        Assert.Equal(AtOrAboveAtc(10000), pending.AltitudeRestriction);
        Assert.NotNull(pending.CrossingTransmissionId);
        Assert.Empty(ac.Targets.NavigationRoute);

        ac.HasLeftStudentFrequency = true;
        for (int t = 0; (t < 30) && !ac.Targets.NavigationRoute.Exists(f => ReferenceEquals(f, pending)); t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
        }

        NavigationTarget live = Assert.Single(ac.Targets.NavigationRoute, f => f.Name == pending.Name);
        Assert.Same(pending, live);
        Assert.Equal(AtOrAboveAtc(10000), live.AltitudeRestriction);
        Assert.NotNull(live.CrossingTransmissionId);
    }

    /// <summary>
    /// An RV SID that defers its vectors heading to the 400 ft AGL gate and is handed off below it loads the departure route
    /// at the hand-off, once: the gate then loads nothing more, so a window CFIX's speed revert stays on its live fix until
    /// the aircraft sequences it, and the phase holds no pending route once the route is live. With
    /// <paramref name="roundTripBeforeTheGate"/> an aircraft snapshot round trip between the hand-off load and the gate
    /// keeps that.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RvSid_DeferredHeading_EarlyHandoff_LoadsTheRouteOnce(bool roundTripBeforeTheGate)
    {
        Departure departure = StartKoak30Departure(LoadZoa(), RvSidNimi5(heightAglFt: 20));
        AircraftState ac = departure.Aircraft;
        PhaseContext ctx = departure.Context;
        double gateFt = departure.Runway.ElevationFt + 400;
        InitialClimbPhase climb = Assert.IsType<InitialClimbPhase>(ac.Phases?.CurrentPhase);
        InitialClimbPhaseDto before = Assert.IsType<InitialClimbPhaseDto>(climb.ToSnapshot());
        List<string> routeNames = [.. climb.DepartureRoute.Select(f => f.Name)];
        _output.WriteLine($"rv={before.RvSidActive} turnApplied={before.VfrTurnApplied} route={string.Join(" ", routeNames)}");
        Assert.True(before.RvSidActive, "setup: the NIMI5 departure must hold its radar-vectors heading");
        Assert.True(climb.RvSidDeferHeadingUntilMinAlt, "setup: the NIMI5 departure must defer its vectors heading to the gate");
        Assert.False(before.VfrTurnApplied, "setup: the aircraft must start below the deferred-turn gate");
        Assert.Contains("FESIK", routeNames);
        double? assignedBefore = ac.Targets.AssignedSpeed;

        Dispatch(ac, "CFIX FESIK A100 230");
        Assert.Same(climb, ac.Phases?.CurrentPhase);
        Assert.Equal(230, ac.Targets.AssignedSpeed);
        Assert.True(NavigationCommandHandler.HasSpeedRevert(climb.DepartureRoute.Single(f => f.Name == "FESIK")), "setup: no speed revert");

        ac.HasLeftStudentFrequency = true;
        for (int t = 0; (t < 30) && (ac.Targets.NavigationRoute.Count == 0); t++)
        {
            PhaseRunner.Tick(ac, ctx);
            FlightPhysics.Update(ac, 1.0);
        }

        _output.WriteLine($"route live at alt={ac.Altitude:F0} (gate {gateFt:F0}), assigned={ac.Targets.AssignedSpeed}");
        Assert.NotEmpty(ac.Targets.NavigationRoute);
        Assert.True(ac.Altitude < gateFt, $"setup: the hand-off load must come below the gate, at {ac.Altitude:F0} ft");
        Assert.Same(climb, ac.Phases?.CurrentPhase);
        Assert.Null(climb.PendingRoute);

        if (roundTripBeforeTheGate)
        {
            ac = AircraftState.FromSnapshot(ac.ToSnapshot(), groundLayout: null);
            ctx = NewContext(ac, departure.Runway);
            climb = Assert.IsType<InitialClimbPhase>(ac.Phases?.CurrentPhase);
            Assert.Null(climb.PendingRoute);
        }

        List<double?> assignedSeen = [ac.Targets.AssignedSpeed];
        bool fesikSequenced = false;
        for (int t = 0; (t < 1200) && !fesikSequenced; t++)
        {
            PhaseRunner.Tick(ac, ctx);
            FlightPhysics.Update(ac, 1.0);
            if (ac.Targets.AssignedSpeed != assignedSeen[^1])
            {
                assignedSeen.Add(ac.Targets.AssignedSpeed);
            }

            if ((ac.Altitude >= gateFt + 100) && (ac.Phases?.CurrentPhase is InitialClimbPhase current))
            {
                Assert.Null(current.PendingRoute);
            }

            if (ac.Altitude < gateFt + 100)
            {
                Assert.Equal(routeNames, ac.Targets.NavigationRoute.Select(f => f.Name));
                Assert.Equal(230, ac.Targets.AssignedSpeed);
            }

            fesikSequenced = !ac.Targets.NavigationRoute.Exists(f => f.Name == "FESIK");
        }

        _output.WriteLine($"assigned speeds seen: {string.Join(" -> ", assignedSeen.Select(s => s?.ToString() ?? "none"))}");
        Assert.True(fesikSequenced, "FESIK was not sequenced");
        Assert.True(ac.Altitude >= gateFt, "the aircraft never flew through the gate");
        Assert.Equal([230, assignedBefore], assignedSeen);
    }

    /// <summary>
    /// A window CFIX with a speed on a pending fix, then "FH 270, SPD 250" in one later transmission: the vector abandons
    /// the pending route, whose speed revert applies at once, before the SPD of the same block, so the SPD 250 holds after
    /// the next ticks and the fix keeps no revert. <paramref name="rvHold"/> stages it on a radar-vectors SID's heading
    /// hold (<see cref="InitialClimbPhase"/>) instead of the COAST9 coded legs (<see cref="DepartureProcedurePhase"/>).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossFix_SpeedInWindow_ThenVectoredWithSpeed_KeepsTheNewSpeed(bool rvHold)
    {
        (Departure departure, NavigationTarget fix, _) = CrossWithSpeedOnPendingFix(rvHold);
        AircraftState ac = departure.Aircraft;

        Dispatch(ac, "FH 270, SPD 250");
        Assert.Equal(250, ac.Targets.AssignedSpeed);

        for (int t = 0; t < 5; t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
            Assert.Equal(250, ac.Targets.AssignedSpeed);
        }

        Assert.False(NavigationCommandHandler.HasSpeedRevert(fix), $"{fix.Name} kept its speed revert past the vector");

        for (int t = 0; t < 300; t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
            Assert.Equal(250, ac.Targets.AssignedSpeed);
        }
    }

    /// <summary>
    /// A window CFIX with a speed on a pending fix, then a constrained direct-to that fix with its own speed in a later
    /// transmission: the pending route's revert applies with the direct-to's own, before it captures the speed it
    /// replaces, so the assigned speed is the pre-CFIX one again, the fix carries the direct-to's crossing speed and a
    /// revert to the pre-CFIX speed, and the CFIX's 230 never comes back, at the fix or past it. <paramref name="rvHold"/> as in
    /// <see cref="CrossFix_SpeedInWindow_ThenVectoredWithSpeed_KeepsTheNewSpeed"/>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossFix_SpeedInWindow_ThenConstrainedDctf_KeepsTheDctfSpeedState(bool rvHold)
    {
        (Departure departure, NavigationTarget fix, double? assignedBefore) = CrossWithSpeedOnPendingFix(rvHold);
        AircraftState ac = departure.Aircraft;

        // The text parser builds no constrained direct-to speed, so the command is built as ConstrainedDctfKeepsStarTests does.
        var direct = new ConstrainedForceDirectToCommand(
            [new ResolvedFix(fix.Name, fix.Position.Lat, fix.Position.Lon)],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(8000, CrossFixAltitudeType.AtOrAbove) },
            new Dictionary<int, int> { [0] = 250 },
            null
        );
        CommandResult result = CommandDispatcher.DispatchCompound(
            new CompoundCommand([new ParsedBlock(null, [direct])]),
            ac,
            TestDispatch.Context(Random.Shared, artccConfig: LoadZoa())
        );
        _output.WriteLine($"DCTF {fix.Name}/A080 250 -> {result.Message}");
        Assert.True(result.Success, result.Message);

        NavigationTarget live = RouteFix(ac, fix.Name);
        _output.WriteLine($"after DCTF: assigned={ac.Targets.AssignedSpeed} before={assignedBefore}");
        _output.WriteLine($"after DCTF: restriction={live.SpeedRestriction} revertAssigned={live.RevertAssignedSpeed}");
        Assert.Equal(assignedBefore, ac.Targets.AssignedSpeed);
        Assert.Equal(new CifpSpeedRestriction(250, CifpSpeedRestrictionType.AtOrBelow), live.SpeedRestriction);
        Assert.True(NavigationCommandHandler.HasSpeedRevert(live), $"{live.Name} holds no speed revert");
        Assert.Equal(assignedBefore, live.RevertAssignedSpeed);

        bool sequenced = false;
        for (int t = 0; (t < 1500) && !sequenced; t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
            sequenced = !ac.Targets.NavigationRoute.Contains(live);
            Assert.NotEqual(230, ac.Targets.AssignedSpeed);
        }

        Assert.True(sequenced, $"{live.Name} was not sequenced");
        Assert.Equal(assignedBefore, ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// Stages a window crossing with a speed, "CFIX {fix} A100 230", on the first pending fix beyond the coded legs of a
    /// COAST9 departure in its climb window, or with <paramref name="rvHold"/> on the first fix of a NIMI5 departure's
    /// pending route while it holds its radar-vectors heading. Returns the assigned speed before the crossing.
    /// </summary>
    private (Departure Departure, NavigationTarget Fix, double? AssignedBefore) CrossWithSpeedOnPendingFix(bool rvHold)
    {
        Departure departure;
        NavigationTarget fix;
        if (rvHold)
        {
            departure = StartKoak30Departure(LoadZoa(), RvSidNimi5(heightAglFt: 450));
            InitialClimbPhase climb = Assert.IsType<InitialClimbPhase>(departure.Aircraft.Phases?.CurrentPhase);
            List<NavigationTarget> pending = Assert.IsType<List<NavigationTarget>>(climb.PendingRoute);
            fix = pending[0];
        }
        else
        {
            departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
            fix = FirstPendingFixBeyondCodedLegs(CurrentProcedure(departure.Aircraft));
        }

        AircraftState ac = departure.Aircraft;
        double? assignedBefore = ac.Targets.AssignedSpeed;
        _output.WriteLine($"pending fix {fix.Name}, assigned speed before the crossing: {assignedBefore}");
        Dispatch(ac, $"CFIX {fix.Name} A100 230");
        Assert.Equal(230, ac.Targets.AssignedSpeed);
        Assert.True(NavigationCommandHandler.HasSpeedRevert(fix), "setup: the crossing speed must hold a speed revert");
        Assert.Empty(ac.Targets.NavigationRoute);
        return (departure, fix, assignedBefore);
    }

    /// <summary>A CFIX stamped on the pending route mid-window survives an aircraft snapshot round trip.</summary>
    [Fact]
    public void CrossFix_InClimbWindow_RoundTripsThroughSnapshot()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        NavigationTarget segul = PendingFix(procedure, "SEGUL");

        Dispatch(ac, "CFIX SEGUL A100 230");
        Assert.Same(procedure, ac.Phases?.CurrentPhase);

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), groundLayout: null);
        NavigationTarget restoredSegul = PendingFix(CurrentProcedure(restored), "SEGUL");

        Assert.Equal(AtOrAboveAtc(10000), restoredSegul.AltitudeRestriction);
        Assert.Equal(segul.SpeedRestriction, restoredSegul.SpeedRestriction);
        Assert.NotNull(restoredSegul.CrossingTransmissionId);
        Assert.Equal(segul.CrossingTransmissionId, restoredSegul.CrossingTransmissionId);
        Assert.Equal(segul.SpeedTransmissionId, restoredSegul.SpeedTransmissionId);
        Assert.Equal(segul.RevertSpeed, restoredSegul.RevertSpeed);
        Assert.Equal(segul.RevertAssignedSpeed, restoredSegul.RevertAssignedSpeed);
        Assert.Empty(restored.Targets.NavigationRoute);
    }

    /// <summary>
    /// "DCT MCKEY, CFIX MCKEY A100 230" in the climb window: the direct-to takes the aircraft off the coded legs first, so no
    /// handover is pending and the crossing stamps the live route the direct-to built. Nothing is left on the abandoned
    /// pending route, and past MCKEY the speed goes back to what it was.
    /// </summary>
    [Fact]
    public void CrossFix_DctThenCfixSameBlock_InClimbWindow_StampsTheLiveRoute()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        double? assignedBefore = ac.Targets.AssignedSpeed;

        Dispatch(ac, "DCT MCKEY, CFIX MCKEY A100 230");

        NavigationTarget mckey = RouteFix(ac, "MCKEY");
        Assert.Equal(AtOrAboveAtc(10000), mckey.AltitudeRestriction);
        Assert.NotNull(mckey.CrossingTransmissionId);
        Assert.True(NavigationCommandHandler.HasSpeedRevert(mckey), "the live MCKEY must hold the crossing's speed revert");
        Assert.DoesNotContain(procedure.PostRoute, f => f.CrossingTransmissionId is not null);
        Assert.DoesNotContain(procedure.PostRoute, NavigationCommandHandler.HasSpeedRevert);

        int t = 0;
        for (; (t < 3000) && ac.Targets.NavigationRoute.Exists(f => ReferenceEquals(f, mckey)); t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
        }

        _output.WriteLine($"MCKEY sequenced after {t} s: assigned={ac.Targets.AssignedSpeed}");
        Assert.False(ac.Phases?.CurrentPhase is DepartureProcedurePhase, "the direct-to must end the departure procedure");
        Assert.DoesNotContain(mckey, ac.Targets.NavigationRoute);
        Assert.Equal(assignedBefore, ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// A heading departure in its initial climb has no departure route pending, so a CFIX clears the phase as it always has,
    /// rather than leaving the phase waiting on its heading.
    /// </summary>
    [Fact]
    public void CrossFix_HeadingDeparture_ClearsTheInitialClimb()
    {
        var heading = new FlyHeadingDeparture(new MagneticHeading(270), null);
        Departure departure = StartKoak30Departure(LoadZoa(), new KoakDeparture("KSAN", "COAST9 MCKEY LAX COMIX2", null, false, heading, 450));
        AircraftState ac = departure.Aircraft;
        PhaseRunner.Tick(ac, departure.Context);
        InitialClimbPhase climb = Assert.IsType<InitialClimbPhase>(ac.Phases?.CurrentPhase);
        _output.WriteLine($"departure route={string.Join(" ", climb.DepartureRoute.Select(f => f.Name))}");
        Assert.Null(climb.PendingRoute);

        Dispatch(ac, "CFIX MCKEY A100");

        Assert.False(ac.Phases?.CurrentPhase is InitialClimbPhase, "the CFIX must clear the heading departure's initial climb");
    }

    /// <summary>
    /// A departure cleared direct to a fix with its turn deferred to the 400 ft gate: a CFIX on that fix before the gate
    /// stamps the departure route, the live route stays empty until the gate, and the route loaded there carries it.
    /// </summary>
    [Fact]
    public void CrossFix_DeferredTurnDirectDeparture_BeforeTheGate_SurvivesTheTurn()
    {
        LatLon segulPosition = FixPosition("SEGUL");
        var direct = new DirectFixDeparture("SEGUL", segulPosition.Lat, segulPosition.Lon, null);
        Departure departure = StartKoak30Departure(LoadZoa(), new KoakDeparture("KSAN", "COAST9 MCKEY LAX COMIX2", null, false, direct, 150));
        AircraftState ac = departure.Aircraft;
        InitialClimbPhase climb = Assert.IsType<InitialClimbPhase>(ac.Phases?.CurrentPhase);
        _output.WriteLine($"departure route={string.Join(" ", climb.DepartureRoute.Select(f => f.Name))}");
        Assert.Empty(ac.Targets.NavigationRoute);
        NavigationTarget segul = Assert.Single(climb.DepartureRoute, f => f.Name == "SEGUL");

        Dispatch(ac, "CFIX SEGUL A100");

        Assert.Same(climb, ac.Phases?.CurrentPhase);
        Assert.Equal(AtOrAboveAtc(10000), segul.AltitudeRestriction);
        Assert.Empty(ac.Targets.NavigationRoute);

        for (int t = 0; (t < 120) && (ac.Targets.NavigationRoute.Count == 0); t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
        }

        NavigationTarget live = Assert.Single(ac.Targets.NavigationRoute, f => f.Name == "SEGUL");
        Assert.Same(segul, live);
        Assert.Equal(AtOrAboveAtc(10000), live.AltitudeRestriction);
        Assert.NotNull(live.CrossingTransmissionId);
    }

    /// <summary>
    /// A crossing speed given in the climb window, then a vector in a later transmission: the vector takes the aircraft off
    /// the coded legs, so the pending route is never loaded, and its speed revert applies as the phase ends.
    /// </summary>
    [Fact]
    public void CrossFix_SpeedInWindow_ThenVectored_RevertsTheSpeed()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        NavigationTarget pending = FirstPendingFixBeyondCodedLegs(procedure);
        double? assignedBefore = ac.Targets.AssignedSpeed;

        Dispatch(ac, $"CFIX {pending.Name} A100 230");
        Assert.Equal(230, ac.Targets.AssignedSpeed);
        Dispatch(ac, "FH 270");

        for (int t = 0; (t < 10) && (ac.Phases?.CurrentPhase is DepartureProcedurePhase); t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
        }

        Assert.False(ac.Phases?.CurrentPhase is DepartureProcedurePhase, "the vector must end the departure procedure");
        Assert.Equal(assignedBefore, ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// "CFIX X A100 230, SPD 250" in the climb window ends as the same line does on a live route
    /// (SpeedRevertSupersededTests.SameTransmission_Cfix_Then_Spd_RestoresTheSpdPastTheFix): the crossing speed is the
    /// assigned speed, and the SPD is what the fix reverts to past it.
    /// </summary>
    [Fact]
    public void CrossFix_SpeedThenSpdSameTransmission_InWindow_MatchesTheLiveRoute()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        NavigationTarget pending = FirstPendingFixBeyondCodedLegs(procedure);

        Dispatch(ac, $"CFIX {pending.Name} A100 230, SPD 250");

        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.Equal(230, ac.Targets.AssignedSpeed);
        Assert.Equal(230, pending.SpeedRestriction?.SpeedKts);
        Assert.Equal(250, pending.RevertAssignedSpeed);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    /// <summary>While an RV SID holds its heading, a CFIX on a fix off its departure route is refused and the hold stays.</summary>
    [Fact]
    public void CrossFix_OffRoute_InInitialClimbRvHold_IsRefused()
    {
        (AircraftState ac, InitialClimbPhase climb) = StartNimi5RvDeparture();
        List<string> routeBefore = [.. climb.DepartureRoute.Select(f => f.Name)];
        Assert.DoesNotContain("SEGUL", routeBefore);

        CommandResult result = DispatchResult(ac, "CFIX SEGUL A100");

        Assert.False(result.Success, $"the off-route CFIX was accepted: {result.Message}");
        Assert.Equal($"unable, {PhraseologyVerbalizer.FixDisplayTextUpper("SEGUL")} isn't on our route", result.Message);
        Assert.Same(climb, ac.Phases?.CurrentPhase);
        Assert.True(Assert.IsType<InitialClimbPhaseDto>(climb.ToSnapshot()).RvSidActive);
        Assert.Equal(routeBefore, climb.DepartureRoute.Select(f => f.Name));
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    /// <summary>
    /// A filed-route token the pending departure route lacks (NIMI5's OAK, stripped as colocated with the departure) is no
    /// fix ahead: a CFIX on it while the handover is pending is refused.
    /// </summary>
    [Fact]
    public void CrossFix_FiledTokenNotOnPendingRoute_IsRefused()
    {
        (AircraftState ac, InitialClimbPhase climb) = StartNimi5RvDeparture();
        Assert.Contains("OAK", ac.FlightPlan.Route.Split(' '));
        Assert.DoesNotContain(climb.DepartureRoute, f => f.Name == "OAK");

        CommandResult result = DispatchResult(ac, "CFIX OAK A100");

        Assert.False(result.Success, $"the CFIX on the filed token was accepted: {result.Message}");
        Assert.Equal($"unable, {PhraseologyVerbalizer.FixDisplayTextUpper("OAK")} isn't on our route", result.Message);
        Assert.Same(climb, ac.Phases?.CurrentPhase);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    /// <summary>A NIMI5 departure off KOAK runway 30, holding its radar-vectors heading in its initial climb.</summary>
    private (AircraftState Aircraft, InitialClimbPhase Climb) StartNimi5RvDeparture()
    {
        Departure departure = StartKoak30Departure(LoadZoa(), RvSidNimi5(heightAglFt: 450));
        InitialClimbPhase climb = Assert.IsType<InitialClimbPhase>(departure.Aircraft.Phases?.CurrentPhase);
        _output.WriteLine($"departure route={string.Join(" ", climb.DepartureRoute.Select(f => f.Name))}");
        Assert.True(Assert.IsType<InitialClimbPhaseDto>(climb.ToSnapshot()).RvSidActive, "setup: NIMI5 must hold its RV heading");
        return (departure.Aircraft, climb);
    }

    [Theory]
    [InlineData(true, "CFIX PORTE A120, CVIA")]
    [InlineData(false, "CFIX PORTE A120, CVIA")]
    [InlineData(true, "CVIA, CFIX PORTE A120")]
    [InlineData(false, "CVIA, CFIX PORTE A120")]
    public void ClimbVia_SameTransmissionCrossFix_KeepsTheRestriction_NoRevert(bool sidAlreadyActive, string line)
    {
        AircraftState ac = Cndel5Departure(sidAlreadyActive, out _);
        DispatchContext ctx = TestDispatch.Context(Random.Shared);

        DispatchLine(ac, "CM 080", ctx);
        DispatchLine(ac, line, ctx);

        NavigationTarget porte = RouteFix(ac, "PORTE");
        Assert.Equal(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 12000) { Source = CifpAltitudeRestrictionSource.Atc },
            porte.AltitudeRestriction
        );
        AssertNoReverts(porte);
        Assert.True(ac.Procedure.SidViaMode);
        Assert.Null(ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// A queued "AT REBAS CFIX TIPRE 110, CVIA" restored from a snapshot before it fires still runs as one transmission:
    /// the rebuilt block stamps and reads the id it was queued with, so TIPRE keeps the restated crossing with no reverts.
    /// </summary>
    [Fact]
    public void QueuedSameTransmissionCrossFix_RestoredFromSnapshot_KeepsTheRestriction_NoRevert()
    {
        Harness harness = BuildHussh2Harness();
        SendOk(harness, "AT REBAS CFIX TIPRE 110, CVIA");
        Assert.Null(harness.Aircraft.Procedure.ClimbViaTargetFt);

        StateSnapshotDto snapshot = harness.Engine.CaptureSnapshot();
        SimulationEngine restored = NewEngine();
        restored.RestoreFromSnapshot(snapshot);
        AircraftState aircraft = Assert.IsType<AircraftState>(restored.FindAircraft(harness.Aircraft.Callsign));

        for (int t = 0; (t < 1800) && (aircraft.Procedure.ClimbViaTargetFt is null); t++)
        {
            restored.TickOneSecond();
        }

        Assert.NotNull(aircraft.Procedure.ClimbViaTargetFt);
        NavigationTarget tipre = RouteFix(aircraft, "TIPRE");
        Assert.Equal(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 11000) { Source = CifpAltitudeRestrictionSource.Atc },
            tipre.AltitudeRestriction
        );
        AssertNoReverts(tipre);
    }

    /// <summary>
    /// A lateral command that drops the queued CVIA out of "AT REBAS CFIX TIPRE 220, CVIA" leaves a lone CFIX behind: it
    /// no longer restates the crossing with a via clearance, so it fires with its reverts, as "AT REBAS CFIX TIPRE 220" would.
    /// The crossing sits high enough that the aircraft, climbing to FL230, is still below it at REBAS.
    /// </summary>
    [Fact]
    public void QueuedSameTransmissionCrossFix_ClimbViaSplitOut_KeepsTheReverts()
    {
        Harness harness = BuildHussh2Harness();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "CM 230");
        SendOk(harness, "AT REBAS CFIX TIPRE 220, CVIA");
        SendOk(harness, "DCTF REBAS");
        Assert.DoesNotContain(aircraft.Queue.Blocks, CarriesClimbVia);

        NavigationTarget tipre = TickUntilCrossingSet(harness.Engine, aircraft, "TIPRE", 22000);
        Assert.Null(aircraft.Procedure.ClimbViaTargetFt);
        Assert.Equal(23000, tipre.RevertAssignedAltitude);
    }

    /// <summary>
    /// "AT REBAS CFIX TIPRE 220; CVIA" queues the CFIX and the CVIA as two blocks of one transmission. A later command that
    /// drops the CVIA block (a lateral DCTF, or DELAT) leaves the CFIX block a lone CFIX: it fires with its reverts.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedSameTransmissionCrossFix_ClimbViaBlockDropped_KeepsTheReverts(bool deleteConditional)
    {
        Harness harness = BuildHussh2Harness();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "CM 230");
        SendOk(harness, "AT REBAS CFIX TIPRE 220; CVIA");
        Assert.Contains(aircraft.Queue.Blocks, CarriesClimbVia);
        SendOk(harness, deleteConditional ? "DELAT 2" : "DCTF REBAS");
        Assert.DoesNotContain(aircraft.Queue.Blocks, CarriesClimbVia);

        NavigationTarget tipre = TickUntilCrossingSet(harness.Engine, aircraft, "TIPRE", 22000);
        Assert.Null(aircraft.Procedure.ClimbViaTargetFt);
        Assert.Equal(23000, tipre.RevertAssignedAltitude);
    }

    /// <summary>
    /// "CFIX TIPRE 110; AT REBAS CVIA" applies the CFIX at once and queues the CVIA. Until that CVIA fires the crossing is
    /// a lone CFIX with its reverts, so dropping the CVIA (DELAT) leaves the reverts the CFIX captured in place (YAAT-984).
    /// </summary>
    [Fact]
    public void AppliedCrossFix_ThenQueuedClimbViaDropped_KeepsTheReverts()
    {
        Harness harness = BuildHussh2Harness();
        AircraftState aircraft = harness.Aircraft;
        SendOk(harness, "CM 140");
        SendOk(harness, "CFIX TIPRE 110; AT REBAS CVIA");
        Assert.Contains(aircraft.Queue.Blocks, CarriesClimbVia);
        SendOk(harness, "DELAT 2");
        Assert.DoesNotContain(aircraft.Queue.Blocks, CarriesClimbVia);

        NavigationTarget tipre = RouteFix(aircraft, "TIPRE");
        Assert.Null(aircraft.Procedure.ClimbViaTargetFt);
        Assert.Equal(14000, tipre.RevertAssignedAltitude);
    }

    /// <summary>
    /// "CM 100; CFIX PORTE at or above 050" from 3,000: the revert to 10,000 continues the climb (AIM 4-4-10.e), so the
    /// CFIX captures it.
    /// </summary>
    [Fact]
    public void CrossFixBelowLaterClimb_KeepsTheRevert()
    {
        AircraftState ac = Cndel5Departure(sidAlreadyActive: false, out _);
        DispatchContext ctx = TestDispatch.Context(Random.Shared);

        DispatchLine(ac, "CM 100", ctx);
        DispatchLine(ac, "CFIX PORTE A050", ctx);

        NavigationTarget porte = RouteFix(ac, "PORTE");
        Assert.Equal(10000, porte.RevertAltitude);
        Assert.Equal(10000, porte.RevertAssignedAltitude);
    }

    /// <summary>
    /// "CM 080; CFIX PORTE 110" from 3,000: descending back to 8,000 past PORTE would reverse the climb, which needs a new
    /// clearance (7110.65 4-5-7.h.8 NOTE 2; 14 CFR 91.123), so the CFIX captures no altitude revert and 11,000 stays
    /// assigned (AIM 4-4-10.g).
    /// </summary>
    [Fact]
    public void CrossFixAboveEarlierClimb_NoAltitudeRevert()
    {
        AircraftState ac = Cndel5Departure(sidAlreadyActive: false, out _);
        DispatchContext ctx = TestDispatch.Context(Random.Shared);

        DispatchLine(ac, "CM 080", ctx);
        DispatchLine(ac, "CFIX PORTE 110", ctx);

        NavigationTarget porte = RouteFix(ac, "PORTE");
        Assert.Null(porte.RevertAltitude);
        Assert.Null(porte.RevertAssignedAltitude);
        Assert.Equal(11000, ac.Targets.AssignedAltitude);
    }

    [Fact]
    public void ClimbViaTarget_RoundTripsThroughSnapshot_RestoredAircraftLevelsTheSame()
    {
        Assert.DoesNotContain(nameof(AircraftProcedureDto.ClimbViaTargetFt), JsonSerializer.Serialize(new AircraftProcedure().ToSnapshot()));

        Departure departure = FlyIntoClimbWindow(LoadZoa(), ctoAltitude: null);
        AircraftState ac = departure.Aircraft;
        Dispatch(ac, "CVIA 120");
        FlyFor(departure, 60);
        Assert.InRange(ac.Altitude, WindowCeilingFt + 500, 12000 - 1000);

        AircraftProcedureDto procedure = Assert.IsType<AircraftProcedureDto>(
            JsonSerializer.Deserialize<AircraftProcedureDto>(JsonSerializer.Serialize(ac.Procedure.ToSnapshot()))
        );
        Assert.Equal(12000, procedure.ClimbViaTargetFt);

        AircraftSnapshotDto snapshot = ac.ToSnapshot();
        var restored = AircraftState.FromSnapshot(snapshot, groundLayout: null);
        Assert.Equal(12000, restored.Procedure.ClimbViaTargetFt);
        var restoredDeparture = new Departure(restored, NewContext(restored, departure.Runway), departure.Runway);

        FlyFor(departure, 540);
        FlyFor(restoredDeparture, 540);
        _output.WriteLine($"live={ac.Altitude:F0} restored={restored.Altitude:F0}");

        Assert.InRange(ac.Altitude, 12000 - LevelToleranceFt, 12000 + LevelToleranceFt);
        Assert.Equal(ac.Altitude, restored.Altitude, 1.0);
    }

    private static int RoundUpToHundred(double altitudeFt) => (int)(Math.Ceiling(Math.Round(altitudeFt) / 100.0) * 100);

    private static ArtccConfigRoot LoadZoa() => Assert.IsType<ArtccConfigRoot>(TestArtccConfig.LoadZoa());

    private static LatLon FixPosition(string fix)
    {
        (double Lat, double Lon) position = Assert.NotNull(NavigationDatabase.Instance.GetFixPosition(fix));
        return new LatLon(position.Lat, position.Lon);
    }

    private static NavigationTarget RouteFix(AircraftState aircraft, string name) => aircraft.Targets.NavigationRoute.First(f => f.Name == name);

    private static bool CarriesClimbVia(CommandBlock block) => block.ParsedCommands?.Exists(c => c is ClimbViaCommand) is true;

    private static DepartureProcedurePhase CurrentProcedure(AircraftState aircraft) =>
        Assert.IsType<DepartureProcedurePhase>(Assert.IsType<PhaseList>(aircraft.Phases).CurrentPhase);

    /// <summary>The pending-route entry named <paramref name="name"/>; fails, listing the pending route, when there is none.</summary>
    private static NavigationTarget PendingFix(DepartureProcedurePhase procedure, string name) =>
        procedure.PostRoute.Find(f => f.Name == name)
        ?? throw new Xunit.Sdk.XunitException(
            $"setup: {name} is not on the pending route {string.Join(" ", procedure.PostRoute.Select(f => f.Name))}"
        );

    /// <summary>The first pending-route entry whose name is none of the coded legs' fixes.</summary>
    private static NavigationTarget FirstPendingFixBeyondCodedLegs(DepartureProcedurePhase procedure)
    {
        var coded = new HashSet<string>(procedure.Legs.Where(l => l.FixName is not null).Select(l => l.FixName!), StringComparer.OrdinalIgnoreCase);
        return Assert.IsType<NavigationTarget>(procedure.PostRoute.Find(f => !coded.Contains(f.Name)));
    }

    private static CifpAltitudeRestriction AtOrAboveAtc(int altitudeFt) =>
        new(CifpAltitudeRestrictionType.AtOrAbove, altitudeFt) { Source = CifpAltitudeRestrictionSource.Atc };

    /// <summary>
    /// COAST9's VM leg holds until a controller vector, so the phase never reaches Finish unaided: rewinds the phase's own
    /// snapshot to "every coded leg flown" and ticks once, so the phase list runs the real completion (Finish), as
    /// DepartureProcedureVectorClimbStallTests does.
    /// </summary>
    private static void RunFinish(Departure departure, DepartureProcedurePhase procedure)
    {
        AircraftState ac = departure.Aircraft;
        PhaseList phases = Assert.IsType<PhaseList>(ac.Phases);
        DepartureProcedurePhaseDto snapshot = Assert.IsType<DepartureProcedurePhaseDto>(procedure.ToSnapshot());
        Assert.False(snapshot.Overridden, "setup: the aircraft must still be flying the coded procedure");
        var allLegsFlown = DepartureProcedurePhase.FromSnapshot(
            new DepartureProcedurePhaseDto
            {
                Status = snapshot.Status,
                ElapsedSeconds = snapshot.ElapsedSeconds,
                Requirements = snapshot.Requirements,
                Legs = snapshot.Legs,
                PostRoute = snapshot.PostRoute,
                AssignedAltitude = snapshot.AssignedAltitude,
                CruiseAltitude = snapshot.CruiseAltitude,
                LegIndex = snapshot.Legs.Count,
                Overridden = false,
                LegEntryPosition = snapshot.LegEntryPosition,
                PreviousSignedCrossTrack = snapshot.PreviousSignedCrossTrack,
                LegElapsedSeconds = snapshot.LegElapsedSeconds,
                ControllerAltitude = snapshot.ControllerAltitude,
            }
        );
        int index = phases.Phases.IndexOf(procedure);
        Assert.True(index >= 0, "the departure-procedure phase must be in the phase list");
        phases.Phases[index] = allLegsFlown;

        PhaseRunner.Tick(ac, departure.Context);
        Assert.False(ReferenceEquals(allLegsFlown, phases.CurrentPhase), "the phase must complete through Finish");
    }

    private static void AssertNoReverts(NavigationTarget fix)
    {
        Assert.Null(fix.RevertAltitude);
        Assert.Null(fix.RevertAssignedAltitude);
        Assert.Null(fix.RevertSpeed);
        Assert.Null(fix.RevertAssignedSpeed);
    }

    /// <summary>
    /// The largest altitude <paramref name="legs"/> publish: the upper bound of a between restriction, the value of every
    /// other kind.
    /// </summary>
    private static int HighestPublishedFt(IEnumerable<CifpLeg> legs) =>
        legs.Select(l => l.Altitude)
            .OfType<CifpAltitudeRestriction>()
            .Max(r => r.Type == CifpAltitudeRestrictionType.Between ? Math.Max(r.Altitude1Ft, r.Altitude2Ft ?? 0) : r.Altitude1Ft);

    private static List<CifpLeg> FlownLegs(string airport, string sidId, string runwayTransition, string enrouteTransition)
    {
        CifpSidProcedure sid = Assert.IsType<CifpSidProcedure>(NavigationDatabase.Instance.GetSid(airport, sidId));
        return [.. sid.RunwayTransitions[runwayTransition].Legs, .. sid.CommonLegs, .. sid.EnrouteTransitions[enrouteTransition].Legs];
    }

    /// <summary>
    /// A departure from <paramref name="airport"/> on <paramref name="sidId"/> from <paramref name="runwayTransition"/> to
    /// <paramref name="enrouteTransition"/>, at <paramref name="startFix"/> at <paramref name="altitude"/>, its route the SID
    /// fixes after it. With the SID active the route carries the SID's published restrictions; otherwise it holds bare fixes
    /// (as after a re-clearance direct) and a CVIA activates the filed SID.
    /// </summary>
    private static AircraftState SidDeparture(
        string airport,
        string sidId,
        string runwayTransition,
        string enrouteTransition,
        string startFix,
        double altitude,
        bool sidAlreadyActive
    )
    {
        List<NavigationTarget> resolved = DepartureClearanceHandler.ResolveLegsToTargets(
            FlownLegs(airport, sidId, runwayTransition, enrouteTransition)
        );
        int start = resolved.FindIndex(t => t.Name == startFix);
        Assert.True((start >= 0) && (start + 1 < resolved.Count), $"setup: {sidId} {runwayTransition} {enrouteTransition} must pass {startFix}");

        LatLon position = resolved[start].Position;
        LatLon next = resolved[start + 1].Position;
        var ac = new AircraftState
        {
            Callsign = "SWA2210",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(GeoMath.BearingTo(position, next)),
            TrueTrack = new TrueHeading(GeoMath.BearingTo(position, next)),
            Altitude = altitude,
            IndicatedAirspeed = 230,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = airport,
                Destination = "KLAS",
                Route = $"{sidId} {enrouteTransition}",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
        };

        foreach (NavigationTarget fix in resolved.Skip(start + 1).DistinctBy(f => f.Name))
        {
            ac.Targets.NavigationRoute.Add(sidAlreadyActive ? fix : new NavigationTarget { Name = fix.Name, Position = fix.Position });
        }

        if (sidAlreadyActive)
        {
            ac.Procedure.ActiveSidId = sidId;
            ac.Procedure.DepartureRunway = runwayTransition[2..];
            ac.Procedure.SidViaMode = true;
        }

        return ac;
    }

    /// <summary>A CNDEL5 SUSEY departure past LEJAY at 3,000. <paramref name="published"/> is PORTE's published restriction.</summary>
    internal static AircraftState Cndel5Departure(bool sidAlreadyActive, out CifpAltitudeRestriction published)
    {
        List<NavigationTarget> resolved = DepartureClearanceHandler.ResolveLegsToTargets(FlownLegs("KOAK", "CNDEL5", "RW30", "SUSEY"));
        published = Assert.IsType<CifpAltitudeRestriction>(resolved.First(t => t.Name == "PORTE").AltitudeRestriction);
        return SidDeparture("KOAK", "CNDEL5", "RW30", "SUSEY", "LEJAY", 3000, sidAlreadyActive);
    }

    /// <summary>
    /// A CNDEL5 SUSEY departure at LEJAY at 3,000, its route cut at the SID's last at or at-or-below fix: the climb via's
    /// ceiling is the route's last fix, as on a filed route that ends at the SID.
    /// </summary>
    private static AircraftState Cndel5RouteEndingAtCeilingFix(out string ceilingFix, out int ceilingFt)
    {
        AircraftState ac = SidDeparture("KOAK", "CNDEL5", "RW30", "SUSEY", "LEJAY", 3000, sidAlreadyActive: false);
        (ceilingFix, ceilingFt) = CutRouteAtLastCeilingFix(ac);
        return ac;
    }

    /// <summary>The filed CNDEL5's active id: the current cycle's revision of that procedure (CNDEL6 in the pinned data).</summary>
    private static string Cndel5ProcedureId() => Assert.IsType<CifpSidProcedure>(NavigationDatabase.Instance.GetSid("KOAK", "CNDEL5")).ProcedureId;

    /// <summary>
    /// The flown CNDEL5 legs' last fix carrying an at or at-or-below restriction (PORTE, at or below 10,000), and the
    /// aircraft's route cut to end there.
    /// </summary>
    private static (string Fix, int CeilingFt) CutRouteAtLastCeilingFix(AircraftState aircraft)
    {
        CifpLeg last = FlownLegs("KOAK", "CNDEL5", "RW30", "SUSEY")
            .Last(l =>
                (l.Altitude is { } restriction) && (restriction.Type is CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.AtOrBelow)
            );
        int cut = aircraft.Targets.NavigationRoute.FindIndex(f => f.Name == last.FixIdentifier);
        Assert.True(cut >= 0, $"setup: the route must pass {last.FixIdentifier}");
        aircraft.Targets.NavigationRoute.RemoveRange(cut + 1, aircraft.Targets.NavigationRoute.Count - cut - 1);
        return (last.FixIdentifier, Assert.IsType<CifpAltitudeRestriction>(last.Altitude).Altitude1Ft);
    }

    /// <summary>Flies physics alone until <paramref name="fixName"/> leaves the route; returns the altitude there.</summary>
    private static double FlyPastFix(AircraftState aircraft, string fixName, int maxSeconds)
    {
        for (int t = 0; t < maxSeconds; t++)
        {
            FlightPhysics.Update(aircraft, 1.0);
            if (!aircraft.Targets.NavigationRoute.Exists(f => f.Name == fixName))
            {
                return aircraft.Altitude;
            }
        }

        Assert.Fail($"the aircraft did not pass {fixName} within {maxSeconds} s");
        return aircraft.Altitude;
    }

    private static void FlyPhysics(AircraftState aircraft, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            FlightPhysics.Update(aircraft, 1.0);
        }
    }

    private static SimScenarioState CheckInScenario() =>
        new()
        {
            ScenarioId = "cvia-check-in",
            ScenarioName = "CVIA check-in",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "KOAK",
            SoloTrainingMode = true,
            StudentPositionType = "APP",
        };

    private static SimulationEngine NewEngine() =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "cvia-supersedes-hussh2",
                ScenarioName = "CVIA supersedes earlier crossings on HUSSH2",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };

    /// <summary>A HUSSH2 TIPRE departure at HUSSH at 3,000 in an engine, its route NIITE REBAS TAMMM TIPRE as bare fixes.</summary>
    private Harness BuildHussh2Harness()
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        AircraftState aircraft = SidDeparture("KOAK", "HUSSH2", "RW30", "TIPRE", "HUSSH", 3000, sidAlreadyActive: false);
        Assert.Equal(["NIITE", "REBAS", "TAMMM", "TIPRE"], aircraft.Targets.NavigationRoute.Select(f => f.Name));

        SimulationEngine engine = NewEngine();
        engine.World.AddAircraft(aircraft);
        return new Harness(engine, aircraft);
    }

    /// <summary>Ticks until a queued CFIX stamps At <paramref name="altitudeFt"/> on <paramref name="fixName"/> and returns that fix.</summary>
    private static NavigationTarget TickUntilCrossingSet(SimulationEngine engine, AircraftState aircraft, string fixName, int altitudeFt)
    {
        var crossing = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, altitudeFt) { Source = CifpAltitudeRestrictionSource.Atc };
        for (int t = 0; t < 1800; t++)
        {
            if ((aircraft.Targets.NavigationRoute.Find(f => f.Name == fixName) is { } fix) && (fix.AltitudeRestriction == crossing))
            {
                return fix;
            }

            engine.TickOneSecond();
        }

        Assert.Fail($"The queued CFIX never set At {altitudeFt:N0} on {fixName} within 1800 s");
        return RouteFix(aircraft, fixName);
    }

    private void SendOk(Harness harness, string command)
    {
        CommandResult result = harness.Engine.SendCommand(harness.Aircraft.Callsign, command);
        _output.WriteLine($"{command} -> {result.Message}");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    /// <summary>A KSJC runway 30 as the navdata publishes it, with the 30 end as end 1.</summary>
    private static RunwayInfo Ksjc30()
    {
        IReadOnlyList<RunwayInfo> runways = NavigationDatabase.Instance.GetRunways(NavigationDatabase.NormalizeAirport("KSJC"));
        if (runways.Count == 0)
        {
            runways = NavigationDatabase.Instance.GetRunways("KSJC");
        }

        RunwayInfo? phys = runways.FirstOrDefault(r =>
            r.Id.End1.StartsWith("30", StringComparison.Ordinal) || r.Id.End2.StartsWith("30", StringComparison.Ordinal)
        );
        Assert.NotNull(phys);
        bool thirtyIsEnd2 = !phys.Id.End1.StartsWith("30", StringComparison.Ordinal);
        return new RunwayInfo
        {
            AirportId = "KSJC",
            Id = phys.Id,
            Designator = thirtyIsEnd2 ? phys.Id.End2 : phys.Id.End1,
            Lat1 = thirtyIsEnd2 ? phys.Lat2 : phys.Lat1,
            Lon1 = thirtyIsEnd2 ? phys.Lon2 : phys.Lon1,
            Elevation1Ft = thirtyIsEnd2 ? phys.Elevation2Ft : phys.Elevation1Ft,
            TrueHeading1 = thirtyIsEnd2 ? phys.TrueHeading2 : phys.TrueHeading1,
            Lat2 = thirtyIsEnd2 ? phys.Lat1 : phys.Lat2,
            Lon2 = thirtyIsEnd2 ? phys.Lon1 : phys.Lon2,
            Elevation2Ft = thirtyIsEnd2 ? phys.Elevation1Ft : phys.Elevation2Ft,
            TrueHeading2 = thirtyIsEnd2 ? phys.TrueHeading1 : phys.TrueHeading2,
            WidthFt = phys.WidthFt,
        };
    }

    private static RunwayInfo Koak30()
    {
        RunwayInfo phys = Assert.IsType<RunwayInfo>(
            NavigationDatabase
                .Instance.GetRunways("KOAK")
                .FirstOrDefault(r => (r.Id.End1 == "30") || (r.Id.End2 == "30") || r.Designator.Contains("30", StringComparison.Ordinal))
        );
        return new RunwayInfo
        {
            AirportId = "KOAK",
            Id = phys.Id,
            Designator = "30",
            Lat1 = phys.Lat1,
            Lon1 = phys.Lon1,
            Elevation1Ft = phys.Elevation1Ft,
            TrueHeading1 = phys.TrueHeading1,
            Lat2 = phys.Lat2,
            Lon2 = phys.Lon2,
            Elevation2Ft = phys.Elevation2Ft,
            TrueHeading2 = phys.TrueHeading2,
            WidthFt = phys.WidthFt,
        };
    }

    private static PhaseContext NewContext(AircraftState ac, RunwayInfo rwy) =>
        new()
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

    /// <summary>
    /// Clears a COAST9 RWY30 departure out of KOAK for takeoff, with <paramref name="ctoAltitude"/> or none, and puts it
    /// airborne just past the DER in its initial climb, its TDLS initial altitude resolved from <paramref name="artccConfig"/>.
    /// With <paramref name="climbViaOnTheGround"/> a bare climb via is issued in position, before the takeoff clearance.
    /// </summary>
    private static Departure StartCoast9Departure(ArtccConfigRoot artccConfig, int? ctoAltitude, bool climbViaOnTheGround)
    {
        Assert.NotNull(NavigationDatabase.Instance.GetSid("KOAK", "COAST9"));
        return StartKoak30Departure(
            artccConfig,
            new KoakDeparture("KSAN", "COAST9 MCKEY LAX COMIX2", ctoAltitude, climbViaOnTheGround, new DefaultDeparture(), 450)
        );
    }

    /// <summary>A NIMI5 radar-vectors SID departure to KSAC (NIMI5 OAK V6 SAC), no CTO altitude, <paramref name="heightAglFt"/> up.</summary>
    private static KoakDeparture RvSidNimi5(double heightAglFt) => new("KSAC", "NIMI5 OAK V6 SAC", null, false, new DefaultDeparture(), heightAglFt);

    /// <summary>
    /// Clears the RWY30 departure out of KOAK that <paramref name="spec"/> describes for takeoff and puts it airborne just
    /// past the DER in its initial climb, as <see cref="StartCoast9Departure"/> does.
    /// </summary>
    private static Departure StartKoak30Departure(ArtccConfigRoot artccConfig, KoakDeparture spec)
    {
        (string destination, string route, int? ctoAltitude, bool climbViaOnTheGround, DepartureInstruction departure, double heightAglFt) = spec;
        RunwayInfo rwy = Koak30();

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
                Departure = "KOAK",
                Destination = destination,
                Route = route,
                Altitude = PlannedAltitude.Ifr(39000),
                FlightRules = "IFR",
            },
            Phases = new PhaseList { AssignedRunway = rwy },
        };
        DepartureClearanceHandler.StoreSidInitialAltitude(
            ac,
            TestDispatch.Context(Random.Shared, artccConfig: artccConfig),
            ctoAltitude,
            isTakeoffClearance: true
        );

        var holding = new HoldingInPositionPhase();
        ac.Phases.Add(holding);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));

        if (climbViaOnTheGround)
        {
            DispatchContext cviaCtx = TestDispatch.Context(Random.Shared, artccConfig: artccConfig);
            CommandResult cvia = NavigationCommandHandler.DispatchClimbVia(new ClimbViaCommand(null), ac, cviaCtx);
            Assert.True(cvia.Success, cvia.Message);
            Assert.NotNull(ac.Procedure.ClimbViaTargetFt);
        }

        CommandResult cto = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            departure,
            assignedAltitude: ctoAltitude,
            requestedRunway: null,
            TestDispatch.Context(Random.Shared),
            TestDispatch.EmptyTaxi,
            NullLogger.Instance
        );
        Assert.True(cto.Success, cto.Message);

        ac.IsOnGround = false;
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.EndLatitude, rwy.EndLongitude), rwy.TrueHeading, 0.2);
        ac.TrueHeading = rwy.TrueHeading;
        ac.Altitude = rwy.ElevationFt + heightAglFt;
        ac.IndicatedAirspeed = 170;

        PhaseContext ctx = NewContext(ac, rwy);
        ac.Phases.SkipTo<InitialClimbPhase>(ctx);
        return new Departure(ac, ctx, rwy);
    }

    /// <summary>
    /// Clears an SJC3 departure out of KSJC for takeoff with <paramref name="ctoAltitude"/> or none and puts it airborne just
    /// past the DER in its initial climb, its TDLS initial altitude (5,000, the ARTCC's default for SJC3) resolved from
    /// <paramref name="artccConfig"/>.
    /// </summary>
    private static Departure StartSjc3Departure(ArtccConfigRoot artccConfig, int? ctoAltitude)
    {
        RunwayInfo rwy = Ksjc30();

        var ac = new AircraftState
        {
            Callsign = "SWA2210",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSJC",
                Destination = "KLAS",
                Route = "SJC3 AVE",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
            Phases = new PhaseList { AssignedRunway = rwy },
        };
        DepartureClearanceHandler.StoreSidInitialAltitude(
            ac,
            TestDispatch.Context(Random.Shared, artccConfig: artccConfig),
            ctoAltitude,
            isTakeoffClearance: true
        );
        Assert.Equal(5000, ac.Procedure.SidInitialAltitudeFt);

        var holding = new HoldingInPositionPhase();
        ac.Phases.Add(holding);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));

        CommandResult cto = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            assignedAltitude: ctoAltitude,
            requestedRunway: null,
            TestDispatch.Context(Random.Shared),
            TestDispatch.EmptyTaxi,
            NullLogger.Instance
        );
        Assert.True(cto.Success, cto.Message);

        ac.IsOnGround = false;
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.EndLatitude, rwy.EndLongitude), rwy.TrueHeading, 0.2);
        ac.TrueHeading = rwy.TrueHeading;
        ac.Altitude = rwy.ElevationFt + 450;
        ac.IndicatedAirspeed = 170;

        PhaseContext ctx = NewContext(ac, rwy);
        ac.Phases.SkipTo<InitialClimbPhase>(ctx);
        return new Departure(ac, ctx, rwy);
    }

    /// <summary>Flies until the departure is under its DepartureProcedurePhase, leveled in COAST9's 2,000 ft climb window.</summary>
    private Departure FlyIntoClimbWindow(ArtccConfigRoot artccConfig, int? ctoAltitude)
    {
        Departure departure = StartCoast9Departure(artccConfig, ctoAltitude, climbViaOnTheGround: false);
        AircraftState ac = departure.Aircraft;
        for (int t = 0; t < 360; t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);

            bool inProcedure = Assert.IsType<PhaseList>(ac.Phases).CurrentPhase is DepartureProcedurePhase;
            bool cappedAtWindow = (ac.Targets.TargetAltitude ?? double.MaxValue) <= WindowCeilingFt + LevelToleranceFt;
            if (inProcedure && (ac.Altitude >= WindowCeilingFt - 150) && cappedAtWindow)
            {
                _output.WriteLine($"reached window at t={t}: alt={ac.Altitude:F0} tgtAlt={ac.Targets.TargetAltitude:F0}");
                return departure;
            }
        }

        Assert.Fail("the aircraft never leveled in the COAST9 climb window under the DepartureProcedurePhase");
        return departure;
    }

    private static void FlyUntilAbove(Departure departure, double altitudeFt, int maxSeconds)
    {
        AircraftState ac = departure.Aircraft;
        for (int t = 0; (t < maxSeconds) && (ac.Altitude <= altitudeFt); t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(ac.Altitude > altitudeFt, $"the aircraft never climbed above {altitudeFt:F0} ft: {ac.Altitude:F0}");
    }

    private static FlightResult FlyFor(Departure departure, int seconds)
    {
        AircraftState ac = departure.Aircraft;
        double minAltitude = ac.Altitude;
        double maxAltitude = ac.Altitude;
        bool sawDepartureProcedure = false;
        for (int t = 0; t < seconds; t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
            minAltitude = Math.Min(minAltitude, ac.Altitude);
            maxAltitude = Math.Max(maxAltitude, ac.Altitude);
            sawDepartureProcedure |= ac.Phases?.CurrentPhase is DepartureProcedurePhase;
        }

        return new FlightResult(minAltitude, maxAltitude, sawDepartureProcedure);
    }

    /// <summary>
    /// Flies a KOAK departure, its live route cut to end at PORTE, until it is well past PORTE, then two more minutes;
    /// <paramref name="eachSecond"/> runs before each tick. Returns the highest altitude flown and the altitude at the
    /// closest approach to PORTE.
    /// </summary>
    private (double MaxAltitude, double AtPorte) FlyPastPorteTrimmedThere(Departure departure, Action<int> eachSecond)
    {
        AircraftState ac = departure.Aircraft;
        LatLon porte = FixPosition("PORTE");
        double closest = double.MaxValue;
        double atPorte = ac.Altitude;
        double maxAltitude = ac.Altitude;
        for (int t = 0; t < 1800; t++)
        {
            eachSecond(t);
            List<NavigationTarget> route = ac.Targets.NavigationRoute;
            int cut = route.FindIndex(f => f.Name == "PORTE");
            if ((cut >= 0) && (cut + 1 < route.Count))
            {
                route.RemoveRange(cut + 1, route.Count - cut - 1);
            }

            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
            maxAltitude = Math.Max(maxAltitude, ac.Altitude);
            double distanceNm = GeoMath.DistanceNm(ac.Position, porte);
            if (distanceNm < closest)
            {
                closest = distanceNm;
                atPorte = ac.Altitude;
            }
            else if ((closest < 2.0) && (distanceNm > closest + 2.0))
            {
                FlightResult after = FlyFor(departure, 120);
                return (Math.Max(maxAltitude, after.MaxAltitude), atPorte);
            }
        }

        _output.WriteLine($"closest to PORTE {closest:F1} nm at {atPorte:F0} ft");
        Assert.Fail("the departure did not pass PORTE within 1800 s");
        return (maxAltitude, atPorte);
    }

    /// <summary>
    /// Dispatches one command to a departure on the COAST9 fixture with the ZOA config, as production resolves it; asserts it
    /// parsed and was accepted.
    /// </summary>
    private void Dispatch(AircraftState aircraft, string text) =>
        DispatchLine(aircraft, text, TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));

    /// <summary>Dispatches one transmission (possibly several chained blocks) as the controller would send it.</summary>
    private void DispatchLine(AircraftState aircraft, string text, DispatchContext ctx)
    {
        CommandResult result = DispatchLineResult(aircraft, text, ctx);
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }

    /// <summary>As <see cref="Dispatch"/>, returning the result rather than asserting it was accepted.</summary>
    private CommandResult DispatchResult(AircraftState aircraft, string text) =>
        DispatchLineResult(aircraft, text, TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));

    private CommandResult DispatchLineResult(AircraftState aircraft, string text, DispatchContext ctx)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(Assert.IsType<CompoundCommand>(parsed.Value), aircraft, ctx);
        _output.WriteLine($"{text} -> {result.Message}");
        return result;
    }
}
