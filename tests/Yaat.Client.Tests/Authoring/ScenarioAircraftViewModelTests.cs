using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Scenario Editor's Aircraft tab over the committed SFO ground scenario (Parking and OnFinal starts) as a working
/// copy: one read-only row per aircraft in document order, and Add, Duplicate and Remove, each one undo step, with the
/// selection held by aircraft id across every rebuild, undo and redo.
/// </summary>
public sealed class ScenarioAircraftViewModelTests : IDisposable
{
    private const string CopyId = "C1";

    private readonly ScenarioEditorFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Rows_AreTheDocumentsAircraft_InOrder_NothingSelected()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenCorpus();

        List<ScenarioAircraft> aircraft = session.Document.Scenario.Aircraft;
        Assert.Equal(106, vm.Rows.Count);
        Assert.Equal(aircraft.Select(a => a.Id), vm.Rows.Select(r => r.Id));
        Assert.Equal(aircraft.Select(a => a.AircraftId), vm.Rows.Select(r => r.Callsign));
        Assert.Equal(Enumerable.Range(0, aircraft.Count), vm.Rows.Select(r => r.Index));
        Assert.Null(vm.Selected);
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void ParkingRow_ShowsEveryColumn()
    {
        (_, ScenarioAircraftViewModel vm) = OpenCorpus();

        ScenarioAircraftRowViewModel row = vm.Rows[0];
        Assert.Equal("EJA427", row.Callsign);
        Assert.Equal("E55P", row.Type);
        Assert.Equal("Standby", row.Transponder);
        Assert.Equal("0:00", row.SpawnDelay);
        Assert.Equal("SFO", row.Airport);
        Assert.Equal("Parking SIG2", row.Start);
        Assert.Equal("TRUKN2 ORRCA Q120 GALLI BQU TWF PRESN", row.Route);
        Assert.Equal("2", row.Presets);
        Assert.Equal("", row.AutoTrack);
    }

    [Fact]
    public void OnFinalRow_ShowsRunwayAndDistance()
    {
        (_, ScenarioAircraftViewModel vm) = OpenCorpus();

        ScenarioAircraftRowViewModel row = vm.Rows[53];
        Assert.Equal("EJA512", row.Callsign);
        Assert.Equal("Final 28R, 10 nm", row.Start);
        Assert.Equal("DCT GPS", row.Route);
    }

    [Fact]
    public void SpawnDelay_ShowsAsMinutesAndSeconds()
    {
        (_, ScenarioAircraftViewModel vm) = OpenCorpus();

        Assert.Equal("1:19", vm.Rows[3].SpawnDelay);
        Assert.Equal(79, vm.Rows[3].SpawnDelaySeconds);
    }

    [Theory]
    [InlineData("""{"type": "FixOrFrd", "fix": "SFO090010", "altitude": 3000}""", "SFO090010")]
    [InlineData("""{"type": "OnFinal", "runway": "28L", "distanceFromRunway": 7.5}""", "Final 28L, 7.5 nm")]
    [InlineData("""{"type": "OnRunway", "runway": "01R"}""", "Runway 01R")]
    [InlineData("""{"type": "Coordinates", "coordinates": {"lat": 37.6, "lon": -122.4}}""", "Coordinates")]
    [InlineData("""{"type": "Parking", "parking": "A1"}""", "Parking A1")]
    [InlineData("""{"type": "Hover"}""", "Hover")]
    public void HandSetStart_ShowsItsKind(string startingConditions, string expected)
    {
        (_, ScenarioAircraftViewModel vm) = OpenCorpusWith(a => a["startingConditions"] = JsonNode.Parse(startingConditions));

        Assert.Equal(expected, vm.Rows[0].Start);
    }

    [Fact]
    public void HandSetAircraft_WithoutPlanAirportOrPresets_ShowsBlanks_AndItsAutoTrackPosition()
    {
        (_, ScenarioAircraftViewModel vm) = OpenCorpusWith(a =>
        {
            a.AsObject().Remove("flightplan");
            a["airportId"] = null;
            a["presetCommands"] = new JsonArray();
            a["autoTrackConditions"] = new JsonObject { ["positionId"] = "01GEAT1S60WNVZ3VQXP7TSQF84" };
        });

        ScenarioAircraftRowViewModel row = vm.Rows[0];
        Assert.Equal("", row.Route);
        Assert.Equal("", row.Airport);
        Assert.Equal("", row.Presets);
        Assert.Equal(0, row.PresetCount);
        Assert.Equal("01GEAT1S60WNVZ3VQXP7TSQF84", row.AutoTrack);
    }

    [Fact]
    public void Add_AppendsAParkingAircraft_SelectsIt_OneUndoStep_UndoRestoresTheSelection()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenCorpus();
        string before = session.Document.Write();
        string lastId = vm.Rows[^1].Id;
        vm.Selected = vm.Rows[^1];
        int edited = 0;
        vm.Edited += (_, _) => edited++;

        vm.AddAircraftCommand.Execute(null);

        List<ScenarioAircraft> aircraft = session.Document.Scenario.Aircraft;
        Assert.Equal(107, vm.Rows.Count);
        Assert.Equal(StartingConditionKinds.Parking, aircraft[^1].StartingConditions.Type);
        Assert.Equal(aircraft[^1].Id, vm.Selected?.Id);
        Assert.Same(vm.Rows[^1], vm.Selected);
        Assert.DoesNotContain(aircraft.Take(106), a => a.Id == aircraft[^1].Id);
        Assert.Equal("Parking", vm.Selected!.Start);
        Assert.Equal("Add aircraft", session.UndoDescription);
        Assert.Equal(1, edited);

        Assert.True(session.Undo());

        Assert.False(session.CanUndo);
        Assert.Equal(before, session.Document.Write());
        Assert.Equal(106, vm.Rows.Count);
        Assert.Equal(lastId, vm.Selected?.Id);
    }

    [Fact]
    public void Duplicate_InsertsACopyAfterTheSource_NewIdAndCallsign_SelectsIt_OneUndoStep()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenCorpus();
        string before = session.Document.Write();
        ScenarioAircraftRowViewModel source = vm.Rows[0];
        vm.Selected = source;

        vm.DuplicateAircraftCommand.Execute(null);

        List<ScenarioAircraft> aircraft = session.Document.Scenario.Aircraft;
        Assert.Equal(107, vm.Rows.Count);
        Assert.Equal(source.Id, vm.Rows[0].Id);
        ScenarioAircraftRowViewModel copy = vm.Rows[1];
        Assert.NotEqual(source.Id, copy.Id);
        Assert.NotEqual(source.Callsign, copy.Callsign);
        Assert.Single(aircraft, a => string.Equals(a.AircraftId, copy.Callsign, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(source.Type, copy.Type);
        Assert.Equal(source.Start, copy.Start);
        Assert.Same(copy, vm.Selected);
        Assert.Equal("Duplicate aircraft", session.UndoDescription);

        Assert.True(session.Undo());

        Assert.False(session.CanUndo);
        Assert.Equal(before, session.Document.Write());
        Assert.Equal(source.Id, vm.Selected?.Id);
    }

    [Fact]
    public void Remove_RemovesTheSelected_SelectsTheRowNowAtItsIndex_UndoRestoresAndReselectsIt()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenCorpus();
        string before = session.Document.Write();
        string removedId = vm.Rows[1].Id;
        string nextId = vm.Rows[2].Id;
        vm.Selected = vm.Rows[1];

        vm.RemoveAircraftCommand.Execute(null);

        Assert.Equal(105, vm.Rows.Count);
        Assert.DoesNotContain(session.Document.Scenario.Aircraft, a => a.Id == removedId);
        Assert.Equal(nextId, vm.Selected?.Id);
        Assert.Equal(1, vm.Selected?.Index);
        Assert.Equal("Remove aircraft", session.UndoDescription);

        Assert.True(session.Undo());

        Assert.False(session.CanUndo);
        Assert.Equal(before, session.Document.Write());
        Assert.Equal(removedId, vm.Selected?.Id);
        Assert.Equal(1, vm.Selected?.Index);
    }

    [Fact]
    public void RemoveTheLastRow_SelectsTheNewLastRow()
    {
        (_, ScenarioAircraftViewModel vm) = OpenCorpus();
        string newLastId = vm.Rows[^2].Id;
        vm.Selected = vm.Rows[^1];

        vm.RemoveAircraftCommand.Execute(null);

        Assert.Equal(newLastId, vm.Selected?.Id);
        Assert.Same(vm.Rows[^1], vm.Selected);
    }

    [Fact]
    public void RemoveTheOnlyAircraft_SelectsNone()
    {
        _fx.AddCopy("L1", "Lima");
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, "L1");
        var vm = new ScenarioAircraftViewModel(session, _fx.Services());
        session.DocumentReplaced += (_, _) => vm.Reload();
        vm.AddAircraftCommand.Execute(null);
        Assert.NotNull(vm.Selected);

        vm.RemoveAircraftCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.Null(vm.Selected);
        Assert.False(vm.RemoveAircraftCommand.CanExecute(null));
    }

    [Fact]
    public void DuplicateAndRemove_AreDisabled_WithNothingSelected()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenCorpus();

        Assert.True(vm.AddAircraftCommand.CanExecute(null));
        Assert.False(vm.DuplicateAircraftCommand.CanExecute(null));
        Assert.False(vm.RemoveAircraftCommand.CanExecute(null));

        vm.Selected = vm.Rows[4];
        Assert.True(vm.DuplicateAircraftCommand.CanExecute(null));
        Assert.True(vm.RemoveAircraftCommand.CanExecute(null));

        vm.Selected = null;
        Assert.False(vm.DuplicateAircraftCommand.CanExecute(null));
        Assert.False(vm.RemoveAircraftCommand.CanExecute(null));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void UndoAndRedo_OfAnotherEdit_KeepTheSelectionOnItsAircraft()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenCorpus();
        ScenarioAircraftRowViewModel picked = vm.Rows[5];
        vm.Selected = picked;

        session.Edit("Edit name", s => s.Name = "Renamed");
        Assert.True(session.Undo());

        Assert.Equal(picked.Id, vm.Selected?.Id);
        Assert.NotSame(picked, vm.Selected);
        Assert.True(session.Redo());
        Assert.Equal(picked.Id, vm.Selected?.Id);
    }

    [Fact]
    public void RedoOfAnAdd_SelectsTheAddedAircraftAgain()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenCorpus();
        vm.Selected = vm.Rows[5];
        string keptId = vm.Rows[5].Id;
        vm.AddAircraftCommand.Execute(null);
        string addedId = vm.Selected!.Id;

        Assert.True(session.Undo());
        Assert.Equal(keptId, vm.Selected?.Id);

        Assert.True(session.Redo());
        Assert.Equal(addedId, vm.Selected?.Id);
        Assert.Equal(107, vm.Rows.Count);
    }

    [Fact]
    public void UndoOfARemove_SelectsTheRestoredAircraft_EvenAfterAnotherRowWasPicked()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenCorpus();
        vm.Selected = vm.Rows[1];
        string removedId = vm.Rows[1].Id;
        vm.RemoveAircraftCommand.Execute(null);
        vm.Selected = vm.Rows[10];

        Assert.True(session.Undo());

        Assert.Equal(removedId, vm.Selected?.Id);
        Assert.Equal(1, vm.Selected?.Index);
    }

    [Fact]
    public void AddThenRemoveIt_UndoTwice_SelectsTheAddedThenThePriorSelection()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenCorpus();
        string priorId = vm.Rows[5].Id;
        vm.Selected = vm.Rows[5];
        vm.AddAircraftCommand.Execute(null);
        string addedId = vm.Selected!.Id;
        vm.RemoveAircraftCommand.Execute(null);
        Assert.DoesNotContain(vm.Rows, r => r.Id == addedId);

        Assert.True(session.Undo());
        Assert.Equal(addedId, vm.Selected?.Id);

        Assert.True(session.Undo());
        Assert.Equal(priorId, vm.Selected?.Id);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void DuplicateThenRemoveTheCopy_UndoTwice_SelectsTheCopyThenTheSource()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = OpenCorpus();
        string sourceId = vm.Rows[3].Id;
        vm.Selected = vm.Rows[3];
        vm.DuplicateAircraftCommand.Execute(null);
        string copyId = vm.Selected!.Id;
        Assert.NotEqual(sourceId, copyId);
        vm.RemoveAircraftCommand.Execute(null);
        Assert.Same(vm.Rows[4], vm.Selected);

        Assert.True(session.Undo());
        Assert.Equal(copyId, vm.Selected?.Id);

        Assert.True(session.Undo());
        Assert.Equal(sourceId, vm.Selected?.Id);
        Assert.False(session.CanUndo);
    }

    private (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) OpenCorpus() => OpenOn(CorpusText());

    // The corpus scenario with its first aircraft changed by hand.
    private (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) OpenCorpusWith(Action<JsonNode> editFirst)
    {
        JsonNode root = JsonNode.Parse(CorpusText()) ?? throw new InvalidOperationException("sfo-gc-scenario.json is not a JSON object");
        editFirst(root["aircraft"]![0]!);
        return OpenOn(root.ToJsonString());
    }

    private (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) OpenOn(string text)
    {
        _fx.Store.CreateLocalScenario(ScenarioEditorFixture.Zoa, text, CopyId);
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, CopyId);
        var vm = new ScenarioAircraftViewModel(session, _fx.Services());
        session.DocumentReplaced += (_, _) => vm.Reload();
        return (session, vm);
    }

    private static string CorpusText() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sfo-gc-scenario.json"));
}
