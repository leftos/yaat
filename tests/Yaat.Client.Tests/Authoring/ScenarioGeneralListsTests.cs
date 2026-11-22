using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The General tab's three lists (ATC positions, initialization triggers, strip placements) over a real session on a
/// working copy in a temp store: each row commit and each add, remove or move is one undo step on the item in place, a
/// time or rack that does not parse writes nothing, and the position and bay pickers list the ARTCC config's entries.
/// </summary>
public sealed class ScenarioGeneralListsTests : IDisposable
{
    // A ZLA position in the committed ZLA snapshot (tests/Yaat.Sim.Tests/TestData/artcc-zla-snapshot.json): LAX_16_CTR, Sector 16.
    private const string ZlaSector16Id = "01G90YC6C0HZEGQ3FYCDPVFDQW";

    // SFO ATCT's Clearance bay in the committed ZOA config (docs/vnas-artcc-config-examples/zoa.json).
    private const string SfoClearanceBayId = "01GEBP8Q4SVK20PG0WXVHJ9TH9";

    private readonly ScenarioEditorFixture _fx = new();
    private readonly List<ScenarioGeneralViewModel> _generals = [];
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "yaat-general-lists-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (ScenarioGeneralViewModel general in _generals)
        {
            general.Dispose();
        }

        _fx.Dispose();
        if (Directory.Exists(_cacheRoot))
        {
            Directory.Delete(_cacheRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("130", 90, "1:30")]
    [InlineData("1:30", 90, "1:30")]
    [InlineData("", 0, "0:00")]
    [InlineData("1:90", 150, "2:30")]
    public async Task TriggerTime_ATimeThatParses_IsWritten_AndShownFormatted(string text, int seconds, string shown)
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Trigger("T1", "WAIT 1", 45));

        InitializationTriggerRowViewModel row = Assert.Single(general.Triggers);
        Assert.Equal("0:45", row.TimeText);
        row.TimeText = text;
        row.CommitTimeCommand.Execute(null);

        Assert.Equal(seconds, session.Document.Scenario.InitializationTriggers[0].TimeOffset);
        Assert.Equal(shown, row.TimeText);
        Assert.Null(row.TimeError);
        Assert.Equal("Change trigger time", session.UndoDescription);
    }

    [Fact]
    public async Task TriggerTime_ATimeThatDoesNotParse_ShowsTheError_KeepsTheText_WritesNothing()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Trigger("T1", "WAIT 1", 45));
        InitializationTriggerRowViewModel row = general.Triggers[0];

        row.TimeText = "abc";
        row.CommitTimeCommand.Execute(null);

        Assert.Equal("Enter a time like 1:30, 1:02:30 or 130.", row.TimeError);
        Assert.Equal("abc", row.TimeText);
        Assert.Equal(45, session.Document.Scenario.InitializationTriggers[0].TimeOffset);
        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo);

        row.TimeText = "1:00";
        row.CommitTimeCommand.Execute(null);
        Assert.Null(row.TimeError);
        Assert.Equal(60, session.Document.Scenario.InitializationTriggers[0].TimeOffset);
    }

    [Fact]
    public async Task TriggerCommand_IsTrimmedOnCommit()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Trigger("T1", "WAIT 1", 45));
        InitializationTriggerRowViewModel row = general.Triggers[0];

        row.Command = "  SQALL  ";
        row.CommitCommandCommand.Execute(null);

        Assert.Equal("SQALL", session.Document.Scenario.InitializationTriggers[0].Command);
        Assert.Equal("SQALL", row.Command);
        Assert.Equal("Change trigger command", session.UndoDescription);
    }

    [Fact]
    public async Task AtcList_AddRemoveMove_EachOneUndoStep_UndoRestoresTheList()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Atc("A1", "P1") + ", " + Atc("A2", "P2"), "atc");

        AssertListOps(
            session,
            new ListOps(
                () => general.AddAtcPositionCommand.Execute(null),
                () => general.RemoveAtcPositionCommand.Execute(general.AtcPositions[0]),
                i => general.MoveAtcPositionUpCommand.Execute(general.AtcPositions[i]),
                i => general.MoveAtcPositionDownCommand.Execute(general.AtcPositions[i]),
                () => [.. session.Document.Scenario.Atc.Select(a => a.Id)],
                () => general.AtcPositions.Count,
                "ATC position"
            )
        );
        general.AddAtcPositionCommand.Execute(null);
        ScenarioAtc added = session.Document.Scenario.Atc[^1];
        Assert.Equal((ScenarioEditorFixture.Zoa, "", "", false), (added.ArtccId, added.FacilityId, added.PositionId, added.AutoConnect));
        Assert.Empty(added.AutoTrackAirportIds);
        Assert.False(string.IsNullOrEmpty(added.Id));
    }

    [Fact]
    public async Task TriggerList_AddRemoveMove_EachOneUndoStep_UndoRestoresTheList()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Trigger("T1", "A", 1) + ", " + Trigger("T2", "B", 2));

        AssertListOps(
            session,
            new ListOps(
                () => general.AddTriggerCommand.Execute(null),
                () => general.RemoveTriggerCommand.Execute(general.Triggers[0]),
                i => general.MoveTriggerUpCommand.Execute(general.Triggers[i]),
                i => general.MoveTriggerDownCommand.Execute(general.Triggers[i]),
                () => [.. session.Document.Scenario.InitializationTriggers.Select(t => t.Id ?? "")],
                () => general.Triggers.Count,
                "trigger"
            )
        );
        general.AddTriggerCommand.Execute(null);
        InitializationTrigger added = session.Document.Scenario.InitializationTriggers[^1];
        Assert.Equal(("", 0), (added.Command, added.TimeOffset));
        Assert.False(string.IsNullOrEmpty(added.Id));
    }

    [Fact]
    public async Task StripList_AddRemoveMove_EachOneUndoStep_UndoRestoresTheList()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(
            Strip("S1", 1) + ", " + Strip("S2", 2),
            "flightStripConfigurations"
        );

        AssertListOps(
            session,
            new ListOps(
                () => general.AddStripPlacementCommand.Execute(null),
                () => general.RemoveStripPlacementCommand.Execute(general.StripPlacements[0]),
                i => general.MoveStripPlacementUpCommand.Execute(general.StripPlacements[i]),
                i => general.MoveStripPlacementDownCommand.Execute(general.StripPlacements[i]),
                () => [.. session.Document.Scenario.FlightStripConfigurations.Select(s => s.Id ?? "")],
                () => general.StripPlacements.Count,
                "strip placement"
            )
        );
        general.AddStripPlacementCommand.Execute(null);
        FlightStripConfiguration added = session.Document.Scenario.FlightStripConfigurations[^1];
        Assert.Equal(((string?)null, (string?)null, 0), (added.FacilityId, added.BayId, added.Rack));
        Assert.Empty(added.AircraftIds);
        Assert.False(string.IsNullOrEmpty(added.Id));
    }

    [Fact]
    public async Task NullLists_OpenEmpty_AndOneAddToEachWritesAOneItemList()
    {
        _fx.Store.CreateLocalScenario(ScenarioEditorFixture.Zoa, RealScenarioWithNullLists(), "L1");
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, "L1");
        var general = new ScenarioGeneralViewModel(session, _fx.Services());
        _generals.Add(general);
        await _fx.Ui.Run(general.PickersLoaded);
        Assert.Empty(general.AtcPositions);
        Assert.Empty(general.Triggers);
        Assert.Empty(general.StripPlacements);
        Assert.False(session.IsDirty);

        general.AddAtcPositionCommand.Execute(null);
        general.AddTriggerCommand.Execute(null);
        general.AddStripPlacementCommand.Execute(null);

        Scenario scenario = session.Document.Scenario;
        Assert.Single(scenario.Atc);
        Assert.Single(scenario.InitializationTriggers);
        Assert.Single(scenario.FlightStripConfigurations);
        Assert.Single(general.AtcPositions);
        Assert.Single(general.Triggers);
        Assert.Single(general.StripPlacements);
    }

    [Fact]
    public async Task StaleRow_AfterUndoingTheAddThatMadeIt_WritesNothing()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Trigger("T1", "A", 1));
        general.AddTriggerCommand.Execute(null);
        InitializationTriggerRowViewModel stale = general.Triggers[^1];
        Assert.True(session.Undo());
        string before = session.Document.Write();

        stale.TimeText = "9:00";
        stale.CommitTimeCommand.Execute(null);
        stale.Command = "SQALL";
        stale.CommitCommandCommand.Execute(null);
        general.RemoveTriggerCommand.Execute(stale);
        general.MoveTriggerUpCommand.Execute(stale);

        Assert.False(session.CanUndo);
        Assert.Equal(before, session.Document.Write());
    }

    [Fact]
    public async Task StaleRow_AfterUndoingAMove_DoesNotWriteToTheItemNowAtItsIndex()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Trigger("T1", "A", 1) + ", " + Trigger("T2", "B", 2));
        general.MoveTriggerDownCommand.Execute(general.Triggers[0]);
        InitializationTriggerRowViewModel stale = general.Triggers[0];
        Assert.True(session.Undo());
        string before = session.Document.Write();

        stale.TimeText = "9:00";
        stale.CommitTimeCommand.Execute(null);
        general.MoveTriggerDownCommand.Execute(stale);

        Assert.False(session.CanUndo);
        Assert.Equal(before, session.Document.Write());
        Assert.Equal(1, session.Document.Scenario.InitializationTriggers[0].TimeOffset);
    }

    [Fact]
    public async Task RowCommands_WithNoRow_DoNothing()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Trigger("T1", "A", 1));

        general.RemoveAtcPositionCommand.Execute(null);
        general.MoveAtcPositionUpCommand.Execute(null);
        general.MoveAtcPositionDownCommand.Execute(null);
        general.RemoveTriggerCommand.Execute(null);
        general.MoveTriggerUpCommand.Execute(null);
        general.MoveTriggerDownCommand.Execute(null);
        general.RemoveStripPlacementCommand.Execute(null);
        general.MoveStripPlacementUpCommand.Execute(null);
        general.MoveStripPlacementDownCommand.Execute(null);

        Assert.False(session.CanUndo);
        Assert.Single(general.Triggers);
    }

    [Fact]
    public async Task RowEdits_KeepUnmodelledFieldsOfTheItem_ThroughSave()
    {
        string atc = "{\"id\": \"A1\", \"artccId\": \"ZOA\", \"facilityId\": \"ZOA\", \"positionId\": \"P1\", \"xAtcExtra\": \"keep-atc\"}";
        string trigger = "{\"id\": \"T1\", \"command\": \"A\", \"timeOffset\": 5, \"xTriggerExtra\": \"keep-trigger\"}";
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open($"\"atc\": [{atc}], \"initializationTriggers\": [{trigger}]");

        general.AtcPositions[0].AutoTrackAirportsText = "OAK,  sfo SJC";
        general.AtcPositions[0].CommitAutoTrackAirportsCommand.Execute(null);
        Assert.Equal("Change auto-track airports", session.UndoDescription);
        general.Triggers[0].TimeText = "2:00";
        general.Triggers[0].CommitTimeCommand.Execute(null);
        Assert.Equal(ScenarioSaveOutcome.Saved, session.Save().Outcome);

        using var saved = JsonDocument.Parse(File.ReadAllText(_fx.Store.GetWorkingCopyPath(ScenarioEditorFixture.Zoa, "L1")));
        JsonElement savedAtc = saved.RootElement.GetProperty("atc")[0];
        JsonElement savedTrigger = saved.RootElement.GetProperty("initializationTriggers")[0];
        Assert.Equal("keep-atc", savedAtc.GetProperty("xAtcExtra").GetString());
        Assert.Equal(["OAK", "sfo", "SJC"], savedAtc.GetProperty("autoTrackAirportIds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("keep-trigger", savedTrigger.GetProperty("xTriggerExtra").GetString());
        Assert.Equal(120, savedTrigger.GetProperty("timeOffset").GetInt32());
        Assert.Equal("OAK, sfo, SJC", general.AtcPositions[0].AutoTrackAirportsText);
    }

    [Fact]
    public async Task RowEdit_UndoAndRedoAcrossASave_ReloadTheRows()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Trigger("T1", "A", 30));

        general.Triggers[0].TimeText = "5:00";
        general.Triggers[0].CommitTimeCommand.Execute(null);
        Assert.Equal(ScenarioSaveOutcome.Saved, session.Save().Outcome);
        Assert.False(session.IsDirty);

        Assert.True(session.Undo());
        Assert.True(session.IsDirty);
        Assert.Equal(30, session.Document.Scenario.InitializationTriggers[0].TimeOffset);
        Assert.Equal("0:30", general.Triggers[0].TimeText);

        Assert.True(session.Redo());
        Assert.False(session.IsDirty);
        Assert.Equal(300, session.Document.Scenario.InitializationTriggers[0].TimeOffset);
        Assert.Equal("5:00", general.Triggers[0].TimeText);
    }

    [Fact]
    public async Task AtcAutoConnect_IsCommittedOnChange_AsOneUndoStep()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Atc("A1", "P1"), "atc");

        general.AtcPositions[0].AutoConnect = true;

        Assert.True(session.Document.Scenario.Atc[0].AutoConnect);
        Assert.Equal("Change auto-connect", session.UndoDescription);
    }

    [Fact]
    public async Task AtcPositionPicker_PickingANeighboursPosition_WritesItsPositionFacilityAndArtcc()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await OpenWithRealZoa(
            Atc("A1", ScenarioEditorFixture.ZoaSector14Id),
            "atc"
        );
        ScenarioAtcRowViewModel row = general.AtcPositions[0];
        Assert.Contains(row.PositionOptions, o => o == new ScenarioPickOption(ZlaSector16Id, "LAX_16_CTR — Sector 16 (ZLA)"));
        Assert.Contains(row.PositionOptions, o => o.Value == ScenarioEditorFixture.ZoaSector14Id);

        row.PositionId = ZlaSector16Id;
        row.CommitPositionCommand.Execute(null);

        ScenarioAtc atc = session.Document.Scenario.Atc[0];
        Assert.Equal((ZlaSector16Id, "ZLA", "ZLA"), (atc.PositionId, atc.FacilityId, atc.ArtccId));
        Assert.Equal("Change ATC position", session.UndoDescription);
        Assert.True(session.Undo());
        Assert.Equal(
            (ScenarioEditorFixture.ZoaSector14Id, "ZOA", "ZOA"),
            (session.Document.Scenario.Atc[0].PositionId, session.Document.Scenario.Atc[0].FacilityId, session.Document.Scenario.Atc[0].ArtccId)
        );
    }

    [Fact]
    public async Task AtcPositionPicker_AnUnlistedPosition_IsAppendedWithItsLabel()
    {
        (_, ScenarioGeneralViewModel general) = await OpenWithRealZoa(Atc("A1", "GONE1"), "atc");

        ScenarioPickOption last = general.AtcPositions[0].PositionOptions[^1];

        Assert.Equal(new ScenarioPickOption("GONE1", "GONE1 (not in the ARTCC config)"), last);
        Assert.Equal("GONE1", general.AtcPositions[0].PositionId);
    }

    [Fact]
    public async Task StripBayPicker_ListsTheArtccConfigsBays_AndPickingOneWritesFacilityAndBay()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await OpenWithRealZoa(Strip("S1", 0), "flightStripConfigurations");
        StripPlacementRowViewModel row = general.StripPlacements[0];
        string value = "SFO/" + SfoClearanceBayId;
        Assert.Contains(new ScenarioPickOption(value, "Clearance (SFO)"), row.BayOptions);
        Assert.Contains(row.BayOptions, o => o.Value.StartsWith("OAK/", StringComparison.Ordinal));
        Assert.DoesNotContain(row.BayOptions, o => o.Value.StartsWith("ZLA/", StringComparison.Ordinal));

        row.BayValue = value;
        row.CommitBayCommand.Execute(null);

        FlightStripConfiguration strip = session.Document.Scenario.FlightStripConfigurations[0];
        Assert.Equal(("SFO", SfoClearanceBayId), (strip.FacilityId, strip.BayId));
        Assert.Equal("Change strip bay", session.UndoDescription);
    }

    [Fact]
    public async Task StripBayPicker_AnUnlistedPair_IsAppendedWithItsLabel()
    {
        string strip = "{\"id\": \"S1\", \"facilityId\": \"XYZ\", \"bayId\": \"B9\", \"rack\": 1}";
        (_, ScenarioGeneralViewModel general) = await OpenWithRealZoa($"\"flightStripConfigurations\": [{strip}]");

        StripPlacementRowViewModel row = general.StripPlacements[0];

        Assert.Equal("XYZ/B9", row.BayValue);
        Assert.Equal(new ScenarioPickOption("XYZ/B9", "XYZ/B9 (not in the ARTCC config)"), row.BayOptions[^1]);
    }

    [Fact]
    public async Task StripRack_NotANumber_ShowsTheError_WritesNothing_ANumberIsWritten()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Strip("S1", 1), "flightStripConfigurations");
        StripPlacementRowViewModel row = general.StripPlacements[0];

        row.RackText = "x";
        row.CommitRackCommand.Execute(null);

        Assert.Equal("Enter a rack number.", row.RackError);
        Assert.Equal(1, session.Document.Scenario.FlightStripConfigurations[0].Rack);
        Assert.False(session.IsDirty);

        row.RackText = " 2 ";
        row.CommitRackCommand.Execute(null);
        Assert.Null(row.RackError);
        Assert.Equal(2, session.Document.Scenario.FlightStripConfigurations[0].Rack);
        Assert.Equal("Change strip rack", session.UndoDescription);
    }

    [Fact]
    public async Task StripAircraft_IsSplitOnCommasAndWhitespace_InOrder()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(Strip("S1", 1), "flightStripConfigurations");
        StripPlacementRowViewModel row = general.StripPlacements[0];

        row.AircraftText = " UAL1, ,dal2   SWA3 ";
        row.CommitAircraftCommand.Execute(null);

        Assert.Equal(["UAL1", "dal2", "SWA3"], session.Document.Scenario.FlightStripConfigurations[0].AircraftIds);
        Assert.Equal("UAL1, dal2, SWA3", row.AircraftText);
        Assert.Equal("Change strip aircraft", session.UndoDescription);
    }

    private static string Trigger(string id, string command, int offset) =>
        $"{{\"id\": \"{id}\", \"command\": \"{command}\", \"timeOffset\": {offset}}}";

    private static string Atc(string id, string positionId) =>
        $"{{\"id\": \"{id}\", \"artccId\": \"ZOA\", \"facilityId\": \"ZOA\", \"positionId\": \"{positionId}\"}}";

    private static string Strip(string id, int rack) => $"{{\"id\": \"{id}\", \"rack\": {rack}, \"aircraftIds\": []}}";

    // Two items: add appends (one undo step that restores the list), remove takes the first, moves swap, and a move past
    // either end writes nothing.
    private static void AssertListOps(ScenarioEditSession session, ListOps ops)
    {
        Assert.Equal(["1", "2"], ops.Ids().Select(id => id[^1..]));
        AssertOneStep(session, ops, ops.Add, "Add " + ops.Noun, after => Assert.Equal(3, after.Count));
        AssertOneStep(session, ops, ops.Remove, "Remove " + ops.Noun, after => Assert.Equal(["2"], after.Select(id => id[^1..])));
        AssertOneStep(session, ops, () => ops.MoveDown(0), "Move " + ops.Noun, after => Assert.Equal(["2", "1"], after.Select(id => id[^1..])));
        AssertOneStep(session, ops, () => ops.MoveUp(1), "Move " + ops.Noun, after => Assert.Equal(["2", "1"], after.Select(id => id[^1..])));

        ops.MoveUp(0);
        ops.MoveDown(1);
        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo);
    }

    private static void AssertOneStep(
        ScenarioEditSession session,
        ListOps ops,
        Action op,
        string description,
        Action<IReadOnlyList<string>> checkAfter
    )
    {
        string before = session.Document.Write();
        op();
        Assert.Equal(description, session.UndoDescription);
        IReadOnlyList<string> after = ops.Ids();
        checkAfter(after);
        Assert.Equal(after.Count, ops.RowCount());
        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        Assert.Equal(before, session.Document.Write());
        Assert.Equal(ops.Ids().Count, ops.RowCount());
    }

    // A committed real scenario's text with its three lists written as JSON null.
    private static string RealScenarioWithNullLists()
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sfo-gc-scenario.json")))!;
        root["atc"] = null;
        root["initializationTriggers"] = null;
        root["flightStripConfigurations"] = null;
        return root.ToJsonString();
    }

    // Working copy L1 holding the members, its session (rows reload when undo or redo replaces the document), and a
    // General tab whose pickers have loaded with no ARTCC config.
    private Task<(ScenarioEditSession Session, ScenarioGeneralViewModel General)> Open(string items, string member = "initializationTriggers") =>
        OpenOn(items.StartsWith('{') ? $"\"{member}\": [{items}]" : items, _fx.Services());

    // As Open, with data-api answering ZOA with the committed real ZOA config and ZLA with the committed real ZLA snapshot.
    private Task<(ScenarioEditSession Session, ScenarioGeneralViewModel General)> OpenWithRealZoa(string items, string member = "atc")
    {
        string zoa = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "zoa.json"));
        string zla = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "artcc-zla-snapshot.json"));
        var configs = new ArtccConfigSource(new HttpClient(new StubArtccHandler(r => Answer(r, zoa, zla))), _cacheRoot, () => DateTime.UtcNow);
        return OpenOn(items.StartsWith('{') ? $"\"{member}\": [{items}]" : items, _fx.Services() with { Configs = configs });
    }

    private async Task<(ScenarioEditSession Session, ScenarioGeneralViewModel General)> OpenOn(string extra, ScenarioBrowserServices services)
    {
        _fx.AddCopy("L1", "Lima", extra);
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, "L1");
        var general = new ScenarioGeneralViewModel(session, services);
        _generals.Add(general);
        session.DocumentReplaced += (_, _) => general.Reload();
        await _fx.Ui.Run(general.PickersLoaded);
        return (session, general);
    }

    private static string? Answer(HttpRequestMessage request, string zoa, string zla)
    {
        string path = request.RequestUri?.AbsolutePath ?? "";
        return path.EndsWith("/ZOA", StringComparison.Ordinal) ? zoa
            : path.EndsWith("/ZLA", StringComparison.Ordinal) ? zla
            : null;
    }

    private sealed record ListOps(
        Action Add,
        Action Remove,
        Action<int> MoveUp,
        Action<int> MoveDown,
        Func<IReadOnlyList<string>> Ids,
        Func<int> RowCount,
        string Noun
    );
}
