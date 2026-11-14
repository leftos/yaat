using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Situation;

namespace Yaat.Client.Tests;

/// <summary>
/// Pins the preferences v1 → v2 migration of the stored quick-command lists: Accept handoff, Cancel handoff and Acknowledge
/// pointout are inserted once into every stored list but Live traffic's, after Squawk assigned code (else Squawk normal,
/// else first), and never twice. Tests share the per-process preferences.json as <see cref="QuickCommandStorageTests"/>
/// does, writing a file at <c>"preferencesVersion": 1</c>, and each test resets every stored list when it ends.
/// </summary>
public class QuickCommandMigrationTests : IDisposable
{
    private static readonly string PreferencesPath = YaatPaths.Combine("preferences.json");

    private static readonly QuickCommandEntry SquawkAssigned = Catalog(MenuIds.SquawkAssigned);
    private static readonly QuickCommandEntry SquawkNormal = Catalog(MenuIds.SquawkNormal);
    private static readonly QuickCommandEntry HoldPosition = Catalog(MenuIds.GroundHoldPosition);
    private static readonly QuickCommandEntry Accept = Catalog(MenuIds.TrackAcceptHandoff);
    private static readonly QuickCommandEntry Cancel = Catalog(MenuIds.TrackCancelHandoff);
    private static readonly QuickCommandEntry Acknowledge = Catalog(MenuIds.TrackAcknowledgePointout);

    public void Dispose() => ClearStoredQuickCommandLists();

    [Fact]
    public void StoredCustomList_GainsTheThreeRowsAfterSquawkAssigned()
    {
        InjectVersion1Lists($$"""{ "Taxiing": [ {{Json(MenuIds.SquawkAssigned)}}, {{Json(MenuIds.GroundHoldPosition)}} ] }""");

        var prefs = new UserPreferences();

        Assert.Equal([SquawkAssigned, Accept, Cancel, Acknowledge, HoldPosition], prefs.GetQuickCommandList(AircraftSituation.Taxiing));
        Assert.Equal(UserPreferences.CurrentPreferencesVersion, StoredVersion());
    }

    [Fact]
    public void Reload_DoesNotInsertTheRowsTwice()
    {
        InjectVersion1Lists($$"""{ "Taxiing": [ {{Json(MenuIds.SquawkAssigned)}}, {{Json(MenuIds.GroundHoldPosition)}} ] }""");
        _ = new UserPreferences();

        var reloaded = new UserPreferences();

        Assert.Equal([SquawkAssigned, Accept, Cancel, Acknowledge, HoldPosition], reloaded.GetQuickCommandList(AircraftSituation.Taxiing));
    }

    [Fact]
    public void StoredListWithoutSquawkAssigned_GetsTheRowsAfterSquawkNormalOrFirst()
    {
        InjectVersion1Lists(
            $$"""
            {
              "Taxiing": [ {{Json(MenuIds.SquawkNormal)}}, {{Json(MenuIds.GroundHoldPosition)}} ],
              "Final": [ {{Json(MenuIds.TowerGoAround)}} ]
            }
            """
        );

        var prefs = new UserPreferences();

        Assert.Equal([SquawkNormal, Accept, Cancel, Acknowledge, HoldPosition], prefs.GetQuickCommandList(AircraftSituation.Taxiing));
        Assert.Equal([Accept, Cancel, Acknowledge, Catalog(MenuIds.TowerGoAround)], prefs.GetQuickCommandList(AircraftSituation.Final));
    }

    [Fact]
    public void StoredListAlreadyHoldingARow_GainsOnlyTheMissingRows()
    {
        InjectVersion1Lists(
            $$"""
            {
              "Taxiing": [
                {{Json(MenuIds.SquawkAssigned)}},
                {{Json(MenuIds.GroundHoldPosition)}},
                { "kind": "catalog", "catalogId": "{{MenuIds.TrackCancelHandoff}}", "flightRules": "VfrOnly" }
              ]
            }
            """
        );

        var prefs = new UserPreferences();

        Assert.Equal(
            [SquawkAssigned, Accept, Acknowledge, HoldPosition, new CatalogQuickCommandEntry(MenuIds.TrackCancelHandoff, MenuFlightRules.VfrOnly)],
            prefs.GetQuickCommandList(AircraftSituation.Taxiing)
        );
    }

    [Fact]
    public void StoredListEqualToTheNewDefault_IsNoLongerStored()
    {
        string[] oldDefault =
        [
            .. QuickCommandDefaults
                .For(AircraftSituation.Taxiing)
                .Where(entry => (entry != Accept) && (entry != Cancel) && (entry != Acknowledge))
                .Select(entry => Json(((CatalogQuickCommandEntry)entry).CatalogId)),
        ];
        InjectVersion1Lists($$"""{ "Taxiing": [ {{string.Join(", ", oldDefault)}} ] }""");

        var prefs = new UserPreferences();

        Assert.Equal(QuickCommandDefaults.For(AircraftSituation.Taxiing), prefs.GetQuickCommandList(AircraftSituation.Taxiing));
        Assert.Empty(prefs.QuickCommandOverrides);
        Assert.Empty(StoredSituationNames());
    }

    [Fact]
    public void DefaultOnlyFile_IsUnaffected()
    {
        InjectVersion1Lists("{}");

        var prefs = new UserPreferences();

        Assert.Empty(prefs.QuickCommandOverrides);
        Assert.All(QuickCommandDefaults.Lists, pair => Assert.Equal(pair.Value, prefs.GetQuickCommandList(pair.Key)));
        Assert.Empty(StoredSituationNames());
    }

    [Fact]
    public void LiveTrafficStoredList_IsLeftAlone()
    {
        InjectVersion1Lists($$"""{ "LiveTraffic": [ {{Json(MenuIds.LiveTrafficAssume)}} ] }""");

        var prefs = new UserPreferences();

        Assert.Equal([Catalog(MenuIds.LiveTrafficAssume)], prefs.GetQuickCommandList(AircraftSituation.LiveTraffic));
    }

    [Fact]
    public void FreshFile_StartsAtVersion2WithNoMigration()
    {
        File.Delete(PreferencesPath);
        new UserPreferences().SetQuickCommandList(AircraftSituation.Taxiing, [SquawkAssigned, HoldPosition]);

        var reloaded = new UserPreferences();

        Assert.Equal(UserPreferences.CurrentPreferencesVersion, StoredVersion());
        Assert.Equal([SquawkAssigned, HoldPosition], reloaded.GetQuickCommandList(AircraftSituation.Taxiing));
    }

    [Fact]
    public void ARemovedRow_StaysRemoved()
    {
        InjectVersion1Lists($$"""{ "Taxiing": [ {{Json(MenuIds.SquawkAssigned)}}, {{Json(MenuIds.GroundHoldPosition)}} ] }""");
        new UserPreferences().SetQuickCommandList(AircraftSituation.Taxiing, [SquawkAssigned, Accept, HoldPosition]);

        var reloaded = new UserPreferences();

        Assert.Equal([SquawkAssigned, Accept, HoldPosition], reloaded.GetQuickCommandList(AircraftSituation.Taxiing));
    }

    private static QuickCommandEntry Catalog(string id) => new CatalogQuickCommandEntry(id, null);

    private static string Json(string catalogId) => $$"""{ "kind": "catalog", "catalogId": "{{catalogId}}" }""";

    /// <summary>Stores every situation's default list, leaving the shared preferences.json with no quick-command list.</summary>
    private static void ClearStoredQuickCommandLists()
    {
        var prefs = new UserPreferences();
        foreach (AircraftSituation situation in QuickCommandDefaults.Lists.Keys)
        {
            prefs.SetQuickCommandList(situation, QuickCommandDefaults.For(situation));
        }
    }

    /// <summary>The situation names the saved preferences.json stores a quick-command list for.</summary>
    private static List<string> StoredSituationNames()
    {
        JsonObject root = JsonNode.Parse(File.ReadAllText(PreferencesPath))!.AsObject();
        return root["quickCommandLists"] is JsonObject lists ? [.. lists.Select(pair => pair.Key)] : [];
    }

    /// <summary>The <c>preferencesVersion</c> the saved preferences.json holds.</summary>
    private static int StoredVersion() => JsonNode.Parse(File.ReadAllText(PreferencesPath))!["preferencesVersion"]!.GetValue<int>();

    /// <summary>
    /// Writes <paramref name="listsJson"/> as the stored quick-command lists in the shared preferences.json on disk, at
    /// preferences version 1, the last before the Track and coordination rows.
    /// </summary>
    private static void InjectVersion1Lists(string listsJson)
    {
        JsonObject root = File.Exists(PreferencesPath) ? JsonNode.Parse(File.ReadAllText(PreferencesPath))!.AsObject() : [];
        root["quickCommandLists"] = JsonNode.Parse(listsJson);
        root["preferencesVersion"] = 1;
        Directory.CreateDirectory(YaatPaths.AppDataRoot);
        File.WriteAllText(PreferencesPath, root.ToJsonString());
    }
}
