using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One row of the container picker shown in the favorite flyouts: an existing set (SetId non-null)
/// or a container that will be created on demand when first checked (the active airport/scenario
/// before any favorite was saved to it).
/// </summary>
public sealed record FavoriteContainerOption(string Label, string? SetId, FavoriteSetKind Kind, string? Key);

/// <summary>
/// Favorite commands: quick-access buttons for frequently used commands. Favorites are identity
/// entities in <see cref="FavoriteStore"/>; every container (Global, per-airport, per-scenario,
/// named sets) is a set of favorite ids, and the display renders each visible container's block.
/// </summary>
public partial class MainViewModel
{
    public FavoriteStore FavoriteStore => _favoriteStore;

    public ObservableCollection<FavoriteDisplayEntry> DisplayFavorites { get; } = [];

    /// <summary>
    /// Label shown in the pop-out Favorites Panel status bar so the user can see which aircraft a
    /// favorite click will act on (favorites target <see cref="SelectedAircraft"/>). Notified via
    /// <c>[NotifyPropertyChangedFor]</c> on <c>SelectedAircraft</c>.
    /// </summary>
    public string FavoritePanelTargetText => SelectedAircraft is { } aircraft ? $"Target: {aircraft.Callsign}" : "No aircraft selected";

    public void RefreshDisplayFavorites()
    {
        var composed = _favoriteStore.ComposeDisplay(ActiveScenarioId, ActiveScenarioPrimaryAirportId, _preferences.LoadedFavoriteSetIds);

        // Entries are records over stable entity references, so sequence equality is a valid
        // change detector. Skipping the no-op case matters: mutators refresh synchronously AND
        // the store's Changed event posts a deferred refresh, so without this guard every
        // ordinary favorites action would rebuild the bar/panel a second time.
        if (composed.SequenceEqual(DisplayFavorites))
        {
            return;
        }

        DisplayFavorites.Clear();
        foreach (var entry in composed)
        {
            DisplayFavorites.Add(entry);
        }
    }

    /// <summary>
    /// The container list for the flyout membership pickers, in display order: Global, the active
    /// airport (created on demand), other airports with favorites, the active scenario (created on
    /// demand), other scenarios with favorites, then every named set.
    /// </summary>
    public List<FavoriteContainerOption> BuildFavoriteContainerOptions()
    {
        var options = new List<FavoriteContainerOption> { new("Global", _favoriteStore.GlobalSet.Id, FavoriteSetKind.Global, Key: null) };

        var activeAirport = FavoriteStore.NormalizeAirportId(ActiveScenarioPrimaryAirportId);
        if (activeAirport is not null && _favoriteStore.FindAirportSet(activeAirport) is null)
        {
            options.Add(new FavoriteContainerOption($"Airport ({activeAirport})", SetId: null, FavoriteSetKind.Airport, activeAirport));
        }

        var activeScenarioPending = ActiveScenarioId is not null && _favoriteStore.FindScenarioSet(ActiveScenarioId) is null;
        foreach (var set in _favoriteStore.OrderedSets)
        {
            switch (set.Kind)
            {
                case FavoriteSetKind.Airport:
                case FavoriteSetKind.Scenario:
                    options.Add(new FavoriteContainerOption(set.DisplayName, set.Id, set.Kind, set.Key));
                    break;
                case FavoriteSetKind.Named:
                    var label = IsFavoriteSetLoaded(set.Id) ? set.Name : $"{set.Name} (not loaded)";
                    options.Add(new FavoriteContainerOption(label, set.Id, set.Kind, Key: null));
                    break;
            }
        }

        if (activeScenarioPending)
        {
            var scenarioLabel = $"Scenario ({ActiveScenarioName ?? ActiveScenarioId})";
            var scenarioOption = new FavoriteContainerOption(scenarioLabel, SetId: null, FavoriteSetKind.Scenario, ActiveScenarioId);
            var insertAt = options.FindLastIndex(o => o.Kind is FavoriteSetKind.Global or FavoriteSetKind.Airport or FavoriteSetKind.Scenario) + 1;
            options.Insert(insertAt, scenarioOption);
        }

        return options;
    }

    /// <summary>Resolves a picker option to a concrete set id, creating the on-demand airport/scenario container if needed.</summary>
    public string EnsureFavoriteContainer(FavoriteContainerOption option)
    {
        if (option.SetId is not null)
        {
            return option.SetId;
        }

        return option.Kind switch
        {
            FavoriteSetKind.Airport when option.Key is not null => _favoriteStore.GetOrCreateAirportSet(option.Key).Id,
            FavoriteSetKind.Scenario when option.Key is not null => _favoriteStore
                .GetOrCreateScenarioSet(option.Key, ActiveScenarioName ?? option.Key)
                .Id,
            _ => _favoriteStore.GlobalSet.Id,
        };
    }

    public IReadOnlyList<FavoriteSet> NamedFavoriteSets => _favoriteStore.OrderedSets.Where(s => s.Kind == FavoriteSetKind.Named).ToList();

    public bool IsFavoriteSetLoaded(string setId) =>
        _preferences.LoadedFavoriteSetIds.Any(id => string.Equals(id, setId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads (appends to the load order) or unloads one named set; the display refreshes via FavoriteSetsChanged.</summary>
    public void SetFavoriteSetLoaded(string setId, bool loaded)
    {
        _preferences.SetFavoriteSetLoaded(setId, loaded);
        RefreshDisplayFavorites();
    }

    public static FavoriteCommandCategory NormalizeFavoriteCategory(FavoriteCommandCategory category)
    {
        return Enum.IsDefined(category) ? category : FavoriteCommandCategory.Air;
    }

    /// <summary>Airport-id normalization shared with the video-map favorite scopes (trim + uppercase, blank → null).</summary>
    public static string? NormalizeFavoriteAirportId(string? airportId) => FavoriteStore.NormalizeAirportId(airportId);

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

    /// <summary>Saves the favorite entity and appends it to each destination set.</summary>
    public void AddFavorite(FavoriteCommand favorite, IReadOnlyList<string> setIds)
    {
        AddFavorites([favorite], setIds);
    }

    /// <summary>Saves each favorite entity and appends all of them to each destination set (shared entities, not copies).</summary>
    public void AddFavorites(IReadOnlyList<FavoriteCommand> favorites, IReadOnlyList<string> setIds)
    {
        foreach (var favorite in favorites)
        {
            _favoriteStore.SaveFavorite(favorite);
            foreach (var setId in setIds)
            {
                _favoriteStore.AddToSet(setId, favorite.Id);
            }
        }
        RefreshDisplayFavorites();
    }

    /// <summary>
    /// Commits an edit to the entity (visible in every container at once) and syncs its membership
    /// to exactly the given set ids: newly checked sets get it appended, unchecked ones lose it.
    /// </summary>
    public void UpdateFavorite(FavoriteCommand updated, IReadOnlyList<string> setIds)
    {
        _favoriteStore.SaveFavorite(updated);
        var current = _favoriteStore.GetMembershipSetIds(updated.Id);
        foreach (var setId in setIds.Where(id => !current.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            _favoriteStore.AddToSet(setId, updated.Id);
        }

        foreach (var setId in current.Where(id => !setIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            _favoriteStore.RemoveFromSet(setId, updated.Id);
        }
        RefreshDisplayFavorites();
    }

    /// <summary>Deletes the entity everywhere (all containers, its file, the works).</summary>
    public void DeleteFavorite(string favoriteId)
    {
        _favoriteStore.DeleteFavorite(favoriteId);
        RefreshDisplayFavorites();
    }

    public List<string> GetFavoriteMembership(string favoriteId) => _favoriteStore.GetMembershipSetIds(favoriteId);

    public void InsertBlankBefore(FavoriteDisplayEntry anchor, FavoriteCommand blank)
    {
        InsertBlankNear(anchor, blank, offset: 0);
    }

    public void InsertBlankAfter(FavoriteDisplayEntry anchor, FavoriteCommand blank)
    {
        InsertBlankNear(anchor, blank, offset: 1);
    }

    public void MoveFavoriteBefore(FavoriteDisplayEntry moving, FavoriteDisplayEntry anchor)
    {
        MoveFavoriteNear(moving, anchor, offset: 0);
    }

    public void MoveFavoriteAfter(FavoriteDisplayEntry moving, FavoriteDisplayEntry anchor)
    {
        MoveFavoriteNear(moving, anchor, offset: 1);
    }

    private void InsertBlankNear(FavoriteDisplayEntry anchor, FavoriteCommand blank, int offset)
    {
        var set = _favoriteStore.GetSet(anchor.SetId);
        if (set is null)
        {
            return;
        }

        _favoriteStore.SaveFavorite(blank);
        var anchorIndex = set.FavoriteIds.FindIndex(id => string.Equals(id, anchor.Favorite.Id, StringComparison.OrdinalIgnoreCase));
        _favoriteStore.InsertInSet(set.Id, blank.Id, anchorIndex < 0 ? int.MaxValue : anchorIndex + offset);
        RefreshDisplayFavorites();
    }

    private void MoveFavoriteNear(FavoriteDisplayEntry moving, FavoriteDisplayEntry anchor, int offset)
    {
        // Bar/palette reordering stays within one container; changing which containers a favorite
        // is in is the membership checkboxes' (or the editor's) job.
        if (moving == anchor || !string.Equals(moving.SetId, anchor.SetId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var set = _favoriteStore.GetSet(moving.SetId);
        if (set is null)
        {
            return;
        }

        var ids = set.FavoriteIds.ToList();
        if (ids.RemoveAll(id => string.Equals(id, moving.Favorite.Id, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return;
        }

        var anchorIndex = ids.FindIndex(id => string.Equals(id, anchor.Favorite.Id, StringComparison.OrdinalIgnoreCase));
        if (anchorIndex < 0)
        {
            ids.Add(moving.Favorite.Id);
        }
        else
        {
            ids.Insert(anchorIndex + offset, moving.Favorite.Id);
        }

        _favoriteStore.ReplaceSetFavorites(set.Id, ids);
        RefreshDisplayFavorites();
    }

    public void ExportFavoriteSet(string setId, Stream output) => FavoriteExport.ExportSet(_favoriteStore, setId, output);

    public void ExportFavoriteLibrary(Stream output) => FavoriteExport.ExportLibrary(_favoriteStore, _preferences.LoadedFavoriteSetIds, output);

    /// <summary>Imports a favorites zip or entity json; newly imported sets that the export had loaded get loaded here too.</summary>
    public FavoriteImportResult? ImportFavoritesFile(string fileName, Stream input)
    {
        var result = FavoriteExport.ImportFile(_favoriteStore, fileName, input);
        if (result is null)
        {
            return null;
        }

        foreach (var setId in result.NewSetIdsToLoad)
        {
            _preferences.SetFavoriteSetLoaded(setId, true);
        }
        RefreshDisplayFavorites();
        return result;
    }
}
