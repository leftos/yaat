using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Sim.Situation;

namespace Yaat.Client.Tests;

/// <summary>
/// Pins the shareable quick-command list file (<see cref="QuickCommandListsFile"/>): the round trip, overrides only,
/// unreadable situations and entries dropped and reported, and the Replace and Merge imports.
/// </summary>
public class QuickCommandListsFileTests
{
    private static readonly IReadOnlyList<QuickCommandEntry> TaxiingList =
    [
        new CatalogQuickCommandEntry(MenuIds.GroundHoldPosition, null),
        new CatalogQuickCommandEntry(MenuIds.TowerClearedForTakeoff, MenuFlightRules.IfrOnly),
        new CustomQuickCommandEntry("Monitor tower", "FC TWR", "CT 118.3", MenuFlightRules.VfrOnly),
        new CustomQuickCommandEntry("Say heading", "SH", null, MenuFlightRules.Both),
    ];

    private static readonly IReadOnlyList<QuickCommandEntry> FinalOurs = [new CatalogQuickCommandEntry(MenuIds.TowerGoAround, null)];

    private static readonly IReadOnlyList<QuickCommandEntry> FinalTheirs =
    [
        new CustomQuickCommandEntry("Wind check", "WIND", null, MenuFlightRules.Both),
    ];

    private static readonly IReadOnlyList<QuickCommandEntry> PatternTheirs = [new CatalogQuickCommandEntry(MenuIds.TowerClearedToLand, null)];

    private static Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> Lists(
        params (AircraftSituation Situation, IReadOnlyList<QuickCommandEntry> List)[] pairs
    ) => pairs.ToDictionary(pair => pair.Situation, pair => pair.List);

    [Fact]
    public void File_RoundTrips()
    {
        Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> lists = Lists(
            (AircraftSituation.Taxiing, TaxiingList),
            (AircraftSituation.Final, FinalOurs)
        );

        QuickCommandListsImport import = QuickCommandListsFile.Deserialize(QuickCommandListsFile.Serialize(lists));

        Assert.Empty(import.Dropped);
        Assert.Equal([AircraftSituation.Taxiing, AircraftSituation.Final], import.Lists.Keys.Order());
        Assert.Equal(TaxiingList, import.Lists[AircraftSituation.Taxiing]);
        Assert.Equal(FinalOurs, import.Lists[AircraftSituation.Final]);
    }

    [Fact]
    public void File_CarriesAVersion_AndTaggedEntries()
    {
        JsonObject root = JsonNode.Parse(QuickCommandListsFile.Serialize(Lists((AircraftSituation.Taxiing, TaxiingList))))!.AsObject();

        Assert.Equal(QuickCommandListsFile.CurrentVersion, root["version"]!.GetValue<int>());
        JsonArray taxiing = root["situations"]!["Taxiing"]!.AsArray();
        Assert.Equal(["catalog", "catalog", "custom", "custom"], taxiing.Select(entry => entry!["kind"]!.GetValue<string>()));
    }

    [Fact]
    public void Serialize_WritesOnlyOverrides()
    {
        Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> lists = Lists(
            (AircraftSituation.Taxiing, TaxiingList),
            (AircraftSituation.Final, QuickCommandDefaults.For(AircraftSituation.Final)),
            (AircraftSituation.Unknown, FinalOurs)
        );

        JsonObject situations = JsonNode.Parse(QuickCommandListsFile.Serialize(lists))!["situations"]!.AsObject();

        Assert.Equal(["Taxiing"], situations.Select(pair => pair.Key));
    }

    [Fact]
    public void Deserialize_DropsAndReportsUnknownSituationsAndCatalogIds_KeepingTheRest()
    {
        const string Json = """
            {
              "version": 1,
              "situations": {
                "Taxiing": [
                  { "kind": "catalog", "catalogId": "ground.no-such-action" },
                  { "kind": "catalog", "catalogId": "ground.hold-position" },
                  { "kind": "teleport", "label": "Beam me up" }
                ],
                "NoSuchSituation": [ { "kind": "catalog", "catalogId": "ground.hold-position" } ],
                "Final": [ { "kind": "custom", "label": "Wind check", "commandText": "WIND" } ]
              }
            }
            """;

        QuickCommandListsImport import = QuickCommandListsFile.Deserialize(Json);

        Assert.Equal([new CatalogQuickCommandEntry(MenuIds.GroundHoldPosition, null)], import.Lists[AircraftSituation.Taxiing]);
        Assert.Equal(FinalTheirs, import.Lists[AircraftSituation.Final]);
        Assert.Equal(2, import.Lists.Count);
        Assert.Equal(
            [("Taxiing", "ground.no-such-action"), ("Taxiing", "Beam me up"), ("NoSuchSituation", (string?)null)],
            import.Dropped.Select(drop => (drop.Situation, drop.Entry))
        );
    }

    [Fact]
    public void Deserialize_DropsAndReportsAMalformedEntry_KeepingItsNeighbours()
    {
        const string Json = """
            {
              "version": 1,
              "situations": {
                "Taxiing": [
                  { "kind": 5, "catalogId": "ground.hold-position" },
                  { "kind": "catalog", "catalogId": "ground.hold-position" },
                  { "kind": "catalog", "catalogId": "tower.cto", "flightRules": true },
                  { "kind": "custom", "label": "Wind check", "commandText": "WIND" }
                ],
                "Final": "not a list"
              }
            }
            """;

        QuickCommandListsImport import = QuickCommandListsFile.Deserialize(Json);

        Assert.Equal([new CatalogQuickCommandEntry(MenuIds.GroundHoldPosition, null), FinalTheirs[0]], import.Lists[AircraftSituation.Taxiing]);
        Assert.Single(import.Lists);
        Assert.Equal(
            [("Taxiing", "ground.hold-position"), ("Taxiing", "tower.cto"), ("Final", (string?)null)],
            import.Dropped.Select(drop => (drop.Situation, drop.Entry))
        );
    }

    [Theory]
    [InlineData("""{ "situations": {} }""")]
    [InlineData("""{ "version": 1 }""")]
    [InlineData("not json")]
    public void Deserialize_RejectsAFileWithoutVersionOrSituations(string json) =>
        Assert.ThrowsAny<JsonException>(() => QuickCommandListsFile.Deserialize(json));

    [Fact]
    public void Replace_MakesTheFilesListsAllTheOverrides()
    {
        QuickCommandListsImport import = QuickCommandListsFile.Deserialize(
            QuickCommandListsFile.Serialize(Lists((AircraftSituation.Final, FinalTheirs)))
        );

        IReadOnlyDictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> replaced = QuickCommandListsFile.Replace(import.Lists);

        Assert.Equal([AircraftSituation.Final], replaced.Keys);
        Assert.Equal(FinalTheirs, replaced[AircraftSituation.Final]);
    }

    [Fact]
    public void Merge_ListsEachSituationBothCarry_AsAClash()
    {
        Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> ours = Lists(
            (AircraftSituation.Taxiing, TaxiingList),
            (AircraftSituation.Final, FinalOurs)
        );
        Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> theirs = Lists(
            (AircraftSituation.Pattern, PatternTheirs),
            (AircraftSituation.Final, FinalTheirs)
        );

        Assert.Equal([AircraftSituation.Final], QuickCommandListsFile.Clashes(ours, theirs));
    }

    [Theory]
    [InlineData(QuickCommandClashChoice.Skip)]
    [InlineData(QuickCommandClashChoice.Overwrite)]
    public void Merge_AddsTheirNewSituations_AndSettlesTheClashByTheChoice(QuickCommandClashChoice choice)
    {
        Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> ours = Lists(
            (AircraftSituation.Taxiing, TaxiingList),
            (AircraftSituation.Final, FinalOurs)
        );
        Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> theirs = Lists(
            (AircraftSituation.Pattern, PatternTheirs),
            (AircraftSituation.Final, FinalTheirs)
        );

        IReadOnlyDictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> merged = QuickCommandListsFile.Merge(
            ours,
            theirs,
            new Dictionary<AircraftSituation, QuickCommandClashChoice> { [AircraftSituation.Final] = choice }
        );

        Assert.Equal(TaxiingList, merged[AircraftSituation.Taxiing]);
        Assert.Equal(PatternTheirs, merged[AircraftSituation.Pattern]);
        Assert.Equal(choice == QuickCommandClashChoice.Skip ? FinalOurs : FinalTheirs, merged[AircraftSituation.Final]);
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void Merge_WithoutAChoiceForAClash_Throws()
    {
        Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> ours = Lists((AircraftSituation.Final, FinalOurs));
        Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> theirs = Lists((AircraftSituation.Final, FinalTheirs));

        Assert.Throws<ArgumentException>(() =>
            QuickCommandListsFile.Merge(ours, theirs, new Dictionary<AircraftSituation, QuickCommandClashChoice>())
        );
    }
}
