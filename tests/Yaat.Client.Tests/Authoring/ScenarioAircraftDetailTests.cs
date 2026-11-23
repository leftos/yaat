using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Aircraft tab's detail panel over the committed SFO ground scenario as a working copy, its first aircraft's start
/// set by hand: each starting-condition kind shows only the fields <see cref="StartingConditionKinds.FieldsOf"/> lists,
/// each field's commit writes its model member as one undo step that undo takes back, a kind switch clears what the new
/// kind does not use in one step, and a text that is not a value of its field keeps the text, shows the error and writes
/// nothing.
/// </summary>
public sealed class ScenarioAircraftDetailTests : IDisposable
{
    private const string CopyId = "D1";

    // A Coordinates start that holds every member the kind uses.
    private const string CoordinatesStart =
        """{"type": "Coordinates", "coordinates": {"lat": 37.6, "lon": -122.4},"""
        + """ "altitude": 3000, "speed": 180, "navigationPath": "SFO", "heading": 90}""";

    // Every starting-condition field the panel edits: the member it writes (by JSON name), whether the panel shows it,
    // its commit, how the member reads back, a text to commit and the value it writes.
    private static readonly StartField[] StartFields =
    [
        new(
            "parking",
            vm => vm.ShowsParking,
            (vm, t) => Commit(() => vm.Parking = t, vm.CommitParkingCommand),
            s => s.Parking,
            "B12",
            "B12",
            "Edit parking"
        ),
        new(
            "runway",
            vm => vm.ShowsRunway,
            (vm, t) => Commit(() => vm.Runway = t, vm.CommitRunwayCommand),
            s => s.Runway,
            "01L",
            "01L",
            "Edit runway"
        ),
        new(
            "coordinates",
            vm => vm.ShowsCoordinates,
            (vm, t) => Commit(() => vm.Latitude = t, vm.CommitLatitudeCommand),
            s => s.Coordinates?.Lat,
            "37.5",
            37.5,
            "Edit latitude"
        ),
        new(
            "coordinates",
            vm => vm.ShowsCoordinates,
            (vm, t) => Commit(() => vm.Longitude = t, vm.CommitLongitudeCommand),
            s => s.Coordinates?.Lon,
            "-122.25",
            -122.25,
            "Edit longitude"
        ),
        new(
            "altitude",
            vm => vm.ShowsAltitude,
            (vm, t) => Commit(() => vm.Altitude = t, vm.CommitAltitudeCommand),
            s => s.Altitude,
            "4500",
            4500.0,
            "Edit altitude"
        ),
        new("speed", vm => vm.ShowsSpeed, (vm, t) => Commit(() => vm.Speed = t, vm.CommitSpeedCommand), s => s.Speed, "210", 210.0, "Edit speed"),
        new(
            "navigationPath",
            vm => vm.ShowsNavigationPath,
            (vm, t) => Commit(() => vm.NavigationPath = t, vm.CommitNavigationPathCommand),
            s => s.NavigationPath,
            "SFO DCT OAK",
            "SFO DCT OAK",
            "Edit navigation path"
        ),
        new(
            "heading",
            vm => vm.ShowsHeading,
            (vm, t) => Commit(() => vm.Heading = t, vm.CommitHeadingCommand),
            s => s.Heading,
            "270",
            270.0,
            "Edit heading"
        ),
        new("fix", vm => vm.ShowsFix, (vm, t) => Commit(() => vm.Fix = t, vm.CommitFixCommand), s => s.Fix, "SFO090010", "SFO090010", "Edit fix"),
        new(
            "distanceFromRunway",
            vm => vm.ShowsDistanceFromRunway,
            (vm, t) => Commit(() => vm.DistanceFromRunway = t, vm.CommitDistanceFromRunwayCommand),
            s => s.DistanceFromRunway,
            "6.5",
            6.5,
            "Edit distance from runway"
        ),
    ];

    private readonly ScenarioEditorFixture _fx = new();

    public ScenarioAircraftDetailTests()
    {
        TestVnasData.EnsureAircraftDataInitialized();
    }

    public void Dispose() => _fx.Dispose();

    [Theory]
    [InlineData(StartingConditionKinds.Parking, """{"type": "Parking", "parking": "A1"}""")]
    [InlineData(StartingConditionKinds.OnRunway, """{"type": "OnRunway", "runway": "28R"}""")]
    [InlineData(StartingConditionKinds.Coordinates, CoordinatesStart)]
    [InlineData(
        StartingConditionKinds.FixOrFrd,
        """{"type": "FixOrFrd", "fix": "OAK", "altitude": 5000, "speed": 250, "navigationPath": "OAK DCT SFO", "heading": 180}"""
    )]
    [InlineData(StartingConditionKinds.OnFinal, """{"type": "OnFinal", "runway": "28L", "altitude": 2000, "speed": 160, "distanceFromRunway": 8}""")]
    public void Kind_ShowsOnlyItsFields_AndEachEditIsOneUndoStepThatUndoTakesBack(string kind, string start)
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart(start);

        Assert.Equal(kind, vm.StartKind);
        Assert.Equal(kind, vm.StartKindSelection?.Value);
        IReadOnlyList<string> fields = StartingConditionKinds.FieldsOf(kind);
        foreach (StartField field in StartFields)
        {
            Assert.True(fields.Contains(field.Member) == field.Shown(vm), $"{kind}: {field.Step} shown should be {fields.Contains(field.Member)}");
        }

        foreach (StartField field in StartFields.Where(f => fields.Contains(f.Member)))
        {
            object? before = field.Read(StartOf(session));
            field.Commit(vm, field.Text);

            Assert.Equal(field.Expected, field.Read(StartOf(session)));
            Assert.Equal(field.Step, session.UndoDescription);
            Assert.True(session.Undo());
            Assert.Equal(before, field.Read(StartOf(session)));
            Assert.False(session.CanUndo, $"{kind}: {field.Step} recorded more than one step");
            Assert.Equal(vm.Rows[0], vm.Selected);
        }
    }

    [Fact]
    public void OwnFields_EachEditWritesItsMember_AsOneUndoStep_AndUndoRestoresEveryField()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart("""{"type": "Parking", "parking": "A1"}""");
        string[] original = OwnFieldTexts(vm);
        (Action Edit, Func<ScenarioAircraft, object?> Read, object? Expected, string Step)[] edits =
        [
            (() => Commit(() => vm.Callsign = " AAL12 ", vm.CommitCallsignCommand), a => a.AircraftId, "AAL12", "Edit callsign"),
            (() => Commit(() => vm.AircraftType = "B738", vm.CommitAircraftTypeCommand), a => a.AircraftType, "B738", "Edit type"),
            (() => Commit(() => vm.Transponder = "C", vm.CommitTransponderCommand), a => a.TransponderMode, "C", "Edit transponder"),
            (() => Commit(() => vm.SpawnDelay = "2:30", vm.CommitSpawnDelayCommand), a => a.SpawnDelay, 150, "Edit spawn delay"),
            (() => Commit(() => vm.TaxiTime = "120", vm.CommitTaxiTimeCommand), a => TaxiSeconds(a), 120, "Edit taxi time"),
            (() => Commit(() => vm.RpoNote = "Hold short", vm.CommitRpoNoteCommand), a => RpoNoteOf(a), "Hold short", "Edit RPO note"),
            (() => Commit(() => vm.Airport = "OAK", vm.CommitAirportCommand), a => a.AirportId, "OAK", "Edit airport"),
            (() => Commit(() => vm.Approach = "I28R", vm.CommitApproachCommand), a => a.ExpectedApproach, "I28R", "Edit approach"),
            (() => Commit(() => vm.Difficulty = "Hard", vm.CommitDifficultyCommand), a => a.Difficulty, "Hard", "Edit difficulty"),
            (() => Commit(() => vm.Airport = "", vm.CommitAirportCommand), a => a.AirportId, null, "Edit airport"),
        ];

        foreach ((Action edit, Func<ScenarioAircraft, object?> read, object? expected, string step) in edits)
        {
            edit();
            Assert.Equal(expected, read(First(session)));
            Assert.Equal(step, session.UndoDescription);
        }

        Assert.Equal("AAL12", vm.Rows[0].Callsign);
        Assert.Equal("AAL12", vm.Callsign);
        for (int i = 0; i < edits.Length; i++)
        {
            Assert.True(session.Undo());
        }

        Assert.False(session.CanUndo);
        Assert.Equal("EJA427", vm.Callsign);
        Assert.Equal("SFO", First(session).AirportId);
        Assert.Equal(original, OwnFieldTexts(vm));
    }

    [Fact]
    public void RedoOfAPanelEdit_ShowsTheRedoneValue()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart("""{"type": "Parking", "parking": "A1"}""");
        Commit(() => vm.Callsign = "AAL12", vm.CommitCallsignCommand);
        Assert.True(session.Undo());
        Assert.Equal("EJA427", vm.Callsign);

        Assert.True(session.Redo());

        Assert.Equal("AAL12", vm.Callsign);
        Assert.Equal("AAL12", vm.Rows[0].Callsign);
        Assert.Same(vm.Rows[0], vm.Selected);
    }

    [Fact]
    public void ANoOpCommit_KeepsAnotherFieldsErrorAndText()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart(CoordinatesStart);
        Commit(() => vm.Latitude = "91", vm.CommitLatitudeCommand);
        Assert.Equal(ScenarioFieldText.LatitudeErrorText, vm.LatitudeError);

        vm.CommitLongitudeCommand.Execute(null);

        Assert.Equal("91", vm.Latitude);
        Assert.Equal(ScenarioFieldText.LatitudeErrorText, vm.LatitudeError);
        Assert.Null(vm.LongitudeError);
        Assert.Equal("-122.4", vm.Longitude);
        Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData("taxiTime")]
    [InlineData("speed")]
    [InlineData("distanceFromRunway")]
    [InlineData("heading")]
    public void Zero_IsAccepted_AndWritten(string field)
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart(CoordinatesStart);

        (Func<string?> error, Func<double?> written) = field switch
        {
            "taxiTime" => Zero(() => vm.TaxiTime = "0", vm.CommitTaxiTimeCommand, () => vm.TaxiTimeError, () => TaxiSeconds(First(session))),
            "speed" => Zero(() => vm.Speed = "0", vm.CommitSpeedCommand, () => vm.SpeedError, () => StartOf(session).Speed),
            "distanceFromRunway" => Zero(
                () => vm.DistanceFromRunway = "0",
                vm.CommitDistanceFromRunwayCommand,
                () => vm.DistanceFromRunwayError,
                () => StartOf(session).DistanceFromRunway
            ),
            _ => Zero(() => vm.Heading = "0", vm.CommitHeadingCommand, () => vm.HeadingError, () => StartOf(session).Heading),
        };

        Assert.Null(error());
        Assert.Equal(0.0, written());
        Assert.True(session.CanUndo);
    }

    [Fact]
    public void KindSwitch_ClearsWhatTheNewKindDoesNotUse_InOneStep_AndUndoRestoresIt()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart(CoordinatesStart);

        vm.StartKindSelection = vm.StartKindOptions.Single(o => o.Value == StartingConditionKinds.OnFinal);

        StartingConditions start = StartOf(session);
        Assert.Equal(StartingConditionKinds.OnFinal, start.Type);
        Assert.Null(start.Coordinates);
        Assert.Null(start.NavigationPath);
        Assert.Null(start.Heading);
        Assert.Equal(3000, start.Altitude);
        Assert.Equal(180, start.Speed);
        Assert.Equal("Change start", session.UndoDescription);
        Assert.True(vm.ShowsDistanceFromRunway);
        Assert.False(vm.ShowsCoordinates);
        Assert.Equal("On final", vm.StartKindSelection?.Label);

        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        start = StartOf(session);
        Assert.Equal(StartingConditionKinds.Coordinates, start.Type);
        Assert.Equal(37.6, start.Coordinates?.Lat);
        Assert.Equal("SFO", start.NavigationPath);
        Assert.Equal(90, start.Heading);
        Assert.Equal(StartingConditionKinds.Coordinates, vm.StartKind);
        Assert.Equal("37.6", vm.Latitude);
        Assert.Equal("90", vm.Heading);
    }

    [Theory]
    [InlineData("altitude", "high", ScenarioFieldText.NumberErrorText)]
    [InlineData("speed", "fast", ScenarioFieldText.NonNegativeNumberErrorText)]
    [InlineData("speed", "-1", ScenarioFieldText.NonNegativeNumberErrorText)]
    [InlineData("distanceFromRunway", "far", ScenarioFieldText.NonNegativeNumberErrorText)]
    [InlineData("distanceFromRunway", "-1", ScenarioFieldText.NonNegativeNumberErrorText)]
    [InlineData("heading", "-1", ScenarioFieldText.HeadingErrorText)]
    [InlineData("heading", "361", ScenarioFieldText.HeadingErrorText)]
    [InlineData("latitude", "91", ScenarioFieldText.LatitudeErrorText)]
    [InlineData("longitude", "-181", ScenarioFieldText.LongitudeErrorText)]
    [InlineData("latitude", "", ScenarioFieldText.LatitudeErrorText)]
    [InlineData("spawnDelay", "soon", ScenarioFieldText.TimeErrorText)]
    [InlineData("taxiTime", "5m", ScenarioFieldText.NonNegativeWholeNumberErrorText)]
    [InlineData("taxiTime", "-30", ScenarioFieldText.NonNegativeWholeNumberErrorText)]
    public void BadText_KeepsTheText_ShowsTheError_AndWritesNothing(string field, string text, string error)
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart(CoordinatesStart);
        string before = session.Document.Write();

        (Func<string> shown, Func<string?> shownError) = field switch
        {
            "altitude" => Bad(() => vm.Altitude = text, vm.CommitAltitudeCommand, () => vm.Altitude, () => vm.AltitudeError),
            "speed" => Bad(() => vm.Speed = text, vm.CommitSpeedCommand, () => vm.Speed, () => vm.SpeedError),
            "heading" => Bad(() => vm.Heading = text, vm.CommitHeadingCommand, () => vm.Heading, () => vm.HeadingError),
            "latitude" => Bad(() => vm.Latitude = text, vm.CommitLatitudeCommand, () => vm.Latitude, () => vm.LatitudeError),
            "longitude" => Bad(() => vm.Longitude = text, vm.CommitLongitudeCommand, () => vm.Longitude, () => vm.LongitudeError),
            "spawnDelay" => Bad(() => vm.SpawnDelay = text, vm.CommitSpawnDelayCommand, () => vm.SpawnDelay, () => vm.SpawnDelayError),
            "distanceFromRunway" => Bad(
                () => vm.DistanceFromRunway = text,
                vm.CommitDistanceFromRunwayCommand,
                () => vm.DistanceFromRunway,
                () => vm.DistanceFromRunwayError
            ),
            _ => Bad(() => vm.TaxiTime = text, vm.CommitTaxiTimeCommand, () => vm.TaxiTime, () => vm.TaxiTimeError),
        };

        Assert.Equal(text, shown());
        Assert.Equal(error, shownError());
        Assert.False(session.CanUndo);
        Assert.Equal(before, session.Document.Write());
    }

    [Fact]
    public void UnchangedFields_TabbedThrough_KeepTheRowsTheSelectionAndThePanel_AndReportNoEdit()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart(CoordinatesStart);
        int edited = 0;
        vm.Edited += (_, _) => edited++;
        ScenarioAircraftRowViewModel selected = vm.Selected!;
        ScenarioAircraftRowViewModel firstRow = vm.Rows[0];

        foreach (
            System.Windows.Input.ICommand command in new[]
            {
                vm.CommitCallsignCommand,
                vm.CommitLatitudeCommand,
                vm.CommitAltitudeCommand,
                vm.CommitHeadingCommand,
            }
        )
        {
            command.Execute(null);
        }

        Assert.Same(selected, vm.Selected);
        Assert.Same(firstRow, vm.Rows[0]);
        Assert.True(vm.IsDetailVisible);
        Assert.Equal("EJA427", vm.Callsign);
        Assert.Equal(0, edited);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void FractionalAltitudeAndSpeed_InTheFile_TabbedThrough_ShowNoErrorAndWriteNothing()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart(
            """{"type": "FixOrFrd", "fix": "OAK", "altitude": 2500.5, "speed": 180.5}"""
        );
        Assert.Equal("2500.5", vm.Altitude);

        vm.CommitAltitudeCommand.Execute(null);
        vm.CommitSpeedCommand.Execute(null);

        Assert.Null(vm.AltitudeError);
        Assert.Null(vm.SpeedError);
        Assert.False(session.CanUndo);

        Commit(() => vm.Altitude = "2600.25", vm.CommitAltitudeCommand);
        Assert.Equal(2600.25, StartOf(session).Altitude);
    }

    [Fact]
    public void Type_SuggestsCatalogMatches_AndAnUnknownTypeIsWrittenWithTheHint()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart("""{"type": "Parking", "parking": "A1"}""");
        Assert.Null(vm.TypeHint);

        vm.AircraftType = "B73";

        Assert.Contains("B738", vm.TypeSuggestions);
        Assert.All(vm.TypeSuggestions, d => Assert.StartsWith("B73", d, StringComparison.OrdinalIgnoreCase));
        Assert.True(vm.TypeSuggestions.Count <= ScenarioAircraftViewModel.TypeSuggestionLimit);
        Assert.Equal(AircraftTypeCatalog.Search("B73", 20).Select(e => e.Designator), vm.TypeSuggestions);

        Commit(() => vm.AircraftType = "ZZZ9", vm.CommitAircraftTypeCommand);
        Assert.Equal("ZZZ9", First(session).AircraftType);
        Assert.Equal("Edit type", session.UndoDescription);
        Assert.Equal(ScenarioAircraftViewModel.NotInAircraftSpecsText, vm.TypeHint);

        Commit(() => vm.AircraftType = "B738", vm.CommitAircraftTypeCommand);
        Assert.Equal("B738", First(session).AircraftType);
        Assert.Null(vm.TypeHint);

        Assert.True(session.Undo());
        Assert.Equal("ZZZ9", vm.AircraftType);
        Assert.Equal(ScenarioAircraftViewModel.NotInAircraftSpecsText, vm.TypeHint);
        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        Assert.Equal("E55P", First(session).AircraftType);
        Assert.Equal("E55P", vm.AircraftType);
        Assert.Null(vm.TypeHint);
    }

    [Fact]
    public void TaxiTime_WritesItsYaatOnlyKey_AndBlankRemovesIt()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart("""{"type": "Parking", "parking": "A1"}""");

        Commit(() => vm.TaxiTime = "300", vm.CommitTaxiTimeCommand);

        Assert.Equal(300, First(session).ExtensionData![WorkingCopyExporter.AircraftTaxiSecondsKey].GetInt32());
        Assert.Equal(300, (int)JsonNode.Parse(session.Document.Write())!["aircraft"]![0]!["yaatTaxiSeconds"]!);
        Assert.Equal("Edit taxi time", session.UndoDescription);
        Assert.Equal("300", vm.TaxiTime);

        Commit(() => vm.TaxiTime = "", vm.CommitTaxiTimeCommand);

        Assert.Null(First(session).ExtensionData);
        Assert.False(JsonNode.Parse(session.Document.Write())!["aircraft"]![0]!.AsObject().ContainsKey("yaatTaxiSeconds"));
        Assert.True(session.Undo());
        Assert.Equal("300", vm.TaxiTime);
    }

    [Fact]
    public void RpoNote_WritesItsYaatOnlyKey_AndBlankRemovesIt()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart("""{"type": "Parking", "parking": "A1"}""");
        const string Note = "Call ready to taxi\nthen ask for flight following";

        Commit(() => vm.RpoNote = Note, vm.CommitRpoNoteCommand);

        JsonElement written = First(session).ExtensionData![WorkingCopyExporter.AircraftRpoNoteKey];
        Assert.Equal(JsonValueKind.String, written.ValueKind);
        Assert.Equal(Note, written.GetString());
        Assert.Equal("Edit RPO note", session.UndoDescription);

        Commit(() => vm.RpoNote = "  ", vm.CommitRpoNoteCommand);

        Assert.Null(First(session).ExtensionData);
        Assert.Equal("", vm.RpoNote);
        Assert.True(session.Undo());
        Assert.Equal(Note, vm.RpoNote);
    }

    [Fact]
    public void AltitudeProfile_TogglesAsOneUndoStep()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenWithStart("""{"type": "Parking", "parking": "A1"}""");
        bool before = First(session).OnAltitudeProfile;
        Assert.Equal(before, vm.OnAltitudeProfile);

        vm.OnAltitudeProfile = !before;

        Assert.Equal(!before, First(session).OnAltitudeProfile);
        Assert.Equal("Edit altitude profile", session.UndoDescription);
        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        Assert.Equal(before, First(session).OnAltitudeProfile);
        Assert.Equal(before, vm.OnAltitudeProfile);
    }

    [Fact]
    public void Panel_ShowsOnlyWithASelection()
    {
        (_, ScenarioAircraftViewModel vm) = Open("""{"type": "Parking", "parking": "A1"}""");
        Assert.False(vm.IsDetailVisible);
        Assert.Equal("", vm.Callsign);

        vm.Selected = vm.Rows[0];

        Assert.True(vm.IsDetailVisible);
        Assert.Equal("EJA427", vm.Callsign);
    }

    // Sets a field's text and runs its commit, as focus loss or Enter does.
    private static void Commit(Action set, System.Windows.Input.ICommand command)
    {
        set();
        Assert.True(command.CanExecute(null));
        command.Execute(null);
    }

    // Commits a field's text and hands back how to read the field's text and error after it.
    private static (Func<string> Shown, Func<string?> Error) Bad(
        Action set,
        System.Windows.Input.ICommand command,
        Func<string> shown,
        Func<string?> error
    )
    {
        Commit(set, command);
        return (shown, error);
    }

    // Commits a field's 0 and hands back how to read its error and the value written.
    private static (Func<string?> Error, Func<double?> Written) Zero(
        Action set,
        System.Windows.Input.ICommand command,
        Func<string?> error,
        Func<double?> written
    )
    {
        Commit(set, command);
        return (error, written);
    }

    // Every own field's text the panel shows, in panel order.
    private static string[] OwnFieldTexts(ScenarioAircraftViewModel vm) =>
        [vm.Callsign, vm.AircraftType, vm.Transponder, vm.SpawnDelay, vm.TaxiTime, vm.RpoNote, vm.Airport, vm.Approach, vm.Difficulty];

    private static int? TaxiSeconds(ScenarioAircraft aircraft) =>
        ((aircraft.ExtensionData is { } extension) && extension.TryGetValue(WorkingCopyExporter.AircraftTaxiSecondsKey, out JsonElement v))
            ? v.GetInt32()
            : null;

    private static string? RpoNoteOf(ScenarioAircraft aircraft) =>
        ((aircraft.ExtensionData is { } extension) && extension.TryGetValue(WorkingCopyExporter.AircraftRpoNoteKey, out JsonElement v))
            ? v.GetString()
            : null;

    private static ScenarioAircraft First(ScenarioEditSession session) => session.Document.Scenario.Aircraft[0];

    private static StartingConditions StartOf(ScenarioEditSession session) => First(session).StartingConditions;

    // As Open, with the first row selected.
    private (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) OpenWithStart(string start)
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open(start);
        vm.Selected = vm.Rows[0];
        return (session, vm);
    }

    // The corpus scenario with its first aircraft's starting conditions replaced, opened with nothing selected.
    private (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) Open(string start)
    {
        string corpus = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sfo-gc-scenario.json"));
        JsonNode root = JsonNode.Parse(corpus) ?? throw new InvalidOperationException("sfo-gc-scenario.json is not a JSON object");
        root["aircraft"]![0]!["startingConditions"] = JsonNode.Parse(start);
        _fx.Store.CreateLocalScenario(ScenarioEditorFixture.Zoa, root.ToJsonString(), CopyId);
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, CopyId);
        var vm = new ScenarioAircraftViewModel(session, _fx.Services());
        session.DocumentReplaced += (_, _) => vm.Reload();
        return (session, vm);
    }

    private sealed record StartField(
        string Member,
        Func<ScenarioAircraftViewModel, bool> Shown,
        Action<ScenarioAircraftViewModel, string> Commit,
        Func<StartingConditions, object?> Read,
        string Text,
        object? Expected,
        string Step
    );
}
