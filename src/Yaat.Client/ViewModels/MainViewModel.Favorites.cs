using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Favorite commands: quick-access buttons for frequently used commands.
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<FavoriteCommand> DisplayFavorites { get; } = [];

    /// <summary>
    /// Label shown in the pop-out Favorites Panel status bar so the user can see which aircraft a
    /// favorite click will act on (favorites target <see cref="SelectedAircraft"/>). Notified via
    /// <c>[NotifyPropertyChangedFor]</c> on <c>SelectedAircraft</c>.
    /// </summary>
    public string FavoritePanelTargetText => SelectedAircraft is { } aircraft ? $"Target: {aircraft.Callsign}" : "No aircraft selected";

    public void RefreshDisplayFavorites()
    {
        var composed = ComposeDisplayFavorites(
            _preferences.FavoriteCommands,
            _preferences.FavoriteCommandSets,
            _preferences.LoadedFavoriteSetNames,
            ActiveScenarioId,
            ActiveScenarioPrimaryAirportId
        );

        // Reference-sequence equality is a valid change detector because every edit path replaces
        // FavoriteCommand instances rather than mutating them in place. Skipping the no-op case
        // matters: mutators refresh synchronously AND UserPreferences.FavoriteSetsChanged posts a
        // deferred refresh (for Favorites Editor mutations that bypass this VM), so without this
        // guard every ordinary favorites action would rebuild the bar/panel a second time.
        if (composed.SequenceEqual(DisplayFavorites))
        {
            return;
        }

        DisplayFavorites.Clear();
        foreach (var fav in composed)
        {
            DisplayFavorites.Add(fav);
        }
    }

    /// <summary>
    /// Builds the merged display list: base-pool favorites filtered by their Global/Scenario/Airport
    /// scope, followed by every favorite of each loaded set in load order. Set membership is the
    /// visibility gate for set favorites — their scope fields are deliberately ignored (they are
    /// preserved only so a favorite copied back to the base pool keeps its original behavior).
    /// </summary>
    public static List<FavoriteCommand> ComposeDisplayFavorites(
        IReadOnlyList<FavoriteCommand> basePool,
        IReadOnlyList<FavoriteCommandSet> sets,
        IReadOnlyList<string> loadedSetNames,
        string? activeScenarioId,
        string? activeAirportId
    )
    {
        var result = basePool.Where(fav => IsFavoriteVisible(fav, activeScenarioId, activeAirportId)).ToList();
        foreach (var name in loadedSetNames)
        {
            var set = sets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (set is not null)
            {
                result.AddRange(set.Favorites);
            }
        }
        return result;
    }

    public IReadOnlyList<string> FavoriteSetNames => _preferences.FavoriteCommandSets.Select(s => s.Name).ToList();

    public IReadOnlyList<string> LoadedFavoriteSetNames => _preferences.LoadedFavoriteSetNames;

    public bool IsFavoriteSetLoaded(string name) =>
        _preferences.LoadedFavoriteSetNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads (appends to the load order) or unloads one named set; the display refreshes via FavoriteSetsChanged.</summary>
    public void SetFavoriteSetLoaded(string name, bool loaded)
    {
        _preferences.SetFavoriteSetLoaded(name, loaded);
        RefreshDisplayFavorites();
    }

    public static bool IsFavoriteVisible(FavoriteCommand favorite, string? activeScenarioId, string? activeAirportId)
    {
        if (!string.IsNullOrWhiteSpace(favorite.ScenarioId))
        {
            return string.Equals(favorite.ScenarioId, activeScenarioId, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(favorite.AirportId))
        {
            return string.Equals(
                NormalizeFavoriteAirportId(favorite.AirportId),
                NormalizeFavoriteAirportId(activeAirportId),
                StringComparison.Ordinal
            );
        }

        return true;
    }

    public static string? NormalizeFavoriteAirportId(string? airportId)
    {
        var trimmed = airportId?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    public static FavoriteCommandCategory NormalizeFavoriteCategory(FavoriteCommandCategory category)
    {
        return Enum.IsDefined(category) ? category : FavoriteCommandCategory.Air;
    }

    public async Task ExecuteFavoriteAsync(FavoriteCommand favorite)
    {
        if (favorite.IsSpacer)
        {
            return;
        }

        var currentText = CommandText.Trim();
        var favoriteCommandText = ResolveFavoriteCommandText(favorite);

        // Build the full command: "{currentText} {favoriteCommandText}" or just "{favoriteCommandText}"
        var fullCommand = string.IsNullOrEmpty(currentText) ? favoriteCommandText : $"{currentText} {favoriteCommandText}";

        var savedText = CommandText;
        var savedCaret = CommandCaretIndex;
        CommandText = fullCommand;
        CommandCaretIndex = fullCommand.Length;

        try
        {
            await SendCommandAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Favorite command execution failed");
            CommandText = savedText;
            CommandCaretIndex = Math.Min(savedCaret, savedText.Length);
        }
    }

    public void AppendFavoriteToInput(FavoriteCommand favorite)
    {
        if (favorite.IsSpacer)
        {
            return;
        }

        var current = CommandText.TrimEnd();
        var favoriteCommandText = ResolveFavoriteCommandText(favorite);
        var newText = string.IsNullOrEmpty(current) ? favoriteCommandText : $"{current}, {favoriteCommandText}";
        CommandText = newText;
        CommandCaretIndex = newText.Length;
    }

    public string ResolveFavoriteCommandText(FavoriteCommand favorite) => ResolveFavoriteCommandTextFor(favorite, SelectedAircraft);

    public static string ResolveFavoriteCommandTextFor(FavoriteCommand favorite, AircraftModel? aircraft)
    {
        if (favorite.IsSpacer)
        {
            return "";
        }

        if (aircraft?.IsOnGround == true && !string.IsNullOrWhiteSpace(favorite.GroundCommandText))
        {
            return favorite.GroundCommandText.Trim();
        }

        return favorite.CommandText.Trim();
    }

    public void AddFavorite(FavoriteCommand favorite)
    {
        var list = _preferences.FavoriteCommands.ToList();
        list.Add(favorite);
        _preferences.SetFavoriteCommands(list);
        RefreshDisplayFavorites();
    }

    public void AddFavorites(IEnumerable<FavoriteCommand> favorites)
    {
        var items = favorites.ToList();
        if (items.Count == 0)
        {
            return;
        }

        var list = _preferences.FavoriteCommands.ToList();
        list.AddRange(items);
        _preferences.SetFavoriteCommands(list);
        RefreshDisplayFavorites();
    }

    public void InsertFavoriteBefore(FavoriteCommand anchor, FavoriteCommand favorite)
    {
        InsertFavoriteNear(anchor, favorite, offset: 0);
    }

    public void InsertFavoriteAfter(FavoriteCommand anchor, FavoriteCommand favorite)
    {
        InsertFavoriteNear(anchor, favorite, offset: 1);
    }

    public void MoveFavoriteBefore(FavoriteCommand favorite, FavoriteCommand anchor)
    {
        MoveFavoriteNear(favorite, anchor, offset: 0);
    }

    public void MoveFavoriteAfter(FavoriteCommand favorite, FavoriteCommand anchor)
    {
        MoveFavoriteNear(favorite, anchor, offset: 1);
    }

    public void UpdateFavorite(FavoriteCommand old, FavoriteCommand updated)
    {
        var (list, setName) = FindFavoriteContainer(old);
        var index = list.IndexOf(old);
        if (index >= 0)
        {
            list[index] = updated;
            CommitFavoriteContainer(setName, list);
        }
    }

    public void RemoveFavorite(FavoriteCommand favorite)
    {
        var (list, setName) = FindFavoriteContainer(favorite);
        list.Remove(favorite);
        CommitFavoriteContainer(setName, list);
    }

    /// <summary>Name of the set that owns the favorite, or null when it lives in the base pool.</summary>
    public string? GetFavoriteOwningSetName(FavoriteCommand favorite) => FindFavoriteContainer(favorite).SetName;

    /// <summary>
    /// Resolves which container — the base pool (setName null) or a named set — holds the given
    /// favorite, by reference identity. Returns a mutable copy of that container's list; commit it
    /// back via <see cref="CommitFavoriteContainer"/>. Falls back to the base pool when the favorite
    /// is in neither (e.g. it was already deleted from another window).
    /// </summary>
    private (List<FavoriteCommand> List, string? SetName) FindFavoriteContainer(FavoriteCommand favorite)
    {
        foreach (var set in _preferences.FavoriteCommandSets)
        {
            if (set.Favorites.Contains(favorite))
            {
                return (set.Favorites.ToList(), set.Name);
            }
        }
        return (_preferences.FavoriteCommands.ToList(), null);
    }

    private void CommitFavoriteContainer(string? setName, List<FavoriteCommand> list)
    {
        if (setName is null)
        {
            _preferences.SetFavoriteCommands(list);
        }
        else
        {
            _preferences.SetFavoriteSetFavorites(setName, list);
        }
        RefreshDisplayFavorites();
    }

    private void InsertFavoriteNear(FavoriteCommand anchor, FavoriteCommand favorite, int offset)
    {
        var (list, setName) = FindFavoriteContainer(anchor);
        var anchorIndex = list.IndexOf(anchor);
        if (anchorIndex < 0)
        {
            list.Add(favorite);
        }
        else
        {
            list.Insert(anchorIndex + offset, favorite);
        }

        CommitFavoriteContainer(setName, list);
    }

    private void MoveFavoriteNear(FavoriteCommand favorite, FavoriteCommand anchor, int offset)
    {
        if (ReferenceEquals(favorite, anchor))
        {
            return;
        }

        // Bar/palette reordering stays within one container; moving a favorite between the base
        // pool and a set (or between sets) is the editor's job.
        var (list, setName) = FindFavoriteContainer(favorite);
        var (_, anchorSetName) = FindFavoriteContainer(anchor);
        if (!string.Equals(setName, anchorSetName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!list.Remove(favorite))
        {
            return;
        }

        var anchorIndex = list.IndexOf(anchor);
        if (anchorIndex < 0)
        {
            list.Add(favorite);
        }
        else
        {
            list.Insert(anchorIndex + offset, favorite);
        }

        CommitFavoriteContainer(setName, list);
    }

    /// <summary>Returns a copy of every saved favorite (all categories and scopes, including blank spacers).</summary>
    public List<FavoriteCommand> ExportFavorites()
    {
        return _preferences.FavoriteCommands.ToList();
    }

    /// <summary>Returns a copy of the saved favorites in a single category (including that category's blank spacers).</summary>
    public List<FavoriteCommand> ExportFavorites(FavoriteCommandCategory category)
    {
        var target = NormalizeFavoriteCategory(category);
        return _preferences.FavoriteCommands.Where(f => NormalizeFavoriteCategory(f.Category) == target).ToList();
    }

    /// <summary>
    /// Merges imported favorites into the existing set. <see cref="FavoriteImportMode.Append"/> keeps the existing
    /// favorites and adds the imported ones after them; <see cref="FavoriteImportMode.Replace"/> discards the existing
    /// set and keeps only the imported favorites. Null entries (from malformed JSON arrays) are dropped.
    /// </summary>
    public static List<FavoriteCommand> MergeImportedFavorites(
        IReadOnlyList<FavoriteCommand> existing,
        IReadOnlyList<FavoriteCommand> imported,
        FavoriteImportMode mode
    )
    {
        var incoming = imported.Where(f => f is not null).ToList();
        if (mode == FavoriteImportMode.Replace)
        {
            return incoming;
        }

        var merged = existing.Where(f => f is not null).ToList();
        merged.AddRange(incoming);
        return merged;
    }

    public void ImportFavorites(IReadOnlyList<FavoriteCommand> imported, FavoriteImportMode mode)
    {
        var list = MergeImportedFavorites(_preferences.FavoriteCommands, imported, mode);
        _preferences.SetFavoriteCommands(list);
        RefreshDisplayFavorites();
    }

    /// <summary>
    /// Merges an imported all-sets bundle into the existing named sets. <see cref="FavoriteImportMode.Append"/>
    /// appends each imported set's favorites onto the same-named existing set (case-insensitive) and adds
    /// unmatched imported sets as new ones; <see cref="FavoriteImportMode.Replace"/> discards the existing
    /// sets entirely and keeps only the imported ones. Sets with null/blank names are dropped.
    /// </summary>
    public static List<FavoriteCommandSet> MergeImportedFavoriteSets(
        IReadOnlyList<FavoriteCommandSet> existing,
        IReadOnlyList<FavoriteCommandSet> imported,
        FavoriteImportMode mode
    )
    {
        var incoming = imported.Where(s => (s is not null) && !string.IsNullOrWhiteSpace(s.Name)).ToList();
        foreach (var set in incoming)
        {
            set.Favorites = set.Favorites.Where(f => f is not null).ToList();
        }

        if (mode == FavoriteImportMode.Replace)
        {
            return incoming;
        }

        var merged = existing.Select(s => new FavoriteCommandSet { Name = s.Name, Favorites = s.Favorites.ToList() }).ToList();
        foreach (var set in incoming)
        {
            var match = merged.FirstOrDefault(s => string.Equals(s.Name, set.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                merged.Add(set);
            }
            else
            {
                match.Favorites.AddRange(set.Favorites);
            }
        }
        return merged;
    }

    /// <summary>
    /// Applies an imported all-sets bundle. Replace adopts the bundle's loaded-set list (unknown names
    /// are pruned by the store's normalize invariant); Append keeps the current loaded list untouched.
    /// </summary>
    public void ImportFavoriteSets(FavoriteSetsExportFile bundle, FavoriteImportMode mode)
    {
        var sets = MergeImportedFavoriteSets(_preferences.FavoriteCommandSets, bundle.Sets, mode);
        var loaded = mode == FavoriteImportMode.Replace ? bundle.LoadedSetNames.ToList() : _preferences.LoadedFavoriteSetNames.ToList();
        _preferences.ReplaceFavoriteSets(sets, loaded);
        RefreshDisplayFavorites();
    }
}

/// <summary>How an imported favorites file should combine with the user's existing favorites.</summary>
public enum FavoriteImportMode
{
    Append,
    Replace,
}
