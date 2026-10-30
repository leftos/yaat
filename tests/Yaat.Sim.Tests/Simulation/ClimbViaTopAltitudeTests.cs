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

    /// <summary>COAST9's top altitude: every COAST9 transition's TDLS climb-via text at KOAK is "CLB VIA SID EXC MAINT 10000FT".</summary>
    private const int Coast9TopAltitudeFt = 10000;

    /// <summary>The first entry of KOAK's TDLS initial-altitude menu in the ZOA snapshot: what CTO stores for every OAK SID.</summary>
    private const int OakMenuInitialAltitudeFt = 2000;

    private static readonly LatLon KoakPosition = new(37.7212, -122.2208);

    private readonly ITestOutputHelper _output;

    public ClimbViaTopAltitudeTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private sealed record Departure(AircraftState Aircraft, PhaseContext Context, RunwayInfo Runway);

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
    /// NUEVO8's SNS transition carries "CLB VIA SID EXC MAINT 10000FT" in each of KOAK's op configs in the ZOA snapshot,
    /// above anything NUEVO8 publishes. A bare CVIA climbs to it, not to the 2,000 CTO stored from KOAK's menu.
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
        LatLon ksfo = new(37.6189, -122.3750);
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
    /// A SID departure cleared "CTO 050" with no climb via checks in naming its assigned altitude, "leaving (altitude) for
    /// 5,000, climbing via the COAST9 departure" (AIM 5-3-1.b.2.a).
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

        (string procTerm, string procTts) = PhraseologyVerbalizer.ProcedureName("COAST9");
        Assert.Contains($" for {PhraseologyVerbalizer.CompactAltitude(5000)}, climbing via the {procTerm} departure", line.Terminal);
        Assert.Contains($" for {AtcNumberParser.AltitudeToWords(5000)}, climbing via the {procTts} departure", line.Tts);
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
        Assert.Equal(new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 12000), porte.AltitudeRestriction);
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
        Assert.Equal(new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 11000), tipre.AltitudeRestriction);
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
        var crossing = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, altitudeFt);
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
        RunwayInfo rwy = Koak30();
        Assert.NotNull(NavigationDatabase.Instance.GetSid("KOAK", "COAST9"));

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
                Destination = "KSAN",
                Route = "COAST9 MCKEY LAX COMIX2",
                Altitude = PlannedAltitude.Ifr(39000),
                FlightRules = "IFR",
            },
            Phases = new PhaseList { AssignedRunway = rwy },
        };
        DepartureClearanceHandler.StoreSidInitialAltitude(ac, artccConfig);

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
            new DefaultDeparture(),
            assignedAltitude: ctoAltitude,
            TestDispatch.Context(Random.Shared),
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
    /// Dispatches one command to a departure on the COAST9 fixture with the ZOA config, as production resolves it; asserts it
    /// parsed and was accepted.
    /// </summary>
    private void Dispatch(AircraftState aircraft, string text) =>
        DispatchLine(aircraft, text, TestDispatch.Context(Random.Shared, artccConfig: LoadZoa()));

    /// <summary>Dispatches one transmission (possibly several chained blocks) as the controller would send it.</summary>
    private void DispatchLine(AircraftState aircraft, string text, DispatchContext ctx)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(Assert.IsType<CompoundCommand>(parsed.Value), aircraft, ctx);
        _output.WriteLine($"{text} -> {result.Message}");
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }
}
