using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Gates;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Aircraft tab's Assign Gate over the committed SFO ground scenario and SFO map, with the committed sidecars: a draw
/// writes a free, fitting stand as one "Edit parking" step; a draw within a parking area keeps to it; it is offered only
/// for a Parking start; with nothing free that fits it writes nothing and lists each stand's reason; and with navdata
/// still loading it ranks without the sidecars' areas.
/// </summary>
public sealed class ScenarioAircraftGateAssignTests : IDisposable
{
    private const string CopyId = "G1";

    private const string OldParking = "ZZ9";

    private const string Signature = "Signature";

    private static readonly Lazy<AirportGroundLayout> SfoLayout = new(() =>
        GeoJsonParser.Parse(
            "sfo",
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sfo.geojson")),
            "SFO",
            new LayoutInputs([], VideoMap: null, SpotEdges: [], CornerFillets: CornerFilletOverrides.None)
        )
    );

    private readonly ScenarioEditorFixture _fx = new();

    public ScenarioAircraftGateAssignTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AirportGroundLayout Sfo => SfoLayout.Value;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void AssignGate_Random_WritesFreeFittingStand_AsOneUndoStep()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open("DAL123", "A320", []);
        Scenario scenario = session.Document.Scenario;
        IReadOnlyList<GateCandidate> ranked = GateRanking.Rank(Sfo, Sidecars, scenario, scenario.Aircraft[0], GateScope.Any);
        string expected = GateRanking.Pick(ranked, new Random(42))!.Stand;

        GateAssignment assignment = Assign(vm, vm.CreateGateAssignContext(new Random(42)), GateScope.Any)!;

        Assert.Equal(expected, assignment.Stand);
        Assert.Contains(ranked, c => c.IsFit && (c.Stand == assignment.Stand));
        Assert.Equal(expected, scenario.Aircraft[0].StartingConditions.Parking);
        Assert.Equal("Edit parking", session.UndoDescription);

        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        Assert.Equal(OldParking, session.Document.Scenario.Aircraft[0].StartingConditions.Parking);
    }

    [Fact]
    public void AssignGate_Area_WritesStandInArea()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open("N123AB", "C172", []);

        GateAssignment assignment = Assign(vm, vm.CreateGateAssignContext(new Random(7)), GateScope.Area(Signature))!;

        Assert.NotEmpty(assignment.Ranked);
        Assert.All(assignment.Ranked, c => Assert.Equal("SIGNATURE", c.Area));
        GateCandidate written = Assert.Single(assignment.Ranked, c => c.Stand == assignment.Stand);
        Assert.True(written.IsFit);
        Assert.Equal(written.Stand, session.Document.Scenario.Aircraft[0].StartingConditions.Parking);
        Assert.Equal("Edit parking", session.UndoDescription);
    }

    [Fact]
    public void AssignGate_Hidden_WhenNotParkingStart()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open("DAL123", "A320", []);
        vm.StartKindSelection = vm.StartKindOptions.Single(o => o.Value == StartingConditionKinds.OnRunway);
        string? undo = session.UndoDescription;
        string? parking = session.Document.Scenario.Aircraft[0].StartingConditions.Parking;

        Assert.False(vm.CanPickParking);
        Assert.Null(vm.CreateGateAssignContext(new Random(1)));
        Assert.Null(Assign(vm, vm.CreateGateAssignContext(new Random(1)), GateScope.Any));
        Assert.Equal(undo, session.UndoDescription);
        Assert.Equal(parking, session.Document.Scenario.Aircraft[0].StartingConditions.Parking);
    }

    [Fact]
    public void AssignGate_NoFit_WritesNothing_ListsReasons()
    {
        var probe = new ScenarioAircraft
        {
            Id = "probe",
            AircraftId = "N123AB",
            AircraftType = "C172",
            AirportId = "SFO",
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking },
        };
        List<string> stands =
        [
            .. GateRanking.Rank(Sfo, Sidecars, new Scenario { Aircraft = [probe] }, probe, GateScope.Area(Signature)).Select(c => c.Stand),
        ];
        Assert.NotEmpty(stands);
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open("N123AB", "C172", stands);

        GateAssignment assignment = Assign(vm, vm.CreateGateAssignContext(new Random(7)), GateScope.Area(Signature))!;

        Assert.Null(assignment.Stand);
        Assert.Equal(stands.Order(StringComparer.Ordinal), assignment.Ranked.Select(c => c.Stand).Order(StringComparer.Ordinal));
        GateAssignContext context = vm.CreateGateAssignContext(new Random(7))!;
        Assert.All(
            assignment.Ranked,
            c =>
            {
                Assert.Equal(GateReason.Occupied, c.Reason);
                Assert.Equal("taken until the scenario's end", context.ReasonText(c));
            }
        );
        Assert.Equal(OldParking, session.Document.Scenario.Aircraft[0].StartingConditions.Parking);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void AssignGate_NavdataLoading_RanksWithoutTags()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open("DAL123", "A320", []);
        Scenario scenario = session.Document.Scenario;
        Assert.Contains(GateRanking.Rank(Sfo, Sidecars, scenario, scenario.Aircraft[0], GateScope.Any), c => c.Area is not null);

        GateAssignContext context = vm.CreateGateAssignContext(new Random(3), navdata: null)!;
        GateAssignment assignment = Assign(vm, vm.CreateGateAssignContext(new Random(3), navdata: null), GateScope.Any)!;

        Assert.True(context.NavdataLoading);
        Assert.Same(AirportSidecarCatalog.Empty, context.Sidecars);
        Assert.NotEmpty(assignment.Ranked);
        Assert.All(assignment.Ranked, c => Assert.Null(c.Area));
        Assert.NotNull(assignment.Stand);
        Assert.Equal(assignment.Stand, scenario.Aircraft[0].StartingConditions.Parking);
    }

    [Fact]
    public void ReasonTexts_NameTheAirline_AFit_AndAnotherAirlines()
    {
        var tagged = new AirportSidecarCatalog([
            new AirportSidecar("SFO")
            {
                ParkingAreas = [new ParkingAreaAlias("TERMINALA", ["A*"], null) { MaxAdg = AirplaneDesignGroup.IV, Airlines = ["UAL"] }],
            },
        ]);
        GateAssignContext ual = Context(" UAL123", tagged);
        GateAssignContext dal = Context("DAL123", tagged);
        IReadOnlyList<GateCandidate> forUal = ual.Rank(Sfo, GateScope.Any);
        IReadOnlyList<GateCandidate> forDal = dal.Rank(Sfo, GateScope.Any);

        Assert.Equal("preferred for UAL", ual.ReasonText(forUal.First(c => c.Reason == GateReason.Preferred)));
        Assert.Equal("fits", ual.ReasonText(forUal.First(c => c.Reason == GateReason.Fits)));
        Assert.Equal("another airline's", dal.ReasonText(forDal.First(c => c.Reason == GateReason.OtherAirline)));
    }

    private static AirportSidecarCatalog Sidecars => NavigationDatabase.Instance.AirportSidecars;

    // A ranking context for one SFO Parking start of the callsign, alone in its scenario.
    private static GateAssignContext Context(string callsign, AirportSidecarCatalog sidecars)
    {
        var aircraft = new ScenarioAircraft
        {
            Id = "a1",
            AircraftId = callsign,
            AircraftType = "A320",
            AirportId = "SFO",
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking },
        };
        return new GateAssignContext(new Scenario { Aircraft = [aircraft] }, aircraft, sidecars, false, new Random(1));
    }

    // What the Assign Gate button ships: the context's draw, written through Pick on map's commit when a stand is drawn.
    private static GateAssignment? Assign(ScenarioAircraftViewModel vm, GateAssignContext? context, GateScope scope)
    {
        if (context is null)
        {
            return null;
        }

        GateAssignment assignment = context.Assign(Sfo, scope);
        if (assignment.Stand is { } stand)
        {
            vm.ApplyPickedParking(stand);
        }

        return assignment;
    }

    // The corpus scenario with its first aircraft made a Parking start at SFO, parked at a stand SFO lacks, plus one
    // aircraft parked from the start to the end at each stand of occupied; opened with the first aircraft selected.
    private (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) Open(string callsign, string type, List<string> occupied)
    {
        string corpus = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sfo-gc-scenario.json"));
        JsonNode root = JsonNode.Parse(corpus) ?? throw new InvalidOperationException("sfo-gc-scenario.json is not a JSON object");
        JsonObject first = root["aircraft"]![0]!.AsObject();
        first["aircraftId"] = callsign;
        first["aircraftType"] = type;
        first["airportId"] = "SFO";
        first["startingConditions"] = JsonNode.Parse($$"""{"type": "Parking", "parking": "{{OldParking}}"}""");
        JsonArray aircraft = root["aircraft"]!.AsArray();
        for (int i = 0; i < occupied.Count; i++)
        {
            aircraft.Add(
                new JsonObject
                {
                    ["id"] = $"occ{i}",
                    ["aircraftId"] = $"N9{i}X",
                    ["aircraftType"] = "C172",
                    ["airportId"] = "SFO",
                    ["spawnDelay"] = 0,
                    ["startingConditions"] = new JsonObject { ["type"] = "Parking", ["parking"] = occupied[i] },
                }
            );
        }

        _fx.Store.CreateLocalScenario(ScenarioEditorFixture.Zoa, root.ToJsonString(), CopyId);
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, CopyId);
        var vm = new ScenarioAircraftViewModel(session, _fx.Services());
        session.DocumentReplaced += (_, _) => vm.Reload();
        vm.Selected = vm.Rows[0];
        return (session, vm);
    }
}
