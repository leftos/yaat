using Avalonia.Controls;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public static class FavoritesContextMenu
{
    public static MenuItem Build(MainViewModel? vm, AircraftModel? aircraft, string callsign, string initials)
    {
        var menu = new MenuItem { Header = "Favorite Commands" };

        if (vm is null)
        {
            menu.Items.Add(new MenuItem { Header = "(no items)", IsEnabled = false });
            return menu;
        }

        var entries = FavoritesContextMenuModel.Build(vm.DisplayFavorites, aircraft);
        if (entries.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(no items)", IsEnabled = false });
            return menu;
        }

        foreach (var entry in entries)
        {
            if (entry.IsSpacer)
            {
                menu.Items.Add(new Separator());
                continue;
            }

            var text = entry.CommandText;
            var item = new MenuItem { Header = entry.Label };
            item.Click += async (_, _) => await vm.Connection.SendCommandAsync(callsign, text, initials);
            menu.Items.Add(item);
        }

        return menu;
    }
}
