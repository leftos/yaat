using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The feed an id names, and the provenance and feed rows a folder corpus's segments pool into.
/// </summary>
public sealed class CorpusWerGroupsTests
{
    [Theory]
    [InlineData("koak-app_seg0004", "koak-app")]
    [InlineData("ksfo-gnd_seg0012", "ksfo-gnd")]
    [InlineData("abc_seg1", "abc")]
    [InlineData("sm1_01_PI", null)]
    [InlineData("koak-app_seg", null)]
    [InlineData("KOAK-APP_seg0004", null)]
    [InlineData("koak-app_seg0004_x", null)]
    public void FeedFromId_Reads_The_Feed_Only_From_A_Segment_Id(string id, string? expected) => Assert.Equal(expected, CorpusRoles.FeedFromId(id));

    [Fact]
    public void ProvenanceRows_Is_Null_When_No_Segment_Carries_A_Provenance() =>
        Assert.Null(CorpusWerGroups.ProvenanceRows([(Segment("a_seg0001", feed: null, provenance: null), Counts(1, 2, 3, 40))]));

    [Fact]
    public void ProvenanceRows_Pools_Owner_And_Owner_Plus_Agreed_And_Leaves_Claude_Out()
    {
        IReadOnlyList<CorpusWerRoleTotals>? nullableRows = CorpusWerGroups.ProvenanceRows([
            (Segment("a_seg0001", feed: null, provenance: "owner"), Counts(1, 0, 0, 10)),
            (Segment("b_seg0001", feed: null, provenance: "agreed"), Counts(0, 2, 0, 20)),
            (Segment("c_seg0001", feed: null, provenance: "claude"), Counts(5, 5, 5, 50)),
        ]);

        IReadOnlyList<CorpusWerRoleTotals> rows = Assert.IsAssignableFrom<IReadOnlyList<CorpusWerRoleTotals>>(nullableRows);
        Assert.Equal(2, rows.Count);
        Assert.Equal("owner", rows[0].Role);
        Assert.Equal(1, rows[0].Segments);
        Assert.Equal(10, rows[0].ReferenceWords);
        Assert.Equal(1, rows[0].Substitutions);
        Assert.Equal(0.1, rows[0].Wer, 6);
        Assert.Equal("owner+agreed", rows[1].Role);
        Assert.Equal(2, rows[1].Segments);
        Assert.Equal(30, rows[1].ReferenceWords);
        Assert.Equal(2, rows[1].Deletions);
        Assert.Equal(0.1, rows[1].Wer, 6);
    }

    [Fact]
    public void ProvenanceRows_Emits_A_Zero_Segment_Owner_Row_When_No_Segment_Is_Owned()
    {
        IReadOnlyList<CorpusWerRoleTotals>? nullableRows = CorpusWerGroups.ProvenanceRows([
            (Segment("a_seg0001", feed: null, provenance: "agreed"), Counts(0, 1, 0, 10)),
            (Segment("b_seg0001", feed: null, provenance: "claude"), Counts(0, 0, 0, 10)),
        ]);

        IReadOnlyList<CorpusWerRoleTotals> rows = Assert.IsAssignableFrom<IReadOnlyList<CorpusWerRoleTotals>>(nullableRows);
        Assert.Equal(2, rows.Count);
        Assert.Equal("owner", rows[0].Role);
        Assert.Equal(0, rows[0].Segments);
        Assert.Equal(0, rows[0].ReferenceWords);
        Assert.Equal(0, rows[0].Wer);
        Assert.Equal(1, rows[1].Segments);
    }

    [Fact]
    public void FeedRows_Is_Null_When_No_Segment_Carries_A_Feed() =>
        Assert.Null(CorpusWerGroups.FeedRows([(Segment("a_seg0001", feed: null, provenance: null), Counts(1, 2, 3, 40))]));

    [Fact]
    public void FeedRows_Orders_The_Feeds_Ordinally()
    {
        IReadOnlyList<CorpusWerRoleTotals>? nullableRows = CorpusWerGroups.FeedRows([
            (Segment("ksfo-gnd_seg0001", feed: "ksfo-gnd", provenance: null), Counts(0, 1, 0, 10)),
            (Segment("koak-app_seg0001", feed: "koak-app", provenance: null), Counts(1, 0, 0, 10)),
            (Segment("koak-app_seg0002", feed: "koak-app", provenance: null), Counts(0, 1, 0, 10)),
        ]);

        IReadOnlyList<CorpusWerRoleTotals> rows = Assert.IsAssignableFrom<IReadOnlyList<CorpusWerRoleTotals>>(nullableRows);
        Assert.Equal(["koak-app", "ksfo-gnd"], [.. rows.Select(r => r.Role)]);
        Assert.Equal(2, rows[0].Segments);
        Assert.Equal(20, rows[0].ReferenceWords);
        Assert.Equal(1, rows[0].Substitutions);
        Assert.Equal(0.1, rows[0].Wer, 6);
        Assert.Equal(0.1, rows[1].Wer, 6);
        Assert.Equal(1, rows[1].Segments);
    }

    [Fact]
    public void FeedRows_Pools_The_Segments_Without_A_Feed_Last_As_Unknown()
    {
        IReadOnlyList<CorpusWerRoleTotals>? nullableRows = CorpusWerGroups.FeedRows([
            (Segment("a_seg0001", feed: "koak-app", provenance: null), Counts(0, 0, 0, 10)),
            (Segment("b_seg0001", feed: null, provenance: null), Counts(1, 0, 0, 20)),
            (Segment("c_seg0001", feed: null, provenance: null), Counts(0, 1, 0, 20)),
        ]);

        IReadOnlyList<CorpusWerRoleTotals> rows = Assert.IsAssignableFrom<IReadOnlyList<CorpusWerRoleTotals>>(nullableRows);
        Assert.Equal(["koak-app", "unknown"], [.. rows.Select(r => r.Role)]);
        Assert.Equal("unknown", rows[1].Role);
        Assert.Equal(2, rows[1].Segments);
        Assert.Equal(40, rows[1].ReferenceWords);
        Assert.Equal(0.05, rows[1].Wer, 6);
    }

    private static CorpusSegment Segment(string id, string? feed, string? provenance) =>
        new(id, CorpusRoles.FromId(id), [], "reference", feed, provenance);

    private static WerCounts Counts(int substitutions, int deletions, int insertions, int referenceWords) =>
        new(substitutions, deletions, insertions, referenceWords);
}
