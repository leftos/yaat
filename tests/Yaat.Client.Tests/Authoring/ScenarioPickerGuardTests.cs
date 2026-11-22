using System.Collections.Specialized;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The General tab's pickers commit only on a user selection. Each picker keeps one options collection for its view
/// model's lifetime, and a rebuild (a commit, the ARTCC catalog arriving) updates it in place, removing, inserting or
/// replacing only the entries that differ, then raises the picker's selection again. A selection the picker writes while
/// that update runs (a ComboBox clearing or moving it as items change), or a null selection, writes nothing and records
/// no undo step.
/// </summary>
public sealed class ScenarioPickerGuardTests : IDisposable
{
    private readonly ScenarioEditorFixture _fx = new();
    private readonly List<ScenarioGeneralViewModel> _generals = [];

    public void Dispose()
    {
        foreach (ScenarioGeneralViewModel general in _generals)
        {
            general.Dispose();
        }

        _fx.Dispose();
    }

    [Fact]
    public async Task RebuildingPickerOptions_AfterACommit_WritesNothing_AndRecordsNoUndoStep()
    {
        _fx.ConfigBody = ScenarioEditorFixture.ZoaConfig();
        _fx.AddCopy(
            "L1",
            "Lima",
            "\"atc\": [{\"id\": \"A1\", \"artccId\": \"ZOA\", \"positionId\": \"" + ScenarioEditorFixture.ZoaSector14Id + "\"}]"
        );
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, "L1");
        var general = new ScenarioGeneralViewModel(session, _fx.Services());
        _generals.Add(general);
        await _fx.Ui.Run(general.PickersLoaded);
        IReadOnlyList<ScenarioPickOption> positions = general.StudentPositionOptions;
        IReadOnlyList<ScenarioPickOption> ratings = general.RatingOptions;
        IReadOnlyList<ScenarioPickOption> atcPositions = general.AtcPositions[0].PositionOptions;

        general.StudentPositionPick = ScenarioEditorFixture.ZoaSector14Id;

        Assert.Equal(ScenarioEditorFixture.ZoaSector14Id, session.Document.Scenario.StudentPositionId);
        Assert.Same(positions, general.StudentPositionOptions);
        Assert.Same(ratings, general.RatingOptions);
        Assert.Same(atcPositions, general.AtcPositions[0].PositionOptions);

        general.StudentPositionPick = null;
        general.MinimumRatingPick = null;
        general.AutoDeletePick = null;
        general.AtcPositions[0].PositionPick = null;

        Assert.Equal(ScenarioEditorFixture.ZoaSector14Id, general.StudentPositionId);
        Assert.Equal(ScenarioEditorFixture.ZoaSector14Id, session.Document.Scenario.StudentPositionId);
        Assert.Equal(ScenarioEditorFixture.ZoaSector14Id, general.AtcPositions[0].PositionId);
        Assert.Equal("", general.MinimumRating);
        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        Assert.Null(session.Document.Scenario.StudentPositionId);
    }

    [Fact]
    public async Task RebuildingAnOptionList_InPlace_WritesNothing_AndRecordsNoUndoStep()
    {
        _fx.ConfigBody = ScenarioEditorFixture.ZoaConfig();
        _fx.AddCopy(
            "L2",
            "Lima",
            "\"studentPositionId\": \""
                + ScenarioEditorFixture.ZoaSector14Id
                + "\", \"atc\": [{\"id\": \"A1\", \"artccId\": \"ZOA\", \"positionId\": \""
                + ScenarioEditorFixture.ZoaSector14Id
                + "\"}]"
        );
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, "L2");
        var general = new ScenarioGeneralViewModel(session, _fx.Services());
        _generals.Add(general);
        ScenarioAtcRowViewModel row = general.AtcPositions[0];
        IReadOnlyList<ScenarioPickOption> positions = general.StudentPositionOptions;
        IReadOnlyList<ScenarioPickOption> atcPositions = row.PositionOptions;
        int changes = 0;
        var raisedAfterLastChange = new HashSet<string>(StringComparer.Ordinal);

        // As a ComboBox does while its items change: clear its selection, then move it to another option.
        ((INotifyCollectionChanged)positions).CollectionChanged += (_, _) =>
        {
            changes++;
            raisedAfterLastChange.Clear();
            general.StudentPositionSelection = null;
            general.StudentPositionSelection = positions.FirstOrDefault(o => o.Value != ScenarioEditorFixture.ZoaSector14Id);
        };
        ((INotifyCollectionChanged)atcPositions).CollectionChanged += (_, _) =>
        {
            changes++;
            row.PositionSelection = null;
            row.PositionSelection = atcPositions.FirstOrDefault(o => o.Value != ScenarioEditorFixture.ZoaSector14Id);
        };
        general.PropertyChanged += (_, e) => raisedAfterLastChange.Add(e.PropertyName ?? "");

        await _fx.Ui.Run(general.PickersLoaded);

        Assert.True(changes > 0);
        Assert.Same(positions, general.StudentPositionOptions);
        Assert.Same(atcPositions, row.PositionOptions);
        Assert.False(session.CanUndo);
        Assert.Equal(ScenarioEditorFixture.ZoaSector14Id, session.Document.Scenario.StudentPositionId);
        Assert.Equal(ScenarioEditorFixture.ZoaSector14Id, session.Document.Scenario.Atc[0].PositionId);
        Assert.Equal(ScenarioEditorFixture.ZoaSector14Id, general.StudentPositionSelection?.Value);
        Assert.Equal(ScenarioEditorFixture.ZoaSector14Id, row.PositionSelection?.Value);
        Assert.DoesNotContain(general.StudentPositionOptions, o => o.Label.EndsWith("(not in the ARTCC config)", StringComparison.Ordinal));
        Assert.Contains(nameof(ScenarioGeneralViewModel.StudentPositionSelection), raisedAfterLastChange);
    }
}
