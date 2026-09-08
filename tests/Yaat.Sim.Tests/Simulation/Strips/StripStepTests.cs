using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Strips;

/// <summary>
/// The flight-strip bodies on the bare engine: the spawn hook's auto-print, the approach student's takeoff-roll
/// print, the creating verbs behind <c>SEP</c> / <c>HSC</c>, the deferred strip dispatch the engine applies itself,
/// and the change tracker the router and the post-physics drain step hand to the host. They decide from engine state
/// alone — the scenario, the student position and the ARTCC's bay configuration — so a plain
/// <see cref="SimulationEngine.RunSecond"/> over the real ZOA data reaches every gate the live room reaches, with no
/// server in the process.
/// </summary>
public class StripStepTests
{
    private const string Callsign = "SWA1234";
    private const string FatCallsign = "SKW5150";
    private const string ArrivalCallsign = "ASA451";

    /// <summary>An IFR departure filed at KOAK, parked on the field — what the spawn-time strip auto-print is for.</summary>
    private const string DepartureAtOak = """
        {
          "id": "strip-departure",
          "name": "Strip departure at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "SWA1234",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.7213, "lon": -122.2208 },
                "altitude": 9,
                "heading": 290,
                "speed": 0
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KOAK",
                "destination": "KSFO",
                "cruiseAltitude": 35000,
                "cruiseSpeed": 250,
                "route": "",
                "remarks": "",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ]
        }
        """;

    /// <summary>The same shape at KFAT, whose ATCT/TRACON carries the "Friant" bay the FAT_F_APP student's strips print into.</summary>
    private const string DepartureAtFat = """
        {
          "id": "strip-departure-fat",
          "name": "Strip departure at FAT",
          "artccId": "ZOA",
          "primaryAirportId": "FAT",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "SKW5150",
              "aircraftType": "CRJ2",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 36.7762, "lon": -119.7181 },
                "altitude": 336,
                "heading": 290,
                "speed": 0
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KFAT",
                "destination": "KLAX",
                "cruiseAltitude": 24000,
                "cruiseSpeed": 250,
                "route": "",
                "remarks": "",
                "aircraftType": "CRJ2"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "FAT",
              "difficulty": "Easy"
            }
          ]
        }
        """;

    /// <summary>An IFR arrival inbound to KOAK, ~40 nm out at 250 KIAS — inside the 20-minute auto-print window.</summary>
    private const string ArrivalToOak = """
        {
          "id": "strip-arrival",
          "name": "Strip arrival at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "ASA451",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.7213, "lon": -121.37 },
                "altitude": 8000,
                "heading": 270,
                "speed": 250
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KSEA",
                "destination": "KOAK",
                "cruiseAltitude": 35000,
                "cruiseSpeed": 250,
                "route": "",
                "remarks": "",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public StripStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// A bare engine with the ZOA configuration resolved and the student position set by hand — the Sim's own scenario
    /// load resolves neither — then the strip/TDLS initialisation the server's load runs, which is what pre-creates
    /// the position's empty racks.
    /// </summary>
    private SimulationEngine? Engine(string scenarioJson, string positionCallsign, string positionType)
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(scenarioJson, _zoa, 7, []);
        var scenario = engine.Scenario!;
        scenario.StudentPosition = TrackOwner.CreateStars(positionCallsign, positionCallsign[..3], 3, "O");
        scenario.StudentPositionType = positionType;
        engine.InitializeFromArtcc();
        return engine;
    }

    private static AircraftState Departure(SimulationEngine engine, string callsign) => engine.FindAircraft(callsign)!;

    private static ActionOutcome Issue(SimulationEngine engine, AttendanceActionHost host, string callsign, string command) =>
        engine.Actions.Issue(new ActionInput(callsign, command, "conn-1", "XX", Baked: null), host);

    /// <summary>A recording of everything <paramref name="engine"/> has run so far, carrying the ARTCC the replay re-initialises from.</summary>
    private SessionRecording Recording(SimulationEngine engine, string scenarioJson)
    {
        var scenario = engine.Scenario!;
        return new SessionRecording
        {
            ScenarioJson = scenarioJson,
            RngSeed = 7,
            Actions = [.. scenario.ActionLog],
            TotalElapsedSeconds = scenario.ElapsedSeconds,
            ArtccConfigJson = JsonSerializer.Serialize(_zoa, RecordingJsonOptions.Default),
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
            StudentPositionState = new ReplayStudentPosition(scenario.StudentPosition, scenario.StudentTcp, scenario.StudentPositionType, false),
        };
    }

    private static List<string> StripIds(SimulationEngine engine) => [.. engine.Strips.Items.Keys.Order(StringComparer.Ordinal)];

    private AccessibleBay Bay(string positionCallsign, string bayName) =>
        _zoa!.GetAllAccessibleStripBays(positionCallsign).Single(b => b.Bay.Name == bayName);

    [Fact]
    public void AfterAircraftSpawned_TowerStudent_PrintsIntoTheFirstGroundBay()
    {
        if (Engine(DepartureAtOak, "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        engine.AfterAircraftSpawned(Departure(engine, Callsign));

        var strip = Assert.Single(engine.Strips.Items.Values);
        Assert.Equal($"STRIP_{Callsign}", strip.Id);
        Assert.Equal(Callsign, strip.AircraftId);
        Assert.Equal((int)StripItemType.DepartureStrip, strip.Type);
        Assert.Equal(Bay("OAK_TWR", "Ground 1").Bay.Id, strip.BayId);
        Assert.Equal("OAK", strip.FacilityId);
        Assert.Equal(0, strip.Rack);
        Assert.Empty(engine.Strips.DeparturePrinterQueue);
        Assert.Contains(strip.Id, engine.Strips.Bays[strip.BayId]["0"][0]);
    }

    [Fact]
    public void AfterAircraftSpawned_GroundStudent_PrintsToThePrinterQueue()
    {
        if (Engine(DepartureAtOak, "OAK_GND", "GND") is not { } engine)
        {
            return;
        }

        engine.AfterAircraftSpawned(Departure(engine, Callsign));

        var strip = Assert.Single(engine.Strips.Items.Values);
        Assert.Equal($"STRIP_{Callsign}", strip.Id);
        Assert.Equal("", strip.BayId);
        Assert.Equal([strip.Id], engine.Strips.DeparturePrinterQueue);
    }

    [Fact]
    public void AfterAircraftSpawned_ConfiguredPlacementWinsAndClampsTheRack()
    {
        if (Engine(DepartureAtOak, "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        var local = Bay("OAK_TWR", "Local 1");
        engine.Scenario!.InitialStripBayByCallsign[Callsign] = new ScenarioStripBayAssignment("OAK", local.Bay.Id, 99);

        engine.AfterAircraftSpawned(Departure(engine, Callsign));

        var strip = Assert.Single(engine.Strips.Items.Values);
        Assert.Equal(local.Bay.Id, strip.BayId);
        Assert.Equal(local.Bay.NumberOfRacks - 1, strip.Rack);
        Assert.Empty(engine.Strips.DeparturePrinterQueue);
    }

    [Fact]
    public void AfterAircraftSpawned_ApproachStudent_PrintsNothingUntilTakeoffRoll()
    {
        if (Engine(DepartureAtFat, "FAT_F_APP", "APP") is not { } engine)
        {
            return;
        }

        var aircraft = Departure(engine, FatCallsign);
        engine.AfterAircraftSpawned(aircraft);

        Assert.Empty(engine.Strips.Items);

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new TakeoffPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft));

        var spine = new SpineCapturingHost(engine);
        engine.RunSecond(spine);

        var strip = Assert.Single(engine.Strips.Items.Values);
        Assert.Equal($"STRIP_{FatCallsign}", strip.Id);
        Assert.Equal(Bay("FAT_F_APP", "FRIANT").Bay.Id, strip.BayId);
        var changes = Assert.Single(spine.StripChanges);
        Assert.Contains(strip.Id, changes.ChangedItemIds);
    }

    [Fact]
    public void Sep_AndHsc_CreateOnTheBareHostAndBakeTheirIds()
    {
        if (Engine(DepartureAtOak, "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var ground = Bay("OAK_TWR", "Ground 1").Bay;

        var separator = Issue(engine, host, "", "SEP W OAK/Ground 1/1/1 Foo");

        Assert.True(separator.Result.Success, separator.Result.Message);
        var sepRecord = Assert.Single(engine.Strips.Items.Values, i => i.Id.StartsWith("SEP_", StringComparison.Ordinal));
        Assert.Equal((int)StripItemType.WhiteSeparator, sepRecord.Type);
        Assert.Equal(ground.Id, sepRecord.BayId);
        Assert.Equal(["Foo"], sepRecord.FieldValues);
        Assert.Equal(sepRecord.Id, separator.ToRecord!.StripId);

        var half = Issue(engine, host, "", @"HSC OAK/Ground 1/1 a\b");

        Assert.True(half.Result.Success, half.Result.Message);
        var halfRecord = Assert.Single(engine.Strips.Items.Values, i => i.Id.StartsWith("HSTRIP_", StringComparison.Ordinal));
        Assert.Equal((int)StripItemType.HalfStripLeft, halfRecord.Type);
        Assert.Equal(ground.Id, halfRecord.BayId);
        Assert.Equal(halfRecord.Id, half.ToRecord!.StripId);

        // Re-applying the records over the engine that already holds their ids creates nothing: the ids are baked, so
        // the verbs reuse them instead of minting a second item beside the one the restore carried in.
        engine.Actions.Apply(separator.ToRecord!, host);
        engine.Actions.Apply(half.ToRecord!, host);

        Assert.Equal(2, engine.Strips.Items.Count);
    }

    [Fact]
    public void DeferredAnnotate_AppliesInsideTheEngine()
    {
        if (Engine(DepartureAtOak, "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        var aircraft = Departure(engine, Callsign);
        engine.AfterAircraftSpawned(aircraft);
        var stripId = Assert.Single(engine.Strips.Items.Values).Id;

        engine.DispatchPresetCommands(
            new LoadedAircraft { State = aircraft, PresetCommands = [new PresetCommand { Command = "WAIT 1 AN 1 ✓", TimeOffset = 0 }] }
        );

        Assert.Single(aircraft.DeferredDispatches);
        Assert.Equal("", engine.Strips.Items[stripId].FieldValues[10]);

        engine.TickOneSecond();
        engine.TickOneSecond();

        Assert.Empty(aircraft.PendingStripDispatches);
        // Box 1 is FieldValues[10]; the annotation is applied by the engine's own strip step, with no host involved.
        Assert.Equal("✓", engine.Strips.Items[stripId].FieldValues[10]);
    }

    /// <summary>
    /// The same deferred annotation typed by a controller instead of loaded as a preset. It reaches the engine through
    /// the action router, where the scoped-special splitter sees a comma-less block that expands to two commands
    /// (<c>WAIT</c> + <c>AN</c>) — it must decline to split it, or the router re-routes byte-identical text forever and
    /// the process dies on a stack overflow. Declined, the aviation arm builds the <c>WAIT</c> block exactly as the
    /// preset path does.
    /// </summary>
    [Fact]
    public void WaitPrefixedStripVerb_IssuedThroughTheRouter_AppliesAfterTheWait()
    {
        if (Engine(DepartureAtOak, "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        engine.AfterAircraftSpawned(Departure(engine, Callsign));
        var stripId = Assert.Single(engine.Strips.Items.Values).Id;

        var outcome = Issue(engine, host, Callsign, "WAIT 1 AN 1 ✓");

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("", engine.Strips.Items[stripId].FieldValues[10]);

        engine.TickOneSecond();
        engine.TickOneSecond();

        // Box 1 is FieldValues[10] — the annotation lands when the WAIT fires, not at issue time.
        Assert.Equal("✓", engine.Strips.Items[stripId].FieldValues[10]);

        // One recorded command, the text as typed: the router did not split it into units.
        var recorded = Assert.Single(engine.Scenario!.ActionLog.OfType<RecordedCommand>());
        Assert.Equal("WAIT 1 AN 1 ✓", recorded.Command);
    }

    [Fact]
    public void ChangeSet_DeliversItemsBeforeFullState()
    {
        if (Engine(DepartureAtOak, "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        engine.AfterAircraftSpawned(Departure(engine, Callsign));
        var stripId = Assert.Single(engine.Strips.Items.Values).Id;

        var spine = new SpineCapturingHost(engine);
        engine.RunSecond(spine);

        var changes = Assert.Single(spine.StripChanges);
        Assert.Equal(stripId, Assert.Single(changes.ChangedItemIds));
        Assert.True(changes.FullState);
    }

    /// <summary>
    /// The amendment reprint draws an id like a creating verb, so it bakes it onto the derived
    /// <see cref="RecordedAmendFlightPlan"/> the <c>FP</c> arm appends — the record a replay reprints from — and onto
    /// the command record through the <c>ctx.StripId</c> channel. Re-applying either record over an engine that already holds the id prints nothing,
    /// which is what keeps a rewind across an amendment from stacking a second departure strip, and a from-scratch
    /// replay lands on the same id set the live run had. The spawn strip sits in a bay here, so
    /// <c>STRIP_{callsign}</c> is taken and the reprint has to mint a duplicate id — the case a re-mint cannot
    /// reproduce.
    /// </summary>
    [Fact]
    public void Amendment_ReprintBakesItsIdAndAReplayDoesNotStackASecondCopy()
    {
        if (Engine(DepartureAtOak, "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        engine.AfterAircraftSpawned(Departure(engine, Callsign));
        var spawned = Assert.Single(engine.Strips.Items.Values);
        Assert.Empty(engine.Strips.DeparturePrinterQueue);

        var outcome = Issue(engine, host, Callsign, "FP B738/L 350 OAK LAX");

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        var reprinted = Assert.Single(engine.Strips.DeparturePrinterQueue);
        Assert.NotEqual(spawned.Id, reprinted);
        Assert.Equal(2, engine.Strips.Items.Count);

        // Both records the amendment wrote carry the id the reprint drew. The derived one is what a replay reprints
        // from — a recorded FP returns at the arm's IsRecorded guard — so the command record's copy is written but
        // never read back.
        var amendment = Assert.Single(engine.Scenario!.ActionLog.OfType<RecordedAmendFlightPlan>());
        Assert.Equal(reprinted, amendment.StripId);
        Assert.Equal(reprinted, outcome.ToRecord!.StripId);

        var recording = Recording(engine, DepartureAtOak);
        var liveIds = StripIds(engine);

        // Re-applied over the engine that already holds the id — a snapshot-based restore's shape — neither record
        // prints: the derived one because the reprint finds its baked id, the command one because the arm refuses a
        // recorded FP outright.
        engine.Actions.ApplyRecorded(amendment, host);
        Assert.Equal(liveIds, StripIds(engine));

        engine.Actions.Apply(outcome.ToRecord!, host);
        Assert.Equal(liveIds, StripIds(engine));

        // And from scratch: the spawn hook prints the bay strip at t=0, the log reprints under the baked id.
        var replayed = new SimulationEngine(new TestAirportGroundData());
        replayed.Replay(recording, engine.Scenario.ElapsedSeconds);

        Assert.Equal(liveIds, StripIds(replayed));
    }

    /// <summary>
    /// The arrival auto-print is gated on the owning facility's <c>enableArrivalStrips</c>: OAK ATCT has it off in the
    /// real ZOA configuration — real vStrips never auto-prints arrivals there — while NCT has it on. Same aircraft,
    /// same ETA, two student positions.
    /// </summary>
    [Fact]
    public void ArrivalAutoPrint_GatedOnEnableArrivalStrips()
    {
        if (Engine(ArrivalToOak, "OAK_TWR", "TWR") is not { } off)
        {
            return;
        }

        Assert.False(Bay("OAK_TWR", "Ground 1").Owner.FlightStripsConfiguration!.EnableArrivalStrips);

        off.TickAutoArrivalStrips();

        Assert.Empty(off.Strips.Items);

        // OAK_APP is an NCT position, and NCT does enable arrival strips.
        var on = Engine(ArrivalToOak, "OAK_APP", "APP")!;
        Assert.True(Bay("OAK_APP", "NCT").Owner.FlightStripsConfiguration!.EnableArrivalStrips);

        on.TickAutoArrivalStrips();

        var strip = Assert.Single(on.Strips.Items.Values);
        Assert.Equal($"ARRIVAL_{ArrivalCallsign}", strip.Id);
        Assert.Equal((int)StripItemType.ArrivalStrip, strip.Type);
        Assert.Equal("NCT", strip.FacilityId);
        Assert.Equal([strip.Id], on.Strips.ArrivalPrinterQueue);
    }

    /// <summary>
    /// <see cref="StripItemRecord.Type"/> is the wire number, and <see cref="StripMutations"/> still carries the
    /// handful of them it compares against as <c>int</c> constants. They and the enum are one numbering or a strip
    /// created through one and matched through the other silently changes kind.
    /// </summary>
    [Fact]
    public void StripItemType_AgreesWithTheMutationConstants()
    {
        Assert.Equal(StripMutations.DepartureStripType, (int)StripItemType.DepartureStrip);
        Assert.Equal(StripMutations.ArrivalStripType, (int)StripItemType.ArrivalStrip);
        Assert.Equal(StripMutations.HalfStripLeft, (int)StripItemType.HalfStripLeft);
        Assert.Equal(StripMutations.HalfStripRight, (int)StripItemType.HalfStripRight);
        Assert.Equal(StripMutations.BlankStripType, (int)StripItemType.BlankStrip);

        // IsSeparator is the fifth reader of the numbering: exactly the four separator styles, nothing else.
        foreach (var type in Enum.GetValues<StripItemType>())
        {
            var isSeparator =
                type
                is StripItemType.HandwrittenSeparator
                    or StripItemType.WhiteSeparator
                    or StripItemType.RedSeparator
                    or StripItemType.GreenSeparator;
            Assert.Equal(isSeparator, StripMutations.IsSeparator((int)type));
        }
    }

    /// <summary>
    /// A strip pushed to another facility's bay has no receiver unless someone is signed on there, and the engine
    /// answers that from its own <see cref="SimulationEngine.Attendance"/> — the recorded derivation of the room's CRC
    /// clients — so every run kind reaches the same verdict.
    /// </summary>
    [Fact]
    public void Scan_WarnsWhenNoAttendedPositionStaffsTheReceivingFacility()
    {
        if (Engine(DepartureAtOak, "OAK_TWR", "TWR") is not { } engine)
        {
            return;
        }

        // OAK_TWR's STARS TCP lives in NCT's configuration, which is what resolves a TCP code from this position.
        engine.Scenario!.StudentPosition = TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O");
        var host = new AttendanceActionHost();
        engine.AfterAircraftSpawned(Departure(engine, Callsign));

        var unattended = Issue(engine, host, Callsign, "SCAN NCT/NCT");

        Assert.True(unattended.Result.Success, unattended.Result.Message);
        Assert.Contains("no controller connected at NCT", unattended.Result.Message);

        // TCP 2B is NCT's: attending it staffs the receiving facility.
        AttendanceTestSupport.Attend(engine, "2B");
        Assert.NotEmpty(engine.Attendance.PositionIds);

        var attended = Issue(engine, host, Callsign, "SCAN NCT/NCT");

        Assert.True(attended.Result.Success, attended.Result.Message);
        Assert.DoesNotContain("no controller connected", attended.Result.Message);
    }
}
