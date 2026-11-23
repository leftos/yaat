using System.ComponentModel;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Aircraft tab's parking picker, over the committed SFO ground scenario as a working copy: a picked stand is written
/// through the parking field's commit as one "Edit parking" step that marks the copy dirty and raises Edited; the picker
/// opens the airport the scenario loader places a Parking start at; and it is offered only for a Parking start.
/// </summary>
public sealed class ScenarioAircraftParkingPickTests : IDisposable
{
    private const string CopyId = "P1";

    // One aircraft parked at A1 with no airport and no flight plan, in a scenario with no primary airport.
    private const string NoAirportAircraft =
        "\"aircraft\": [{\"id\": \"a1\", \"aircraftId\": \"N1\", \"aircraftType\": \"C172\","
        + " \"startingConditions\": {\"type\": \"Parking\", \"parking\": \"A1\"}}]";

    private readonly ScenarioEditorFixture _fx = new();

    public ScenarioAircraftParkingPickTests()
    {
        TestVnasData.EnsureAircraftDataInitialized();
    }

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void PickedStand_CommitsOneUndoStep_AndMarksDirty()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open(root => root["aircraft"]![0]!["airportId"] = "KOAK");
        int edited = 0;
        vm.Edited += (_, _) => edited++;
        Assert.False(session.IsDirty);

        vm.ApplyPickedParking("B12");

        Assert.Equal("B12", session.Document.Scenario.Aircraft[0].StartingConditions.Parking);
        Assert.Equal("B12", vm.Parking);
        Assert.Equal("Edit parking", session.UndoDescription);
        Assert.True(session.IsDirty);
        Assert.Equal(1, edited);

        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        Assert.Equal("A1", session.Document.Scenario.Aircraft[0].StartingConditions.Parking);
        Assert.Equal("A1", vm.Parking);
    }

    [Theory]
    [InlineData("KOAK", "SFO", "KSJC", "KOAK")]
    [InlineData(null, "SFO", "KSJC", "SFO")]
    [InlineData(null, null, "KSJC", "KSJC")]
    [InlineData(null, null, null, null)]
    public void AirportIdFollowsTheLoaderRule(string? airportId, string? primaryAirportId, string? departure, string? expected)
    {
        (_, ScenarioAircraftViewModel vm) = Open(root =>
        {
            JsonObject aircraft = root["aircraft"]![0]!.AsObject();
            Set(aircraft, "airportId", airportId);
            Set(root.AsObject(), "primaryAirportId", primaryAirportId);
            if (departure is null)
            {
                aircraft.Remove("flightplan");
            }
            else
            {
                JsonObject plan = (aircraft["flightplan"] as JsonObject) ?? [];
                plan["departure"] = departure;
                aircraft["flightplan"] = plan;
            }
        });

        Assert.Equal(expected, vm.ParkingAirportId);
        Assert.Equal(expected is not null, vm.CanPickParking);
    }

    [Fact]
    public void PickerOnlyOfferedForParkingKind()
    {
        (_, ScenarioAircraftViewModel vm) = Open(root => root["aircraft"]![0]!["airportId"] = "KOAK");
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(vm.CanPickParking);

        vm.StartKindSelection = vm.StartKindOptions.Single(o => o.Value == StartingConditionKinds.OnRunway);

        Assert.False(vm.CanPickParking);
        Assert.Contains(nameof(ScenarioAircraftViewModel.CanPickParking), changed);

        vm.StartKindSelection = vm.StartKindOptions.Single(o => o.Value == StartingConditionKinds.Parking);
        Assert.True(vm.CanPickParking);

        vm.Selected = null;
        Assert.False(vm.CanPickParking);
    }

    [Fact]
    public async Task SettingTheGeneralTabsPrimaryAirport_OffersThePicker_AndClearingItWithdrawsIt()
    {
        _fx.AddCopy("K1", "Kilo", NoAirportAircraft);
        (_, ScenarioEditorViewModel editor) = await _fx.OpenEditorOn("K1");
        ScenarioGeneralViewModel general = editor.General!;
        ScenarioAircraftViewModel aircraft = editor.Aircraft!;
        aircraft.Selected = aircraft.Rows[0];
        var raised = new List<string?>();
        ((INotifyPropertyChanged)aircraft).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(aircraft.CanPickParking);

        general.PrimaryAirportId = "OAK";
        general.CommitPrimaryAirportCommand.Execute(null);

        Assert.Equal("OAK", aircraft.ParkingAirportId);
        Assert.True(aircraft.CanPickParking);
        Assert.Contains(nameof(ScenarioAircraftViewModel.CanPickParking), raised);

        raised.Clear();
        general.PrimaryAirportId = "";
        general.CommitPrimaryAirportCommand.Execute(null);

        Assert.Null(aircraft.ParkingAirportId);
        Assert.False(aircraft.CanPickParking);
        Assert.Contains(nameof(ScenarioAircraftViewModel.CanPickParking), raised);
    }

    private static void Set(JsonObject target, string key, string? value)
    {
        if (value is null)
        {
            target.Remove(key);
        }
        else
        {
            target[key] = value;
        }
    }

    // The corpus scenario with its first aircraft parked at A1, changed further by change, opened with that aircraft selected.
    private (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) Open(Action<JsonNode> change)
    {
        string corpus = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sfo-gc-scenario.json"));
        JsonNode root = JsonNode.Parse(corpus) ?? throw new InvalidOperationException("sfo-gc-scenario.json is not a JSON object");
        root["aircraft"]![0]!["startingConditions"] = JsonNode.Parse("""{"type": "Parking", "parking": "A1"}""");
        change(root);
        _fx.Store.CreateLocalScenario(ScenarioEditorFixture.Zoa, root.ToJsonString(), CopyId);
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, CopyId);
        var vm = new ScenarioAircraftViewModel(session, _fx.Services());
        session.DocumentReplaced += (_, _) => vm.Reload();
        vm.Selected = vm.Rows[0];
        return (session, vm);
    }
}
