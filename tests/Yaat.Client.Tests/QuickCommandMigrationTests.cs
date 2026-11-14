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
    private static readonly QuickCommandEntry IfrMaintain = new CatalogQuickCommandEntry(MenuIds.AltitudeMaintain, MenuFlightRules.IfrOnly);
    private static readonly QuickCommandEntry VfrAltitude = new CatalogQuickCommandEntry(MenuIds.AltitudeVfr, MenuFlightRules.VfrOnly);

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
        Assert.Equal([Accept, Cancel, Acknowledge, Catalog(MenuIds.TowerGoAround), VfrAltitude], prefs.GetQuickCommandList(AircraftSituation.Final));
    }

    // --- v2 → v3: Altitude… for VFR aircraft -------------------------------------------------------

    [Fact]
    public void V2StoredMaintain_BecomesIfrOnly_WithAltitudeAfterIt()
    {
        InjectLists(
            2,
            $$"""{ "Departing": [ {{Json(MenuIds.SquawkAssigned)}}, {{Json(MenuIds.AltitudeMaintain)}}, {{Json(MenuIds.HeadingFly)}} ] }"""
        );

        var prefs = new UserPreferences();

        Assert.Equal([SquawkAssigned, IfrMaintain, VfrAltitude, Catalog(MenuIds.HeadingFly)], prefs.GetQuickCommandList(AircraftSituation.Departing));
        Assert.Equal(UserPreferences.CurrentPreferencesVersion, StoredVersion());
    }

    [Fact]
    public void V2StoredListAlreadyHoldingAltitude_IsUntouched()
    {
        InjectLists(2, $$"""{ "Departing": [ {{Json(MenuIds.AltitudeMaintain)}}, {{Json(MenuIds.AltitudeVfr)}}, {{Json(MenuIds.HeadingFly)}} ] }""");

        var prefs = new UserPreferences();

        Assert.Equal(
            [Catalog(MenuIds.AltitudeMaintain), Catalog(MenuIds.AltitudeVfr), Catalog(MenuIds.HeadingFly)],
            prefs.GetQuickCommandList(AircraftSituation.Departing)
        );
    }

    /// <summary>A stored Pattern list gains Altitude… before Land; a stored Final list with no Land gains it at the end.</summary>
    [Fact]
    public void V2StoredPatternList_GainsAltitudeBeforeLand_FinalWithoutLandAtTheEnd()
    {
        InjectLists(
            2,
            $$"""
            {
              "Pattern": [ {{Json(MenuIds.TowerClearedToLand)}}, {{Json(MenuIds.GroundLandHelicopter)}} ],
              "Final": [ {{Json(MenuIds.TowerGoAround)}} ]
            }
            """
        );

        var prefs = new UserPreferences();

        Assert.Equal(
            [Catalog(MenuIds.TowerClearedToLand), VfrAltitude, Catalog(MenuIds.GroundLandHelicopter)],
            prefs.GetQuickCommandList(AircraftSituation.Pattern)
        );
        Assert.Equal([Catalog(MenuIds.TowerGoAround), VfrAltitude], prefs.GetQuickCommandList(AircraftSituation.Final));
    }

    /// <summary>A Maintain altitude the user restricted to VFR keeps its override, and the list gains no Altitude… after it.</summary>
    [Fact]
    public void V2StoredVfrOnlyMaintain_IsLeftAlone()
    {
        InjectLists(
            2,
            $$"""
            {
              "Departing": [
                {{Json(MenuIds.SquawkAssigned)}},
                { "kind": "catalog", "catalogId": "{{MenuIds.AltitudeMaintain}}", "flightRules": "VfrOnly" }
              ]
            }
            """
        );

        var prefs = new UserPreferences();

        Assert.Equal(
            [SquawkAssigned, new CatalogQuickCommandEntry(MenuIds.AltitudeMaintain, MenuFlightRules.VfrOnly)],
            prefs.GetQuickCommandList(AircraftSituation.Departing)
        );
    }

    [Fact]
    public void V2StoredVfrArrivalInboundList_GainsAltitudeBeforeLand()
    {
        InjectLists(2, $$"""{ "VfrArrivalInbound": [ {{Json(MenuIds.PatternFollow)}}, {{Json(MenuIds.GroundLandHelicopter)}} ] }""");

        var prefs = new UserPreferences();

        Assert.Equal(
            [Catalog(MenuIds.PatternFollow), VfrAltitude, Catalog(MenuIds.GroundLandHelicopter)],
            prefs.GetQuickCommandList(AircraftSituation.VfrArrivalInbound)
        );
    }

    [Fact]
    public void V2StoredListMadeDefault_IsNoLongerStored()
    {
        string[] oldDefault =
        [
            .. QuickCommandDefaults
                .For(AircraftSituation.Departing)
                .Where(entry => entry != VfrAltitude)
                .Select(entry => (CatalogQuickCommandEntry)entry)
                .Select(entry =>
                    (entry.CatalogId == MenuIds.AltitudeMaintain) || (entry.FlightRules is null)
                        ? Json(entry.CatalogId)
                        : $$"""{ "kind": "catalog", "catalogId": "{{entry.CatalogId}}", "flightRules": "{{entry.FlightRules}}" }"""
                ),
        ];
        InjectLists(2, $$"""{ "Departing": [ {{string.Join(", ", oldDefault)}} ] }""");

        var prefs = new UserPreferences();

        Assert.Equal(QuickCommandDefaults.For(AircraftSituation.Departing), prefs.GetQuickCommandList(AircraftSituation.Departing));
        Assert.Empty(prefs.QuickCommandOverrides);
        Assert.Empty(StoredSituationNames());
    }

    [Fact]
    public void V2LiveTrafficStoredList_IsLeftAlone()
    {
        InjectLists(2, $$"""{ "LiveTraffic": [ {{Json(MenuIds.LiveTrafficAssume)}}, {{Json(MenuIds.AltitudeMaintain)}} ] }""");

        var prefs = new UserPreferences();

        Assert.Equal(
            [Catalog(MenuIds.LiveTrafficAssume), Catalog(MenuIds.AltitudeMaintain)],
            prefs.GetQuickCommandList(AircraftSituation.LiveTraffic)
        );
    }

    [Fact]
    public void V1File_RunsBothMigrations()
    {
        InjectVersion1Lists($$"""{ "Departing": [ {{Json(MenuIds.SquawkAssigned)}}, {{Json(MenuIds.AltitudeMaintain)}} ] }""");

        var prefs = new UserPreferences();

        Assert.Equal([SquawkAssigned, Accept, Cancel, Acknowledge, IfrMaintain, VfrAltitude], prefs.GetQuickCommandList(AircraftSituation.Departing));
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
    public void FreshFile_StartsAtTheCurrentVersionWithNoMigration()
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
    private static void InjectVersion1Lists(string listsJson) => InjectLists(1, listsJson);

    /// <summary>Writes <paramref name="listsJson"/> as the stored quick-command lists at preferences version <paramref name="version"/>.</summary>
    private static void InjectLists(int version, string listsJson)
    {
        JsonObject root = File.Exists(PreferencesPath) ? JsonNode.Parse(File.ReadAllText(PreferencesPath))!.AsObject() : [];
        root["quickCommandLists"] = JsonNode.Parse(listsJson);
        root["preferencesVersion"] = version;
        Directory.CreateDirectory(YaatPaths.AppDataRoot);
        File.WriteAllText(PreferencesPath, root.ToJsonString());
    }
}
