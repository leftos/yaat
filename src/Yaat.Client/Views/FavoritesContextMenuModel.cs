using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public readonly record struct FavoritesMenuEntry(bool IsSpacer, string Label, string CommandText);

public static class FavoritesContextMenuModel
{
    public static IReadOnlyList<FavoritesMenuEntry> Build(IEnumerable<FavoriteCommand> visibleFavorites, AircraftModel? aircraft)
    {
        var raw = new List<FavoritesMenuEntry>();
        foreach (var fav in visibleFavorites)
        {
            if (fav.IsSpacer)
            {
                raw.Add(new FavoritesMenuEntry(IsSpacer: true, Label: "", CommandText: ""));
                continue;
            }

            var text = MainViewModel.ResolveFavoriteCommandTextFor(fav, aircraft);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(fav.Label) ? text : fav.Label;
            raw.Add(new FavoritesMenuEntry(IsSpacer: false, Label: label, CommandText: text));
        }

        var result = new List<FavoritesMenuEntry>(raw.Count);
        var seenNonSpacer = false;
        foreach (var entry in raw)
        {
            if (entry.IsSpacer)
            {
                if (!seenNonSpacer)
                {
                    continue;
                }

                if (result.Count > 0 && result[^1].IsSpacer)
                {
                    continue;
                }

                result.Add(entry);
            }
            else
            {
                result.Add(entry);
                seenNonSpacer = true;
            }
        }

        while (result.Count > 0 && result[^1].IsSpacer)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }
}
