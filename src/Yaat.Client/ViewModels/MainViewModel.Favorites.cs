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
        foreach (var fav in _preferences.FavoriteCommands)
        {
            if (fav.ScenarioId is null || fav.ScenarioId == scenarioId)
            {
                DisplayFavorites.Add(fav);
            }
        }
    }

    public async Task ExecuteFavoriteAsync(FavoriteCommand favorite)
    {
        var currentText = CommandText.Trim();

        // Build the full command: "{currentText} {fav.CommandText}" or just "{fav.CommandText}"
        var fullCommand = string.IsNullOrEmpty(currentText) ? favorite.CommandText : $"{currentText} {favorite.CommandText}";

        var savedText = CommandText;
        CommandText = fullCommand;

        try
        {
            await SendCommandAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Favorite command execution failed");
            CommandText = savedText;
        }
    }

    public void AppendFavoriteToInput(FavoriteCommand favorite)
    {
        var current = CommandText.TrimEnd();
        if (string.IsNullOrEmpty(current))
        {
            CommandText = favorite.CommandText;
        }
        else
        {
            CommandText = $"{current}, {favorite.CommandText}";
        }
    }

    public void AddFavorite(FavoriteCommand favorite)
    {
        var list = _preferences.FavoriteCommands.ToList();
        list.Add(favorite);
        _preferences.SetFavoriteCommands(list);
        RefreshDisplayFavorites();
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
}
