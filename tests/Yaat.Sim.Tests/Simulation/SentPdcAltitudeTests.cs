using System.Globalization;
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
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Simulation.Tdls;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The altitude of a sent PDC is what the pilot flies: captured when <c>TDLSS</c> sends it, read by the line-up and takeoff
/// clearances before the facility's TDLS default. The climb-via text governs; a Maintain beside a bare climb via is its
/// "except maintain"; a dump or a STARS track removal after the send keeps it; a PDC dumped before it is sent captures
/// nothing; a later voice altitude outranks it. The TDLS bodies run on the bare engine over the real ZOA/ZLA data, the
/// clearances through the dispatcher, the climb on real navdata.
/// </summary>
public class SentPdcAltitudeTests
{
    private const string Callsign = "SWA1234";
    private const double LevelToleranceFt = 50.0;
    private const string Trukn2Route = "TRUKN2 DEDHD";

    private readonly ITestOutputHelper _output;

    public SentPdcAltitudeTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>SFO TRUKN2, whose TDLS default is a bare climb via (FL190): a sent "EXC MAINT 7000" levels at 7,000.</summary>
    [Fact]
    public void SentExceptMaintain_LuawThenCto_LevelsAtThatAltitude()
    {
        Departure dep = AtSfo();
        Send(dep, "CLIMB VIA SID EXC MAINT 7000", "");

        DispatchLine(dep, "LUAW");
        DispatchLine(dep, "CTO");

        Assert.True(dep.Aircraft.Procedure.SidViaMode);
        Assert.Equal(7000, dep.Aircraft.Procedure.SidViaCeiling);
        Assert.Equal(7000, dep.Aircraft.Procedure.ClimbViaTargetFt);
        AssertLevelsAt(dep.Aircraft, 7000);
    }

    /// <summary>OAK COAST9, whose TDLS default is "CLB VIA SID EXC MAINT 10000FT": a sent Maintain 5000 is a maintain, not the via.</summary>
    [Fact]
    public void SentMaintain_OnAClimbViaDefaultSid_MaintainsIt()
    {
        Departure dep = At(LoadZoa(), "ZOA", "KOAK", "OAK", "28R", "COAST9 MCKEY LAX COMIX2");
        Send(dep, "", "5000");

        DispatchLine(dep, "CTO");

        Assert.Equal(5000, dep.Aircraft.Procedure.SidInitialAltitudeFt);
        Assert.False(dep.Aircraft.Procedure.SidViaMode);
        Assert.Null(dep.Aircraft.Procedure.ClimbViaTargetFt);
        AssertLevelsAt(dep.Aircraft, 5000);
    }

    /// <summary>
    /// OAK SUNNE1, whose TDLS default is a Maintain 5000: a sent bare "CLIMB VIA SID" puts the climb via in force to the
    /// SID's published top (SUNNE1 publishes only floors, so FL190) and the departure climbs past 5,000.
    /// </summary>
    [Fact]
    public void SentBareClimbVia_OnAMaintainDefaultSid_ClimbsViaToThePublishedTop()
    {
        Departure dep = At(LoadZoa(), "ZOA", "KOAK", "OAK", "30", "SUNNE1 SUNNE");
        Send(dep, "CLIMB VIA SID", "");

        DispatchLine(dep, "CTO");

        Assert.True(dep.Aircraft.Procedure.SidViaMode);
        Assert.Null(dep.Aircraft.Procedure.SidViaCeiling);
        Assert.Equal(19000, dep.Aircraft.Procedure.ClimbViaTargetFt);
        Assert.Null(dep.Aircraft.Procedure.SidInitialAltitudeFt);
        double maxAltitude = Fly(dep.Aircraft, Airborne(dep.Aircraft), 400);
        _output.WriteLine($"maxAlt={maxAltitude:F0}");
        Assert.True(maxAltitude > 5000 + 500, $"the sent climb via levelled at the 5,000 default: {maxAltitude:F0}");
    }

    /// <summary>
    /// KLAS GIDGT4 publishes JETTG at or below 14,000 from runways 8 and only a floor from 1L (top FL190): a sent bare climb
    /// via has its top worked out again for the runway set at the takeoff clearance.
    /// </summary>
    [Fact]
    public void SentBareClimbVia_RunwayChangeBeforeTakeoff_ReResolvesTheTop()
    {
        Departure dep = At(Assert.IsType<ArtccConfigRoot>(TestArtccConfig.LoadZla()), "ZLA", "KLAS", "LAS", "08L", "GIDGT4 TUKRR");
        Send(dep, "CLIMB VIA SID", "");

        DispatchLine(dep, "LUAW");
        Assert.Equal(14000, dep.Aircraft.Procedure.ClimbViaTargetFt);

        dep.Aircraft.Procedure.DepartureRunway = "01L";
        DepartureClearanceHandler.StoreSidInitialAltitude(dep.Aircraft, Context(dep), clearanceAltitude: null, isTakeoffClearance: true);

        Assert.Equal(19000, dep.Aircraft.Procedure.ClimbViaTargetFt);
        Assert.Null(dep.Aircraft.Procedure.SidViaCeiling);
    }

    /// <summary>A bare sent climb via with a typed Maintain 8000 is "climb via SID except maintain 8,000".</summary>
    [Fact]
    public void SentBareClimbViaWithMaintain_ClimbsViaExceptMaintainThatAltitude()
    {
        Departure dep = AtSfo();
        Send(dep, "CLIMB VIA SID", "8000");

        DispatchLine(dep, "CTO");

        Assert.True(dep.Aircraft.Procedure.SidViaMode);
        Assert.Equal(8000, dep.Aircraft.Procedure.SidViaCeiling);
        Assert.Null(dep.Aircraft.Procedure.SidInitialAltitudeFt);
        AssertLevelsAt(dep.Aircraft, 8000);
    }

    /// <summary>The climb-via text's "EXC MAINT 7000" governs a different typed Maintain 9000.</summary>
    [Fact]
    public void SentExceptMaintainText_BeatsADifferentMaintain()
    {
        Departure dep = AtSfo();
        Send(dep, "CLIMB VIA SID EXC MAINT 7000", "9000");

        DispatchLine(dep, "CTO");

        Assert.Equal(7000, dep.Aircraft.Procedure.SidViaCeiling);
        Assert.Equal(7000, dep.Aircraft.Procedure.ClimbViaTargetFt);
    }

    /// <summary>A dump after the send removes the item but not what the pilot was cleared to fly.</summary>
    [Fact]
    public void DumpAfterSend_StillFliesTheSentAltitude()
    {
        Departure dep = AtSfo();
        Send(dep, "CLIMB VIA SID EXC MAINT 7000", "");

        Assert.True(Issue(dep, "TDLSDUMP").Result.Success);
        Assert.Empty(dep.Engine.Tdls.Items);
        DispatchLine(dep, "CTO");

        Assert.Equal(7000, dep.Aircraft.Procedure.SidViaCeiling);
        Assert.Equal(7000, dep.Aircraft.Procedure.ClimbViaTargetFt);
    }

    /// <summary>A STARS track removal after the send removes the item but not what the pilot was cleared to fly.</summary>
    [Fact]
    public void TrackRemovalAfterSend_StillFliesTheSentAltitude()
    {
        Departure dep = AtSfo();
        Send(dep, "CLIMB VIA SID EXC MAINT 7000", "");

        dep.Aircraft.Track.Owner = TrackOwner.CreateStars("NCT_2B", "NCT", 2, "B");
        dep.Engine.TickTdlsTrackRemoval();
        Assert.Empty(dep.Engine.Tdls.Items);
        DispatchLine(dep, "CTO");

        Assert.Equal(7000, dep.Aircraft.Procedure.SidViaCeiling);
        Assert.Equal(7000, dep.Aircraft.Procedure.ClimbViaTargetFt);
    }

    /// <summary>A PDC dumped before it is sent captures nothing: the send is refused and the takeoff flies TRUKN2's default (FL190).</summary>
    [Fact]
    public void DumpBeforeSend_FliesTheFacilityDefault()
    {
        Departure dep = AtSfo();
        Assert.True(Issue(dep, "TDLSDUMP").Result.Success);
        Assert.False(Issue(dep, SendLine("CLIMB VIA SID EXC MAINT 7000", "")).Result.Success);

        DispatchLine(dep, "CTO");

        Assert.Equal(TdlsDepartureAltitudeKind.None, dep.Aircraft.Procedure.SentPdcAltitudeKind);
        Assert.True(dep.Aircraft.Procedure.SidViaMode);
        Assert.Null(dep.Aircraft.Procedure.SidViaCeiling);
        Assert.Equal(19000, dep.Aircraft.Procedure.ClimbViaTargetFt);
    }

    /// <summary>
    /// A voice climb after the send outranks the sent PDC: it ends the via the line-up put in force, the takeoff puts none
    /// back, and the departure levels at 9,000.
    /// </summary>
    [Fact]
    public void VoiceClimbAfterSend_ThenCto_FliesTheVoiceAltitude()
    {
        Departure dep = AtSfo();
        Send(dep, "CLIMB VIA SID EXC MAINT 7000", "");

        DispatchLine(dep, "LUAW");
        Assert.Equal(7000, dep.Aircraft.Procedure.SidViaCeiling);
        // Staged through the CM handler, as CtoSidAltitudeResolutionTests stages a ground CM: the ground phases refuse a dispatched one.
        Assert.True(
            FlightCommandHandler.ApplyClimbMaintain(new ClimbMaintainCommand(9000, AltitudeAssignmentModifier.None), dep.Aircraft, null).Success
        );
        DispatchLine(dep, "CTO");

        Assert.Equal(9000, dep.Aircraft.Targets.AssignedAltitude);
        Assert.False(dep.Aircraft.Procedure.SidViaMode);
        Assert.Null(dep.Aircraft.Procedure.ClimbViaTargetFt);
        AssertLevelsAt(dep.Aircraft, 9000);
    }

    /// <summary>
    /// A voice bare CVIA after a sent "EXC MAINT 7000" is a new clearance: it keeps the facility-default top (FL190), not
    /// the sent 7,000.
    /// </summary>
    [Fact]
    public void VoiceBareCviaAfterSentExceptMaintain_KeepsTheFacilityDefaultTop()
    {
        Departure dep = AtSfo();
        Send(dep, "CLIMB VIA SID EXC MAINT 7000", "");
        DispatchLine(dep, "CTO");
        Assert.Equal(7000, dep.Aircraft.Procedure.ClimbViaTargetFt);
        Fly(dep.Aircraft, Airborne(dep.Aircraft), 10);

        DispatchLine(dep, "CVIA");

        Assert.Null(dep.Aircraft.Procedure.SidViaCeiling);
        Assert.Equal(19000, dep.Aircraft.Procedure.ClimbViaTargetFt);
    }

    /// <summary>The captured sent altitude survives a snapshot taken after the item was dumped.</summary>
    [Fact]
    public void CapturedSentAltitude_RoundTripsThroughSnapshotAfterADump()
    {
        Departure dep = AtSfo();
        Send(dep, "CLIMB VIA SID EXC MAINT 7000", "");
        Assert.True(Issue(dep, "TDLSDUMP").Result.Success);

        AircraftState restored = RoundTripThroughJson(dep.Aircraft);

        Assert.Equal(TdlsDepartureAltitudeKind.ClimbVia, restored.Procedure.SentPdcAltitudeKind);
        Assert.Equal(7000, restored.Procedure.SentPdcAltitudeFt);
    }

    /// <summary>An aircraft with no sent PDC writes no captured altitude, so its snapshot is unchanged, and restores none.</summary>
    [Fact]
    public void NoCapture_IsAbsentFromTheSnapshot()
    {
        Departure dep = AtSfo();

        string json = JsonSerializer.Serialize(dep.Aircraft.ToSnapshot(), RecordingJsonOptions.Default);

        Assert.DoesNotContain(nameof(AircraftProcedure.SentPdcAltitudeKind), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(AircraftProcedure.SentPdcAltitudeFt), json, StringComparison.OrdinalIgnoreCase);
        AircraftState restored = RoundTripThroughJson(dep.Aircraft);
        Assert.Equal(TdlsDepartureAltitudeKind.None, restored.Procedure.SentPdcAltitudeKind);
        Assert.Null(restored.Procedure.SentPdcAltitudeFt);
    }

    /// <summary>
    /// An amendment moving the departure to an airport another TDLS facility serves clears the capture, whether the item
    /// still stands or was dumped first: the takeoff at KOAK flies KOAK's COAST9 default (EXC MAINT 10,000), not the 7,000
    /// sent at KSFO.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmendedToAnotherFacility_ThenCto_FliesTheNewAirportsDefault(bool dumpFirst)
    {
        Departure dep = AtSfo();
        Send(dep, "CLIMB VIA SID EXC MAINT 7000", "");
        if (dumpFirst)
        {
            Assert.True(Issue(dep, "TDLSDUMP").Result.Success);
        }

        AmendToOakland(dep);
        DispatchLine(dep, "CTO");

        Assert.Equal(TdlsDepartureAltitudeKind.None, dep.Aircraft.Procedure.SentPdcAltitudeKind);
        Assert.Null(dep.Aircraft.Procedure.SentPdcAltitudeFt);
        Assert.Equal(10000, dep.Aircraft.Procedure.SidViaCeiling);
    }

    /// <summary>
    /// The except-maintain spellings ZLA's TDLS texts use ("EXCPT MTN" at KONT, "EXP MTN" at KVNY/KSAN, "EXPT
    /// MAINTAIN" at KSNA) read as an except-maintain in a sent PDC: the departure levels at that altitude.
    /// </summary>
    [Theory]
    [InlineData("CLIMB VIA SID EXCPT MTN 7000", 7000)]
    [InlineData("CLIMB VIA SID EXP MTN 4000", 4000)]
    [InlineData("CLIMB VIA SID EXPT MAINTAIN 5000", 5000)]
    public void SentExceptMaintainSpellings_LevelAtTheirAltitude(string climbvia, int altitudeFt)
    {
        Departure dep = AtSfo();
        Send(dep, climbvia, "");

        DispatchLine(dep, "CTO");

        Assert.Equal(altitudeFt, dep.Aircraft.Procedure.SidViaCeiling);
        AssertLevelsAt(dep.Aircraft, altitudeFt);
    }

    /// <summary>The same spellings in a facility default (the committed ZLA snapshot) read as an except-maintain too.</summary>
    [Theory]
    [InlineData("KONT", "NIKKL1 MZB", 7000)]
    [InlineData("KVNY", "ADAMM3 ADAMM", 4000)]
    public void FacilityDefault_ZlaExceptMaintainSpellings_ReadAsExceptMaintain(string airport, string route, int altitudeFt)
    {
        TdlsDepartureAltitude resolved = Assert.IsType<ArtccConfigRoot>(TestArtccConfig.LoadZla()).GetSidDepartureAltitude(airport, route);

        Assert.Equal(TdlsDepartureAltitude.ClimbVia(altitudeFt), resolved);
    }

    /// <summary>A second PDC, sent once the first item expired and a fresh one was queued, replaces the first one's altitude.</summary>
    [Fact]
    public void SecondSend_ReplacesTheFirst()
    {
        Departure dep = AtSfo();
        Send(dep, "CLIMB VIA SID EXC MAINT 7000", "");
        ExpireAndRequeue(dep);

        Send(dep, "CLIMB VIA SID EXC MAINT 9000", "");
        DispatchLine(dep, "CTO");

        Assert.Equal(9000, dep.Aircraft.Procedure.SidViaCeiling);
        Assert.Equal(9000, dep.Aircraft.Procedure.ClimbViaTargetFt);
    }

    /// <summary>A second PDC with no altitude replaces the first with none: the takeoff flies TRUKN2's default (FL190).</summary>
    [Fact]
    public void SecondSendWithNoAltitude_FallsBackToTheFacilityDefault()
    {
        Departure dep = AtSfo();
        Send(dep, "CLIMB VIA SID EXC MAINT 7000", "");
        ExpireAndRequeue(dep);

        Send(dep, "", "");
        DispatchLine(dep, "CTO");

        Assert.Equal(TdlsDepartureAltitudeKind.None, dep.Aircraft.Procedure.SentPdcAltitudeKind);
        Assert.Null(dep.Aircraft.Procedure.SidViaCeiling);
        Assert.Equal(19000, dep.Aircraft.Procedure.ClimbViaTargetFt);
    }

    /// <summary>
    /// A sent climb via whose filed SID cannot be activated (KOAK QUAKE2 is in the TDLS config but not in the navdata) falls
    /// through to the facility default, QUAKE2's Maintain 5000.
    /// </summary>
    [Fact]
    public void SentClimbVia_FiledSidCannotActivate_FallsThroughToTheFacilityDefault()
    {
        Departure dep = At(LoadZoa(), "ZOA", "KOAK", "OAK", "28R", "QUAKE2 SAU");
        Send(dep, "CLIMB VIA SID EXC MAINT 7000", "");

        DispatchLine(dep, "CTO");

        Assert.Equal(TdlsDepartureAltitudeKind.ClimbVia, dep.Aircraft.Procedure.SentPdcAltitudeKind);
        Assert.False(dep.Aircraft.Procedure.SidViaMode);
        Assert.Equal(5000, dep.Aircraft.Procedure.SidInitialAltitudeFt);
    }

    /// <summary>The "no value" placeholder in both the Climbvia and the Maintain field captures none.</summary>
    [Fact]
    public void PlaceholderInBothFields_CapturesNone()
    {
        Departure dep = AtSfo();

        Send(dep, "- - - -", "- - - -");

        Assert.Equal(TdlsDepartureAltitudeKind.None, dep.Aircraft.Procedure.SentPdcAltitudeKind);
        Assert.Null(dep.Aircraft.Procedure.SentPdcAltitudeFt);
    }

    // ----- Fixtures -----

    private sealed record Departure(SimulationEngine Engine, AircraftState Aircraft, ArtccConfigRoot Artcc);

    private static ArtccConfigRoot LoadZoa() => Assert.IsType<ArtccConfigRoot>(TestArtccConfig.LoadZoa());

    private static Departure AtSfo() => At(LoadZoa(), "ZOA", "KSFO", "SFO", "01L", Trukn2Route);

    /// <summary>
    /// A bare engine with <paramref name="artcc"/>'s TDLS configuration on it, its one IFR departure filed on
    /// <paramref name="route"/> holding short of <paramref name="runway"/> at <paramref name="airport"/> with a Pending PDC.
    /// </summary>
    private static Departure At(ArtccConfigRoot artcc, string artccId, string airport, string facility, string runway, string route)
    {
        RunwayInfo rwy = DepartureEnd(airport, runway);
        SimulationEngine engine = AiTestFixture.Load(Scenario(artccId, airport, rwy), artcc, 7, []);
        engine.InitializeFromArtcc();
        Assert.True(engine.Tdls.Configs.ContainsKey(facility), $"{facility} should carry a TDLS configuration");

        AircraftState ac = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        ac.FlightPlan.Route = route;
        HoldShort(ac, rwy);

        engine.AfterAircraftSpawned(ac);
        Assert.Equal(TdlsItemStatus.Pending, Assert.Single(engine.Tdls.Items.Values).Status);
        return new Departure(engine, ac, artcc);
    }

    /// <summary>Puts <paramref name="ac"/> on the ground holding short of <paramref name="rwy"/>'s threshold.</summary>
    private static void HoldShort(AircraftState ac, RunwayInfo rwy)
    {
        ac.Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        ac.TrueHeading = rwy.TrueHeading;
        ac.Altitude = rwy.ElevationFt;
        ac.IndicatedAirspeed = 0;
        ac.IsOnGround = true;
        ac.Phases = new PhaseList();
        ac.Phases.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 10,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = rwy.Designator,
                },
                HoldShortArrival.HeldShort
            )
        );
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
    }

    /// <summary>
    /// Amends the departure to KOAK filed on COAST9 (TDLS default "CLB VIA SID EXC MAINT 10000FT") and puts the aircraft
    /// holding short of 28R there.
    /// </summary>
    private static void AmendToOakland(Departure dep)
    {
        CommandResult amended = dep.Engine.AmendFlightPlan(
            Callsign,
            new FlightPlanAmendment(ClearBeaconCode: false, Departure: "KOAK", Route: "COAST9 MCKEY LAX COMIX2")
        );
        Assert.True(amended.Success, amended.Message);
        HoldShort(dep.Aircraft, DepartureEnd("KOAK", "28R"));
    }

    /// <summary>Expires the standing item at its TTL, as the spine does, and queues a fresh Pending one by hand.</summary>
    private static void ExpireAndRequeue(Departure dep)
    {
        dep.Engine.Scenario!.ElapsedSeconds = TdlsMutations.DefaultTtl.TotalSeconds - 1;
        dep.Engine.RunSecond(new SpineCapturingHost(dep.Engine));
        Assert.Empty(dep.Engine.Tdls.Items);
        HoldShort(dep.Aircraft, DepartureEnd("KSFO", "01L"));
        Assert.True(Issue(dep, "TDLSQ").Result.Success);
    }

    private static string Scenario(string artccId, string airport, RunwayInfo rwy)
    {
        string lat = rwy.ThresholdLatitude.ToString(CultureInfo.InvariantCulture);
        string lon = rwy.ThresholdLongitude.ToString(CultureInfo.InvariantCulture);
        string faa = airport[1..];
        return $$"""
            {
              "id": "sent-pdc-departure",
              "name": "Sent PDC departure",
              "artccId": "{{artccId}}",
              "primaryAirportId": "{{faa}}",
              "aircraft": [
                {
                  "id": "a1",
                  "aircraftId": "{{Callsign}}",
                  "aircraftType": "B738",
                  "transponderMode": "C",
                  "startingConditions": {
                    "type": "Coordinates",
                    "coordinates": { "lat": {{lat}}, "lon": {{lon}} },
                    "altitude": 10,
                    "heading": 0,
                    "speed": 0
                  },
                  "flightplan": {
                    "rules": "IFR",
                    "departure": "{{airport}}",
                    "destination": "KLAX",
                    "cruiseAltitude": 35000,
                    "cruiseSpeed": 250,
                    "route": "",
                    "remarks": "",
                    "aircraftType": "B738"
                  },
                  "presetCommands": [],
                  "spawnDelay": 0,
                  "airportId": "{{faa}}",
                  "difficulty": "Easy"
                }
              ]
            }
            """;
    }

    /// <summary>The nine <c>TDLSS</c> fields: Expect|Sid|Transition|Climbout|Climbvia|InitialAlt|ContactInfo|DepFreq|LocalInfo.</summary>
    private static string SendLine(string climbvia, string maintain) => $"TDLSS 10 MIN||||{climbvia}|{maintain}||120.9|";

    private static void Send(Departure dep, string climbvia, string maintain)
    {
        ActionOutcome outcome = Issue(dep, SendLine(climbvia, maintain));
        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(TdlsItemStatus.Sent, Assert.Single(dep.Engine.Tdls.Items.Values).Status);
    }

    private static ActionOutcome Issue(Departure dep, string command) =>
        dep.Engine.Actions.Issue(new ActionInput(Callsign, command, "conn-1", "XX", Baked: null), new AttendanceActionHost());

    private static DispatchContext Context(Departure dep) => TestDispatch.Context(Random.Shared, validateDctFixes: false, artccConfig: dep.Artcc);

    private void DispatchLine(Departure dep, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(Assert.IsType<CompoundCommand>(parsed.Value), dep.Aircraft, Context(dep));
        _output.WriteLine($"{text} -> {result.Message}");
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }

    private void AssertLevelsAt(AircraftState ac, int altitudeFt)
    {
        double maxAltitude = Fly(ac, Airborne(ac), 400);
        _output.WriteLine($"maxAlt={maxAltitude:F0} finalAlt={ac.Altitude:F0}");
        Assert.True(maxAltitude < altitudeFt + LevelToleranceFt, $"climbed through {altitudeFt}: {maxAltitude:F0}");
        Assert.InRange(ac.Altitude, altitudeFt - LevelToleranceFt, altitudeFt + LevelToleranceFt);
    }

    private static AircraftState RoundTripThroughJson(AircraftState ac)
    {
        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        return AircraftState.FromSnapshot(JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!, null);
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
}
