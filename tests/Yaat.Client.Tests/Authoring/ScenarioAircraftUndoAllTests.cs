using System.Text.Json.Nodes;
using System.Windows.Input;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// Every flight-plan, preset and auto-track command of the Aircraft tab's detail panel, run once each on the corpus SFO
/// ground scenario's first aircraft: each is one undo step, undoing them all gives back the document as opened, and
/// redoing them all gives back the document as edited.
/// </summary>
public sealed class ScenarioAircraftUndoAllTests : IDisposable
{
    private readonly ScenarioEditorFixture _fx = new();

    public ScenarioAircraftUndoAllTests()
    {
        TestVnasData.EnsureAircraftDataInitialized();
    }

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void UndoAll_EveryEdit_UndoesToOriginalAndRedoesToFinal()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });
        vm.PositionCatalog = [new ScenarioPickOption("POS1", "Position 1")];
        int edited = 0;
        vm.Edited += (_, _) => edited++;
        string original = session.Document.Write();
        (Action Edit, string Step)[] edits =
        [
            (() => vm.HasFlightPlan = false, "Remove flight plan"),
            (() => vm.HasFlightPlan = true, "Add flight plan"),
            (() => vm.FlightRulesPick = "VFR", "Edit flight rules"),
            (() => Commit(() => vm.Departure = "KOAK", vm.CommitDepartureCommand), "Edit departure"),
            (() => Commit(() => vm.Destination = "KLAX", vm.CommitDestinationCommand), "Edit destination"),
            (() => Commit(() => vm.CruiseAltitude = "9500", vm.CommitCruiseAltitudeCommand), "Edit cruise altitude"),
            (() => Commit(() => vm.CruiseSpeed = "120", vm.CommitCruiseSpeedCommand), "Edit cruise speed"),
            (() => Commit(() => vm.FlightPlanRoute = "OAK V25 LAX", vm.CommitFlightPlanRouteCommand), "Edit route"),
            (() => Commit(() => vm.Remarks = "/V/ STUDENT", vm.CommitRemarksCommand), "Edit remarks"),
            (() => Commit(() => vm.FiledType = "C172/G", vm.CommitFiledTypeCommand), "Edit filed type"),
            (() => vm.AddPresetCommand.Execute(null), "Add preset"),
            (() => Commit(() => vm.Presets[^1].Command = "SQ 1200", vm.Presets[^1].CommitCommandCommand), "Edit preset command"),
            (() => Commit(() => vm.Presets[^1].Offset = "1:00", vm.Presets[^1].CommitOffsetCommand), "Edit preset offset"),
            (() => vm.Presets[0].DuplicateCommand.Execute(null), "Duplicate preset"),
            (() => vm.Presets[^1].MoveUpCommand.Execute(null), "Move preset"),
            (() => vm.Presets[0].MoveDownCommand.Execute(null), "Move preset"),
            (() => vm.Presets[1].RemoveCommand.Execute(null), "Remove preset"),
            (() => vm.HasAutoTrack = true, "Add auto-track"),
            (() => vm.AutoTrackPositionSelection = vm.AutoTrackPositionOptions.Single(o => o.Value == "POS1"), "Edit auto-track position"),
            (() => Commit(() => vm.HandoffDelay = "30", vm.CommitHandoffDelayCommand), "Edit handoff delay"),
            (() => Commit(() => vm.ScratchPad = "ABC", vm.CommitScratchPadCommand), "Edit scratchpad"),
            (() => Commit(() => vm.InterimAltitude = "050", vm.CommitInterimAltitudeCommand), "Edit interim altitude"),
            (() => Commit(() => vm.ClearedAltitude = "120", vm.CommitClearedAltitudeCommand), "Edit cleared altitude"),
            (() => vm.HasAutoTrack = false, "Remove auto-track"),
        ];

        for (int i = 0; i < edits.Length; i++)
        {
            string before = session.Document.Write();
            edits[i].Edit();
            Assert.Equal(edits[i].Step, session.UndoDescription);
            Assert.NotEqual(before, session.Document.Write());
            Assert.Equal(i + 1, edited);
        }

        string final = session.Document.Write();
        for (int i = 0; i < edits.Length; i++)
        {
            Assert.True(session.Undo(), $"undo {i + 1} of {edits.Length}");
        }

        Assert.False(session.CanUndo);
        Assert.Equal(original, session.Document.Write());
        for (int i = 0; i < edits.Length; i++)
        {
            Assert.True(session.Redo(), $"redo {i + 1} of {edits.Length}");
        }

        Assert.False(session.CanRedo);
        Assert.Equal(final, session.Document.Write());
    }

    private static void Commit(Action set, ICommand command) => ScenarioAircraftPanel.Commit(set, command);
}

/// <summary>The detail panel tests' document: the corpus SFO ground scenario, its first aircraft edited and selected.</summary>
internal static class ScenarioAircraftPanel
{
    private const string CopyId = "P1";

    /// <summary>Opens the corpus scenario with its first aircraft's JSON edited, that aircraft selected and undo followed.</summary>
    public static (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) Open(ScenarioEditorFixture fx, Action<JsonObject> editFirst)
    {
        string corpus = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sfo-gc-scenario.json"));
        JsonNode root = JsonNode.Parse(corpus) ?? throw new InvalidOperationException("sfo-gc-scenario.json is not a JSON object");
        editFirst(root["aircraft"]![0]!.AsObject());
        fx.Store.CreateLocalScenario(ScenarioEditorFixture.Zoa, root.ToJsonString(), CopyId);
        var session = new ScenarioEditSession(fx.Store, ScenarioEditorFixture.Zoa, CopyId);
        var vm = new ScenarioAircraftViewModel(session, fx.Services());
        session.DocumentReplaced += (_, _) => vm.Reload();
        vm.Selected = vm.Rows[0];
        return (session, vm);
    }

    /// <summary>Sets a field's text and runs its commit, as focus loss or Enter does.</summary>
    public static void Commit(Action set, ICommand command)
    {
        set();
        Assert.True(command.CanExecute(null));
        command.Execute(null);
    }

    public static ScenarioAircraft First(ScenarioEditSession session) => session.Document.Scenario.Aircraft[0];

    // Counts the undo steps by undoing them all and redoing them back.
    public static int UndoSteps(ScenarioEditSession session)
    {
        int steps = 0;
        while (session.Undo())
        {
            steps++;
        }

        for (int i = 0; i < steps; i++)
        {
            Assert.True(session.Redo());
        }

        return steps;
    }
}
