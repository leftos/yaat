using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Aircraft tab's auto-track block: the Auto-track checkbox makes conditions with no position or removes them, one
/// undo step each; a blank handoff delay writes none; the position picker offers the General tab's catalog once it loads.
/// </summary>
public sealed class ScenarioAircraftAutoTrackTests : IDisposable
{
    private const string Conditions = """{"positionId": "POS1", "handoffDelay": 30, "scratchPad": "ABC", "clearedAltitude": "120"}""";

    private readonly ScenarioEditorFixture _fx = new();

    public ScenarioAircraftAutoTrackTests()
    {
        TestVnasData.EnsureAircraftDataInitialized();
    }

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void AutoTrack_TurnOn_EmptyPosition()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(_fx, _ => { });
        Assert.False(vm.HasAutoTrack);

        vm.HasAutoTrack = true;

        AutoTrackConditions conditions = Assert.IsType<AutoTrackConditions>(ScenarioAircraftPanel.First(session).AutoTrackConditions);
        Assert.Equal("", conditions.PositionId);
        Assert.Null(conditions.HandoffDelay);
        Assert.Null(conditions.ScratchPad);
        Assert.Equal("Add auto-track", session.UndoDescription);
        Assert.Equal(1, ScenarioAircraftPanel.UndoSteps(session));
    }

    [Fact]
    public void AutoTrack_TurnOff_SetsNull()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(
            _fx,
            a => a["autoTrackConditions"] = JsonNode.Parse(Conditions)
        );
        string original = session.Document.Write();
        Assert.True(vm.HasAutoTrack);
        Assert.Equal("30", vm.HandoffDelay);

        vm.HasAutoTrack = false;

        Assert.Null(ScenarioAircraftPanel.First(session).AutoTrackConditions);
        Assert.Equal("Remove auto-track", session.UndoDescription);
        Assert.Equal(1, ScenarioAircraftPanel.UndoSteps(session));
        Assert.True(session.Undo());
        Assert.Equal(original, session.Document.Write());
        Assert.True(vm.HasAutoTrack);
    }

    [Fact]
    public void AutoTrack_HandoffDelayBlank_WritesNull()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = ScenarioAircraftPanel.Open(
            _fx,
            a => a["autoTrackConditions"] = JsonNode.Parse(Conditions)
        );

        ScenarioAircraftPanel.Commit(() => vm.HandoffDelay = "", vm.CommitHandoffDelayCommand);

        Assert.Null(ScenarioAircraftPanel.First(session).AutoTrackConditions!.HandoffDelay);
        Assert.Equal("Edit handoff delay", session.UndoDescription);
        Assert.Null(vm.HandoffDelayError);
    }

    [Fact]
    public async Task AutoTrack_PositionPicker_FillsWhenTheGeneralCatalogArrivesAfterTheAircraftTabWasBuilt()
    {
        _fx.ConfigBody = ScenarioEditorFixture.ZoaConfig();
        _fx.AddCopy("A1", "Alpha");
        (ScenarioBrowserViewModel browser, ScenarioEditorViewModel editor) = await _fx.OpenEditor();
        await _fx.StartOpen(browser, "A1");
        await _fx.Ui.Run(editor.OpenTask);
        ScenarioAircraftViewModel aircraft = editor.Aircraft!;

        await _fx.Ui.Run(editor.General!.PickersLoaded);

        Assert.Same(aircraft, editor.Aircraft);
        Assert.Contains(aircraft.AutoTrackPositionOptions, o => o.Value == ScenarioEditorFixture.ZoaSector14Id);
    }
}
