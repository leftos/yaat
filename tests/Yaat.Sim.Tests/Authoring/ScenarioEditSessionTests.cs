using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="ScenarioEditSession"/> edits one working copy: each edit is an undo step, Save writes only a changed
/// document with a name, and a document edited and then undone back to its start reads not modified, whatever number
/// spelling the server copy used.
/// </summary>
public class ScenarioEditSessionTests
{
    private const string Zoa = TrainingFixtures.Artcc;
    private const string IdA = TrainingFixtures.IdA;
    private const string IdB = TrainingFixtures.IdB;

    // Starts a working copy of a server scenario with the given text, then runs the body over it.
    private static void WithCopy(string id, string serverText, Action<string, LocalScenarioStore> body)
    {
        string root = TrainingFixtures.NewRoot();
        try
        {
            var store = new LocalScenarioStore(root);
            store.ApplyServerBody(Zoa, id, serverText);
            store.CreateWorkingCopy(Zoa, id);
            body(root, store);
        }
        finally
        {
            TrainingFixtures.DeleteDir(root);
        }
    }

    private static bool IsModified(LocalScenarioStore store, string id) => store.Load(Zoa).WorkingCopies.Single(c => c.Id == id).Modified;

    private static JsonObject SavedJson(string root, string id) => JsonNode.Parse(File.ReadAllText(TrainingFixtures.LocalPath(root, id)))!.AsObject();

    [Fact]
    public void Open_ExposesTheWorkingCopy_NotDirty()
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);

                Assert.Equal(Zoa, session.ArtccId);
                Assert.Equal(IdA, session.ScenarioId);
                Assert.Equal(TrainingFixtures.NameOf(IdA), session.Document.Scenario.Name);
                Assert.False(session.IsDirty);
                Assert.False(session.CanUndo);
                Assert.False(session.CanRedo);
            }
        );
    }

    [Fact]
    public void EditName_ThenSave_WritesTheNewName_Modified()
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);

                _ = session.Edit("Rename", s => s.Name = "Renamed scenario");
                Assert.True(session.IsDirty);
                ScenarioSaveResult result = session.Save();

                Assert.Equal(ScenarioSaveOutcome.Saved, result.Outcome);
                Assert.False(session.IsDirty);
                Assert.Equal("Renamed scenario", SavedJson(root, IdA)["name"]!.GetValue<string>());
                Assert.True(IsModified(store, IdA));
            }
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankName_SaveIsRefused_WritesNothing(string name)
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                byte[] before = File.ReadAllBytes(TrainingFixtures.LocalPath(root, IdA));
                var session = new ScenarioEditSession(store, Zoa, IdA);

                _ = session.Edit("Clear name", s => s.Name = name);
                ScenarioSaveResult result = session.Save();

                Assert.Equal(ScenarioSaveOutcome.Refused, result.Outcome);
                Assert.False(string.IsNullOrWhiteSpace(result.Reason));
                Assert.True(session.IsDirty);
                Assert.Equal(before, File.ReadAllBytes(TrainingFixtures.LocalPath(root, IdA)));
            }
        );
    }

    [Fact]
    public void SaveWithoutAnEdit_LeavesTheFileBytesUnchanged()
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                string path = TrainingFixtures.LocalPath(root, IdA);
                byte[] before = File.ReadAllBytes(path);
                DateTime stamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(path, stamp);
                var session = new ScenarioEditSession(store, Zoa, IdA);

                ScenarioSaveResult result = session.Save();

                Assert.Equal(ScenarioSaveOutcome.Unchanged, result.Outcome);
                Assert.Equal(before, File.ReadAllBytes(path));
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
                Assert.False(IsModified(store, IdA));
            }
        );
    }

    [Fact]
    public void EditANumberField_SaveUndoSave_NotModified()
    {
        WithCopy(
            IdA,
            TrainingFixtures.WithAltitudeText(IdA, "250.0"),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);

                _ = session.Edit("Altitude", s => s.Aircraft[0].StartingConditions.Altitude = 300);
                session.Save();
                Assert.True(IsModified(store, IdA));
                Assert.True(session.Undo());
                Assert.Equal(250, session.Document.Scenario.Aircraft[0].StartingConditions.Altitude);
                session.Save();

                Assert.Equal(250, SavedJson(root, IdA)["aircraft"]![0]!["startingConditions"]!["altitude"]!.GetValue<double>());
                Assert.False(IsModified(store, IdA));
            }
        );
    }

    [Fact]
    public void ServerNumberText_UntouchedAcrossAnUndoneEdit_NotModified()
    {
        WithCopy(
            IdA,
            TrainingFixtures.WithAltitudeText(IdA, "250.0"),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);

                _ = session.Edit("Rename", s => s.Name = "Elsewhere");
                session.Save();
                session.Undo();
                session.Save();

                // The save re-spelled the untouched number: the premise the not-modified answer must survive.
                string saved = File.ReadAllText(TrainingFixtures.LocalPath(root, IdA));
                Assert.DoesNotContain("250.0", saved, StringComparison.Ordinal);
                Assert.False(IsModified(store, IdA));
            }
        );
    }

    [Fact]
    public void UnmodelledFields_AtRootAndInAnEditedTrigger_SurviveEditAndSave()
    {
        JsonObject source = JsonNode.Parse(TrainingFixtures.Read(IdB))!.AsObject();
        source["unmodelledRootField"] = new JsonObject { ["nested"] = "kept" };
        source["initializationTriggers"]![0]!["unmodelledTriggerField"] = 7;
        WithCopy(
            IdB,
            source.ToJsonString(),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdB);

                _ = session.Edit("Trigger command", s => s.InitializationTriggers[0].Command = "SQALL 1200");
                session.Save();

                JsonObject saved = SavedJson(root, IdB);
                Assert.Equal("kept", saved["unmodelledRootField"]!["nested"]!.GetValue<string>());
                JsonObject trigger = saved["initializationTriggers"]![0]!.AsObject();
                Assert.Equal(7, trigger["unmodelledTriggerField"]!.GetValue<int>());
                Assert.Equal("SQALL 1200", trigger["command"]!.GetValue<string>());
            }
        );
    }

    [Fact]
    public void AbsentMembers_LeftAlone_StayAbsent_AcrossEditSaveAndUndo()
    {
        WithCopy(
            IdB,
            TrainingFixtures.Read(IdB),
            (root, store) =>
            {
                JsonObject trigger0 = JsonNode.Parse(TrainingFixtures.Read(IdB))!["initializationTriggers"]![0]!.AsObject();
                Assert.False(trigger0.ContainsKey("timeOffset"), "the fixture trigger is expected to leave timeOffset out");
                var session = new ScenarioEditSession(store, Zoa, IdB);

                _ = session.Edit("Trigger command", s => s.InitializationTriggers[0].Command = "SQALL 1200");
                _ = session.Edit("Rename", s => s.Name = "Renamed");
                session.Undo();
                session.Save();

                JsonObject saved = SavedJson(root, IdB);
                Assert.False(saved["initializationTriggers"]![0]!.AsObject().ContainsKey("timeOffset"));
                Assert.False(saved.ContainsKey("minimumRating"));
                Assert.False(saved.ContainsKey("primaryApproach"));
            }
        );
    }

    [Fact]
    public void UndoAndRedo_SurviveASave_UndoAfterSaveIsDirty()
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);
                _ = session.Edit("Rename", s => s.Name = "Saved name");
                session.Save();

                Assert.False(session.IsDirty);
                Assert.True(session.CanUndo);
                Assert.True(session.Undo());
                Assert.True(session.IsDirty);
                Assert.Equal(TrainingFixtures.NameOf(IdA), session.Document.Scenario.Name);
                Assert.True(session.Redo());
                Assert.False(session.IsDirty);
                Assert.Equal("Saved name", session.Document.Scenario.Name);
            }
        );
    }

    [Fact]
    public void UndoAndRedo_AcrossSeveralEdits_WalkTheStack_WithDescriptions()
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);
                _ = session.Edit("Name one", s => s.Name = "One");
                _ = session.Edit("Approach", s => s.PrimaryApproach = "ILS 28R");
                _ = session.Edit("Name three", s => s.Name = "Three");
                Assert.Equal("Name three", session.UndoDescription);
                Assert.Null(session.RedoDescription);

                session.Undo();
                session.Undo();

                Assert.Equal("One", session.Document.Scenario.Name);
                Assert.Null(session.Document.Scenario.PrimaryApproach);
                Assert.Equal("Name one", session.UndoDescription);
                Assert.Equal("Approach", session.RedoDescription);

                session.Redo();

                Assert.Equal("ILS 28R", session.Document.Scenario.PrimaryApproach);
                Assert.Equal("One", session.Document.Scenario.Name);
                Assert.Equal("Name three", session.RedoDescription);

                _ = session.Edit("Name four", s => s.Name = "Four");

                Assert.False(session.CanRedo);
                session.Undo();
                session.Undo();
                session.Undo();
                Assert.False(session.CanUndo);
                Assert.False(session.Undo());
                Assert.False(session.IsDirty);
            }
        );
    }

    [Fact]
    public void Undo_ReplacesTheDocument_RaisesDocumentReplaced()
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);
                _ = session.Edit("Rename", s => s.Name = "Renamed");
                ScenarioDocument before = session.Document;
                int raised = 0;
                session.DocumentReplaced += (_, _) => raised++;

                session.Undo();

                Assert.Equal(1, raised);
                Assert.NotSame(before, session.Document);
                session.Redo();
                Assert.Equal(2, raised);
            }
        );
    }

    [Fact]
    public void RefusedSave_ThenANameGiven_SaveSaves()
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);
                _ = session.Edit("Clear name", s => s.Name = "");
                Assert.Equal(ScenarioSaveOutcome.Refused, session.Save().Outcome);

                _ = session.Edit("Rename", s => s.Name = "Fixed name");
                ScenarioSaveResult result = session.Save();

                Assert.Equal(ScenarioSaveOutcome.Saved, result.Outcome);
                Assert.Null(result.Reason);
                Assert.False(session.IsDirty);
                Assert.Equal("Fixed name", SavedJson(root, IdA)["name"]!.GetValue<string>());
            }
        );
    }

    [Fact]
    public void UndoWithNothingToUndo_ReturnsFalse_RaisesNoDocumentReplaced()
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);
                ScenarioDocument before = session.Document;
                int raised = 0;
                session.DocumentReplaced += (_, _) => raised++;

                Assert.False(session.Undo());
                Assert.False(session.Redo());

                Assert.Equal(0, raised);
                Assert.Same(before, session.Document);
            }
        );
    }

    [Fact]
    public void Edit_ThatThrows_LeavesTheDocumentDirtyStateAndUndoStackAsBefore()
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);
                _ = session.Edit("Rename", s => s.Name = "Renamed");
                session.Save();
                string written = session.Document.Write();
                int raised = 0;
                session.DocumentReplaced += (_, _) => raised++;

                Assert.Throws<InvalidOperationException>(() =>
                    _ = session.Edit(
                        "Half done",
                        s =>
                        {
                            s.Name = "Half";
                            s.PrimaryApproach = "ILS 28R";
                            throw new InvalidOperationException("change failed");
                        }
                    )
                );

                Assert.Equal("Renamed", session.Document.Scenario.Name);
                Assert.Null(session.Document.Scenario.PrimaryApproach);
                Assert.Equal(written, session.Document.Write());
                Assert.False(session.IsDirty);
                Assert.Equal(1, raised);
                Assert.Equal("Rename", session.UndoDescription);
                Assert.False(session.CanRedo);
                Assert.True(session.Undo());
                Assert.False(session.CanUndo);
                Assert.Equal(TrainingFixtures.NameOf(IdA), session.Document.Scenario.Name);
            }
        );
    }

    [Fact]
    public void Edit_ThatChangesNothing_IsNotAnUndoStep()
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);
                string name = session.Document.Scenario.Name;

                _ = session.Edit("Same name", s => s.Name = name);

                Assert.False(session.CanUndo);
                Assert.False(session.IsDirty);
            }
        );
    }

    [Fact]
    public void Edit_ReturnsWhetherItRecordedAStep()
    {
        WithCopy(
            IdA,
            TrainingFixtures.Read(IdA),
            (root, store) =>
            {
                var session = new ScenarioEditSession(store, Zoa, IdA);
                string name = session.Document.Scenario.Name;

                Assert.False(session.Edit("Same name", s => s.Name = name));
                Assert.True(session.Edit("Rename", s => s.Name = name + " renamed"));
                Assert.Equal("Rename", session.UndoDescription);
            }
        );
    }
}
