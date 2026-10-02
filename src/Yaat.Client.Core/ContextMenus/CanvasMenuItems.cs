using System.Globalization;
using Avalonia.Controls;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The canvas-only items a view section carries, each built from the state the surface hands it rather than from the
/// menu host; the overlay items send their commands through the host. They are the data-block form and its position
/// reset, the nav route, the measurement in progress, the taxi-route display mode, the hidden data block, the Draw route
/// entry and the overlays (leader direction, J-ring, cone, blank and unblank). A surface builds them from its own canvas
/// state and places them in its own section, so the labels stay testable headlessly.
/// </summary>
public static class CanvasMenuItems
{
    /// <summary>The taxi-route submenu's items, in menu order: each item's text and the mode it sets.</summary>
    private static readonly (string Label, TaxiRouteDisplayMode Mode)[] TaxiRouteModeItems =
    [
        ("Always show", TaxiRouteDisplayMode.AlwaysShow),
        ("Always hide", TaxiRouteDisplayMode.AlwaysHide),
        ("Follow “Show all” setting", TaxiRouteDisplayMode.Follow),
    ];

    /// <summary>The J-ring radii and cone lengths the overlay submenus offer, in nautical miles.</summary>
    private static readonly double[] RingDistances = [1.0, 2.0, 3.0, 5.0, 10.0];

    /// <summary>
    /// The data-block form item: it toggles the data block and reads "Mini datablock" or "Full datablock" by the form
    /// the surface is showing.
    /// </summary>
    public static MenuItem DataBlockForm(bool minified, Action toggle)
    {
        var item = new MenuItem { Header = minified ? "Full datablock" : "Mini datablock" };
        item.Click += (_, _) => toggle();
        return item;
    }

    /// <summary>
    /// The "Reset datablock position" item, which the surface offers only while the data block sits away from the
    /// position the student sees it in; null otherwise.
    /// </summary>
    public static MenuItem? ResetDataBlockPosition(bool moved, Action reset)
    {
        if (!moved)
        {
            return null;
        }

        var item = new MenuItem { Header = "Reset datablock position" };
        item.Click += (_, _) => reset();
        return item;
    }

    /// <summary>The nav-route item: it toggles the route and reads "Show nav route" or "Hide nav route" by whether it is drawn.</summary>
    public static MenuItem NavRoute(bool shown, Action toggle)
    {
        var item = new MenuItem { Header = shown ? "Hide nav route" : "Show nav route" };
        item.Click += (_, _) => toggle();
        return item;
    }

    /// <summary>
    /// The measure item, offered whenever the surface has a measure tool: it reads "Measure from {callsign}" while the
    /// tool has no endpoint and "Measure to {callsign}" once one is anchored, and latches that endpoint to the
    /// aircraft. Null with no tool at all.
    /// </summary>
    public static MenuItem? Measure(MenuMeasureState state, string callsign, Action pick)
    {
        if (state == MenuMeasureState.None)
        {
            return null;
        }

        string direction = state == MenuMeasureState.HasAnchor ? "to" : "from";
        var item = new MenuItem { Header = $"Measure {direction} {callsign}" };
        item.Click += (_, _) => pick();
        return item;
    }

    /// <summary>
    /// The taxi-route submenu: one radio item per <see cref="TaxiRouteDisplayMode"/>, <paramref name="mode"/> checked,
    /// each setting that mode on the surface when clicked.
    /// </summary>
    public static MenuItem TaxiRoute(TaxiRouteDisplayMode mode, Action<TaxiRouteDisplayMode> set)
    {
        var menu = new MenuItem { Header = "Taxi route" };
        foreach ((string itemLabel, TaxiRouteDisplayMode itemMode) in TaxiRouteModeItems)
        {
            var item = new MenuItem
            {
                Header = itemLabel,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "TaxiRouteMode",
                IsChecked = itemMode == mode,
            };
            item.Click += (_, _) => set(itemMode);
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>The hidden-datablock item: it toggles the data block and reads "Show datablock" while it is hidden.</summary>
    public static MenuItem HideDataBlock(bool hidden, Action toggle)
    {
        var item = new MenuItem { Header = hidden ? "Show datablock" : "Hide datablock" };
        item.Click += (_, _) => toggle();
        return item;
    }

    /// <summary>The Draw route item, which puts the surface into drawing a route for the aircraft.</summary>
    public static MenuItem DrawRoute(string label, Action enter)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => enter();
        return item;
    }

    /// <summary>The leader-direction submenu: one line per 1-9, with 5 marked as the STARS default; each sends <c>LDR n</c>.</summary>
    public static MenuItem LeaderDirection(MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = "Leader direction" };
        for (int direction = 1; direction <= 9; direction++)
        {
            string itemLabel = direction == 5 ? "5 (default)" : direction.ToString(CultureInfo.InvariantCulture);
            menu.Items.Add(MenuCatalog.BuildSend(itemLabel, $"LDR {direction}", context, host));
        }

        return menu;
    }

    /// <summary>The J-ring submenu: Clear turns the overlay off, then one item per ring radius.</summary>
    public static MenuItem JRing(MenuContext context, IMenuHost host) => RingMenu("J-ring", "JRING", context, host);

    /// <summary>The cone submenu: Clear turns the overlay off, then one item per cone length.</summary>
    public static MenuItem Cone(MenuContext context, IMenuHost host) => RingMenu("Cone", "CONE", context, host);

    /// <summary>The blank item, which sends <c>BLANK</c> for the menu's aircraft.</summary>
    public static MenuItem Blank(MenuContext context, IMenuHost host) => MenuCatalog.BuildSend("Blank target", "BLANK", context, host);

    /// <summary>The unblank item, which sends <c>BLANKD</c> for the menu's aircraft.</summary>
    public static MenuItem Unblank(MenuContext context, IMenuHost host) => MenuCatalog.BuildSend("Unblank target", "BLANKD", context, host);

    /// <summary>
    /// The Display submenu from the surface's blocks, in order: each block's items are added after a separator when
    /// they follow at least one item and the block has any, so no block opens or closes the submenu with a separator
    /// and no two sit in a row. A block whose items are all null (hidden or unsupported) adds nothing.
    /// </summary>
    public static MenuItem Display(IReadOnlyList<IReadOnlyList<MenuItem?>> blocks)
    {
        var menu = new MenuItem { Header = "Display" };
        foreach (IReadOnlyList<MenuItem?> block in blocks)
        {
            AddItemsAsBlock(menu.Items, block);
        }

        return menu;
    }

    /// <summary>
    /// A J-ring or cone submenu: Clear sends the bare command, then each distance sends it with the size. The item
    /// reads "3 nm" while the command carries the same figure without the unit.
    /// </summary>
    private static MenuItem RingMenu(string label, string command, MenuContext context, IMenuHost host)
    {
        var menu = new MenuItem { Header = label };
        menu.Items.Add(MenuCatalog.BuildSend("Clear", command, context, host));
        foreach (double distance in RingDistances)
        {
            string size = distance.ToString("0.#", CultureInfo.InvariantCulture);
            menu.Items.Add(MenuCatalog.BuildSend($"{distance:0} nm", $"{command} {size}", context, host));
        }

        return menu;
    }

    /// <summary>
    /// Adds the block's items after a separator when <paramref name="items"/> already holds at least one item and the
    /// block adds at least one of its own, so a block never opens or closes the group with a separator and no two sit
    /// in a row.
    /// </summary>
    private static void AddItemsAsBlock(ItemCollection items, IReadOnlyList<MenuItem?> block)
    {
        if (!block.Any(item => item is not null))
        {
            return;
        }

        if (items.Count > 0)
        {
            items.Add(new Separator());
        }

        foreach (MenuItem? item in block)
        {
            if (item is not null)
            {
                items.Add(item);
            }
        }
    }
}
