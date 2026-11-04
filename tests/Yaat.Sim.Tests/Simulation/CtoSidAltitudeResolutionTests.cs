using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A bare CTO resolves the departure's altitude the way vTDLS fills the clearance: from the selected SID transition's own
/// TDLS defaults, never from the facility's initial-altitude menu. The SID is looked up in the facility's SID list, then in
/// each ops config's; the transition is the filed one matched by first route point, then by name, else the SID's first. A
/// climb-via default ("CLIMB VIA SID", "... EXC MAINT X") puts a climb via in force from takeoff; otherwise a parsable
/// initial altitude is a maintain; otherwise there is none and the instructor is warned. Real navdata, the committed ZOA
/// snapshot and the ZHU config bundled in the KIAH/KHOU departure recording.
/// </summary>
public class CtoSidAltitudeResolutionTests
{
    private const string ZhuRecordingPath = "TestData/issue187-star-dvia-recording.yaat-bug-report-bundle.zip";
    private const double LevelToleranceFt = 50.0;

    private readonly ITestOutputHelper _output;

    public CtoSidAltitudeResolutionTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    // ----- The resolver -----

    /// <summary>Each row's SID and filed route against the ZOA snapshot, and what its selected transition's defaults give.</summary>
    [Theory]
    // COAST9: every transition, MCKEY's "- - - -" entry included, is "CLB VIA SID EXC MAINT 10000FT".
    [InlineData("KOAK", "COAST9 MCKEY LAX COMIX2", TdlsDepartureAltitudeKind.ClimbVia, 10000)]
    // OAK6's "- - - -" variants are told apart by first route point: ENI's is EXC MAINT 10000FT, RBL's EXC MAINT FL190.
    [InlineData("KOAK", "OAK6 ENI V200 RBL", TdlsDepartureAltitudeKind.ClimbVia, 10000)]
    [InlineData("KOAK", "OAK6 RBL V23 RDD", TdlsDepartureAltitudeKind.ClimbVia, 19000)]
    // A filed OAK7 finds the configured OAK6 by its base name.
    [InlineData("KOAK", "OAK7 RBL V23 RDD", TdlsDepartureAltitudeKind.ClimbVia, 19000)]
    // CNDEL5: a plain "CLIMB VIA SID", to the SID's published top.
    [InlineData("KOAK", "CNDEL5 SUSEY", TdlsDepartureAltitudeKind.ClimbVia, null)]
    // NIMI6 and QUAKE2 carry an initial altitude and no climb via.
    [InlineData("KOAK", "NIMI6 SAU", TdlsDepartureAltitudeKind.Maintain, 3000)]
    [InlineData("KOAK", "QUAKE2 SAU", TdlsDepartureAltitudeKind.Maintain, 5000)]
    // SSTIK5 carries no initial altitude, only a plain "CLIMB VIA SID"; the menu's 10,000 never takes part.
    [InlineData("KSFO", "SSTIK5 SUSEY", TdlsDepartureAltitudeKind.ClimbVia, null)]
    // MOLEN9: the filed ENI is "EXC MAINT 15000FT"; a filed transition matching none takes the first, "- - - -", 3000FT.
    [InlineData("KSFO", "MOLEN9 ENI", TdlsDepartureAltitudeKind.ClimbVia, 15000)]
    [InlineData("KSFO", "MOLEN9 SAU", TdlsDepartureAltitudeKind.ClimbVia, 3000)]
    // SCTWN4's initial altitude is the "- - - -" placeholder, no value: its climb via "EXC MAINT 9000FT" stands.
    [InlineData("KSMF", "SCTWN4 MACUS", TdlsDepartureAltitudeKind.ClimbVia, 9000)]
    // FROGO1 carries neither.
    [InlineData("KSMF", "FROGO1", TdlsDepartureAltitudeKind.None, null)]
    // A SID the config does not hold, and a route that starts at a fix (OAK is not OAK6).
    [InlineData("KOAK", "ZZZZZ9 SAU", TdlsDepartureAltitudeKind.None, null)]
    [InlineData("KOAK", "OAK V27 SNS", TdlsDepartureAltitudeKind.None, null)]
    public void Zoa_ResolvesTheSelectedTransitionsDefault(string departure, string route, TdlsDepartureAltitudeKind kind, int? altitudeFt)
    {
        TdlsDepartureAltitude resolved = LoadZoa().GetSidDepartureAltitude(departure, route);

        _output.WriteLine($"{departure} {route} -> {resolved}");
        Assert.Equal(kind, resolved.Kind);
        Assert.Equal(altitudeFt, resolved.AltitudeFt);
    }

    /// <summary>
    /// KIAH's transitions carry no initial altitude and "CLIMB VIA SID EXC MAINT 4000FT": a climb via to 4,000. KHOU's carry
    /// an initial altitude of 5000 and no climb via: a maintain.
    /// </summary>
    [Theory]
    [InlineData("KIAH", "BLTWY7 CRIED", TdlsDepartureAltitudeKind.ClimbVia, 4000)]
    [InlineData("KHOU", "BLTWY7 CRIED", TdlsDepartureAltitudeKind.Maintain, 5000)]
    public void Zhu_ResolvesTheSelectedTransitionsDefault(string departure, string route, TdlsDepartureAltitudeKind kind, int altitudeFt)
    {
        TdlsDepartureAltitude resolved = LoadZhu().GetSidDepartureAltitude(departure, route);

        _output.WriteLine($"{departure} {route} -> {resolved}");
        Assert.Equal(kind, resolved.Kind);
        Assert.Equal(altitudeFt, resolved.AltitudeFt);
    }

    // ----- The store, through a dispatched takeoff clearance -----

    /// <summary>
    /// A bare CTO on KIAH's BLTWY7 puts the climb via in force from takeoff with 4,000 as its "except maintain": the
    /// aircraft checks in climbing via the SID for 4,000 and levels there.
    /// </summary>
    [Fact]
    public void BareCto_KiahClimbViaDefault_ClimbsViaTheSidAndLevelsAtTheExceptMaintain()
    {
        AircraftState ac = HoldingShortAtKiah();
        DispatchContext ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZhu());

        DispatchLine(ac, "CTO", ctx);

        Assert.True(ac.Procedure.SidViaMode);
        Assert.Equal(4000, ac.Procedure.SidViaCeiling);
        Assert.True(ac.Procedure.ClimbViaTargetFt >= 4000, $"climb via top {ac.Procedure.ClimbViaTargetFt}");
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        Assert.Null(ac.Targets.AssignedAltitude);
        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("no initial altitude", StringComparison.Ordinal));

        PhaseContext phaseCtx = Airborne(ac);
        Fly(ac, phaseCtx, 30);
        PilotSpeechText line = Assert.IsType<PilotSpeechText>(PilotResponder.BuildAirborneCheckIn(ac, CheckInScenario(), ac.Position));
        _output.WriteLine($"{ac.Altitude:F0} ft: {line.Terminal}");
        Assert.Contains($"for {PhraseologyVerbalizer.CompactAltitude(4000)}, climbing via the", line.Terminal);

        double maxAltitude = Fly(ac, phaseCtx, 300);
        _output.WriteLine($"maxAlt={maxAltitude:F0} finalAlt={ac.Altitude:F0}");
        Assert.True(maxAltitude < 4000 + LevelToleranceFt, $"climbed through the except-maintain 4,000: {maxAltitude:F0}");
        Assert.InRange(ac.Altitude, 4000 - LevelToleranceFt, 4000 + LevelToleranceFt);
    }

    /// <summary>A bare CTO on a SID whose transition carries an initial altitude (KOAK QUAKE2, 5000FT) stores it as a maintain.</summary>
    [Fact]
    public void BareCto_MaintainDefault_StoresTheInitialAltitude_NoClimbVia()
    {
        AircraftState ac = HoldingShortAtOakland("QUAKE2 SAU");

        DispatchLine(ac, "CTO", TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZoa()));

        Assert.Equal(5000, ac.Procedure.SidInitialAltitudeFt);
        Assert.False(ac.Procedure.SidViaMode);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Procedure.SidViaCeiling);
    }

    /// <summary>
    /// A bare CTO on a filed SID the airport publishes (COAST9) with no TDLS value to resolve (here no ARTCC config at all)
    /// stores nothing and warns the instructor; a bare LUAW, which authorises no climb, does not.
    /// </summary>
    [Theory]
    [InlineData("CTO", true)]
    [InlineData("LUAW", false)]
    public void BareClearance_FiledSidWithoutTdlsValue_WarnsOnTakeoffOnly(string command, bool warns)
    {
        AircraftState ac = HoldingShortAtOakland("COAST9 MCKEY LAX COMIX2");

        DispatchLine(ac, command, TestDispatch.Context(Random.Shared, validateDctFixes: false));

        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(warns ? 1 : 0, ac.PendingWarnings.Count(w => w == NoInitialAltitudeWarning(ac)));
    }

    /// <summary>A bare CTO on a departure that files no SID (no route, or one starting at a fix) resolves nothing and warns nothing.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("OAK V27 SNS")]
    public void BareCto_NoSidFiled_StaysSilent(string route)
    {
        AircraftState ac = HoldingShortAtOakland(route);

        DispatchLine(ac, "CTO", TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZoa()));

        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.DoesNotContain(NoInitialAltitudeWarning(ac), ac.PendingWarnings);
    }

    private string NoInitialAltitudeWarning(AircraftState ac)
    {
        foreach (string w in ac.PendingWarnings)
        {
            _output.WriteLine($"warning: {w}");
        }

        return $"no initial altitude for {ac.Callsign}; assign one";
    }

    /// <summary>An explicit climb via given in position ("CVIA 060") outranks the TDLS default at the bare CTO after it.</summary>
    [Fact]
    public void GroundClimbViaThenBareCto_KeepsTheExplicitClimbVia()
    {
        AircraftState ac = InPositionAtKiah();
        DispatchContext ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZhu());

        // Staged as the existing climb-via fixtures stage it: the ground phases refuse a dispatched CVIA.
        Assert.True(NavigationCommandHandler.DispatchClimbVia(new ClimbViaCommand(6000), ac, ctx).Success);
        DispatchLine(ac, "CTO", ctx);

        Assert.Equal(6000, ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(6000, ac.Procedure.SidViaCeiling);
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
    }

    /// <summary>An altitude assigned ahead of the takeoff clearance (as a CM would) outranks the TDLS climb via: none is put in force.</summary>
    [Fact]
    public void ClimbThenBareCto_PutsNoClimbViaInForce()
    {
        AircraftState ac = InPositionAtKiah();
        DispatchContext ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZhu());

        // Staged: the ground phases refuse a dispatched CM, so the altitude stands in for one assigned before the clearance.
        ac.Targets.AssignedAltitude = 3000;
        DispatchLine(ac, "CTO", ctx);

        Assert.Equal(3000, ac.Targets.AssignedAltitude);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Procedure.SidViaCeiling);
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
    }

    /// <summary>A takeoff clearance carrying an altitude ("CTO 150 050": heading 150, climb 5,000) puts no climb via in force.</summary>
    [Fact]
    public void CtoWithAltitude_PutsNoClimbViaInForce()
    {
        AircraftState ac = HoldingShortAtKiah();

        DispatchLine(ac, "CTO 150 050", TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZhu()));

        Assert.Equal(5000, ac.Targets.AssignedAltitude);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Procedure.SidViaCeiling);
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        Assert.False(ac.Procedure.SidViaMode);
        Assert.Null(ac.Procedure.ActiveSidId);
    }

    /// <summary>
    /// A takeoff clearance that is not given effect — "CTO 28L" to a departure lined up on 28R draws the pilot's query
    /// and changes nothing — resolves nothing either: no climb via, no SID activated, no warning.
    /// </summary>
    [Fact]
    public void CtoNamingAnotherRunway_ResolvesNothing()
    {
        AircraftState ac = InPositionAt("KOAK", "28R", "COAST9 MCKEY LAX COMIX2");
        int warningsBefore = ac.PendingWarnings.Count;

        CommandResult result = DispatchUnchecked(ac, "CTO 28L", TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZoa()));

        _output.WriteLine($"CTO 28L -> {result.Success} {result.Message}");
        Assert.Null(ac.Phases!.DepartureClearance);
        Assert.False(ac.Procedure.SidViaMode);
        Assert.Null(ac.Procedure.ActiveSidId);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(warningsBefore, ac.PendingWarnings.Count);
    }

    /// <summary>
    /// The climb via a TDLS default puts in force at takeoff is the one the clearance read to the crew before anything said
    /// on the ground, so a speed assigned on the ground before the takeoff clearance survives it (7110.65 5-7-1.d; AIM
    /// 5-2-9.h.6.a).
    /// </summary>
    [Fact]
    public void GroundSpeedThenBareCto_KeepsTheSpeed()
    {
        AircraftState ac = InPositionAtKiah();
        // Staged: the ground phases refuse a dispatched SPD, so the speed stands in for one assigned before the clearance.
        ac.Targets.AssignedSpeed = 180;
        ac.Targets.TargetSpeed = 180;
        ac.Targets.HasExplicitSpeedCommand = true;
        ac.Targets.SpeedCommandIsControllerIssued = true;

        DispatchLine(ac, "CTO", TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZhu()));

        Assert.Equal(4000, ac.Procedure.SidViaCeiling);
        Assert.Equal(180, ac.Targets.AssignedSpeed);
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    /// <summary>
    /// A plain "CLIMB VIA SID" default (KOAK CNDEL5) climbs to the SID's published top, the highest altitude its flown legs
    /// publish (runway 30, the common route, SUSEY), with no "except maintain".
    /// </summary>
    [Fact]
    public void BareCto_PlainClimbViaDefault_ClimbsToThePublishedTop()
    {
        CifpSidProcedure sid = Assert.IsType<CifpSidProcedure>(NavigationDatabase.Instance.GetSid("KOAK", "CNDEL5"));
        int published = sid.RunwayTransitions["RW30"]
            .Legs.Concat(sid.CommonLegs)
            .Concat(sid.EnrouteTransitions["SUSEY"].Legs)
            .Select(l => l.Altitude)
            .OfType<CifpAltitudeRestriction>()
            .Max(r => r.Type == CifpAltitudeRestrictionType.Between ? Math.Max(r.Altitude1Ft, r.Altitude2Ft ?? 0) : r.Altitude1Ft);
        AircraftState ac = InPositionAt("KOAK", "30", "CNDEL5 SUSEY");

        DispatchLine(ac, "CTO", TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZoa()));

        Assert.True(ac.Procedure.SidViaMode);
        Assert.Null(ac.Procedure.SidViaCeiling);
        Assert.Equal(published, ac.Procedure.ClimbViaTargetFt);
    }

    /// <summary>A departing CTOPP (<c>CTOPP 150</c>) resolves the TDLS default as a bare CTO does: KIAH's climb via to 4,000.</summary>
    [Fact]
    public void DepartingCtopp_KiahClimbViaDefault_PutsTheClimbViaInForce()
    {
        AircraftState ac = HoldingShortAtKiah();
        ac.AircraftType = "S76";

        DispatchLine(ac, "CTOPP 150", TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZhu()));

        Assert.True(ac.Procedure.SidViaMode);
        Assert.Equal(4000, ac.Procedure.SidViaCeiling);
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
    }

    /// <summary>
    /// A bare CTOPP is a hover, not a departing clearance: it resolves nothing from the TDLS defaults and leaves the hover
    /// altitude the vertical liftoff set as the target, so the helicopter rises to the hover rather than the via top.
    /// </summary>
    [Fact]
    public void BareCtopp_Hover_ResolvesNothingAndKeepsTheHover()
    {
        AircraftState ac = HoldingShortAtKiah();
        ac.AircraftType = "S76";
        double elevation = ac.Altitude;
        int warningsBefore = ac.PendingWarnings.Count;

        DispatchLine(ac, "CTOPP", TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZhu()));

        HelicopterTakeoffPhase hover = Assert.IsType<HelicopterTakeoffPhase>(ac.Phases!.CurrentPhase);
        Assert.False(ac.Procedure.SidViaMode);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        Assert.Equal(warningsBefore, ac.PendingWarnings.Count);
        Assert.Equal(elevation + hover.CompletionAgl, Assert.IsType<double>(ac.Targets.TargetAltitude));
    }

    /// <summary>
    /// A filed SID the airport's TDLS config does not hold (KRNO GMBLL1 in the ZOA snapshot) has no value: a bare CTO
    /// warns the instructor.
    /// </summary>
    [Fact]
    public void BareCto_SidAbsentFromTheTdlsConfig_Warns()
    {
        AircraftState ac = HoldingShortAt("KRNO", "17R", "GMBLL1");

        DispatchLine(ac, "CTO", TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZoa()));

        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(1, ac.PendingWarnings.Count(w => w == NoInitialAltitudeWarning(ac)));
    }

    /// <summary>
    /// LUAW then CTO: the line-up clearance resolves the TDLS default silently, and the takeoff clearance keeps the climb via
    /// it put in force; with no value, only the takeoff clearance warns, once.
    /// </summary>
    [Theory]
    [InlineData("KIAH", "15L", "BLTWY7 CRIED", 4000, 0)]
    [InlineData("KRNO", "17R", "GMBLL1", null, 1)]
    public void LuawThenCto_ResolvesOnceAndWarnsOnlyAtTakeoff(string airport, string runway, string route, int? ceiling, int warnings)
    {
        AircraftState ac = HoldingShortAt(airport, runway, route);
        DispatchContext ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: airport == "KIAH" ? LoadZhu() : LoadZoa());

        DispatchLine(ac, "LUAW", ctx);
        Assert.Equal(0, ac.PendingWarnings.Count(w => w == NoInitialAltitudeWarning(ac)));
        Assert.Equal(ceiling, ac.Procedure.SidViaCeiling);

        DispatchLine(ac, "CTO", ctx);
        Assert.Equal(ceiling, ac.Procedure.SidViaCeiling);
        Assert.Equal(warnings, ac.PendingWarnings.Count(w => w == NoInitialAltitudeWarning(ac)));
    }

    /// <summary>
    /// LUAW then a takeoff clearance carrying an altitude: the altitude is the last clearance, so it cancels the climb via the
    /// line-up put in force (AIM 5-2-9.h.10), and the departure climbs to and levels at it.
    /// </summary>
    [Fact]
    public void LuawThenCtoWithAltitude_EndsTheTdlsClimbVia()
    {
        AircraftState ac = HoldingShortAtKiah();
        DispatchContext ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: LoadZhu());

        DispatchLine(ac, "LUAW", ctx);
        Assert.True(ac.Procedure.SidViaMode);
        Assert.Equal(4000, ac.Procedure.SidViaCeiling);

        DispatchLine(ac, "CTO 150 050", ctx);

        Assert.False(ac.Procedure.SidViaMode);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Procedure.SidViaCeiling);
        Assert.Equal(5000, ac.Targets.AssignedAltitude);

        double maxAltitude = Fly(ac, Airborne(ac), 400);
        _output.WriteLine($"maxAlt={maxAltitude:F0} finalAlt={ac.Altitude:F0}");
        Assert.True(maxAltitude < 5000 + LevelToleranceFt, $"climbed through the assigned 5,000: {maxAltitude:F0}");
        Assert.InRange(ac.Altitude, 5000 - LevelToleranceFt, 5000 + LevelToleranceFt);
    }

    // ----- Fixtures -----

    private static ArtccConfigRoot LoadZoa() => Assert.IsType<ArtccConfigRoot>(TestArtccConfig.LoadZoa());

    private static ArtccConfigRoot LoadZhu()
    {
        SessionRecording recording = Assert.IsType<SessionRecording>(RecordingLoader.Load(ZhuRecordingPath));
        string json = Assert.IsType<string>(recording.ArtccConfigJson);
        return Assert.IsType<ArtccConfigRoot>(JsonSerializer.Deserialize<ArtccConfigRoot>(json, RecordingJsonOptions.Default));
    }

    private static AircraftState HoldingShortAtOakland(string route) =>
        HoldingShort("SWA1822", "KOAK", "KLAX", route, "28R", new LatLon(37.728, -122.218), new TrueHeading(280), 6);

    private static AircraftState HoldingShortAtKiah() => HoldingShortAt("KIAH", "15L", "BLTWY7 CRIED");

    private static AircraftState InPositionAtKiah() => InPositionAt("KIAH", "15L", "BLTWY7 CRIED");

    /// <summary>A B738 departure from <paramref name="airport"/> holding short of <paramref name="runway"/>'s threshold.</summary>
    private static AircraftState HoldingShortAt(string airport, string runway, string route)
    {
        RunwayInfo rwy = DepartureEnd(airport, runway);
        return HoldingShort(
            "UAL1402",
            airport,
            "KLAX",
            route,
            runway,
            new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            rwy.TrueHeading,
            rwy.ElevationFt
        );
    }

    /// <summary>A B738 departure from <paramref name="airport"/> in position on <paramref name="runway"/>, not yet cleared for takeoff.</summary>
    private static AircraftState InPositionAt(string airport, string runway, string route)
    {
        RunwayInfo rwy = DepartureEnd(airport, runway);
        var ac = new AircraftState
        {
            Callsign = "UAL1402",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = airport,
                Destination = "KLAX",
                Route = route,
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
            Phases = new PhaseList { AssignedRunway = rwy },
        };
        ac.Phases.Add(new HoldingInPositionPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        return ac;
    }

    /// <summary><paramref name="airport"/>'s runway <paramref name="designator"/> as the navdata publishes it, that end as end 1.</summary>
    private static RunwayInfo DepartureEnd(string airport, string designator)
    {
        RunwayInfo phys = Assert.IsType<RunwayInfo>(
            NavigationDatabase
                .Instance.GetRunways(airport)
                .Concat(NavigationDatabase.Instance.GetRunways(NavigationDatabase.NormalizeAirport(airport)))
                .FirstOrDefault(r => (r.Id.End1 == designator) || (r.Id.End2 == designator))
        );
        bool isEnd2 = phys.Id.End1 != designator;
        return new RunwayInfo
        {
            AirportId = airport,
            Id = phys.Id,
            Designator = designator,
            Lat1 = isEnd2 ? phys.Lat2 : phys.Lat1,
            Lon1 = isEnd2 ? phys.Lon2 : phys.Lon1,
            Elevation1Ft = isEnd2 ? phys.Elevation2Ft : phys.Elevation1Ft,
            TrueHeading1 = isEnd2 ? phys.TrueHeading2 : phys.TrueHeading1,
            Lat2 = isEnd2 ? phys.Lat1 : phys.Lat2,
            Lon2 = isEnd2 ? phys.Lon1 : phys.Lon2,
            Elevation2Ft = isEnd2 ? phys.Elevation1Ft : phys.Elevation2Ft,
            TrueHeading2 = isEnd2 ? phys.TrueHeading1 : phys.TrueHeading2,
            WidthFt = phys.WidthFt,
        };
    }

    private static AircraftState HoldingShort(
        string callsign,
        string departure,
        string destination,
        string route,
        string runway,
        LatLon position,
        TrueHeading heading,
        double elevation
    )
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = heading,
            Altitude = elevation,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = departure,
                Destination = destination,
                Route = route,
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
            Phases = new PhaseList(),
        };
        ac.Phases.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 10,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = runway,
                },
                HoldShortArrival.HeldShort
            )
        );
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        return ac;
    }

    /// <summary>
    /// Puts the cleared departure airborne past the DER in its initial climb; the roll needs a ground layout this class does
    /// not load, so the lineup and takeoff phases are skipped.
    /// </summary>
    private static PhaseContext Airborne(AircraftState ac)
    {
        RunwayInfo runway = Assert.IsType<RunwayInfo>(ac.Phases!.AssignedRunway);
        ac.IsOnGround = false;
        ac.Position = GeoMath.ProjectPoint(new LatLon(runway.EndLatitude, runway.EndLongitude), runway.TrueHeading, 0.3);
        ac.TrueHeading = runway.TrueHeading;
        ac.Altitude = runway.ElevationFt + 450;
        ac.IndicatedAirspeed = 190;

        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
        };
        ac.Phases.SkipTo<InitialClimbPhase>(ctx);
        return ctx;
    }

    /// <summary>Ticks phase and physics for <paramref name="seconds"/>; returns the highest altitude flown.</summary>
    private static double Fly(AircraftState ac, PhaseContext ctx, int seconds)
    {
        double max = ac.Altitude;
        for (int t = 0; t < seconds; t++)
        {
            PhaseRunner.Tick(ac, ctx);
            FlightPhysics.Update(ac, 1.0);
            max = Math.Max(max, ac.Altitude);
        }

        return max;
    }

    private static SimScenarioState CheckInScenario() =>
        new()
        {
            ScenarioId = "cto-sid-altitude-check-in",
            ScenarioName = "CTO SID altitude check-in",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "KIAH",
            SoloTrainingMode = true,
            StudentPositionType = "APP",
        };

    private static CommandResult DispatchUnchecked(AircraftState aircraft, string text, DispatchContext ctx)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(Assert.IsType<CompoundCommand>(parsed.Value), aircraft, ctx);
    }

    private void DispatchLine(AircraftState aircraft, string text, DispatchContext ctx)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(Assert.IsType<CompoundCommand>(parsed.Value), aircraft, ctx);
        _output.WriteLine($"{text} -> {result.Message}");
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }
}
