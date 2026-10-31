using Xunit;
using Yaat.Sim.Speech.Context;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// Slot scoring of the context decoder's output against a corpus clip's gold: per-kind recall and false
/// insertions, pooled overall and by provenance and feed.
/// </summary>
public sealed class CorpusSlotScorerTests
{
    [Fact]
    public void ResolvedCallsignAndRunway_AllRecovered()
    {
        CorpusSlotClip clip = Clip(
            "koak-app_seg0004",
            null,
            null,
            [Slot(EntityKind.Callsign, "N4926F"), Slot(EntityKind.Runway, "KHWD/28L")],
            Result(DecodeState.Resolved, Match(EntityKind.Callsign, "N4926F"), null, [Match(EntityKind.Runway, "KHWD/28L")])
        );

        CorpusSlotGroup group = Pool(CorpusSlotScorer.Score([clip]), "all");

        Assert.Equal(Enum.GetValues<EntityKind>().Length + 1, group.Kinds.Count);
        Assert.Equal("all", group.Kinds[0].Kind);
        Assert.Equal(2, group.Kinds[0].Gold);
        Assert.Equal(2, group.Kinds[0].Recovered);
        Assert.Equal(0, group.Kinds[0].Inserted);
        Assert.Equal(1, group.Kinds[0].Recall, 6);
        Assert.Equal(0, group.Kinds[0].InsertedPer100Clips, 6);
        CorpusSlotKindRow callsign = Kind(group, "callsign");
        Assert.Equal(1, callsign.Gold);
        Assert.Equal(1, callsign.Recovered);
        Assert.Equal(0, callsign.Inserted);
        Assert.Equal(1, callsign.Recall, 6);
        CorpusSlotKindRow runway = Kind(group, "runway");
        Assert.Equal(1, runway.Gold);
        Assert.Equal(1, runway.Recovered);
        Assert.Equal(0, runway.Inserted);
        Assert.Equal(1, runway.Recall, 6);
    }

    [Fact]
    public void WrongResolvedCallsign_CountsInsertionAndWrongAddressee()
    {
        CorpusSlotClip clip = Clip(
            "koak-gnd_seg0024",
            null,
            null,
            [Slot(EntityKind.Callsign, "N4926F")],
            Result(DecodeState.Resolved, Match(EntityKind.Callsign, "N111AA"), null, [])
        );

        CorpusSlotGroup group = Pool(CorpusSlotScorer.Score([clip]), "all");

        CorpusSlotKindRow callsign = Kind(group, "callsign");
        Assert.Equal(1, callsign.Gold);
        Assert.Equal(0, callsign.Recovered);
        Assert.Equal(1, callsign.Inserted);
        Assert.Equal(0, callsign.Recall, 6);
        Assert.Equal(1, group.CallsignClips);
        Assert.Equal(1, group.WrongAddressee);
        Assert.Equal(0, group.SayAgain);
        Assert.Equal(0, group.NoCallsign);
    }

    [Fact]
    public void SayAgainWithGoldCallsign_CountsRejectionNotInsertion()
    {
        CorpusSlotClip clip = Clip(
            "koak-gnd_seg0024",
            null,
            null,
            [Slot(EntityKind.Callsign, "N4926F")],
            Result(DecodeState.SayAgain, null, Match(EntityKind.Callsign, "N4926F"), [])
        );

        CorpusSlotGroup group = Pool(CorpusSlotScorer.Score([clip]), "all");

        CorpusSlotKindRow callsign = Kind(group, "callsign");
        Assert.Equal(1, callsign.Gold);
        Assert.Equal(0, callsign.Recovered);
        Assert.Equal(0, callsign.Inserted);
        Assert.Equal(0, callsign.Recall, 6);
        Assert.Equal(1, group.SayAgain);
        Assert.Equal(0, group.NoCallsign);
        Assert.Equal(0, group.WrongAddressee);
    }

    [Fact]
    public void NoCallsignEntityResolved_EntityStillCounts()
    {
        CorpusSlotClip withCallsign = Clip(
            "koak-app_seg0004",
            null,
            null,
            [Slot(EntityKind.Callsign, "SWA2495"), Slot(EntityKind.Taxiway, "KOAK/W4")],
            Result(DecodeState.NoCallsign, null, null, [Match(EntityKind.Taxiway, "KOAK/W4")])
        );
        CorpusSlotClip withoutCallsign = Clip(
            "koak-app_seg0005",
            null,
            null,
            [Slot(EntityKind.Runway, "KOAK/30")],
            Result(DecodeState.NoCallsign, null, null, [])
        );

        CorpusSlotGroup group = Pool(CorpusSlotScorer.Score([withCallsign, withoutCallsign]), "all");

        Assert.Equal(1, Kind(group, "taxiway").Recovered);
        Assert.Equal(1, Kind(group, "callsign").Gold);
        Assert.Equal(0, Kind(group, "callsign").Recovered);
        Assert.Equal(0, Kind(group, "callsign").Inserted);
        Assert.Equal(0, Kind(group, "runway").Recall, 6);
        Assert.Equal(1, group.CallsignClips);
        Assert.Equal(1, group.NoCallsign);
    }

    [Fact]
    public void TrafficCallsignEntity_RecoversGoldCallsign()
    {
        CorpusSlotClip clip = Clip(
            "koak-app_seg0004",
            null,
            null,
            [Slot(EntityKind.Callsign, "SWA2495"), Slot(EntityKind.Callsign, "N4926F")],
            Result(DecodeState.Resolved, Match(EntityKind.Callsign, "SWA2495"), null, [Match(EntityKind.Callsign, "N4926F")])
        );

        CorpusSlotGroup group = Pool(CorpusSlotScorer.Score([clip]), "all");

        CorpusSlotKindRow callsign = Kind(group, "callsign");
        Assert.Equal(2, callsign.Gold);
        Assert.Equal(2, callsign.Recovered);
        Assert.Equal(0, callsign.Inserted);
        Assert.Equal(1, callsign.Recall, 6);
        Assert.Equal(2, group.Kinds[0].Gold);
    }

    [Fact]
    public void DuplicateGold_MultisetIntersection()
    {
        CorpusSlotClip heardOnce = Clip(
            "koak-gnd_seg0024",
            null,
            null,
            [Slot(EntityKind.Taxiway, "KOAK/W4"), Slot(EntityKind.Taxiway, "KOAK/W4")],
            Result(DecodeState.NoCallsign, null, null, [Match(EntityKind.Taxiway, "KOAK/W4")])
        );
        CorpusSlotClip heardThrice = Clip(
            "koak-gnd_seg0025",
            null,
            null,
            [Slot(EntityKind.Taxiway, "KOAK/W4")],
            Result(
                DecodeState.NoCallsign,
                null,
                null,
                [Match(EntityKind.Taxiway, "KOAK/W4"), Match(EntityKind.Taxiway, "KOAK/W4"), Match(EntityKind.Taxiway, "KOAK/W4")]
            )
        );

        CorpusSlotKindRow twiceGold = Kind(Pool(CorpusSlotScorer.Score([heardOnce]), "all"), "taxiway");
        CorpusSlotKindRow thriceHeard = Kind(Pool(CorpusSlotScorer.Score([heardThrice]), "all"), "taxiway");

        Assert.Equal(2, twiceGold.Gold);
        Assert.Equal(1, twiceGold.Recovered);
        Assert.Equal(0, twiceGold.Inserted);
        Assert.Equal(1, thriceHeard.Gold);
        Assert.Equal(1, thriceHeard.Recovered);
        Assert.Equal(2, thriceHeard.Inserted);
    }

    [Fact]
    public void ValueMatchIgnoresCase()
    {
        CorpusSlotClip clip = Clip(
            "ksfo-gnd_seg0099",
            null,
            null,
            [Slot(EntityKind.Gate, "KSFO/spot 6B")],
            Result(DecodeState.NoCallsign, null, null, [Match(EntityKind.Gate, "KSFO/SPOT 6B")])
        );

        CorpusSlotKindRow gate = Kind(Pool(CorpusSlotScorer.Score([clip]), "all"), "gate");

        Assert.Equal(1, gate.Gold);
        Assert.Equal(1, gate.Recovered);
        Assert.Equal(0, gate.Inserted);
    }

    [Fact]
    public void ClipWithoutGold_InsertionCounts()
    {
        CorpusSlotClip withoutGold = Clip(
            "koak-app_seg0004",
            null,
            null,
            [],
            Result(DecodeState.NoCallsign, null, null, [Match(EntityKind.Fix, "MITOE")])
        );
        CorpusSlotClip withGold = Clip(
            "koak-app_seg0005",
            null,
            null,
            [Slot(EntityKind.Fix, "MITOE")],
            Result(DecodeState.NoCallsign, null, null, [Match(EntityKind.Fix, "MITOE")])
        );

        CorpusSlotGroup group = Pool(CorpusSlotScorer.Score([withoutGold, withGold]), "all");

        Assert.Equal(2, group.Clips);
        Assert.Equal(1, group.Kinds[0].Inserted);
        Assert.Equal(50, group.Kinds[0].InsertedPer100Clips, 6);
        CorpusSlotKindRow fix = Kind(group, "fix");
        Assert.Equal(1, fix.Gold);
        Assert.Equal(1, fix.Recovered);
        Assert.Equal(1, fix.Inserted);
    }

    [Fact]
    public void Groups_ProvenanceThenFeeds()
    {
        CorpusSlotClip owner = Clip(
            "koak-app_seg0004",
            "koak-app",
            "owner",
            [Slot(EntityKind.Fix, "MITOE")],
            Result(DecodeState.NoCallsign, null, null, [Match(EntityKind.Fix, "MITOE")])
        );
        CorpusSlotClip agreed = Clip(
            "koak-app_seg0005",
            "koak-app",
            "agreed",
            [Slot(EntityKind.Fix, "MITOE")],
            Result(DecodeState.NoCallsign, null, null, [Match(EntityKind.Fix, "MITOE")])
        );
        CorpusSlotClip claude = Clip(
            "ksfo-gnd_seg0099",
            "ksfo-gnd",
            "claude",
            [Slot(EntityKind.Fix, "MITOE")],
            Result(DecodeState.NoCallsign, null, null, [Match(EntityKind.Fix, "MITOE")])
        );
        CorpusSlotClip bare = Clip(
            "a_PI",
            null,
            null,
            [Slot(EntityKind.Fix, "MITOE")],
            Result(DecodeState.NoCallsign, null, null, [Match(EntityKind.Fix, "MITOE")])
        );

        IReadOnlyList<CorpusSlotGroup> groups = CorpusSlotScorer.Score([owner, agreed, claude, bare]);

        Assert.Equal(["all", "owner", "owner+agreed", "koak-app", "ksfo-gnd", "unknown"], [.. groups.Select(group => group.Group)]);
        Assert.Equal(4, Pool(groups, "all").Clips);
        Assert.Equal(1, Pool(groups, "owner").Clips);
        Assert.Equal(2, Pool(groups, "owner+agreed").Clips);
        Assert.Equal(2, Pool(groups, "koak-app").Clips);
        Assert.Equal(1, Pool(groups, "ksfo-gnd").Clips);
        Assert.Equal(1, Pool(groups, "unknown").Clips);

        IReadOnlyList<CorpusSlotGroup> onlyAll = CorpusSlotScorer.Score([bare]);

        Assert.Equal(["all"], [.. onlyAll.Select(group => group.Group)]);
    }

    private static CorpusSlotClip Clip(string id, string? feed, string? provenance, IReadOnlyList<CorpusSlot> gold, DecodeResult result) =>
        new(new CorpusSegment(id, CorpusRoles.FromId(id), [], "reference", feed, provenance), gold, result);

    private static CorpusSlot Slot(EntityKind kind, string value) => new(kind, value, "spoken", null);

    private static ContextEntity Entity(EntityKind kind, string value) => new(kind, value, []);

    private static EntityMatch Match(EntityKind kind, string value) => new(Entity(kind, value), new WordSpan(0, 0), 1.0, false, value);

    private static DecodeResult Result(DecodeState state, EntityMatch? callsign, EntityMatch? bestGuess, IReadOnlyList<EntityMatch> entities) =>
        new(state, callsign, bestGuess, entities, string.Empty, 0, 0, false);

    private static CorpusSlotGroup Pool(IReadOnlyList<CorpusSlotGroup> groups, string label) => groups.Single(group => group.Group == label);

    private static CorpusSlotKindRow Kind(CorpusSlotGroup group, string label) => group.Kinds.Single(row => row.Kind == label);
}
