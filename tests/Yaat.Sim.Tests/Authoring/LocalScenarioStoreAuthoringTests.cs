using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// The authoring operations of <see cref="LocalScenarioStore"/>: new, duplicate, delete and import of working copies.
/// Every new id is fresh, a duplicate is always local-only, a delete never touches the server mirror, and an import that
/// keeps a server scenario's id becomes that scenario's working copy.
/// </summary>
public class LocalScenarioStoreAuthoringTests
{
    private const string Zoa = TrainingFixtures.Artcc;
    private const string IdA = TrainingFixtures.IdA;
    private const string IdB = TrainingFixtures.IdB;
    private const string LocalId = "LOCAL_ONLY-1";

    private static void InRoot(Action<string, LocalScenarioStore> body)
    {
        string root = TrainingFixtures.NewRoot();
        try
        {
            body(root, new LocalScenarioStore(root));
        }
        finally
        {
            TrainingFixtures.DeleteDir(root);
        }
    }

    private static Func<string> Ids(int seed)
    {
        var random = new Random(seed);
        DateTimeOffset now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
        int tick = 0;
        return () => ScenarioIdGenerator.Create(now.AddMilliseconds(tick++), random);
    }

    private static string IdIn(string json) => JsonNode.Parse(json)!["id"]!.GetValue<string>();

    private static WorkingCopyEntry CopyOf(LocalScenarioStore store, string id) => store.Load(Zoa).WorkingCopies.Single(c => c.Id == id);

    [Fact]
    public void Ids_StayUniqueOnDuplicateAndImport() =>
        InRoot(
            (root, store) =>
            {
                Func<string> newId = Ids(11);
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);

                string dup1 = newId();
                string dup2 = newId();
                store.DuplicateWorkingCopy(Zoa, IdA, dup1);
                store.DuplicateWorkingCopy(Zoa, IdA, dup2);
                ImportResult imp1 = store.ImportScenario(Zoa, TrainingFixtures.Read(IdA), ImportIdChoice.NewId, newId);
                ImportResult imp2 = store.ImportScenario(Zoa, TrainingFixtures.Read(IdA), ImportIdChoice.NewId, newId);

                string[] ids = [dup1, dup2, imp1.Id!, imp2.Id!];
                Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
                Assert.DoesNotContain(IdA, ids);
                Assert.Equal(5, store.Load(Zoa).WorkingCopies.Count);
                Assert.All(ids, id => Assert.Equal(id, IdIn(store.ReadWorkingCopyText(Zoa, id))));
            }
        );

    [Fact]
    public void Duplicate_OfServerDerivedCopy_IsLocalOnly_NeverServerChanged() =>
        InRoot(
            (root, store) =>
            {
                string newId = Ids(1)();
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);

                store.DuplicateWorkingCopy(Zoa, IdA, newId);
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Changed(IdA));

                WorkingCopyEntry dup = CopyOf(store, newId);
                Assert.Null(dup.OriginId);
                Assert.Null(dup.BaseVersion);
                Assert.False(dup.ServerChanged);
                Assert.False(dup.Corrupt);
                Assert.Equal(newId, IdIn(store.ReadWorkingCopyText(Zoa, newId)));
                Assert.True(CopyOf(store, IdA).ServerChanged);
            }
        );

    [Fact]
    public void Duplicate_OfCorruptCopy_Fails_WritesNothing() =>
        InRoot(
            (root, store) =>
            {
                string newId = Ids(2)();
                store.SaveWorkingCopy(Zoa, LocalId, "[1, 2]");

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => store.DuplicateWorkingCopy(Zoa, LocalId, newId));

                Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(TrainingFixtures.LocalPath(root, newId)));
                Assert.False(File.Exists(TrainingFixtures.SidecarPath(root, newId)));
            }
        );

    [Fact]
    public void Delete_RemovesJsonAndSidecar_LeavesTheMirror() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                byte[] mirrorBefore = File.ReadAllBytes(TrainingFixtures.MirrorPath(root, IdA));

                store.DeleteWorkingCopy(Zoa, IdA);

                Assert.False(File.Exists(TrainingFixtures.LocalPath(root, IdA)));
                Assert.False(File.Exists(TrainingFixtures.SidecarPath(root, IdA)));
                Assert.Equal(mirrorBefore, File.ReadAllBytes(TrainingFixtures.MirrorPath(root, IdA)));
                ArtccStoreListing listing = store.Load(Zoa);
                Assert.Empty(listing.WorkingCopies);
                Assert.Equal(IdA, Assert.Single(listing.Mirrors).Mirror.Id);
            }
        );

    [Fact]
    public void Delete_CorruptCopy_IsDeleted() =>
        InRoot(
            (root, store) =>
            {
                store.SaveWorkingCopy(Zoa, LocalId, "{ broken");

                store.DeleteWorkingCopy(Zoa, LocalId);

                Assert.False(File.Exists(TrainingFixtures.LocalPath(root, LocalId)));
                Assert.False(File.Exists(TrainingFixtures.SidecarPath(root, LocalId)));
            }
        );

    [Fact]
    public void Delete_CopyWithoutSidecar_IsDeleted() =>
        InRoot(
            (root, store) =>
            {
                store.SaveWorkingCopy(Zoa, LocalId, TrainingFixtures.WithId(IdB, LocalId));
                File.Delete(TrainingFixtures.SidecarPath(root, LocalId));

                store.DeleteWorkingCopy(Zoa, LocalId);

                Assert.False(File.Exists(TrainingFixtures.LocalPath(root, LocalId)));
            }
        );

    [Fact]
    public void Import_NewId_RewritesId_LocalOnly() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));

                ImportResult result = store.ImportScenario(Zoa, TrainingFixtures.Read(IdA), ImportIdChoice.NewId, Ids(3));

                Assert.Equal(ImportOutcome.Imported, result.Outcome);
                Assert.NotEqual(IdA, result.Id);
                Assert.False(result.Overwrote);
                Assert.False(result.ArtccMismatch);
                Assert.Equal(result.Id, IdIn(store.ReadWorkingCopyText(Zoa, result.Id!)));
                Assert.Null(CopyOf(store, result.Id!).OriginId);
            }
        );

    [Fact]
    public void Import_Skip_WritesNothing() =>
        InRoot(
            (root, store) =>
            {
                ImportResult result = store.ImportScenario(Zoa, TrainingFixtures.Read(IdA), ImportIdChoice.Skip, Ids(4));

                Assert.Equal(ImportOutcome.Skipped, result.Outcome);
                Assert.Null(result.Id);
                Assert.False(Directory.Exists(Path.Combine(root, Zoa, "local")));
            }
        );

    [Fact]
    public void Import_KeepId_OverExistingCopy_OverwritesText_KeepsSidecar() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                byte[] sidecarBefore = File.ReadAllBytes(TrainingFixtures.SidecarPath(root, IdA));
                string edited = TrainingFixtures.WithNameSuffix(IdA, " (imported)");
                Assert.Equal(ScenarioIdConflict.Both, store.FindIdConflict(Zoa, IdA));

                ImportResult result = store.ImportScenario(Zoa, edited, ImportIdChoice.KeepId, Ids(5));

                Assert.Equal(ImportOutcome.Imported, result.Outcome);
                Assert.Equal(IdA, result.Id);
                Assert.True(result.Overwrote);
                Assert.Equal(edited, store.ReadWorkingCopyText(Zoa, IdA));
                Assert.Equal(sidecarBefore, File.ReadAllBytes(TrainingFixtures.SidecarPath(root, IdA)));
                WorkingCopyEntry copy = CopyOf(store, IdA);
                Assert.Equal(IdA, copy.OriginId);
                Assert.True(copy.Modified);
            }
        );

    [Fact]
    public void Import_KeepId_EqualToAMirror_BecomesItsWorkingCopy_Modified() =>
        InRoot(
            (root, store) =>
            {
                string mirror = TrainingFixtures.Read(IdA);
                store.ApplyServerBody(Zoa, IdA, mirror);
                Assert.Equal(ScenarioIdConflict.Mirror, store.FindIdConflict(Zoa, IdA));

                ImportResult result = store.ImportScenario(Zoa, TrainingFixtures.WithNameSuffix(IdA, " (mine)"), ImportIdChoice.KeepId, Ids(6));

                Assert.Equal(ImportOutcome.Imported, result.Outcome);
                Assert.Equal(IdA, result.Id);
                Assert.False(result.Overwrote);
                WorkingCopyEntry copy = CopyOf(store, IdA);
                Assert.Equal(IdA, copy.OriginId);
                Assert.Equal(LocalScenarioStore.ComputeVersion(mirror), copy.BaseVersion);
                Assert.True(copy.Modified);
                Assert.False(copy.ServerChanged);

                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Changed(IdA));

                Assert.True(CopyOf(store, IdA).ServerChanged);
            }
        );

    [Fact]
    public void Import_KeepId_EqualToAMirror_SameContent_NotModified() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));

                store.ImportScenario(Zoa, TrainingFixtures.Reordered(IdA), ImportIdChoice.KeepId, Ids(7));

                WorkingCopyEntry copy = CopyOf(store, IdA);
                Assert.Equal(IdA, copy.OriginId);
                Assert.False(copy.Modified);
            }
        );

    [Fact]
    public void Import_KeepId_NoConflict_IsLocalOnlyWithThatId() =>
        InRoot(
            (root, store) =>
            {
                Assert.Equal(ScenarioIdConflict.None, store.FindIdConflict(Zoa, IdB));

                ImportResult result = store.ImportScenario(Zoa, TrainingFixtures.Read(IdB), ImportIdChoice.KeepId, Ids(8));

                Assert.Equal(IdB, result.Id);
                Assert.False(result.Overwrote);
                Assert.Equal(TrainingFixtures.Read(IdB), store.ReadWorkingCopyText(Zoa, IdB));
                WorkingCopyEntry copy = CopyOf(store, IdB);
                Assert.Null(copy.OriginId);
                Assert.False(copy.ServerChanged);
                Assert.Equal(ScenarioIdConflict.WorkingCopy, store.FindIdConflict(Zoa, IdB));
            }
        );

    [Theory]
    [InlineData("{ not json", ImportIdChoice.NewId)]
    [InlineData("{ not json", ImportIdChoice.KeepId)]
    [InlineData("{ not json", ImportIdChoice.Skip)]
    [InlineData("[1, 2]", ImportIdChoice.NewId)]
    [InlineData("[1, 2]", ImportIdChoice.KeepId)]
    [InlineData("[1, 2]", ImportIdChoice.Skip)]
    [InlineData("""{"name":"No id"}""", ImportIdChoice.NewId)]
    [InlineData("""{"name":"No id"}""", ImportIdChoice.KeepId)]
    [InlineData("""{"name":"No id"}""", ImportIdChoice.Skip)]
    [InlineData("""{"id":"01H06NVK7VN8BS7MCDXHKJZ7MQ"}""", ImportIdChoice.NewId)]
    [InlineData("""{"id":"01H06NVK7VN8BS7MCDXHKJZ7MQ"}""", ImportIdChoice.KeepId)]
    [InlineData("""{"id":"01H06NVK7VN8BS7MCDXHKJZ7MQ"}""", ImportIdChoice.Skip)]
    public void Import_NotAScenario_IsRejectedWithTheReason_WritesNothing(string json, ImportIdChoice choice) =>
        InRoot(
            (root, store) =>
            {
                ImportResult result = store.ImportScenario(Zoa, json, choice, Ids(9));

                Assert.Equal(ImportOutcome.Rejected, result.Outcome);
                Assert.StartsWith("not a scenario", result.Reason, StringComparison.Ordinal);
                Assert.Null(result.Id);
                Assert.False(Directory.Exists(Path.Combine(root, Zoa)));
            }
        );

    [Fact]
    public void Import_FileOfAnotherArtcc_IsImported_FlagsTheMismatch() =>
        InRoot(
            (root, store) =>
            {
                JsonObject scenario = JsonNode.Parse(TrainingFixtures.Read(IdB))!.AsObject();
                scenario["artccId"] = "ZLA";

                ImportResult result = store.ImportScenario(Zoa, scenario.ToJsonString(), ImportIdChoice.KeepId, Ids(10));

                Assert.Equal(ImportOutcome.Imported, result.Outcome);
                Assert.True(result.ArtccMismatch);
                Assert.True(File.Exists(TrainingFixtures.LocalPath(root, IdB)));
            }
        );

    [Fact]
    public void CreateLocalScenario_WritesLocalOnlyCopy_WithTheId() =>
        InRoot(
            (root, store) =>
            {
                string id = Ids(12)();

                store.CreateLocalScenario(Zoa, TrainingFixtures.Read(IdB), id);

                Assert.Equal(id, IdIn(store.ReadWorkingCopyText(Zoa, id)));
                Assert.Equal(store.GetWorkingCopyPath(Zoa, id), TrainingFixtures.LocalPath(root, id));
                WorkingCopyEntry copy = CopyOf(store, id);
                Assert.Null(copy.OriginId);
                Assert.False(copy.Corrupt);
                Assert.DoesNotContain(store.Load(Zoa).Problems, p => p.FileName.StartsWith("local/", StringComparison.Ordinal));
            }
        );

    [Fact]
    public void CreateLocalScenario_OnExistingId_Fails_LeavesTheFile() =>
        InRoot(
            (root, store) =>
            {
                store.SaveWorkingCopy(Zoa, LocalId, TrainingFixtures.WithId(IdB, LocalId));
                byte[] before = File.ReadAllBytes(TrainingFixtures.LocalPath(root, LocalId));

                Assert.Throws<InvalidOperationException>(() => store.CreateLocalScenario(Zoa, TrainingFixtures.Read(IdA), LocalId));

                Assert.Equal(before, File.ReadAllBytes(TrainingFixtures.LocalPath(root, LocalId)));
            }
        );

    [Fact]
    public void ReadWorkingCopyText_ReturnsTheTextAsWritten() =>
        InRoot(
            (root, store) =>
            {
                string json = """{ "id": "LOCAL_ONLY-1", "name": "Numbers", "altitude": 250.0, "speed": 1e3 }""";
                store.SaveWorkingCopy(Zoa, LocalId, json);

                Assert.Equal(json, store.ReadWorkingCopyText(Zoa, LocalId));
            }
        );

    [Fact]
    public void Duplicate_KeepsNumberTextAsWritten() =>
        InRoot(
            (root, store) =>
            {
                store.SaveWorkingCopy(Zoa, LocalId, """{ "id": "LOCAL_ONLY-1", "name": "Numbers", "altitude": 250.0 }""");

                store.DuplicateWorkingCopy(Zoa, LocalId, "LOCAL_ONLY-2");

                Assert.Contains("250.0", store.ReadWorkingCopyText(Zoa, "LOCAL_ONLY-2"), StringComparison.Ordinal);
            }
        );

    [Fact]
    public void ReadMirrorText_ReturnsTheServerText() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));

                Assert.Equal(TrainingFixtures.Read(IdA), store.ReadMirrorText(Zoa, IdA));
            }
        );

    private static string LocalDir(string root) => Path.Combine(root, Zoa, "local");

    private static string[] LocalFileNames(string root) =>
        [.. Directory.GetFiles(LocalDir(root)).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    private static string SidecarOrigin(string root, string id) =>
        JsonNode.Parse(File.ReadAllText(TrainingFixtures.SidecarPath(root, id)))!["originId"]!.GetValue<string>();

    // Keep-id over the mirror of IdA, with the file's id in the given casing: the copy takes the mirror's casing in the
    // file name, the JSON id and the sidecar origin, and reads server-changed when the mirror changes.
    private static void KeepIdOverMirror(string fileId) =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                Assert.Equal(ScenarioIdConflict.Mirror, store.FindIdConflict(Zoa, fileId));

                ImportResult result = store.ImportScenario(Zoa, TrainingFixtures.WithId(IdA, fileId), ImportIdChoice.KeepId, Ids(20));

                Assert.Equal(ImportOutcome.Imported, result.Outcome);
                Assert.Equal(IdA, result.Id);
                Assert.Equal([IdA + ".json", IdA + ".meta.json"], LocalFileNames(root));
                Assert.Equal(IdA, IdIn(store.ReadWorkingCopyText(Zoa, IdA)));
                Assert.Equal(IdA, SidecarOrigin(root, IdA));
                Assert.False(CopyOf(store, IdA).ServerChanged);

                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Changed(IdA));

                Assert.True(CopyOf(store, IdA).ServerChanged);
            }
        );

    [Fact]
    public void Import_KeepId_ExactMirrorId_UsesItInFileNameJsonAndOrigin() => KeepIdOverMirror(IdA);

    [Fact]
    public void Import_KeepId_MirrorIdInOtherCase_TakesTheMirrorsCasing_ReadsServerChanged() => KeepIdOverMirror(IdA.ToLowerInvariant());

    [Fact]
    public void Import_KeepId_WorkingCopyIdInOtherCase_OverwritesThatCopy_KeepsItsCasing() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                string lower = IdA.ToLowerInvariant();

                ImportResult result = store.ImportScenario(Zoa, TrainingFixtures.WithId(IdA, lower), ImportIdChoice.KeepId, Ids(21));

                Assert.Equal(IdA, result.Id);
                Assert.True(result.Overwrote);
                Assert.Equal([IdA + ".json", IdA + ".meta.json"], LocalFileNames(root));
                Assert.Equal(IdA, IdIn(store.ReadWorkingCopyText(Zoa, IdA)));
                Assert.Equal(IdA, SidecarOrigin(root, IdA));
            }
        );

    [Fact]
    public void Import_KeepId_IdThatCannotNameAFile_IsRejected_WritesNothing() =>
        InRoot(
            (root, store) =>
            {
                ImportResult result = store.ImportScenario(Zoa, TrainingFixtures.WithId(IdB, "a/b"), ImportIdChoice.KeepId, Ids(22));

                Assert.Equal(ImportOutcome.Rejected, result.Outcome);
                Assert.Contains("cannot name a file", result.Reason, StringComparison.Ordinal);
                Assert.False(Directory.Exists(Path.Combine(root, Zoa)));
            }
        );

    [Theory]
    [InlineData("{ broken", "not a scenario")]
    [InlineData(null, "holds scenario")]
    public void Import_KeepId_OverACorruptMirror_IsRejected_WritesNothing(string? mirrorText, string reason) =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, IdA), mirrorText ?? TrainingFixtures.Read(IdB));

                ImportResult result = store.ImportScenario(Zoa, TrainingFixtures.Read(IdA), ImportIdChoice.KeepId, Ids(23));

                Assert.Equal(ImportOutcome.Rejected, result.Outcome);
                Assert.Contains(reason, result.Reason, StringComparison.Ordinal);
                Assert.False(Directory.Exists(LocalDir(root)));
            }
        );

    [Fact]
    public void Import_KeepId_OverAnUnreadableMirror_IsRejected_WritesNothing() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                ImportResult result;
                using (new FileStream(TrainingFixtures.MirrorPath(root, IdA), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    result = store.ImportScenario(Zoa, TrainingFixtures.Read(IdA), ImportIdChoice.KeepId, Ids(24));
                }

                Assert.Equal(ImportOutcome.Rejected, result.Outcome);
                Assert.Contains("unreadable", result.Reason, StringComparison.Ordinal);
                Assert.False(Directory.Exists(LocalDir(root)));
            }
        );

    [Fact]
    public void Import_Skip_FileOfAnotherArtcc_IsSkipped_FlagsTheMismatch() =>
        InRoot(
            (root, store) =>
            {
                JsonObject scenario = JsonNode.Parse(TrainingFixtures.Read(IdB))!.AsObject();
                scenario["artccId"] = "ZLA";

                ImportResult result = store.ImportScenario(Zoa, scenario.ToJsonString(), ImportIdChoice.Skip, Ids(25));

                Assert.Equal(ImportOutcome.Skipped, result.Outcome);
                Assert.True(result.ArtccMismatch);
                Assert.False(Directory.Exists(Path.Combine(root, Zoa)));
            }
        );

    [Fact]
    public void Import_NewId_GeneratorGivesAnExistingCopysId_Throws_WritesNothing() =>
        InRoot(
            (root, store) =>
            {
                store.SaveWorkingCopy(Zoa, LocalId, TrainingFixtures.WithId(IdB, LocalId));
                byte[] before = File.ReadAllBytes(TrainingFixtures.LocalPath(root, LocalId));

                Assert.Throws<InvalidOperationException>(() =>
                    store.ImportScenario(Zoa, TrainingFixtures.Read(IdA), ImportIdChoice.NewId, () => LocalId)
                );

                Assert.Equal(before, File.ReadAllBytes(TrainingFixtures.LocalPath(root, LocalId)));
                Assert.Equal([LocalId + ".json", LocalId + ".meta.json"], LocalFileNames(root));
            }
        );

    [Fact]
    public void Import_ChoiceOutOfRange_Throws_WritesNothing() =>
        InRoot(
            (root, store) =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    store.ImportScenario(Zoa, TrainingFixtures.WithId(IdB, "a/b"), (ImportIdChoice)3, Ids(26))
                );

                Assert.False(Directory.Exists(Path.Combine(root, Zoa)));
            }
        );

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"id":"x"}""")]
    public void CreateLocalScenario_NotAScenario_ThrowsArgumentException_WritesNothing(string json) =>
        InRoot(
            (root, store) =>
            {
                Assert.Throws<ArgumentException>(() => store.CreateLocalScenario(Zoa, json, LocalId));

                Assert.False(Directory.Exists(Path.Combine(root, Zoa)));
            }
        );

    [Fact]
    public void Delete_ReturnsWhetherAnythingWasDeleted() =>
        InRoot(
            (root, store) =>
            {
                store.SaveWorkingCopy(Zoa, LocalId, TrainingFixtures.WithId(IdB, LocalId));

                Assert.True(store.DeleteWorkingCopy(Zoa, LocalId));
                Assert.False(store.DeleteWorkingCopy(Zoa, LocalId));
                Assert.False(store.DeleteWorkingCopy(Zoa, "NEVER-1"));
            }
        );

    [Fact]
    public void Duplicate_OfMissingSource_Throws_WritesNothing() =>
        InRoot(
            (root, store) =>
            {
                Assert.Throws<InvalidOperationException>(() => store.DuplicateWorkingCopy(Zoa, LocalId, "LOCAL_ONLY-2"));

                Assert.False(Directory.Exists(Path.Combine(root, Zoa)));
            }
        );
}
