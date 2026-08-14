using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>The pre-identity-model favorites read out of preferences.json for the one-time migration.</summary>
internal sealed record LegacyFavoritesPayload(
    List<LegacyFavoriteCommand> FavoriteCommands,
    List<LegacyFavoriteCommandSet> FavoriteCommandSets,
    List<string> LoadedSetNames
);

/// <summary>Old favorite shape: no id, visibility carried by per-favorite scope fields.</summary>
public sealed class LegacyFavoriteCommand
{
    public bool IsSpacer { get; set; }
    public string Label { get; set; } = "";
    public string CommandText { get; set; } = "";
    public string GroundCommandText { get; set; } = "";
    public string? ScenarioId { get; set; }
    public string? AirportId { get; set; }
    public FavoriteCommandCategory Category { get; set; } = FavoriteCommandCategory.Air;
    public string BackgroundColor { get; set; } = FavoriteCommandDefaults.BackgroundColor;
    public string TextColor { get; set; } = FavoriteCommandDefaults.TextColor;
    public double ButtonWidth { get; set; } = FavoriteCommandDefaults.ButtonWidth;
    public double ButtonHeight { get; set; } = FavoriteCommandDefaults.ButtonHeight;
}

/// <summary>Old named set shape: favorites embedded by value rather than referenced by id.</summary>
public sealed class LegacyFavoriteCommandSet
{
    public string Name { get; set; } = "";
    public List<LegacyFavoriteCommand> Favorites { get; set; } = [];
}

/// <summary>
/// One-time conversion of the pre-identity-model favorites (base pool with scope fields, named
/// sets embedding favorites by value, loaded-set names) into the file-per-entity
/// <see cref="FavoriteStore"/>. The base pool partitions by each favorite's scope into the
/// Global / Airport / Scenario container (order preserved); named sets become Named containers;
/// loaded-set names and window-profile references map to set ids. Runs only when the store's
/// directories did not exist yet, then drops the legacy fields from preferences.json.
/// </summary>
public static class FavoriteLegacyMigration
{
    private static readonly ILogger Log = AppLog.CreateLogger("FavoriteLegacyMigration");

    public static void Run(UserPreferences preferences, FavoriteStore store)
    {
        if (!store.LoadedFromEmpty)
        {
            return;
        }

        var legacy = preferences.PeekLegacyFavorites();
        if (legacy is null)
        {
            return;
        }

        var migratedFavorites = 0;
        foreach (var old in legacy.FavoriteCommands.Where(f => f is not null))
        {
            var favorite = Convert(old, store.NewFavoriteId());
            store.SaveFavorite(favorite);
            store.AddToSet(ResolveScopeSet(store, old).Id, favorite.Id);
            migratedFavorites++;
        }

        var nameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var oldSet in legacy.FavoriteCommandSets.Where(s => s is not null))
        {
            var set = store.CreateNamedSet(oldSet.Name);
            if (set is null)
            {
                Log.LogWarning("Skipping legacy favorite set '{Name}' during migration (blank or duplicate name)", oldSet.Name);
                continue;
            }

            nameToId[set.Name] = set.Id;
            foreach (var old in oldSet.Favorites.Where(f => f is not null))
            {
                var favorite = Convert(old, store.NewFavoriteId());
                store.SaveFavorite(favorite);
                store.AddToSet(set.Id, favorite.Id);
                migratedFavorites++;
            }
        }

        preferences.SetLoadedFavoriteSets(
            legacy.LoadedSetNames.Select(name => nameToId.GetValueOrDefault(name)).Where(id => id is not null).Cast<string>().ToList()
        );
        preferences.MigrateProfileLoadedSetNames(name => nameToId.GetValueOrDefault(name));
        preferences.ClearLegacyFavorites();
        Log.LogInformation("Migrated {Favorites} favorite(s) and {Sets} named set(s) into the favorites store", migratedFavorites, nameToId.Count);
    }

    private static FavoriteSet ResolveScopeSet(FavoriteStore store, LegacyFavoriteCommand old)
    {
        if (!string.IsNullOrWhiteSpace(old.ScenarioId))
        {
            // The scenario's display name is unknown at migration time; the id stands in until
            // the scenario is next active and GetOrCreateScenarioSet refreshes it.
            return store.GetOrCreateScenarioSet(old.ScenarioId, old.ScenarioId);
        }

        if (!string.IsNullOrWhiteSpace(old.AirportId))
        {
            return store.GetOrCreateAirportSet(old.AirportId);
        }

        return store.GlobalSet;
    }

    private static FavoriteCommand Convert(LegacyFavoriteCommand old, string id) =>
        new()
        {
            Id = id,
            IsSpacer = old.IsSpacer,
            Label = old.Label,
            CommandText = old.CommandText,
            GroundCommandText = old.GroundCommandText,
            Category = Enum.IsDefined(old.Category) ? old.Category : FavoriteCommandCategory.Air,
            BackgroundColor = old.BackgroundColor,
            TextColor = old.TextColor,
            ButtonWidth = old.ButtonWidth,
            ButtonHeight = old.ButtonHeight,
        };
}
