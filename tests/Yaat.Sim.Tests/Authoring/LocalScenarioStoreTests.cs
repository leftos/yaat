using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="LocalScenarioStore"/> keeps an ARTCC's server mirror, working copies and index on disk. These pin the
/// refresh case table: a refresh never deletes or overwrites a working copy, a semantic server change replaces the
/// mirror and marks the copies, and every corrupt file is reported by name.
/// </summary>
public class LocalScenarioStoreTests
{
    private const string Zoa = TrainingFixtures.Artcc;
    private const string IdA = TrainingFixtures.IdA;
    private const string IdB = TrainingFixtures.IdB;

    private static readonly DateTime OldStamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

    [Fact]
    public void Apply_NewServerScenario_WritesMirror_AddsIndexEntry_Downloaded() =>
        InRoot(
            (root, store) =>
            {
                string body = TrainingFixtures.Read(IdA);

                ServerApplyResult result = store.ApplyServerBody(Zoa, IdA, body);

                Assert.Equal(ServerApplyOutcome.Added, result.Outcome);
                Assert.Equal(body, File.ReadAllText(TrainingFixtures.MirrorPath(root, IdA)));
                Assert.True(File.Exists(TrainingFixtures.IndexPath(root)));
                MirrorEntry entry = Assert.Single(store.Load(Zoa).Mirrors).Mirror;
                Assert.Equal(IdA, entry.Id);
                Assert.Equal(TrainingFixtures.NameOf(IdA), entry.Name);
                Assert.Equal(LocalScenarioStore.ComputeVersion(body), entry.Version);
                Assert.False(entry.GoneFromServer);
            }
        );

    [Fact]
    public void Apply_SameServerScenario_NoWorkingCopy_LeavesMirrorUntouched_Unchanged() =>
        InRoot(
            (root, store) =>
            {
                string mirror = TrainingFixtures.MirrorPath(root, IdA);
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.SetLastWriteTimeUtc(mirror, OldStamp);

                ServerApplyResult result = store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));

                Assert.Equal(ServerApplyOutcome.Unchanged, result.Outcome);
                Assert.Equal(OldStamp, File.GetLastWriteTimeUtc(mirror));
                Assert.Empty(store.Load(Zoa).WorkingCopies);
            }
        );

    [Fact]
    public void Apply_SameServerScenario_WithWorkingCopy_LeavesBothUntouched_Unchanged() =>
        InRoot(
            (root, store) =>
            {
                string mirror = TrainingFixtures.MirrorPath(root, IdA);
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                File.SetLastWriteTimeUtc(mirror, OldStamp);
                byte[] copyBefore = File.ReadAllBytes(TrainingFixtures.LocalPath(root, IdA));

                ServerApplyResult result = store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));

                Assert.Equal(ServerApplyOutcome.Unchanged, result.Outcome);
                Assert.Equal(OldStamp, File.GetLastWriteTimeUtc(mirror));
                Assert.Equal(copyBefore, File.ReadAllBytes(TrainingFixtures.LocalPath(root, IdA)));
                WorkingCopyEntry copy = Assert.Single(store.Load(Zoa).WorkingCopies);
                Assert.Equal(IdA, copy.OriginId);
                Assert.False(copy.ServerChanged);
                Assert.False(copy.Modified);
            }
        );

    [Fact]
    public void Apply_ChangedServerScenario_NoWorkingCopy_ReplacesMirror_NewBaseVersion_Updated() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                string changed = TrainingFixtures.Changed(IdA);

                ServerApplyResult result = store.ApplyServerBody(Zoa, IdA, changed);

                Assert.Equal(ServerApplyOutcome.Updated, result.Outcome);
                Assert.Equal(changed, File.ReadAllText(TrainingFixtures.MirrorPath(root, IdA)));
                MirrorEntry entry = Assert.Single(store.Load(Zoa).Mirrors).Mirror;
                Assert.Equal(LocalScenarioStore.ComputeVersion(changed), entry.Version);
                Assert.NotEqual(LocalScenarioStore.ComputeVersion(TrainingFixtures.Read(IdA)), entry.Version);
                Assert.Empty(Directory.GetFiles(Path.Combine(root, Zoa, "server"), "*.tmp"));
            }
        );

    [Fact]
    public void Apply_ChangedServerScenario_UneditedWorkingCopy_LeavesCopyByteForByte_MarksServerChanged() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                byte[] copyBefore = File.ReadAllBytes(TrainingFixtures.LocalPath(root, IdA));
                byte[] sidecarBefore = File.ReadAllBytes(TrainingFixtures.SidecarPath(root, IdA));
                string changed = TrainingFixtures.Changed(IdA);

                ServerApplyResult result = store.ApplyServerBody(Zoa, IdA, changed);

                Assert.Equal(ServerApplyOutcome.ServerChanged, result.Outcome);
                Assert.Equal(changed, File.ReadAllText(TrainingFixtures.MirrorPath(root, IdA)));
                Assert.Equal(copyBefore, File.ReadAllBytes(TrainingFixtures.LocalPath(root, IdA)));
                Assert.Equal(sidecarBefore, File.ReadAllBytes(TrainingFixtures.SidecarPath(root, IdA)));
                WorkingCopyEntry copy = Assert.Single(store.Load(Zoa).WorkingCopies);
                Assert.True(copy.ServerChanged);
                Assert.False(copy.Modified);
            }
        );

    [Fact]
    public void Apply_ChangedServerScenario_EditedWorkingCopy_KeepsModified_MarksServerChanged() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                store.SaveWorkingCopy(Zoa, IdA, TrainingFixtures.WithNameSuffix(IdA, " (my edit)"));
                Assert.True(Assert.Single(store.Load(Zoa).WorkingCopies).Modified);
                byte[] copyBefore = File.ReadAllBytes(TrainingFixtures.LocalPath(root, IdA));

                ServerApplyResult result = store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Changed(IdA));

                Assert.Equal(ServerApplyOutcome.ServerChanged, result.Outcome);
                Assert.Equal(copyBefore, File.ReadAllBytes(TrainingFixtures.LocalPath(root, IdA)));
                WorkingCopyEntry copy = Assert.Single(store.Load(Zoa).WorkingCopies);
                Assert.True(copy.ServerChanged);
                Assert.True(copy.Modified);
            }
        );

    [Fact]
    public void MarkGone_NoWorkingCopy_KeepsMirror_FlagsGone_Missing() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.ApplyServerBody(Zoa, IdB, TrainingFixtures.Read(IdB));

                MarkGoneResult result = store.MarkGoneFromServer(Zoa, [IdB]);

                Assert.Equal([IdA], result.Missing);
                Assert.Equal(TrainingFixtures.Read(IdA), File.ReadAllText(TrainingFixtures.MirrorPath(root, IdA)));
                ArtccStoreListing listing = store.Load(Zoa);
                Assert.True(listing.Mirrors.Single(m => m.Mirror.Id == IdA).Mirror.GoneFromServer);
                Assert.False(listing.Mirrors.Single(m => m.Mirror.Id == IdB).Mirror.GoneFromServer);
            }
        );

    [Fact]
    public void MarkGone_WithWorkingCopy_KeepsBoth_FlagsGone_Missing() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                byte[] copyBefore = File.ReadAllBytes(TrainingFixtures.LocalPath(root, IdA));

                MarkGoneResult result = store.MarkGoneFromServer(Zoa, []);

                Assert.Equal([IdA], result.Missing);
                Assert.True(File.Exists(TrainingFixtures.MirrorPath(root, IdA)));
                Assert.Equal(copyBefore, File.ReadAllBytes(TrainingFixtures.LocalPath(root, IdA)));
                ArtccStoreListing listing = store.Load(Zoa);
                Assert.True(Assert.Single(listing.Mirrors).Mirror.GoneFromServer);
                Assert.Single(listing.WorkingCopies);
            }
        );

    [Fact]
    public void Refresh_LeavesLocalOnlyWorkingCopyUntouched() =>
        InRoot(
            (root, store) =>
            {
                const string LocalId = "LOCAL_ONLY-1";
                store.SaveWorkingCopy(Zoa, LocalId, TrainingFixtures.WithId(IdB, LocalId));
                byte[] copyBefore = File.ReadAllBytes(TrainingFixtures.LocalPath(root, LocalId));
                byte[] sidecarBefore = File.ReadAllBytes(TrainingFixtures.SidecarPath(root, LocalId));

                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Changed(IdA));
                store.MarkGoneFromServer(Zoa, []);

                Assert.Equal(copyBefore, File.ReadAllBytes(TrainingFixtures.LocalPath(root, LocalId)));
                Assert.Equal(sidecarBefore, File.ReadAllBytes(TrainingFixtures.SidecarPath(root, LocalId)));
                WorkingCopyEntry copy = Assert.Single(store.Load(Zoa).WorkingCopies);
                Assert.Equal(LocalId, copy.Id);
                Assert.Null(copy.OriginId);
                Assert.False(copy.ServerChanged);
                Assert.False(copy.Corrupt);
            }
        );

    [Fact]
    public void Load_IndexMissing_RebuildsFromFilesAndSidecars_ReportsRebuilt() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.ApplyServerBody(Zoa, IdB, TrainingFixtures.Read(IdB));
                store.CreateWorkingCopy(Zoa, IdA);
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Changed(IdA));
                File.Delete(TrainingFixtures.IndexPath(root));

                ArtccStoreListing listing = store.Load(Zoa);

                Assert.True(listing.IndexRebuilt);
                Assert.Contains(listing.Problems, p => p.FileName == "index.json");
                Assert.True(File.Exists(TrainingFixtures.IndexPath(root)));
                Assert.Equal([IdA, IdB], listing.Mirrors.Select(m => m.Mirror.Id));
                Assert.Equal(LocalScenarioStore.ComputeVersion(TrainingFixtures.Changed(IdA)), listing.Mirrors[0].Mirror.Version);
                WorkingCopyEntry copy = Assert.Single(listing.WorkingCopies);
                Assert.True(copy.ServerChanged);
                Assert.False(copy.Modified);
            }
        );

    [Fact]
    public void Load_IndexCorrupt_KeepsItAsCorruptCopy_Rebuilds_ReportsByName() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.WriteAllText(TrainingFixtures.IndexPath(root), "{ not json");

                ArtccStoreListing listing = store.Load(Zoa);

                Assert.True(listing.IndexRebuilt);
                Assert.Contains(listing.Problems, p => p.FileName == "index.json");
                string kept = Assert.Single(Directory.GetFiles(Path.Combine(root, Zoa), "index.json.corrupt-*"));
                Assert.Equal("{ not json", File.ReadAllText(kept));
                Assert.Equal(IdA, Assert.Single(listing.Mirrors).Mirror.Id);
                Assert.False(store.Load(Zoa).IndexRebuilt);
            }
        );

    [Fact]
    public void Apply_CorruptMirror_IsReplacedByFetchedBody_ReportsServerFile() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, IdA), "{ broken");

                ServerApplyResult result = store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));

                Assert.Equal(ServerApplyOutcome.Updated, result.Outcome);
                Assert.Contains(result.Problems, p => p.FileName == $"server/{IdA}.json");
                Assert.Equal(TrainingFixtures.Read(IdA), File.ReadAllText(TrainingFixtures.MirrorPath(root, IdA)));
            }
        );

    [Fact]
    public void Load_CorruptWorkingCopy_IsListedCorrupt_NeverRewrittenOrDeleted() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                string copyPath = TrainingFixtures.LocalPath(root, IdA);
                File.WriteAllText(copyPath, "[1, 2]");

                ArtccStoreListing listing = store.Load(Zoa);
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Changed(IdA));
                store.MarkGoneFromServer(Zoa, []);
                ArtccStoreListing after = store.Load(Zoa);

                Assert.True(Assert.Single(listing.WorkingCopies).Corrupt);
                Assert.Contains(listing.Problems, p => p.FileName == $"local/{IdA}.json");
                Assert.Equal("[1, 2]", File.ReadAllText(copyPath));
                Assert.True(Assert.Single(after.WorkingCopies).Corrupt);
            }
        );

    [Fact]
    public void WorkingCopy_SemanticallyEqualServerReorder_DoesNotMarkChanged() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);

                ServerApplyResult result = store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Reordered(IdA));

                Assert.Equal(ServerApplyOutcome.Unchanged, result.Outcome);
                Assert.Equal(TrainingFixtures.Read(IdA), File.ReadAllText(TrainingFixtures.MirrorPath(root, IdA)));
                WorkingCopyEntry copy = Assert.Single(store.Load(Zoa).WorkingCopies);
                Assert.False(copy.ServerChanged);
                Assert.False(copy.Modified);
            }
        );

    [Fact]
    public void Load_IndexWithNullEntry_IsRebuilt_ReportsTheIndex() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.WriteAllText(TrainingFixtures.IndexPath(root), """{"format":1,"mirrors":[null]}""");

                ArtccStoreListing listing = store.Load(Zoa);

                Assert.True(listing.IndexRebuilt);
                Assert.Contains(listing.Problems, p => p.FileName == "index.json");
                Assert.Equal(IdA, Assert.Single(listing.Mirrors).Mirror.Id);
            }
        );

    [Fact]
    public void Load_ChecksMirrorFilesAgainstTheIndex_ReportsEachMismatch() =>
        InRoot(
            (root, store) =>
            {
                string idC = IdA + "-C";
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.ApplyServerBody(Zoa, IdB, TrainingFixtures.Read(IdB));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, IdA), "{ broken");
                File.Delete(TrainingFixtures.MirrorPath(root, IdB));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, idC), TrainingFixtures.WithId(IdA, idC));

                ArtccStoreListing listing = store.Load(Zoa);

                Assert.False(listing.IndexRebuilt);
                Assert.Contains(listing.Problems, p => p.FileName == $"server/{IdA}.json");
                Assert.Contains(listing.Problems, p => p.FileName == $"server/{IdB}.json");
                Assert.Contains(listing.Problems, p => p.FileName == $"server/{idC}.json");
                Assert.True(listing.Mirrors.Single(m => m.Mirror.Id == IdA).Corrupt);
                Assert.True(listing.Mirrors.Single(m => m.Mirror.Id == IdB).Corrupt);
                Assert.False(listing.Mirrors.Single(m => m.Mirror.Id == idC).Corrupt);
                ArtccStoreListing again = store.Load(Zoa);
                Assert.DoesNotContain(again.Problems, p => p.FileName == $"server/{idC}.json");
                Assert.Contains(again.Mirrors, m => m.Mirror.Id == idC);
            }
        );

    [Fact]
    public void Apply_CorruptMirror_RefetchedAtTheCopysBase_IsUpdated_NotServerChanged() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                File.WriteAllText(TrainingFixtures.MirrorPath(root, IdA), "{ broken");

                ServerApplyResult result = store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));

                Assert.Equal(ServerApplyOutcome.Updated, result.Outcome);
                Assert.False(Assert.Single(store.Load(Zoa).WorkingCopies).ServerChanged);
            }
        );

    [Fact]
    public void Apply_ChangedServerScenario_OrphanSidecar_IsAProblem_NotAWorkingCopy() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                File.Delete(TrainingFixtures.LocalPath(root, IdA));

                ServerApplyResult result = store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Changed(IdA));
                ArtccStoreListing listing = store.Load(Zoa);

                Assert.Equal(ServerApplyOutcome.Updated, result.Outcome);
                Assert.Contains(result.Problems, p => p.FileName == $"local/{IdA}.meta.json");
                Assert.Empty(listing.WorkingCopies);
                Assert.Contains(listing.Problems, p => p.FileName == $"local/{IdA}.meta.json");
            }
        );

    [Fact]
    public void Load_CorruptMirror_TheIndexKeepsNoCorruptFlag() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, IdA), "{ broken");

                ArtccStoreListing listing = store.Load(Zoa);

                Assert.True(Assert.Single(listing.Mirrors).Corrupt);
                Assert.DoesNotContain("\"corrupt\"", File.ReadAllText(TrainingFixtures.IndexPath(root)), StringComparison.OrdinalIgnoreCase);
            }
        );

    [Fact]
    public void Load_IndexVersionBehindTheMirrorText_IsCorrected() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                string changed = TrainingFixtures.Changed(IdA);

                // As a crash between the mirror write and the index save leaves it.
                File.WriteAllText(TrainingFixtures.MirrorPath(root, IdA), changed);

                ArtccStoreListing listing = store.Load(Zoa);

                string expected = LocalScenarioStore.ComputeVersion(changed);
                Assert.Empty(listing.Problems);
                Assert.Equal(expected, Assert.Single(listing.Mirrors).Mirror.Version);
                Assert.Contains(expected, File.ReadAllText(TrainingFixtures.IndexPath(root)), StringComparison.Ordinal);
            }
        );

    [Fact]
    public void Load_MirrorHoldingAnotherScenario_IsReportedAndListedCorrupt() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, IdA), TrainingFixtures.Read(IdB));

                ArtccStoreListing listing = store.Load(Zoa);

                StoreProblem problem = Assert.Single(listing.Problems, p => p.FileName == $"server/{IdA}.json");
                Assert.Equal($"holds scenario {IdB}, not {IdA}", problem.Reason);
                Assert.True(Assert.Single(listing.Mirrors).Corrupt);
            }
        );

    [Fact]
    public void Load_LockedMirror_IsReportedByName_TheRestListed() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.ApplyServerBody(Zoa, IdB, TrainingFixtures.Read(IdB));
                ArtccStoreListing listing;
                using (new FileStream(TrainingFixtures.MirrorPath(root, IdA), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    listing = store.Load(Zoa);
                }

                Assert.Contains(listing.Problems, p => p.FileName == $"server/{IdA}.json");
                Assert.True(listing.Mirrors.Single(m => m.Mirror.Id == IdA).Corrupt);
                Assert.False(listing.Mirrors.Single(m => m.Mirror.Id == IdB).Corrupt);
            }
        );

    [Theory]
    [InlineData("250.0", "250")]
    [InlineData("250.0", "2.5e2")]
    [InlineData("250.0", "250.000")]
    [InlineData("-0", "0")]
    [InlineData("0", "-0.0")]
    public void WorkingCopy_SameNumberSpelledOtherwise_IsNotModified(string serverText, string copyText) =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.WithAltitudeText(IdA, serverText));
                store.CreateWorkingCopy(Zoa, IdA);

                store.SaveWorkingCopy(Zoa, IdA, TrainingFixtures.WithAltitudeText(IdA, copyText));

                Assert.False(Assert.Single(store.Load(Zoa).WorkingCopies).Modified);
            }
        );

    [Fact]
    public void WorkingCopy_OtherNumberValue_IsModified() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.WithAltitudeText(IdA, "250.0"));
                store.CreateWorkingCopy(Zoa, IdA);

                store.SaveWorkingCopy(Zoa, IdA, TrainingFixtures.WithAltitudeText(IdA, "250.5"));

                Assert.True(Assert.Single(store.Load(Zoa).WorkingCopies).Modified);
            }
        );

    [Theory]
    [InlineData("CON")]
    [InlineData("prn")]
    [InlineData("Aux")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    public void IsValidId_RefusesWindowsReservedDeviceNames(string id) => Assert.False(LocalScenarioStore.IsValidId(id));

    [Theory]
    [InlineData("CONX")]
    [InlineData("COM10")]
    [InlineData("ZOA")]
    public void IsValidId_AcceptsNamesThatOnlyLookReserved(string id) => Assert.True(LocalScenarioStore.IsValidId(id));

    [Fact]
    public void Load_UnindexedMirrorHoldingAnotherScenario_IsReported_NotListed_NotAdded() =>
        InRoot(
            (root, store) =>
            {
                string idC = IdA + "-C";
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, idC), TrainingFixtures.Read(IdA));
                string indexBefore = File.ReadAllText(TrainingFixtures.IndexPath(root));
                string reason = $"not in the index; holds scenario {IdA}, not {idC}";

                ArtccStoreListing listing = store.Load(Zoa);

                Assert.False(listing.IndexRebuilt);
                Assert.Equal(reason, Assert.Single(listing.Problems, p => p.FileName == $"server/{idC}.json").Reason);
                Assert.DoesNotContain(listing.Mirrors, m => m.Mirror.Id == idC);
                Assert.Equal(indexBefore, File.ReadAllText(TrainingFixtures.IndexPath(root)));
                Assert.Equal(reason, Assert.Single(store.Load(Zoa).Problems, p => p.FileName == $"server/{idC}.json").Reason);
            }
        );

    [Fact]
    public void Load_UnindexedMalformedMirror_IsReported_NotListed() =>
        InRoot(
            (root, store) =>
            {
                string idC = IdA + "-C";
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, idC), "{ broken");
                string indexBefore = File.ReadAllText(TrainingFixtures.IndexPath(root));

                ArtccStoreListing listing = store.Load(Zoa);

                StoreProblem problem = Assert.Single(listing.Problems, p => p.FileName == $"server/{idC}.json");
                Assert.StartsWith("not in the index; not a scenario: malformed JSON (", problem.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain(listing.Mirrors, m => m.Mirror.Id == idC);
                Assert.Equal(indexBefore, File.ReadAllText(TrainingFixtures.IndexPath(root)));
            }
        );

    [Fact]
    public void Load_UnindexedMirrorWithNoName_IsReported_NotListed() =>
        InRoot(
            (root, store) =>
            {
                string idC = IdA + "-C";
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, idC), "{\"id\":\"" + idC + "\"}");

                ArtccStoreListing listing = store.Load(Zoa);

                Assert.Equal(
                    "not in the index; not a scenario: it has no id or no name",
                    Assert.Single(listing.Problems, p => p.FileName == $"server/{idC}.json").Reason
                );
                Assert.DoesNotContain(listing.Mirrors, m => m.Mirror.Id == idC);
            }
        );

    [Fact]
    public void Load_UnindexedLockedMirror_IsReported_ThenAddedOnceUnlocked() =>
        InRoot(
            (root, store) =>
            {
                string idC = IdA + "-C";
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, idC), TrainingFixtures.WithId(IdA, idC));
                ArtccStoreListing listing;
                using (new FileStream(TrainingFixtures.MirrorPath(root, idC), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    listing = store.Load(Zoa);
                }

                Assert.StartsWith(
                    "not in the index; unreadable: ",
                    Assert.Single(listing.Problems, p => p.FileName == $"server/{idC}.json").Reason,
                    StringComparison.Ordinal
                );
                Assert.DoesNotContain(listing.Mirrors, m => m.Mirror.Id == idC);

                ArtccStoreListing after = store.Load(Zoa);

                Assert.Equal("not in the index; added to it", Assert.Single(after.Problems, p => p.FileName == $"server/{idC}.json").Reason);
                Assert.Contains(after.Mirrors, m => m.Mirror.Id == idC);
            }
        );

    [Theory]
    [InlineData("bad name.json")]
    public void Load_UnindexedMirrorWithAnInvalidFileName_IsReported_NotRead(string fileName) =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.WriteAllText(Path.Combine(root, Zoa, "server", fileName), "{ broken");

                ArtccStoreListing listing = store.Load(Zoa);

                Assert.Equal(
                    "not in the index; the file name is not a valid scenario id",
                    Assert.Single(listing.Problems, p => p.FileName == $"server/{fileName}").Reason
                );
                Assert.DoesNotContain(listing.Mirrors, m => m.Mirror.Id == Path.GetFileNameWithoutExtension(fileName));
            }
        );

    [Fact]
    public void Load_IndexMissing_RebuildReportsBadMirrorsWithoutTheNotInIndexPrefix() =>
        InRoot(
            (root, store) =>
            {
                string idC = IdA + "-C";
                string idD = IdA + "-D";
                string idE = IdA + "-E";
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                File.Delete(TrainingFixtures.IndexPath(root));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, idC), TrainingFixtures.Read(IdA));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, idD), TrainingFixtures.WithId(IdA, idD));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, idE), "{ broken");
                ArtccStoreListing listing;
                using (new FileStream(TrainingFixtures.MirrorPath(root, idD), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    listing = store.Load(Zoa);
                }

                Assert.True(listing.IndexRebuilt);
                Assert.Equal($"holds scenario {IdA}, not {idC}", Assert.Single(listing.Problems, p => p.FileName == $"server/{idC}.json").Reason);
                Assert.StartsWith(
                    "unreadable: ",
                    Assert.Single(listing.Problems, p => p.FileName == $"server/{idD}.json").Reason,
                    StringComparison.Ordinal
                );
                Assert.StartsWith(
                    "not a scenario: malformed JSON (",
                    Assert.Single(listing.Problems, p => p.FileName == $"server/{idE}.json").Reason,
                    StringComparison.Ordinal
                );
                Assert.DoesNotContain(listing.Problems, p => p.Reason.Contains("not in the index", StringComparison.Ordinal));
                Assert.DoesNotContain(listing.Problems, p => p.Reason.Contains("added to it", StringComparison.Ordinal));
                Assert.Equal([IdA], listing.Mirrors.Select(m => m.Mirror.Id));
                Assert.DoesNotContain(idC, File.ReadAllText(TrainingFixtures.IndexPath(root)), StringComparison.Ordinal);
            }
        );

    [Fact]
    public void Load_UnindexedMismatchBesideAGoodIndexedMirror_LeavesTheGoodOneAlone() =>
        InRoot(
            (root, store) =>
            {
                string idC = IdA + "-C";
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.ApplyServerBody(Zoa, IdB, TrainingFixtures.Read(IdB));
                File.WriteAllText(TrainingFixtures.MirrorPath(root, idC), TrainingFixtures.Read(IdA));
                string indexBefore = File.ReadAllText(TrainingFixtures.IndexPath(root));

                ArtccStoreListing listing = store.Load(Zoa);

                Assert.False(listing.IndexRebuilt);
                Assert.Contains(listing.Problems, p => p.FileName == $"server/{idC}.json");
                Assert.Equal([IdA, IdB], listing.Mirrors.Select(m => m.Mirror.Id));
                Assert.All(listing.Mirrors, m => Assert.False(m.Corrupt));
                Assert.Equal(indexBefore, File.ReadAllText(TrainingFixtures.IndexPath(root)));
            }
        );

    [Fact]
    public void Store_RefusesRootUnderCache()
    {
        string underCache = YaatPaths.Combine("cache", "scenarios");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => new LocalScenarioStore(underCache));

        Assert.Contains(Path.GetFullPath(underCache), ex.Message, StringComparison.Ordinal);
    }

    private static readonly DateTimeOffset ExportedAt = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordExport_ClearsUnexportedChanges_ALaterDifferentSaveSetsIt() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                store.SaveWorkingCopy(Zoa, IdA, TrainingFixtures.WithNameSuffix(IdA, " (my edit)"));
                WorkingCopyEntry edited = Assert.Single(store.Load(Zoa).WorkingCopies);
                Assert.True(edited.UnexportedChanges);
                Assert.Null(edited.LastExport);

                store.RecordExport(Zoa, IdA, ExportedAt);

                WorkingCopyEntry exported = Assert.Single(store.Load(Zoa).WorkingCopies);
                Assert.False(exported.UnexportedChanges);
                Assert.Equal(ExportedAt, exported.LastExport);
                Assert.True(exported.Modified);

                store.SaveWorkingCopy(Zoa, IdA, TrainingFixtures.Reorder(TrainingFixtures.WithNameSuffix(IdA, " (my edit)")));
                Assert.False(Assert.Single(store.Load(Zoa).WorkingCopies).UnexportedChanges);

                store.SaveWorkingCopy(Zoa, IdA, TrainingFixtures.WithNameSuffix(IdA, " (second edit)"));
                WorkingCopyEntry again = Assert.Single(store.Load(Zoa).WorkingCopies);
                Assert.True(again.UnexportedChanges);
                Assert.Equal(ExportedAt, again.LastExport);
            }
        );

    [Fact]
    public void Load_NeverExportedLocalOnlyCopy_HasUnexportedChanges() =>
        InRoot(
            (root, store) =>
            {
                store.SaveWorkingCopy(Zoa, IdB, TrainingFixtures.Read(IdB));

                WorkingCopyEntry copy = Assert.Single(store.Load(Zoa).WorkingCopies);

                Assert.Null(copy.OriginId);
                Assert.False(copy.Modified);
                Assert.True(copy.UnexportedChanges);
                Assert.Null(copy.LastExport);
            }
        );

    [Fact]
    public void Load_UnmodifiedServerCopy_HasNoUnexportedChanges() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);

                WorkingCopyEntry copy = Assert.Single(store.Load(Zoa).WorkingCopies);

                Assert.False(copy.UnexportedChanges);
                Assert.Null(copy.LastExport);
            }
        );

    [Fact]
    public void Load_CorruptSidecar_ReadsAsNeverExported() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                store.RecordExport(Zoa, IdA, ExportedAt);
                File.WriteAllText(TrainingFixtures.SidecarPath(root, IdA), "{ not json");

                ArtccStoreListing listing = store.Load(Zoa);

                WorkingCopyEntry copy = Assert.Single(listing.WorkingCopies);
                Assert.True(copy.UnexportedChanges);
                Assert.Null(copy.LastExport);
                Assert.Contains(listing.Problems, p => p.FileName == $"local/{IdA}.meta.json");
            }
        );

    [Fact]
    public void RecordExport_MissingCopy_Throws() =>
        InRoot(
            (root, store) =>
            {
                Assert.Throws<InvalidOperationException>(() => store.RecordExport(Zoa, IdA, ExportedAt));

                Assert.False(File.Exists(TrainingFixtures.SidecarPath(root, IdA)));
            }
        );

    [Fact]
    public void RecordExport_CorruptCopy_Throws_LeavesTheSidecar() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                File.WriteAllText(TrainingFixtures.LocalPath(root, IdA), "{ not json");
                byte[] sidecarBefore = File.ReadAllBytes(TrainingFixtures.SidecarPath(root, IdA));

                Assert.Throws<InvalidOperationException>(() => store.RecordExport(Zoa, IdA, ExportedAt));

                Assert.Equal(sidecarBefore, File.ReadAllBytes(TrainingFixtures.SidecarPath(root, IdA)));
            }
        );

    [Fact]
    public void RecordExport_CorruptSidecar_Throws_LeavesItByteForByte() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                File.WriteAllText(TrainingFixtures.SidecarPath(root, IdA), "{ not json");
                byte[] sidecarBefore = File.ReadAllBytes(TrainingFixtures.SidecarPath(root, IdA));

                Assert.Throws<InvalidOperationException>(() => store.RecordExport(Zoa, IdA, ExportedAt));

                Assert.Equal(sidecarBefore, File.ReadAllBytes(TrainingFixtures.SidecarPath(root, IdA)));
            }
        );

    [Fact]
    public void RecordExport_MissingSidecar_WritesALocalOnlyOneWithTheExport() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                File.Delete(TrainingFixtures.SidecarPath(root, IdA));

                store.RecordExport(Zoa, IdA, ExportedAt);

                Assert.True(File.Exists(TrainingFixtures.SidecarPath(root, IdA)));
                WorkingCopyEntry copy = Assert.Single(store.Load(Zoa).WorkingCopies);
                Assert.Null(copy.OriginId);
                Assert.Null(copy.BaseVersion);
                Assert.Equal(ExportedAt, copy.LastExport);
                Assert.False(copy.UnexportedChanges);
            }
        );

    [Fact]
    public void RecordExport_LeavesOriginAndBaseUnchanged() =>
        InRoot(
            (root, store) =>
            {
                store.ApplyServerBody(Zoa, IdA, TrainingFixtures.Read(IdA));
                store.CreateWorkingCopy(Zoa, IdA);
                store.SaveWorkingCopy(Zoa, IdA, TrainingFixtures.WithNameSuffix(IdA, " (my edit)"));
                JsonObject before = JsonNode.Parse(File.ReadAllText(TrainingFixtures.SidecarPath(root, IdA)))!.AsObject();

                store.RecordExport(Zoa, IdA, ExportedAt);

                JsonObject after = JsonNode.Parse(File.ReadAllText(TrainingFixtures.SidecarPath(root, IdA)))!.AsObject();
                Assert.True(JsonNode.DeepEquals(before["originId"], after["originId"]));
                Assert.True(JsonNode.DeepEquals(before["baseVersion"], after["baseVersion"]));
                Assert.True(JsonNode.DeepEquals(before["baseContentHash"], after["baseContentHash"]));

                Assert.NotNull(after["lastExportHash"]);
                Assert.NotNull(after["lastExport"]);
                WorkingCopyEntry copy = Assert.Single(store.Load(Zoa).WorkingCopies);
                Assert.Equal(IdA, copy.OriginId);
                Assert.True(copy.Modified);
                Assert.False(copy.ServerChanged);
            }
        );
}
