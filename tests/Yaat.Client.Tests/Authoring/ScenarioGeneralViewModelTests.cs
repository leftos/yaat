using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The General tab's settings fields over a real session on a working copy in a temp store: each commit is one undo
/// step written to the document, a blank name is refused, the student position picker lists the ARTCC config's
/// positions or falls back to free text, and the rating and auto-delete pickers keep a value they do not list.
/// </summary>
public sealed class ScenarioGeneralViewModelTests : IDisposable
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
    public async Task CommitBlankName_ShowsTheError_LeavesTheDocument_NothingToUndo()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open("Lima");

        general.Name = "   ";
        general.CommitNameCommand.Execute(null);

        Assert.Equal("A scenario needs a name.", general.NameError);
        Assert.Equal("Lima", session.Document.Scenario.Name);
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task CommitTheSameValueTwice_RecordsOneUndoStep()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open("Lima");

        general.Name = "Lima Two";
        general.CommitNameCommand.Execute(null);
        general.CommitNameCommand.Execute(null);

        Assert.Equal("Lima Two", session.Document.Scenario.Name);
        Assert.True(session.Undo());
        Assert.False(session.CanUndo);
        Assert.Equal("Lima", session.Document.Scenario.Name);
    }

    [Fact]
    public async Task EachCommit_IsOneUndoStepNamedForItsField()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open("Lima");

        general.Name = "Lima Two";
        general.CommitNameCommand.Execute(null);
        Assert.Equal("Edit name", session.UndoDescription);
        general.StudentPositionId = ScenarioEditorFixture.ZoaSector14Id;
        general.CommitStudentPositionCommand.Execute(null);
        Assert.Equal("Edit student position", session.UndoDescription);
        general.MinimumRating = "Student3";
        general.CommitMinimumRatingCommand.Execute(null);
        Assert.Equal("Edit minimum rating", session.UndoDescription);
        general.PrimaryAirportId = "KOAK";
        general.CommitPrimaryAirportCommand.Execute(null);
        Assert.Equal("Edit primary airport", session.UndoDescription);
        general.PrimaryApproach = "I28R";
        general.CommitPrimaryApproachCommand.Execute(null);
        Assert.Equal("Edit primary approach", session.UndoDescription);
        general.AutoDeleteMode = "Parked";
        general.CommitAutoDeleteCommand.Execute(null);
        Assert.Equal("Edit auto-delete", session.UndoDescription);

        Scenario scenario = session.Document.Scenario;
        Assert.Equal(
            ("Lima Two", ScenarioEditorFixture.ZoaSector14Id, "Student3", "KOAK", "I28R", "Parked"),
            (
                scenario.Name,
                scenario.StudentPositionId,
                scenario.MinimumRating,
                scenario.PrimaryAirportId,
                scenario.PrimaryApproach,
                scenario.AutoDeleteMode
            )
        );
        Assert.Equal(ScenarioEditorFixture.Zoa, general.ArtccId);
    }

    [Fact]
    public async Task Pickers_ConfigLoads_ListTheConfigsPositionsAndAirports()
    {
        _fx.ConfigBody = ScenarioEditorFixture.ZoaConfig();

        (_, ScenarioGeneralViewModel general) = await Open("Lima");

        Assert.False(general.IsStudentPositionFreeText);
        Assert.False(general.IsPrimaryAirportFreeText);
        Assert.Null(general.ConfigProblem);
        ScenarioPickOption option = Assert.Single(general.StudentPositionOptions);
        Assert.Equal(new ScenarioPickOption(ScenarioEditorFixture.ZoaSector14Id, "OAK_14_CTR — Sector 14 (ZOA)"), option);
        Assert.Equal(["OAK", "SFO"], general.AirportOptions);
    }

    [Fact]
    public async Task Pickers_ConfigCannotLoad_FallBackToFreeText_WithTheLoadsProblem()
    {
        _fx.ConfigBody = null;

        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open("Lima");

        Assert.True(general.IsStudentPositionFreeText);
        Assert.True(general.IsPrimaryAirportFreeText);
        Assert.Empty(general.AirportOptions);
        Assert.False(string.IsNullOrEmpty(general.ConfigProblem));
        Assert.Contains("ZOA", general.ConfigProblem);
        general.StudentPositionId = "  OAK_14  ";
        general.CommitStudentPositionCommand.Execute(null);
        Assert.Equal("OAK_14", session.Document.Scenario.StudentPositionId);
    }

    [Fact]
    public async Task StudentPositionPicker_ADocumentValueTheCatalogDoesNotList_IsShownAndKept()
    {
        _fx.ConfigBody = ScenarioEditorFixture.ZoaConfig();

        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open("Lima", "\"studentPositionId\": \"GONE1\"");

        Assert.Equal("GONE1", general.StudentPositionId);
        Assert.Equal([ScenarioEditorFixture.ZoaSector14Id, "GONE1"], general.StudentPositionOptions.Select(o => o.Value));
        general.PrimaryApproach = "I28R";
        general.CommitPrimaryApproachCommand.Execute(null);
        Assert.Equal("GONE1", session.Document.Scenario.StudentPositionId);
    }

    [Fact]
    public async Task StudentPositionPicker_UnreadNeighbours_AreNamedInTheProblem()
    {
        _fx.ConfigBody = ScenarioEditorFixture.ZoaConfig("ZLA", "ZSE", "SFO");

        (_, ScenarioGeneralViewModel general) = await Open("Lima");

        Assert.False(general.IsStudentPositionFreeText);
        Assert.Equal("positions of ZLA, ZSE could not be read", general.ConfigProblem);
        Assert.Single(general.StudentPositionOptions);
    }

    [Fact]
    public async Task AirportPicker_ADocumentValueTheConfigDoesNotList_StaysOffered()
    {
        _fx.ConfigBody = ScenarioEditorFixture.ZoaConfig();

        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open("Lima", "\"primaryAirportId\": \"KOAK\"");

        Assert.Equal("KOAK", general.PrimaryAirportId);
        Assert.Equal(["OAK", "SFO", "KOAK"], general.AirportOptions);
        general.PrimaryApproach = "I28R";
        general.CommitPrimaryApproachCommand.Execute(null);
        Assert.Equal(["OAK", "SFO", "KOAK"], general.AirportOptions);
        Assert.Equal("KOAK", session.Document.Scenario.PrimaryAirportId);
        general.PrimaryAirportId = "SFO";
        general.CommitPrimaryAirportCommand.Execute(null);
        Assert.Equal(["OAK", "SFO"], general.AirportOptions);
    }

    [Fact]
    public async Task AirportPicker_ADocumentValueDifferingOnlyInCase_IsOfferedOnce_InTheDocumentsSpelling()
    {
        _fx.ConfigBody = ScenarioEditorFixture.ZoaConfig();

        (_, ScenarioGeneralViewModel general) = await Open("Lima", "\"primaryAirportId\": \"oak\"");

        Assert.Equal(["oak", "SFO"], general.AirportOptions);
    }

    [Fact]
    public async Task RatingOptions_AreNoneThenTheElevenLongForms_InOrder()
    {
        (_, ScenarioGeneralViewModel general) = await Open("Lima");

        Assert.Equal(
            [
                "",
                "Observer",
                "Student1",
                "Student2",
                "Student3",
                "Controller1",
                "Controller3",
                "Instructor1",
                "Instructor2",
                "Instructor3",
                "Supervisor",
                "Administrator",
            ],
            general.RatingOptions.Select(o => o.Value)
        );
        Assert.Equal("None", general.RatingOptions[0].Label);
    }

    [Fact]
    public async Task AutoDeleteOptions_AreDefaultNeverOnLandingParked()
    {
        (_, ScenarioGeneralViewModel general) = await Open("Lima");

        Assert.Equal(["Default", "Never", "OnLanding", "Parked"], general.AutoDeleteOptions.Select(o => o.Label));
        Assert.Equal(["", "Never", "OnLanding", "Parked"], general.AutoDeleteOptions.Select(o => o.Value));
        Assert.Equal("", general.AutoDeleteMode);
    }

    [Fact]
    public async Task AutoDelete_ADocumentNone_IsShown_AndSurvivesAnUnrelatedEditAndSave()
    {
        (ScenarioEditSession session, ScenarioGeneralViewModel general) = await Open("Lima", "\"autoDeleteMode\": \"None\"");

        Assert.Equal("None", general.AutoDeleteMode);
        Assert.Equal(["", "Never", "OnLanding", "Parked", "None"], general.AutoDeleteOptions.Select(o => o.Value));
        general.Name = "Lima Two";
        general.CommitNameCommand.Execute(null);
        Assert.Equal(ScenarioSaveOutcome.Saved, session.Save().Outcome);

        Assert.Equal("None", _fx.CopyJsonString("L1", "autoDeleteMode"));
        Assert.Equal("Lima Two", _fx.CopyJsonString("L1", "name"));
    }

    // Working copy L1 with the name and extra members, its session, and a General tab whose pickers have loaded.
    private async Task<(ScenarioEditSession Session, ScenarioGeneralViewModel General)> Open(string name, string extra = "")
    {
        _fx.AddCopy("L1", name, extra);
        var session = new ScenarioEditSession(_fx.Store, ScenarioEditorFixture.Zoa, "L1");
        var general = new ScenarioGeneralViewModel(session, _fx.Services());
        _generals.Add(general);
        await _fx.Ui.Run(general.PickersLoaded);
        return (session, general);
    }
}
