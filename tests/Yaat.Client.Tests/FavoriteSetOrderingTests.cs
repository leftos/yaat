using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class FavoriteSetOrderingTests
{
    private const string Global = FavoriteSetOrdering.GlobalSlot;
    private const string Airport = FavoriteSetOrdering.AirportSlot;
    private const string Scenario = FavoriteSetOrdering.ScenarioSlot;
    private const string S1 = "aaaa1111";
    private const string S2 = "bbbb2222";
    private const string S3 = "cccc3333";
    private const string S4 = "dddd4444";

    // The store's named sets in OrderedSets order.
    private static readonly string[] NamedSetIds = [S1, S2, S3, S4];

    [Fact]
    public void EmptyOrder_ListsSlotsThenLoadedInLoadOrderThenUnloadedInStoreOrder()
    {
        List<string> order = FavoriteSetOrdering.Normalize([], [S3, "deadbeef", S1], NamedSetIds);

        Assert.Equal([Global, Airport, Scenario, S3, S1, S2, S4], order);
    }

    [Fact]
    public void MissingTokens_GoToTheFrontInSlotOrder()
    {
        List<string> order = FavoriteSetOrdering.Normalize([S2, Scenario, S1], [S1, S2], NamedSetIds);

        Assert.Equal([Global, Airport, S2, Scenario, S1, S3, S4], order);
    }

    [Fact]
    public void UnknownIdsAndCaseInsensitiveDuplicates_AreSkipped()
    {
        List<string> order = FavoriteSetOrdering.Normalize(
            ["GLOBAL", "deadbeef", S1, S1.ToUpperInvariant(), Airport, Global, Scenario, "Airport", S2, S3, S4],
            [],
            NamedSetIds
        );

        Assert.Equal([Global, S1, Airport, Scenario, S2, S3, S4], order);
    }

    [Fact]
    public void UnloadedSetListedInTheOrder_KeepsItsPlace()
    {
        List<string> order = FavoriteSetOrdering.Normalize([S4, Global, Airport, Scenario, S1], [S1], NamedSetIds);

        Assert.Equal([S4, Global, Airport, Scenario, S1, S2, S3], order);
    }
}
