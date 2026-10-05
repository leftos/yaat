using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.Tests;

/// <summary>
/// Pins the stored quick-command lists in <see cref="UserPreferences"/>: only changed situations are saved, a list equal
/// to the default is not stored, reset one and reset all, and what a load cannot read is dropped without losing the rest.
/// Tests share the per-process preferences.json (ModuleInit redirects it to a temp folder; this assembly runs its test
/// classes one at a time), and each test resets every stored list when it ends.
/// </summary>
public class QuickCommandStorageTests : IDisposable
{
    private static readonly string PreferencesPath = YaatPaths.Combine("preferences.json");

    private static readonly IReadOnlyList<QuickCommandEntry> TaxiingList =
    [
        new CatalogQuickCommandEntry(MenuIds.GroundHoldPosition, null),
        new CatalogQuickCommandEntry(MenuIds.TowerClearedForTakeoff, MenuFlightRules.IfrOnly),
        new CustomQuickCommandEntry("Monitor tower", "FC TWR", "CT 118.3", MenuFlightRules.VfrOnly),
        new CustomQuickCommandEntry("Say heading", "SH", null, MenuFlightRules.Both),
    ];

    public void Dispose() => new UserPreferences().ResetAllQuickCommandLists();

    [Fact]
    public void UnchangedSituations_AreAbsentFromTheSavedJson()
    {
        var prefs = new UserPreferences();
        prefs.ResetAllQuickCommandLists();

        prefs.SetQuickCommandList(AircraftSituation.Taxiing, TaxiingList);

        Assert.Equal(["Taxiing"], StoredSituationNames());
    }

    [Fact]
    public void SetList_RoundTripsThroughAReload()
    {
        new UserPreferences().SetQuickCommandList(AircraftSituation.Taxiing, TaxiingList);

        var reloaded = new UserPreferences();

        Assert.Equal(TaxiingList, reloaded.GetQuickCommandList(AircraftSituation.Taxiing));
        Assert.Equal(QuickCommandDefaults.For(AircraftSituation.Final), reloaded.GetQuickCommandList(AircraftSituation.Final));
        Assert.Equal([AircraftSituation.Taxiing], reloaded.QuickCommandOverrides.Keys);
    }

    [Fact]
    public void SettingTheDefaultList_RemovesTheStoredSituation()
    {
        var prefs = new UserPreferences();
        prefs.SetQuickCommandList(AircraftSituation.Taxiing, TaxiingList);

        prefs.SetQuickCommandList(AircraftSituation.Taxiing, [.. QuickCommandDefaults.For(AircraftSituation.Taxiing)]);

        Assert.Empty(prefs.QuickCommandOverrides);
        Assert.Empty(StoredSituationNames());
        Assert.Equal(QuickCommandDefaults.For(AircraftSituation.Taxiing), new UserPreferences().GetQuickCommandList(AircraftSituation.Taxiing));
    }

    [Fact]
    public void ResetOne_RemovesOnlyThatSituation()
    {
        var prefs = new UserPreferences();
        prefs.SetQuickCommandList(AircraftSituation.Taxiing, TaxiingList);
        prefs.SetQuickCommandList(AircraftSituation.Final, [new CatalogQuickCommandEntry(MenuIds.TowerGoAround, null)]);

        prefs.ResetQuickCommandList(AircraftSituation.Taxiing);

        var reloaded = new UserPreferences();
        Assert.Equal(QuickCommandDefaults.For(AircraftSituation.Taxiing), reloaded.GetQuickCommandList(AircraftSituation.Taxiing));
        Assert.Equal([new CatalogQuickCommandEntry(MenuIds.TowerGoAround, null)], reloaded.GetQuickCommandList(AircraftSituation.Final));
        Assert.Equal(["Final"], StoredSituationNames());
    }

    [Fact]
    public void ResetAll_RemovesEveryStoredSituation()
    {
        var prefs = new UserPreferences();
        prefs.SetQuickCommandList(AircraftSituation.Taxiing, TaxiingList);
        prefs.SetQuickCommandList(AircraftSituation.Final, [new CatalogQuickCommandEntry(MenuIds.TowerGoAround, null)]);
        Assert.Equal(["Taxiing", "Final"], StoredSituationNames());

        prefs.ResetAllQuickCommandLists();

        Assert.Empty(new UserPreferences().QuickCommandOverrides);
        Assert.Empty(StoredSituationNames());
    }

    [Fact]
    public void EmptyList_IsStoredAsAnOverride()
    {
        new UserPreferences().SetQuickCommandList(AircraftSituation.Final, []);

        Assert.Empty(new UserPreferences().GetQuickCommandList(AircraftSituation.Final));
        Assert.Equal(["Final"], StoredSituationNames());
    }

    [Fact]
    public void UnknownSituation_CannotBeStored()
    {
        var prefs = new UserPreferences();

        Assert.Throws<ArgumentException>(() => prefs.SetQuickCommandList(AircraftSituation.Unknown, TaxiingList));
    }

    [Fact]
    public void Load_DropsAnUnknownCatalogIdAndUnknownSituations_KeepingEverythingElse()
    {
        InjectQuickCommandLists(
            """
            {
              "Taxiing": [
                { "kind": "catalog", "catalogId": "ground.no-such-action" },
                { "kind": "catalog", "catalogId": "ground.hold-position" },
                { "kind": "custom", "label": "Say heading", "commandText": "SH", "flightRules": "Both" }
              ],
              "NoSuchSituation": [ { "kind": "catalog", "catalogId": "ground.hold-position" } ],
              "Unknown": [ { "kind": "catalog", "catalogId": "ground.hold-position" } ],
              "Final": [ { "kind": "catalog", "catalogId": "tower.go-around", "flightRules": "VfrOnly" } ]
            }
            """
        );

        var prefs = new UserPreferences();

        Assert.Equal(
            [
                new CatalogQuickCommandEntry(MenuIds.GroundHoldPosition, null),
                new CustomQuickCommandEntry("Say heading", "SH", null, MenuFlightRules.Both),
            ],
            prefs.GetQuickCommandList(AircraftSituation.Taxiing)
        );
        Assert.Equal(
            [new CatalogQuickCommandEntry(MenuIds.TowerGoAround, MenuFlightRules.VfrOnly)],
            prefs.GetQuickCommandList(AircraftSituation.Final)
        );
        Assert.Equal([AircraftSituation.Taxiing, AircraftSituation.Final], prefs.QuickCommandOverrides.Keys.Order());
    }

    [Fact]
    public void Load_DropsAMalformedEntry_KeepingItsNeighboursAndOtherPreferences()
    {
        var prefs = new UserPreferences();
        VfrCommandsForIfr savedMode = prefs.VfrCommandsForIfr;
        prefs.SetVfrCommandsForIfr(VfrCommandsForIfr.All);
        try
        {
            InjectQuickCommandLists(
                """
                {
                  "Taxiing": [
                    { "kind": 5, "catalogId": "ground.hold-position" },
                    { "kind": "catalog", "catalogId": "ground.hold-position" },
                    "not an entry",
                    { "kind": "custom", "label": "Say heading", "commandText": 7 },
                    { "kind": "custom", "label": "Say heading", "commandText": "SH" }
                  ],
                  "Final": { "kind": "catalog", "catalogId": "tower.go-around" },
                  "Pattern": [ { "kind": "catalog", "catalogId": "tower.go-around" } ]
                }
                """
            );

            var reloaded = new UserPreferences();

            Assert.Equal(
                [
                    new CatalogQuickCommandEntry(MenuIds.GroundHoldPosition, null),
                    new CustomQuickCommandEntry("Say heading", "SH", null, MenuFlightRules.Both),
                ],
                reloaded.GetQuickCommandList(AircraftSituation.Taxiing)
            );
            Assert.Equal([new CatalogQuickCommandEntry(MenuIds.TowerGoAround, null)], reloaded.GetQuickCommandList(AircraftSituation.Pattern));
            Assert.Equal(QuickCommandDefaults.For(AircraftSituation.Final), reloaded.GetQuickCommandList(AircraftSituation.Final));
            Assert.Equal(VfrCommandsForIfr.All, reloaded.VfrCommandsForIfr);
        }
        finally
        {
            new UserPreferences().SetVfrCommandsForIfr(savedMode);
        }
    }

    [Fact]
    public void Load_ASituationWhoseEntriesWereAllDropped_TakesTheDefault()
    {
        InjectQuickCommandLists("""{ "Taxiing": [ { "kind": "catalog", "catalogId": "ground.no-such-action" } ] }""");

        var prefs = new UserPreferences();

        Assert.Equal(QuickCommandDefaults.For(AircraftSituation.Taxiing), prefs.GetQuickCommandList(AircraftSituation.Taxiing));
        Assert.Empty(prefs.QuickCommandOverrides);
    }

    /// <summary>The situation names the saved preferences.json stores a quick-command list for.</summary>
    private static List<string> StoredSituationNames()
    {
        JsonObject root = JsonNode.Parse(File.ReadAllText(PreferencesPath))!.AsObject();
        return root["quickCommandLists"] is JsonObject lists ? [.. lists.Select(pair => pair.Key)] : [];
    }

    /// <summary>Writes <paramref name="listsJson"/> as the stored quick-command lists in the shared preferences.json on disk.</summary>
    private static void InjectQuickCommandLists(string listsJson)
    {
        JsonObject root = File.Exists(PreferencesPath) ? JsonNode.Parse(File.ReadAllText(PreferencesPath))!.AsObject() : [];
        root["quickCommandLists"] = JsonNode.Parse(listsJson);
        Directory.CreateDirectory(YaatPaths.AppDataRoot);
        File.WriteAllText(PreferencesPath, root.ToJsonString());
    }
}
