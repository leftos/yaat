using Xunit;
using Yaat.Client.ContextMenus;

namespace Yaat.Client.Tests;

/// <summary>
/// Pins how the quick-command strip packs its icons: one group per category in the order the list first names it, list
/// order within a group, six icons a row, a category's last partial row shared with the next categories when they fit,
/// a wrapped category's continuation unlabelled, and the uncategorised icons as one trailing unlabelled group.
/// </summary>
public class QuickCommandStripLayoutTests
{
    [Fact]
    public void Categories_RunInTheOrderTheListFirstNamesThem() =>
        Assert.Equal(["Taxi: T1 T2 | Runway: R1 | Vectors: V1"], Describe(Pack("T1", "R1", "T2", "V1")));

    [Fact]
    public void WithinACategory_IconsKeepListOrder() => Assert.Equal(["Runway: R2 R1 R3"], Describe(Pack("R2", "R1", "R3")));

    [Fact]
    public void TwoPlusTwoPlusOne_ShareOneRow() =>
        Assert.Equal(["Runway: R1 R2 | Taxi: T1 T2 | Vectors: V1"], Describe(Pack("R1", "R2", "T1", "T2", "V1")));

    [Fact]
    public void TwoPlusOnePlusOne_ShareOneRow() => Assert.Equal(["Runway: R1 R2 | Taxi: T1 | Vectors: V1"], Describe(Pack("R1", "R2", "T1", "V1")));

    [Fact]
    public void FourPlusThree_DoNotShare() =>
        Assert.Equal(["Runway: R1 R2 R3 R4", "Taxi: T1 T2 T3"], Describe(Pack("R1", "R2", "R3", "R4", "T1", "T2", "T3")));

    [Fact]
    public void OnlyNeighboursShare_ALaterCategoryNeverJoinsAnEarlierRow()
    {
        Assert.Equal(
            ["Runway: R1 R2 R3 R4 R5", "Taxi: T1 T2 T3 | Vectors: V1"],
            Describe(Pack("R1", "R2", "R3", "R4", "R5", "T1", "T2", "T3", "V1"))
        );
    }

    [Fact]
    public void AFullRowOfOneCategory_TakesNoFollower() =>
        Assert.Equal(["Runway: R1 R2 R3 R4 R5 R6", "Taxi: T1"], Describe(Pack("R1", "R2", "R3", "R4", "R5", "R6", "T1")));

    [Fact]
    public void ACategoryOfEight_WrapsAtSix_AndItsLastTwoShareTheNextRowUnlabelled()
    {
        Assert.Equal(
            ["Runway: R1 R2 R3 R4 R5 R6", "(cont): R7 R8 | Taxi: T1 T2 T3 T4"],
            Describe(Pack("R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "T1", "T2", "T3", "T4"))
        );
    }

    [Fact]
    public void AWrappedCategorysLastPart_StandsAlone_WhenTheNextCategoryDoesNotFit()
    {
        Assert.Equal(
            ["Runway: R1 R2 R3 R4 R5 R6", "(cont): R7 R8", "Taxi: T1 T2 T3 T4 T5"],
            Describe(Pack("R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "T1", "T2", "T3", "T4", "T5"))
        );
    }

    [Fact]
    public void UncategorisedIcons_FormOneTrailingUnlabelledGroup() =>
        Assert.Equal(["Runway: R1 | Taxi: T1 | -: X1 X2"], Describe(Pack("X1", "R1", "X2", "T1")));

    [Fact]
    public void AnAllUncategorisedList_IsOneUnlabelledRow() => Assert.Equal(["-: X1 X2 X3"], Describe(Pack("X1", "X2", "X3")));

    [Fact]
    public void NoIcons_LayNoRows() => Assert.Empty(Pack());

    [Fact]
    public void ReleaseIcons_SitInTrackAndCoordination_InListOrder()
    {
        string[] ids =
        [
            MenuIds.GroundPushback,
            MenuIds.CoordinationCheckReleaseWindow,
            MenuIds.SquawkNormal,
            MenuIds.GroundHoldPosition,
            MenuIds.CoordinationReleaseHeld,
        ];

        IReadOnlyList<QuickCommandStripRow<string>> rows = QuickCommandStripLayout.Pack(ids, MenuCategories.Of);

        QuickCommandStripRow<string> row = Assert.Single(rows);
        Assert.Equal([MenuCategory.Taxi, MenuCategory.TrackAndCoordination], row.Segments.Select(segment => segment.Category));
        Assert.Equal([MenuIds.CoordinationCheckReleaseWindow, MenuIds.SquawkNormal, MenuIds.CoordinationReleaseHeld], row.Segments[1].Items);
    }

    [Fact]
    public void LiveTrafficAndSimIcons_GroupUnderTheirOwnLabels()
    {
        string[] ids = [MenuIds.LiveTrafficAssume, MenuIds.SimControlWarp, MenuIds.LiveTrafficAssumeAndTrack];

        Assert.Equal(
            [$"Live traffic: {MenuIds.LiveTrafficAssume} {MenuIds.LiveTrafficAssumeAndTrack} | Sim: {MenuIds.SimControlWarp}"],
            Describe(QuickCommandStripLayout.Pack(ids, MenuCategories.Of))
        );
    }

    [Fact]
    public void AShadowsDefaultList_IsOneRowUnderLiveTraffic() =>
        Assert.Equal(
            [$"Live traffic: {MenuIds.LiveTrafficAssume} {MenuIds.LiveTrafficAssumeAndTrack}"],
            Describe(QuickCommandStripLayout.Pack([MenuIds.LiveTrafficAssume, MenuIds.LiveTrafficAssumeAndTrack], MenuCategories.Of))
        );

    private static IReadOnlyList<QuickCommandStripRow<string>> Pack(params string[] ids) => QuickCommandStripLayout.Pack(ids, CategoryOf);

    /// <summary>A test id's category by its first letter: R, T, V for Runway, Taxi, Vectors, X for none.</summary>
    private static MenuCategory? CategoryOf(string id) =>
        id[0] switch
        {
            'R' => MenuCategory.Runway,
            'T' => MenuCategory.Taxi,
            'V' => MenuCategory.Vectors,
            'X' => null,
            _ => throw new ArgumentException($"No test category for '{id}'.", nameof(id)),
        };

    private static List<string> Describe(IReadOnlyList<QuickCommandStripRow<string>> rows) =>
        [.. rows.Select(row => string.Join(" | ", row.Segments.Select(DescribeSegment)))];

    private static string DescribeSegment(QuickCommandStripSegment<string> segment)
    {
        string head = segment switch
        {
            { Category: null } => "-",
            { ShowsLabel: true, Category: { } category } => MenuCategories.Label(category),
            _ => "(cont)",
        };
        return $"{head}: {string.Join(' ', segment.Items)}";
    }
}
