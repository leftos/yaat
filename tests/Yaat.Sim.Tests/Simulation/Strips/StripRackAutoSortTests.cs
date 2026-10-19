using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Strips;

/// <summary>
/// An auto-sorted rack on the bare engine: <c>RACKSORT</c> turns it on and off, and every path that puts a strip into
/// the rack or re-keys one lands it in its sorted spot — a <c>STRIP</c> move, the spawn hook's placement, an
/// <c>HSA</c> edit of a half strip's box 1, a <c>SEPM</c> of a separator inside the rack. Racks are stored bottom-up
/// (index 0 is the visual bottom), so a top-down rack of N172SP over SWA1234 is stored [SWA1234, N172SP].
/// </summary>
public class StripRackAutoSortTests
{
    private const string Airline = "SWA1234";
    private const string Tail = "N172SP";

    /// <summary>The tower student's first ground bay — where the spawn hook prints — addressed the way the verbs take it.</summary>
    private const string GroundBay = "OAK/Ground1";

    /// <summary>Two IFR departures parked at KOAK: an airline and a tail, so the two sort groups meet.</summary>
    private const string TwoDeparturesAtOak = """
        {
          "id": "rack-sort",
          "name": "Rack sort at OAK",
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
            },
            {
              "id": "a2",
              "aircraftId": "N172SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.7220, "lon": -122.2215 },
                "altitude": 9,
                "heading": 290,
                "speed": 0
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KOAK",
                "destination": "KSFO",
                "cruiseAltitude": 5000,
                "cruiseSpeed": 110,
                "route": "",
                "remarks": "",
                "aircraftType": "C172"
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

    public StripRackAutoSortTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>The bare engine with the ZOA configuration and the OAK_TWR student, racks pre-created as the server's load does.</summary>
    private SimulationEngine? Engine() => Engine("OAK");

    /// <summary>
    /// The same engine with the student's STARS TCP in <paramref name="tcpFacility"/>'s configuration: NCT makes NCT's bay
    /// the external destination a <c>SCAN</c> needs.
    /// </summary>
    private SimulationEngine? Engine(string tcpFacility)
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(TwoDeparturesAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = TrackOwner.CreateStars("OAK_TWR", tcpFacility, 3, "O");
        scenario.StudentPositionType = "TWR";
        engine.InitializeFromArtcc();
        return engine;
    }

    private static ActionOutcome Issue(SimulationEngine engine, string command) => IssueAs(engine, "", command);

    private static ActionOutcome IssueAs(SimulationEngine engine, string callsign, string command) =>
        engine.Actions.Issue(new ActionInput(callsign, command, "conn-1", "XX", Baked: null), new SpineCapturingHost(engine));

    private static void Spawn(SimulationEngine engine, string callsign) => engine.AfterAircraftSpawned(engine.FindAircraft(callsign)!);

    private string GroundBayId() => _zoa!.GetAllAccessibleStripBays("OAK_TWR").Single(b => b.Bay.Name == "Ground 1").Bay.Id;

    private List<string> Rack(SimulationEngine engine, int rack) => [.. engine.Strips.Bays[GroundBayId()][rack.ToString()][0]];

    private static string Id(string callsign) => $"STRIP_{callsign}";

    private static string HalfStripWithBoxOne(SimulationEngine engine, string line1) =>
        engine.Strips.Items.Values.Single(i => (i.Type == StripMutations.HalfStripLeft) && (i.FieldValues[0] == line1)).Id;

    [Fact]
    public void RackSortOn_SortsTheRackAtOnce_AndOffLeavesItAsItIs()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Spawn(engine, Tail);
        Spawn(engine, Airline);
        Assert.Equal([Id(Tail), Id(Airline)], Rack(engine, 0));

        ActionOutcome on = Issue(engine, $"RACKSORT {GroundBay}/1 ON");

        Assert.True(on.Result.Success, on.Result.Message);
        Assert.Equal("Rack OAK/Ground 1/1 auto-sort on", on.Result.Message);
        Assert.Equal([Id(Airline), Id(Tail)], Rack(engine, 0));
        Assert.Equal(1, engine.Strips.Items[Id(Tail)].Index);
        Assert.Contains((GroundBayId(), 0), engine.Strips.AutoSortedRacks);

        ActionOutcome off = Issue(engine, $"RACKSORT {GroundBay}/1 OFF");

        Assert.True(off.Result.Success, off.Result.Message);
        Assert.Equal("Rack OAK/Ground 1/1 auto-sort off", off.Result.Message);
        Assert.Equal([Id(Airline), Id(Tail)], Rack(engine, 0));
        Assert.Empty(engine.Strips.AutoSortedRacks);

        ActionOutcome moved = Issue(engine, $"STRIP {Id(Tail)} {GroundBay}/1/1");
        Assert.True(moved.Result.Success, moved.Result.Message);
        Assert.Equal([Id(Tail), Id(Airline)], Rack(engine, 0));
    }

    [Fact]
    public void RackSort_RackOutOfRange_IsRefused()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        ActionOutcome result = Issue(engine, $"RACKSORT {GroundBay}/99 ON");

        Assert.False(result.Result.Success);
        Assert.Empty(engine.Strips.AutoSortedRacks);
    }

    [Fact]
    public void StripMoveIntoASortedRack_SnapsToTheSortedSpot_AndNamesTheSlotItLandedIn()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Spawn(engine, Airline);
        Spawn(engine, Tail);
        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);
        Assert.Equal([Id(Airline), Id(Tail)], Rack(engine, 0));

        // Asked for the bottom slot; the tail sorts above the airline, so it stays on top.
        ActionOutcome moved = Issue(engine, $"STRIP {Id(Tail)} {GroundBay}/1/1");

        Assert.True(moved.Result.Success, moved.Result.Message);
        Assert.Equal([Id(Airline), Id(Tail)], Rack(engine, 0));
        Assert.Equal("Strip moved to Ground 1/1/2 (auto-sorted)", moved.Result.Message);
    }

    [Fact]
    public void StripMoveIntoASortedRack_ThatLandsWhereAsked_KeepsThePlainResult()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Spawn(engine, Airline);
        Spawn(engine, Tail);
        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);

        ActionOutcome moved = Issue(engine, $"STRIP {Id(Tail)} {GroundBay}/1/2");

        Assert.True(moved.Result.Success, moved.Result.Message);
        Assert.Equal("Strip moved to Ground 1/1/2", moved.Result.Message);
    }

    [Fact]
    public void SpawnPlacement_IntoASortedRack_LandsSorted()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);

        Spawn(engine, Tail);
        Spawn(engine, Airline);

        Assert.Equal([Id(Airline), Id(Tail)], Rack(engine, 0));
        Assert.Equal(0, engine.Strips.Items[Id(Airline)].Index);
        Assert.Equal(1, engine.Strips.Items[Id(Tail)].Index);
    }

    [Fact]
    public void HalfStripAmend_ReKeyingBoxOne_ReSortsTheRack()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);
        Spawn(engine, Airline);
        Assert.True(Issue(engine, $"HSC {GroundBay}/1 AAL10").Result.Success);
        string halfStrip = HalfStripWithBoxOne(engine, "AAL10");
        Assert.Equal([Id(Airline), halfStrip], Rack(engine, 0));

        ActionOutcome amended = Issue(engine, $"HSA {halfStrip} ZZZ10");

        Assert.True(amended.Result.Success, amended.Result.Message);
        Assert.Equal([halfStrip, Id(Airline)], Rack(engine, 0));
        Assert.Equal(0, engine.Strips.Items[halfStrip].Index);
    }

    [Fact]
    public void HalfStripAmend_ClearingBoxOne_MovesItToTheSegmentBottom()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);
        Spawn(engine, Tail);
        // "A1" is a tail that sorts above N172SP, so the half strip starts on top.
        Assert.True(Issue(engine, $"HSC {GroundBay}/1 A1").Result.Success);
        string halfStrip = HalfStripWithBoxOne(engine, "A1");
        Assert.Equal([Id(Tail), halfStrip], Rack(engine, 0));

        ActionOutcome amended = Issue(engine, $"HSA {halfStrip} \\LINE2");

        Assert.True(amended.Result.Success, amended.Result.Message);
        Assert.Equal([halfStrip, Id(Tail)], Rack(engine, 0));
    }

    /// <summary>
    /// A separator dragged inside a sorted rack lands where it is dropped, and the runs on both sides of it re-sort at
    /// once. Before the drop the separator sits under SWA1234, AAL10 and N55 with N172SP alone below it; dropped one
    /// slot higher it puts SWA1234 below it with N172SP, and that run re-sorts to N172SP over SWA1234.
    /// </summary>
    [Fact]
    public void SeparatorMove_InsideASortedRack_KeepsTheDropSlotAndReSortsBothSegments()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);
        Assert.True(Issue(engine, $"SEP W {GroundBay}/1/1 LINE").Result.Success);
        string separator = engine.Strips.Items.Values.Single(i => StripMutations.IsSeparator(i.Type)).Id;
        Spawn(engine, Airline);
        Spawn(engine, Tail);
        Assert.True(Issue(engine, $"STRIP {Id(Tail)} {GroundBay}/1/1").Result.Success);
        Assert.True(Issue(engine, $"HSC {GroundBay}/1 AAL10").Result.Success);
        Assert.True(Issue(engine, $"HSC {GroundBay}/1 N55").Result.Success);
        string aal = HalfStripWithBoxOne(engine, "AAL10");
        string n55 = HalfStripWithBoxOne(engine, "N55");
        Assert.Equal([Id(Tail), separator, Id(Airline), aal, n55], Rack(engine, 0));

        ActionOutcome moved = Issue(engine, $"SEPM {separator} {GroundBay}/1/3");

        Assert.True(moved.Result.Success, moved.Result.Message);
        Assert.Equal("Separator moved to Ground 1/1/3", moved.Result.Message);
        Assert.Equal([Id(Airline), Id(Tail), separator, aal, n55], Rack(engine, 0));
        Assert.Equal(2, engine.Strips.Items[separator].Index);
    }

    /// <summary>
    /// Builds, top-down, <c>N1, AAL1 | separator | N2, AAL2</c> in rack 1 — stored bottom-up as
    /// [AAL2, N2, separator, AAL1, N1] — then turns its auto-sort on, which leaves both segments as they are.
    /// </summary>
    private (string Separator, string N1, string Aal1, string N2, string Aal2) SortedRackWithTwoSegments(SimulationEngine engine)
    {
        Assert.True(Issue(engine, $"HSC {GroundBay}/1 AAL2").Result.Success);
        Assert.True(Issue(engine, $"HSC {GroundBay}/1 N2").Result.Success);
        Assert.True(Issue(engine, $"SEP W {GroundBay}/1/3 LINE").Result.Success);
        Assert.True(Issue(engine, $"HSC {GroundBay}/1 AAL1").Result.Success);
        Assert.True(Issue(engine, $"HSC {GroundBay}/1 N1").Result.Success);
        string separator = engine.Strips.Items.Values.Single(i => StripMutations.IsSeparator(i.Type)).Id;
        (string Separator, string N1, string Aal1, string N2, string Aal2) rack = (
            separator,
            HalfStripWithBoxOne(engine, "N1"),
            HalfStripWithBoxOne(engine, "AAL1"),
            HalfStripWithBoxOne(engine, "N2"),
            HalfStripWithBoxOne(engine, "AAL2")
        );

        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);
        Assert.Equal([rack.Aal2, rack.N2, separator, rack.Aal1, rack.N1], Rack(engine, 0));
        return rack;
    }

    [Fact]
    public void SeparatorDelete_InASortedRack_SortsTheMergedSegments()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        (string separator, string n1, string aal1, string n2, string aal2) = SortedRackWithTwoSegments(engine);

        ActionOutcome deleted = Issue(engine, $"SEPD {separator}");

        Assert.True(deleted.Result.Success, deleted.Result.Message);
        Assert.Equal([aal2, aal1, n2, n1], Rack(engine, 0));
        Assert.Equal(1, engine.Strips.Items[aal1].Index);
        Assert.Equal(2, engine.Strips.Items[n2].Index);
    }

    [Fact]
    public void SeparatorMove_OutOfASortedRack_SortsTheMergedSegments()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        (string separator, string n1, string aal1, string n2, string aal2) = SortedRackWithTwoSegments(engine);

        ActionOutcome moved = Issue(engine, $"SEPM {separator} {GroundBay}/2/1");

        Assert.True(moved.Result.Success, moved.Result.Message);
        Assert.Equal([aal2, aal1, n2, n1], Rack(engine, 0));
        Assert.Equal([separator], Rack(engine, 1));
        Assert.Equal(1, engine.Strips.Items[aal1].Index);
    }

    /// <summary>An index past the top is clamped to it; a separator never moves in the sort, so a clamp is not marked auto-sorted.</summary>
    [Fact]
    public void SeparatorCreate_PastTheTopOfASortedRack_IsNotMarkedAutoSorted()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Spawn(engine, Airline);
        Spawn(engine, Tail);
        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);

        ActionOutcome created = Issue(engine, $"SEP W {GroundBay}/1/99");

        Assert.True(created.Result.Success, created.Result.Message);
        Assert.DoesNotContain("(auto-sorted)", created.Result.Message);
        string separator = engine.Strips.Items.Values.Single(i => StripMutations.IsSeparator(i.Type)).Id;
        Assert.Equal([Id(Airline), Id(Tail), separator], Rack(engine, 0));
    }

    [Fact]
    public void SeparatorCreate_InASortedRack_StaysWhereItIsDropped()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Spawn(engine, Airline);
        Spawn(engine, Tail);
        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);

        ActionOutcome created = Issue(engine, $"SEP W {GroundBay}/1/2");

        Assert.True(created.Result.Success, created.Result.Message);
        Assert.Equal("Separator created at Ground 1/1/2", created.Result.Message);
        string separator = engine.Strips.Items.Values.Single(i => StripMutations.IsSeparator(i.Type)).Id;
        Assert.Equal([Id(Airline), separator, Id(Tail)], Rack(engine, 0));
        Assert.Equal(1, engine.Strips.Items[separator].Index);
    }

    /// <summary>Asked for the top, ZZZ10 sorts under SWA1234 (both airline-shaped), so the move lands it at the bottom.</summary>
    [Fact]
    public void HalfStripMove_IntoASortedRack_LandsSorted()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Spawn(engine, Airline);
        Assert.True(Issue(engine, $"HSC {GroundBay}/1 ZZZ10").Result.Success);
        string halfStrip = HalfStripWithBoxOne(engine, "ZZZ10");
        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);

        ActionOutcome moved = Issue(engine, $"HSM ZZZ10 {GroundBay}/1/2");

        Assert.True(moved.Result.Success, moved.Result.Message);
        Assert.Equal("Half-strip 'ZZZ10' moved to Ground 1/1/1 (auto-sorted)", moved.Result.Message);
        Assert.Equal([halfStrip, Id(Airline)], Rack(engine, 0));
    }

    /// <summary>HSC stacks a half strip on top; ZZZ10 sorts under SWA1234, so the sort takes it to the bottom.</summary>
    [Fact]
    public void HalfStripCreate_InASortedRack_LandsSorted()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Spawn(engine, Airline);
        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);

        ActionOutcome created = Issue(engine, $"HSC {GroundBay}/1 ZZZ10");

        Assert.True(created.Result.Success, created.Result.Message);
        string halfStrip = HalfStripWithBoxOne(engine, "ZZZ10");
        Assert.Equal([halfStrip, Id(Airline)], Rack(engine, 0));
        Assert.Equal(0, engine.Strips.Items[halfStrip].Index);
    }

    /// <summary>A blank has no box 1, so it sorts to the bottom of its segment wherever it is dropped.</summary>
    [Fact]
    public void BlankCreate_InASortedRack_LandsAtTheSegmentBottom()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        Spawn(engine, Airline);
        Spawn(engine, Tail);
        Assert.True(Issue(engine, $"RACKSORT {GroundBay}/1 ON").Result.Success);

        ActionOutcome created = Issue(engine, $"BLANK {GroundBay}/1/3");

        Assert.True(created.Result.Success, created.Result.Message);
        Assert.Equal("Blank strip created at Ground 1/1/1 (auto-sorted)", created.Result.Message);
        string blank = engine.Strips.Items.Values.Single(i => i.Type == StripMutations.BlankStripType).Id;
        Assert.Equal([blank, Id(Airline), Id(Tail)], Rack(engine, 0));
    }

    /// <summary>The tail's copy goes in first; the airline's copy, appended on top of it, sorts under it.</summary>
    [Fact]
    public void ScanIntoASortedExternalRack_LandsSorted()
    {
        if (Engine("NCT") is not { } engine)
        {
            return;
        }

        Spawn(engine, Airline);
        Spawn(engine, Tail);
        ActionOutcome sorted = Issue(engine, "RACKSORT NCT/NCT/1 ON");
        Assert.True(sorted.Result.Success, sorted.Result.Message);

        Assert.True(IssueAs(engine, Tail, "SCAN NCT/NCT").Result.Success);
        ActionOutcome scanned = IssueAs(engine, Airline, "SCAN NCT/NCT");

        Assert.True(scanned.Result.Success, scanned.Result.Message);
        Assert.Contains("NCT/1/1 (auto-sorted)", scanned.Result.Message);
        StripItemRecord airlineCopy = engine.Strips.Items.Values.Single(i => (i.AircraftId == Airline) && (i.Id != Id(Airline)));
        StripItemRecord tailCopy = engine.Strips.Items.Values.Single(i => (i.AircraftId == Tail) && (i.Id != Id(Tail)));
        Assert.Equal([airlineCopy.Id, tailCopy.Id], engine.Strips.Bays[airlineCopy.BayId]["0"][0]);
    }

    [Fact]
    public void ClearSession_KeepsTheFlag_AndResetClearsIt()
    {
        var strips = new FlightStripState();
        StripMutations.SetRackAutoSort(strips, "bay1", 0, true);

        strips.ClearSession();
        Assert.Contains(("bay1", 0), strips.AutoSortedRacks);

        strips.Reset();
        Assert.Empty(strips.AutoSortedRacks);
    }
}
