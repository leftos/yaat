using Avalonia.Controls;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// The "Settings for this view…" item that ends the right-click menus of the radar, ground, aircraft list and terminal
/// views: it asks the main view model for Settings opened at the view's section.
/// </summary>
internal static class ViewSettingsMenu
{
    public const string Header = "Settings for this view…";

    /// <summary>
    /// Appends the item to <paramref name="menu"/>, after a separator when the menu has items and does not already end in one.
    /// Without a main view model (a view not hosted in a YAAT window) there is nothing to ask, so the menu is left as it is.
    /// </summary>
    public static void Append(ItemsControl menu, MainViewModel? mainVm, SettingsSectionId section)
    {
        if (mainVm is null)
        {
            return;
        }

        if ((menu.Items.Count > 0) && (menu.Items[^1] is not Separator))
        {
            menu.Items.Add(new Separator());
        }

        var item = new MenuItem { Header = Header };
        item.Click += (_, _) => mainVm.RequestSettings(section);
        menu.Items.Add(item);
    }
}
