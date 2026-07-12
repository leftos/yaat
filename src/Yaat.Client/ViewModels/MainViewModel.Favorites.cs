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
        DisplayFavorites.Clear();
        var scenarioId = ActiveScenarioId;
        var airportId = ActiveScenarioPrimaryAirportId;
        foreach (var fav in _preferences.FavoriteCommands)
        {
            if (IsFavoriteVisible(fav, scenarioId, airportId))
            {
                DisplayFavorites.Add(fav);
            }
        }
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
        var list = _preferences.FavoriteCommands.ToList();
        var index = list.IndexOf(old);
        if (index >= 0)
        {
            list[index] = updated;
            _preferences.SetFavoriteCommands(list);
            RefreshDisplayFavorites();
        }
    }

    public void RemoveFavorite(FavoriteCommand favorite)
    {
        var list = _preferences.FavoriteCommands.ToList();
        list.Remove(favorite);
        _preferences.SetFavoriteCommands(list);
        RefreshDisplayFavorites();
    }

    private void InsertFavoriteNear(FavoriteCommand anchor, FavoriteCommand favorite, int offset)
    {
        var list = _preferences.FavoriteCommands.ToList();
        var anchorIndex = list.IndexOf(anchor);
        if (anchorIndex < 0)
        {
            list.Add(favorite);
        }
        else
        {
            list.Insert(anchorIndex + offset, favorite);
        }

        _preferences.SetFavoriteCommands(list);
        RefreshDisplayFavorites();
    }

    private void MoveFavoriteNear(FavoriteCommand favorite, FavoriteCommand anchor, int offset)
    {
        if (ReferenceEquals(favorite, anchor))
        {
            return;
        }

        var list = _preferences.FavoriteCommands.ToList();
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

        _preferences.SetFavoriteCommands(list);
        RefreshDisplayFavorites();
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
}

/// <summary>How an imported favorites file should combine with the user's existing favorites.</summary>
public enum FavoriteImportMode
{
    Append,
    Replace,
}
