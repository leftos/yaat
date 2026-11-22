using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The General tab's three generator lists (arrival, VFR arrival, overflight) over a real session on a working copy of
/// the committed SFO scenario in a temp store: each add, remove or move and each committed field is one undo step on the
/// item in place, a field that does not parse writes nothing, and the auto-track picker lists the ARTCC config's
/// positions.
/// </summary>
public sealed class ScenarioGeneratorRowsTests : IDisposable
{
    // A ZLA position in the committed ZLA snapshot (tests/Yaat.Sim.Tests/TestData/artcc-zla-snapshot.json): LAX_16_CTR, Sector 16.
    private const string ZlaSector16Id = "01G90YC6C0HZEGQ3FYCDPVFDQW";

    private readonly ScenarioEditorFixture _fx = new();
    private readonly List<ScenarioGeneralViewModel> _generals = [];
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "yaat-generator-rows-tests", Guid.NewGuid().ToString("N"));

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
    [InlineData("arrival")]
    [InlineData("VFR arrival")]
    [InlineData("overflight")]
    public async Task GeneratorList_AddRemoveMove_EachOneUndoStep_UndoRestoresTheList(string kind)
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open();
        ListOps ops = OpsOf(kind, session, general);
        Assert.Equal(0, ops.RowCount());
        ops.Add();
        ops.Add();
        IReadOnlyList<string> ids = ops.Ids();
        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ops.RowCount());

        AssertOneStep(session, ops, ops.Add, "Add " + kind + " generator", after => Assert.Equal(3, after.Count));
        AssertOneStep(session, ops, () => ops.Remove(0), "Remove " + kind + " generator", after => Assert.Equal([ids[1]], after));
        AssertOneStep(session, ops, () => ops.MoveDown(0), "Move " + kind + " generator", after => Assert.Equal([ids[1], ids[0]], after));
        AssertOneStep(session, ops, () => ops.MoveUp(1), "Move " + kind + " generator", after => Assert.Equal([ids[1], ids[0]], after));

        string before = session.Document.Write();
        ops.MoveUp(0);
        ops.MoveDown(1);
        Assert.Equal(before, session.Document.Write());
        Assert.Equal("Add " + kind + " generator", session.UndoDescription);
    }

    [Fact]
    public async Task NewGenerators_TakeTheModelDefaults_WithANewId()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open();

        general.AddArrivalGeneratorCommand.Execute(null);
        general.AddVfrArrivalGeneratorCommand.Execute(null);
        general.AddOverflightGeneratorCommand.Execute(null);

        Scenario scenario = session.Document.Scenario;
        ScenarioGeneratorConfig arrival = Assert.Single(scenario.AircraftGenerators);
        Assert.Equal(("Jet", "Large", 10.0, 50.0, 0.0, 300, (int?)null), Defaults(arrival));
        Assert.Equal(
            (10.0, 20.0, "Piston", "Small"),
            (
                scenario.VfrArrivalGenerators[0].InitialDistance,
                scenario.VfrArrivalGenerators[0].MaxDistance,
                scenario.VfrArrivalGenerators[0].EngineType,
                scenario.VfrArrivalGenerators[0].WeightCategory
            )
        );
        Assert.True(scenario.OverflightGenerators[0].SnapHemisphericAltitude);
        Assert.False(string.IsNullOrEmpty(arrival.Id));
        Assert.False(string.IsNullOrEmpty(scenario.VfrArrivalGenerators[0].Id));
        Assert.False(string.IsNullOrEmpty(scenario.OverflightGenerators[0].Id));
        Assert.Equal("10", general.ArrivalGenerators[0].InitialDistanceText);
        Assert.Equal("5:00", general.ArrivalGenerators[0].IntervalTimeText);
        Assert.Equal("", general.ArrivalGenerators[0].MaxTimeText);
    }

    [Fact]
    public async Task IsYaatOnly_IsFalseForArrivals_TrueForVfrArrivalsAndOverflights()
    {
        (_, ScenarioGeneralViewModel general) = await Open();

        general.AddArrivalGeneratorCommand.Execute(null);
        general.AddVfrArrivalGeneratorCommand.Execute(null);
        general.AddOverflightGeneratorCommand.Execute(null);

        Assert.False(general.ArrivalGenerators[0].IsYaatOnly);
        Assert.True(general.VfrArrivalGenerators[0].IsYaatOnly);
        Assert.True(general.OverflightGenerators[0].IsYaatOnly);
    }

    [Fact]
    public async Task NumberField_ANumberIsWritten_NotANumberShowsTheErrorAndWritesNothing()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open();
        general.AddArrivalGeneratorCommand.Execute(null);
        ScenarioArrivalGeneratorRowViewModel row = general.ArrivalGenerators[0];

        row.InitialDistanceText = " 12.5 ";
        row.CommitInitialDistanceCommand.Execute(null);
        Assert.Equal(12.5, session.Document.Scenario.AircraftGenerators[0].InitialDistance);
        Assert.Equal("12.5", row.InitialDistanceText);
        Assert.Null(row.InitialDistanceError);
        Assert.Equal("Change generator", session.UndoDescription);

        string before = session.Document.Write();
        row.InitialDistanceText = "abc";
        row.CommitInitialDistanceCommand.Execute(null);
        Assert.Equal("Enter a number.", row.InitialDistanceError);
        Assert.Equal("abc", row.InitialDistanceText);
        Assert.Equal(before, session.Document.Write());
        Assert.True(session.Undo());
        Assert.Equal(10, session.Document.Scenario.AircraftGenerators[0].InitialDistance);
    }

    [Fact]
    public async Task NullableNumberField_BlankWritesNull()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open();
        general.AddVfrArrivalGeneratorCommand.Execute(null);
        general.AddOverflightGeneratorCommand.Execute(null);
        ScenarioVfrArrivalGeneratorRowViewModel vfr = general.VfrArrivalGenerators[0];
        ScenarioOverflightGeneratorRowViewModel overflight = general.OverflightGenerators[0];

        vfr.DescendToAltitudeText = "3000";
        vfr.CommitDescendToAltitudeCommand.Execute(null);
        overflight.ExitDistanceText = "30";
        overflight.CommitExitDistanceCommand.Execute(null);
        Assert.Equal(3000, session.Document.Scenario.VfrArrivalGenerators[0].DescendToAltitude);
        Assert.Equal(30, session.Document.Scenario.OverflightGenerators[0].ExitDistance);

        vfr.DescendToAltitudeText = "  ";
        vfr.CommitDescendToAltitudeCommand.Execute(null);
        overflight.ExitDistanceText = "";
        overflight.CommitExitDistanceCommand.Execute(null);
        Assert.Null(session.Document.Scenario.VfrArrivalGenerators[0].DescendToAltitude);
        Assert.Null(session.Document.Scenario.OverflightGenerators[0].ExitDistance);
        Assert.Equal("", vfr.DescendToAltitudeText);
        Assert.Null(vfr.DescendToAltitudeError);
    }

    [Fact]
    public async Task InitialVsFpm_TakesAWholeNumberOnly()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open();
        general.AddVfrArrivalGeneratorCommand.Execute(null);
        ScenarioVfrArrivalGeneratorRowViewModel row = general.VfrArrivalGenerators[0];
        string before = session.Document.Write();

        row.InitialVsFpmText = "1.5";
        row.CommitInitialVsFpmCommand.Execute(null);
        Assert.Equal("Enter a whole number.", row.InitialVsFpmError);
        Assert.Equal(before, session.Document.Write());

        row.InitialVsFpmText = "-500";
        row.CommitInitialVsFpmCommand.Execute(null);
        Assert.Null(row.InitialVsFpmError);
        Assert.Equal(-500, session.Document.Scenario.VfrArrivalGenerators[0].InitialVsFpm);
        Assert.Equal("Change generator", session.UndoDescription);
    }

    [Fact]
    public async Task TimeFields_ParseThroughScenarioTime_AndATimeThatDoesNotParseKeepsTheLastGoodTime()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open();
        general.AddOverflightGeneratorCommand.Execute(null);
        ScenarioOverflightGeneratorRowViewModel row = general.OverflightGenerators[0];
        OverflightGeneratorConfig Generator() => session.Document.Scenario.OverflightGenerators[0];

        row.StartTimeOffsetText = "1:30";
        row.CommitStartTimeOffsetCommand.Execute(null);
        Assert.Equal(90, Generator().StartTimeOffset);
        Assert.Equal("Change generator", session.UndoDescription);

        row.MaxTimeText = "130";
        row.CommitMaxTimeCommand.Execute(null);
        Assert.Equal(90, Generator().MaxTime);
        Assert.Equal("1:30", row.MaxTimeText);
        row.MaxTimeText = "";
        row.CommitMaxTimeCommand.Execute(null);
        Assert.Null(Generator().MaxTime);

        string before = session.Document.Write();
        row.IntervalTimeText = "abc";
        row.CommitIntervalTimeCommand.Execute(null);
        Assert.Equal("Enter a time like 1:30, 1:02:30 or 130.", row.IntervalTimeError);
        Assert.Equal("abc", row.IntervalTimeText);
        Assert.Equal(300, Generator().IntervalTime);
        Assert.Equal(before, session.Document.Write());

        row.IntervalTimeText = "";
        row.CommitIntervalTimeCommand.Execute(null);
        Assert.Null(row.IntervalTimeError);
        Assert.Equal(0, Generator().IntervalTime);
        Assert.Equal("0:00", row.IntervalTimeText);
    }

    [Fact]
    public async Task Enabled_AbsentStaysAbsentUntouched_AndNullFalseTrueNullIsOneUndoStepEach()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open();
        general.AddArrivalGeneratorCommand.Execute(null);
        general.AddArrivalGeneratorCommand.Execute(null);
        Assert.Null(general.ArrivalGenerators[0].Enabled);
        Assert.Null(general.ArrivalGenerators[1].Enabled);
        general.ArrivalGenerators[0].Runway = " 28L ";
        general.ArrivalGenerators[0].CommitRunwayCommand.Execute(null);
        string untouched = session.Document.Write();

        ScenarioArrivalGeneratorRowViewModel row = general.ArrivalGenerators[1];
        row.Enabled = false;
        Assert.False(session.Document.Scenario.AircraftGenerators[1].Enabled);
        row.Enabled = true;
        Assert.True(session.Document.Scenario.AircraftGenerators[1].Enabled);
        row.Enabled = null;
        Assert.Null(session.Document.Scenario.AircraftGenerators[1].Enabled);
        Assert.Equal("Change generator", session.UndoDescription);
        Assert.Equal(untouched, session.Document.Write());

        Assert.True(session.Undo());
        Assert.True(session.Document.Scenario.AircraftGenerators[1].Enabled);
        Assert.True(general.ArrivalGenerators[1].Enabled);
        Assert.True(session.Undo());
        Assert.False(session.Document.Scenario.AircraftGenerators[1].Enabled);
        Assert.True(session.Undo());
        Assert.Null(session.Document.Scenario.AircraftGenerators[1].Enabled);
        Assert.Equal(untouched, session.Document.Write());
        Assert.True(session.Redo());
        Assert.True(session.Redo());
        Assert.True(session.Redo());
        Assert.Null(session.Document.Scenario.AircraftGenerators[1].Enabled);
        Assert.Equal(ScenarioSaveOutcome.Saved, session.Save().Outcome);

        using JsonDocument saved = SavedCopy();
        JsonElement generators = saved.RootElement.GetProperty("aircraftGenerators");
        Assert.Equal("28L", generators[0].GetProperty("runway").GetString());
        Assert.False(generators[0].TryGetProperty("enabled", out _));
        Assert.False(generators[1].TryGetProperty("enabled", out _));
    }

    [Fact]
    public async Task Enabled_ADocumentsFalse_IsShownAsFalse_AndSetToNullIsWrittenAsNull()
    {
        string generator = "{\"id\": \"G1\", \"enabled\": false}";
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(root =>
            root["aircraftGenerators"] = new JsonArray(JsonNode.Parse(generator))
        );
        ScenarioArrivalGeneratorRowViewModel row = general.ArrivalGenerators[0];
        Assert.False(row.Enabled);

        row.Enabled = null;
        Assert.Equal("Change generator", session.UndoDescription);
        Assert.Equal(ScenarioSaveOutcome.Saved, session.Save().Outcome);

        using JsonDocument saved = SavedCopy();
        Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("aircraftGenerators")[0].GetProperty("enabled").ValueKind);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e400")]
    public async Task NumberField_ANonFiniteNumber_ShowsTheError_WritesNothing(string text)
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open();
        general.AddArrivalGeneratorCommand.Execute(null);
        ScenarioArrivalGeneratorRowViewModel row = general.ArrivalGenerators[0];
        string before = session.Document.Write();

        row.MaxDistanceText = text;
        row.CommitMaxDistanceCommand.Execute(null);

        Assert.Equal("Enter a number.", row.MaxDistanceError);
        Assert.Equal(text, row.MaxDistanceText);
        Assert.Equal(before, session.Document.Write());
        Assert.Equal("Add arrival generator", session.UndoDescription);
    }

    [Fact]
    public async Task TimeFields_ADocumentsNegativeTime_IsShownAsRawSeconds()
    {
        string generator = "{\"id\": \"G1\", \"startTimeOffset\": -30, \"intervalTime\": -5}";
        (_, ScenarioGeneralViewModel general) = await Open(root => root["aircraftGenerators"] = new JsonArray(JsonNode.Parse(generator)));

        Assert.Equal("-30", general.ArrivalGenerators[0].StartTimeOffsetText);
        Assert.Equal("-5", general.ArrivalGenerators[0].IntervalTimeText);
    }

    [Fact]
    public async Task Booleans_AreCommittedOnChange()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open();
        general.AddArrivalGeneratorCommand.Execute(null);
        general.AddOverflightGeneratorCommand.Execute(null);

        general.ArrivalGenerators[0].RandomizeInterval = true;
        general.ArrivalGenerators[0].RandomizeWeightCategory = true;
        general.OverflightGenerators[0].SnapHemisphericAltitude = false;

        Scenario scenario = session.Document.Scenario;
        Assert.True(scenario.AircraftGenerators[0].RandomizeInterval);
        Assert.True(scenario.AircraftGenerators[0].RandomizeWeightCategory);
        Assert.False(scenario.OverflightGenerators[0].SnapHemisphericAltitude);
        Assert.Equal("Change generator", session.UndoDescription);
    }

    [Fact]
    public async Task EnginePicker_ListsTheEngineTypes_AndAnUnlistedValueIsAppended()
    {
        string generator = "{\"id\": \"G1\", \"engineType\": \"Rocket\", \"weightCategory\": \"Heavy\"}";
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(root =>
            root["aircraftGenerators"] = new JsonArray(JsonNode.Parse(generator))
        );
        ScenarioArrivalGeneratorRowViewModel row = general.ArrivalGenerators[0];

        Assert.Equal(["Jet", "Turboprop", "Piston", "Rocket"], row.EngineTypeOptions);
        Assert.Equal(["Small", "SmallPlus", "Large", "Heavy"], row.WeightCategoryOptions);

        row.EngineType = "Turboprop";
        row.CommitEngineTypeCommand.Execute(null);
        row.WeightCategory = "Small";
        row.CommitWeightCategoryCommand.Execute(null);
        Assert.Equal(
            ("Turboprop", "Small"),
            (session.Document.Scenario.AircraftGenerators[0].EngineType, session.Document.Scenario.AircraftGenerators[0].WeightCategory)
        );
    }

    [Fact]
    public async Task AutoTrackPicker_PickingAPosition_CreatesTheConditionsWithThatPositionOnly_BlankNullsThem()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open();
        general.AddArrivalGeneratorCommand.Execute(null);
        general.AddVfrArrivalGeneratorCommand.Execute(null);
        ScenarioArrivalGeneratorRowViewModel row = general.ArrivalGenerators[0];
        Assert.Equal(new ScenarioPickOption("", "None"), row.AutoTrackPositionOptions[0]);
        Assert.Contains(row.AutoTrackPositionOptions, o => o == new ScenarioPickOption(ZlaSector16Id, "LAX_16_CTR — Sector 16 (ZLA)"));
        Assert.Contains(row.AutoTrackPositionOptions, o => o.Value == ScenarioEditorFixture.ZoaSector14Id);

        row.AutoTrackPositionId = ZlaSector16Id;
        row.CommitAutoTrackPositionCommand.Execute(null);
        AutoTrackConditions? conditions = session.Document.Scenario.AircraftGenerators[0].AutoTrackConfiguration;
        Assert.NotNull(conditions);
        Assert.Equal(ZlaSector16Id, conditions.PositionId);
        Assert.Equal(
            ((int?)null, (string?)null, (string?)null, (string?)null),
            (conditions.HandoffDelay, conditions.ScratchPad, conditions.InterimAltitude, conditions.ClearedAltitude)
        );
        Assert.Null(conditions.ExtensionData);
        Assert.Equal("Change generator", session.UndoDescription);

        general.VfrArrivalGenerators[0].AutoTrackPositionId = ScenarioEditorFixture.ZoaSector14Id;
        general.VfrArrivalGenerators[0].CommitAutoTrackPositionCommand.Execute(null);
        Assert.Equal(ScenarioEditorFixture.ZoaSector14Id, session.Document.Scenario.VfrArrivalGenerators[0].AutoTrackConfiguration?.PositionId);

        row.AutoTrackPositionId = "";
        row.CommitAutoTrackPositionCommand.Execute(null);
        Assert.Null(session.Document.Scenario.AircraftGenerators[0].AutoTrackConfiguration);
    }

    [Fact]
    public async Task AutoTrackPicker_OnConditionsWithAHandoffDelay_KeepsTheDelay_AndAnUnlistedPositionIsOffered()
    {
        string generator = "{\"id\": \"G1\", \"autoTrackConfiguration\": {\"positionId\": \"GONE1\", \"handoffDelay\": 30, \"scratchPad\": \"XY\"}}";
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(root =>
            root["aircraftGenerators"] = new JsonArray(JsonNode.Parse(generator))
        );
        ScenarioArrivalGeneratorRowViewModel row = general.ArrivalGenerators[0];
        Assert.Equal("GONE1", row.AutoTrackPositionId);
        Assert.Equal(new ScenarioPickOption("GONE1", "GONE1 (not in the ARTCC config)"), row.AutoTrackPositionOptions[^1]);

        row.AutoTrackPositionId = ZlaSector16Id;
        row.CommitAutoTrackPositionCommand.Execute(null);

        AutoTrackConditions? conditions = session.Document.Scenario.AircraftGenerators[0].AutoTrackConfiguration;
        Assert.Equal((ZlaSector16Id, 30, "XY"), (conditions?.PositionId, conditions?.HandoffDelay, conditions?.ScratchPad));
    }

    [Fact]
    public async Task RowEdit_KeepsUnmodelledFieldsOfTheGenerator_ThroughSave()
    {
        string generator =
            "{\"id\": \"G1\", \"runway\": \"28R\", \"xGeneratorExtra\": \"keep-generator\", "
            + "\"autoTrackConfiguration\": {\"positionId\": \"P1\", \"xAutoTrackExtra\": 7}}";
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open(root =>
            root["aircraftGenerators"] = new JsonArray(JsonNode.Parse(generator))
        );
        ScenarioArrivalGeneratorRowViewModel row = general.ArrivalGenerators[0];

        row.Runway = "28L";
        row.CommitRunwayCommand.Execute(null);
        row.MaxDistanceText = "40";
        row.CommitMaxDistanceCommand.Execute(null);
        Assert.Equal(ScenarioSaveOutcome.Saved, session.Save().Outcome);

        using JsonDocument saved = SavedCopy();
        JsonElement savedGenerator = saved.RootElement.GetProperty("aircraftGenerators")[0];
        Assert.Equal("keep-generator", savedGenerator.GetProperty("xGeneratorExtra").GetString());
        Assert.Equal(7, savedGenerator.GetProperty("autoTrackConfiguration").GetProperty("xAutoTrackExtra").GetInt32());
        Assert.Equal("28L", savedGenerator.GetProperty("runway").GetString());
        Assert.Equal(40, savedGenerator.GetProperty("maxDistance").GetDouble());
        Assert.False(savedGenerator.TryGetProperty("enabled", out _));
    }

    [Fact]
    public async Task StaleRow_AfterUndoingTheAddThatMadeIt_WritesNothing()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open();
        general.AddArrivalGeneratorCommand.Execute(null);
        general.AddVfrArrivalGeneratorCommand.Execute(null);
        ScenarioArrivalGeneratorRowViewModel arrival = general.ArrivalGenerators[0];
        ScenarioVfrArrivalGeneratorRowViewModel vfr = general.VfrArrivalGenerators[0];
        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.Empty(general.ArrivalGenerators);
        string before = session.Document.Write();

        arrival.Runway = "28L";
        arrival.CommitRunwayCommand.Execute(null);
        arrival.InitialDistanceText = "abc";
        arrival.CommitInitialDistanceCommand.Execute(null);
        arrival.Enabled = false;
        vfr.StartTimeOffsetText = "9:00";
        vfr.CommitStartTimeOffsetCommand.Execute(null);
        vfr.AutoTrackPositionId = ZlaSector16Id;
        vfr.CommitAutoTrackPositionCommand.Execute(null);
        general.RemoveArrivalGeneratorCommand.Execute(arrival);
        general.MoveVfrArrivalGeneratorUpCommand.Execute(vfr);
        general.RemoveOverflightGeneratorCommand.Execute(null);

        Assert.Null(arrival.InitialDistanceError);
        Assert.False(session.CanUndo);
        Assert.Equal(before, session.Document.Write());
    }

    private static (string, string, double, double, double, int, int?) Defaults(ScenarioGeneratorConfig g) =>
        (g.EngineType, g.WeightCategory, g.InitialDistance, g.MaxDistance, g.IntervalDistance, g.IntervalTime, g.MaxTime);

    private static ListOps OpsOf(string kind, ScenarioEditSession session, ScenarioGeneralViewModel general) =>
        kind switch
        {
            "arrival" => new ListOps(
                () => general.AddArrivalGeneratorCommand.Execute(null),
                i => general.RemoveArrivalGeneratorCommand.Execute(general.ArrivalGenerators[i]),
                i => general.MoveArrivalGeneratorUpCommand.Execute(general.ArrivalGenerators[i]),
                i => general.MoveArrivalGeneratorDownCommand.Execute(general.ArrivalGenerators[i]),
                () => [.. session.Document.Scenario.AircraftGenerators.Select(g => g.Id)],
                () => general.ArrivalGenerators.Count
            ),
            "VFR arrival" => new ListOps(
                () => general.AddVfrArrivalGeneratorCommand.Execute(null),
                i => general.RemoveVfrArrivalGeneratorCommand.Execute(general.VfrArrivalGenerators[i]),
                i => general.MoveVfrArrivalGeneratorUpCommand.Execute(general.VfrArrivalGenerators[i]),
                i => general.MoveVfrArrivalGeneratorDownCommand.Execute(general.VfrArrivalGenerators[i]),
                () => [.. session.Document.Scenario.VfrArrivalGenerators.Select(g => g.Id)],
                () => general.VfrArrivalGenerators.Count
            ),
            _ => new ListOps(
                () => general.AddOverflightGeneratorCommand.Execute(null),
                i => general.RemoveOverflightGeneratorCommand.Execute(general.OverflightGenerators[i]),
                i => general.MoveOverflightGeneratorUpCommand.Execute(general.OverflightGenerators[i]),
                i => general.MoveOverflightGeneratorDownCommand.Execute(general.OverflightGenerators[i]),
                () => [.. session.Document.Scenario.OverflightGenerators.Select(g => g.Id)],
                () => general.OverflightGenerators.Count
            ),
        };

    // One op is one undo step named by the description, and its undo restores the document text and the undo stack's top.
    private static void AssertOneStep(
        ScenarioEditSession session,
        ListOps ops,
        Action op,
        string description,
        Action<IReadOnlyList<string>> checkAfter
    )
    {
        string before = session.Document.Write();
        string? undoBefore = session.UndoDescription;
        op();
        Assert.Equal(description, session.UndoDescription);
        IReadOnlyList<string> after = ops.Ids();
        checkAfter(after);
        Assert.Equal(after.Count, ops.RowCount());
        Assert.True(session.Undo());
        Assert.Equal(undoBefore, session.UndoDescription);
        Assert.Equal(before, session.Document.Write());
        Assert.Equal(ops.Ids().Count, ops.RowCount());
    }

    private JsonDocument SavedCopy() => JsonDocument.Parse(File.ReadAllText(_fx.Store.GetWorkingCopyPath(ScenarioEditorFixture.Zoa, "L1")));

    // Working copy L1 of the committed SFO scenario (optionally edited as JSON), its session (rows reload when undo or
    // redo replaces the document), and a General tab whose pickers loaded the committed real ZOA config and ZLA snapshot.
    private async Task<(ScenarioEditSession Session, ScenarioGeneralViewModel General)> Open(Action<JsonNode>? edit = null)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "sfo-gc-scenario.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidOperationException("sfo-gc-scenario.json is not a JSON object");
        edit?.Invoke(root);
        _fx.Store.CreateLocalScenario(ScenarioEditorFixture.Zoa, root.ToJsonString(), "L1");

        string zoa = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "zoa.json"));
        string zla = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "artcc-zla-snapshot.json"));
        var configs = new ArtccConfigSource(new HttpClient(new StubArtccHandler(r => Answer(r, zoa, zla))), _cacheRoot, () => DateTime.UtcNow);
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, "L1");
        var general = new ScenarioGeneralViewModel(session, _fx.Services() with { Configs = configs });
        _generals.Add(general);
        session.DocumentReplaced += (_, _) => general.Reload();
        await _fx.Ui.Run(general.PickersLoaded);
        Assert.False(session.CanUndo);
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
        Action<int> Remove,
        Action<int> MoveUp,
        Action<int> MoveDown,
        Func<IReadOnlyList<string>> Ids,
        Func<int> RowCount
    );
}
