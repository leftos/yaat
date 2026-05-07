using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Favorite commands: quick-access buttons for frequently used commands.
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<FavoriteCommand> DisplayFavorites { get; } = [];

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

    public string ResolveFavoriteCommandText(FavoriteCommand favorite)
    {
        if (favorite.IsSpacer)
        {
            return "";
        }

        if (SelectedAircraft?.IsOnGround == true && !string.IsNullOrWhiteSpace(favorite.GroundCommandText))
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
}
