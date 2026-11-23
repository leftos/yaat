using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Aircraft tab's preset commands over the corpus SFO ground scenario's first aircraft (two presets): Add, Move and
/// Duplicate, each one undo step; the offset typed as a ScenarioTime; the command text kept as its commit takes it.
/// </summary>
public sealed class ScenarioAircraftPresetTests : IDisposable
{
    private readonly ScenarioEditorFixture _fx = new();

    public ScenarioAircraftPresetTests()
    {
        TestVnasData.EnsureAircraftDataInitialized();
    }

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Presets_Add_AppendsRowWithNewId()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });
        string[] before = [.. Presets(session).Select(p => p.Id)];

        vm.AddPresetCommand.Execute(null);

        List<PresetCommand> presets = Presets(session);
        Assert.Equal(before, presets.Take(2).Select(p => p.Id));
        PresetCommand added = presets[2];
        Assert.Equal(26, added.Id.Length);
        Assert.DoesNotContain(added.Id, before);
        Assert.Equal(("", 0), (added.Command, added.TimeOffset));
        Assert.Equal("Add preset", session.UndoDescription);
        Assert.Equal(1, ScenarioAircraftPanel.UndoSteps(session));
        Assert.Equal(3, vm.Presets.Count);
        Assert.Equal(added.Id, vm.Presets[2].Id);
        Assert.Equal("0:00", vm.Presets[2].Offset);
    }

    [Fact]
    public void Presets_MoveUpOnFirst_Disabled()
    {
        (_, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });

        Assert.False(vm.Presets[0].MoveUpCommand.CanExecute(null));
        Assert.True(vm.Presets[1].MoveUpCommand.CanExecute(null));
    }

    [Fact]
    public void Presets_MoveDownOnLast_Disabled()
    {
        (_, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });

        Assert.False(vm.Presets[^1].MoveDownCommand.CanExecute(null));
        Assert.True(vm.Presets[0].MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void Presets_Duplicate_InsertsAfterWithNewId()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });
        ScenarioAircraftPanel.Commit(() => vm.Presets[0].Offset = "45", vm.Presets[0].CommitOffsetCommand);
        string[] before = [.. Presets(session).Select(p => p.Id)];

        vm.Presets[0].DuplicateCommand.Execute(null);

        List<PresetCommand> presets = Presets(session);
        Assert.Equal(3, presets.Count);
        Assert.Equal(before[0], presets[0].Id);
        Assert.Equal(before[1], presets[2].Id);
        Assert.DoesNotContain(presets[1].Id, before);
        Assert.Equal((presets[0].Command, presets[0].TimeOffset), (presets[1].Command, presets[1].TimeOffset));
        Assert.Equal(45, presets[1].TimeOffset);
        Assert.Equal("Duplicate preset", session.UndoDescription);
        Assert.Equal(2, ScenarioAircraftPanel.UndoSteps(session));
    }

    [Fact]
    public void Presets_OffsetUsesScenarioTime()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });

        ScenarioAircraftPanel.Commit(() => vm.Presets[1].Offset = "130", vm.Presets[1].CommitOffsetCommand);

        Assert.Equal(90, Presets(session)[1].TimeOffset);
        Assert.Equal("1:30", vm.Presets[1].Offset);
        Assert.Equal("Edit preset offset", session.UndoDescription);

        string before = session.Document.Write();
        ScenarioAircraftPanel.Commit(() => vm.Presets[1].Offset = "1:a", vm.Presets[1].CommitOffsetCommand);

        Assert.Equal("Enter a time like 1:30, 1:02:30 or 130.", vm.Presets[1].OffsetError);
        Assert.Equal(before, session.Document.Write());
    }

    [Fact]
    public void Presets_CommandTextKeptRaw()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });

        ScenarioAircraftPanel.Commit(() => vm.Presets[0].Command = "  FH 270  ,  CM 50", vm.Presets[0].CommitCommandCommand);

        Assert.Equal("FH 270  ,  CM 50", Presets(session)[0].Command);
        Assert.Equal("Edit preset command", session.UndoDescription);
        Assert.Equal("FH 270  ,  CM 50", vm.Presets[0].Command);
    }

    [Fact]
    public void Presets_Duplicate_KeepsExtensionData()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(
            _fx,
            a => a["presetCommands"]![0]!["note"] = "keep me"
        );

        vm.Presets[0].DuplicateCommand.Execute(null);

        PresetCommand copy = Presets(session)[1];
        Assert.NotEqual(Presets(session)[0].Id, copy.Id);
        Assert.Equal("keep me", copy.ExtensionData!["note"].GetString());
    }

    [Fact]
    public void Presets_MoveUp_ReordersIds()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });
        string[] before = [.. Presets(session).Select(p => p.Id)];

        vm.Presets[1].MoveUpCommand.Execute(null);

        Assert.Equal([before[1], before[0]], Presets(session).Select(p => p.Id));
        Assert.Equal([before[1], before[0]], vm.Presets.Select(r => r.Id));
        Assert.Equal("Move preset", session.UndoDescription);
    }

    [Fact]
    public void Presets_MoveDown_ReordersIds()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });
        string[] before = [.. Presets(session).Select(p => p.Id)];

        vm.Presets[0].MoveDownCommand.Execute(null);

        Assert.Equal([before[1], before[0]], Presets(session).Select(p => p.Id));
        Assert.Equal([before[1], before[0]], vm.Presets.Select(r => r.Id));
        Assert.Equal("Move preset", session.UndoDescription);
    }

    [Fact]
    public void Presets_Remove_DropsThatId()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });
        string[] before = [.. Presets(session).Select(p => p.Id)];

        vm.Presets[0].RemoveCommand.Execute(null);

        Assert.Equal([before[1]], Presets(session).Select(p => p.Id));
        Assert.Equal([before[1]], vm.Presets.Select(r => r.Id));
        Assert.Equal("Remove preset", session.UndoDescription);
    }

    [Fact]
    public void Presets_NoOpCommandCommit_KeepsAnotherRowsOffsetError()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });
        ScenarioAircraftPanel.Commit(() => vm.Presets[1].Offset = "1:a", vm.Presets[1].CommitOffsetCommand);
        Assert.NotNull(vm.Presets[1].OffsetError);

        ScenarioAircraftPanel.Commit(() => { }, vm.Presets[0].CommitCommandCommand);

        Assert.NotNull(vm.Presets[1].OffsetError);
        Assert.Equal("1:a", vm.Presets[1].Offset);
        Assert.False(session.CanUndo);
    }

    private static List<PresetCommand> Presets(ScenarioEditSession session) => ScenarioAircraftPanel.First(session).PresetCommands;
}
