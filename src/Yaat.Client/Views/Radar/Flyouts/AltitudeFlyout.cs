using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// EuroScope-style assigned-altitude picker. Builds a ContextMenu with FL010..FL400 in
/// 1000-ft steps; selection dispatches CM (climb) or DM (descend) based on current
/// altitude. The menu is height-capped so the visible window is roughly +/-5,000 ft
/// around the current altitude; all other values remain reachable by scrolling.
/// Opens with the current-altitude entry scrolled into view.
/// </summary>
internal static class AltitudeFlyout
{
    private const int MinFlHundreds = 10;
    private const int MaxFlHundreds = 400;
    private const int StepHundreds = 10;
    private const double MaxScrollHeight = 280.0;

    public static ContextMenu Build(AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        var menu = new ContextMenu();
        FlyoutAppearance.ApplyFontSize(menu);
        ApplyScrollCap(menu);

        menu.Items.Add(
            new MenuItem
            {
                Header = $"Altitude — {aircraft.Callsign}",
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        menu.Items.Add(new Separator());

        int currentFlHundreds = (int)aircraft.Altitude / 100;
        int? assignedFlHundreds = aircraft.AssignedAltitude is double a ? (int)a / 100 : null;
        int focusFl = assignedFlHundreds ?? RoundToStep(currentFlHundreds, StepHundreds);

        MenuItem? focusItem = null;

        for (int fl = MaxFlHundreds; fl >= MinFlHundreds; fl -= StepHundreds)
        {
            int targetFl = fl;
            string label = $"FL{fl:D3}";
            if (assignedFlHundreds == fl)
            {
                label = $"▶ {label}";
            }

            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await DispatchAsync(aircraft, radarVm, initials, targetFl, currentFlHundreds);
            menu.Items.Add(item);

            if (fl == focusFl)
            {
                focusItem = item;
            }
        }

        ScrollFocusItemIntoView(menu, focusItem);

        return menu;
    }

    private static int RoundToStep(int value, int step) => (int)Math.Round((double)value / step) * step;

    private static async Task DispatchAsync(AircraftModel ac, RadarViewModel vm, string initials, int targetFlHundreds, int currentFlHundreds)
    {
        if (targetFlHundreds >= currentFlHundreds)
        {
            await vm.ClimbAndMaintainAsync(ac.Callsign, initials, targetFlHundreds);
        }
        else
        {
            await vm.DescendAndMaintainAsync(ac.Callsign, initials, targetFlHundreds);
        }
    }

    /// <summary>
    /// Cap the ContextMenu's internal ScrollViewer so values outside the visible window
    /// are reached via mouse-wheel/keyboard scroll.
    /// </summary>
    internal static void ApplyScrollCap(ContextMenu menu)
    {
        var style = new Style(x => x.OfType<ScrollViewer>())
        {
            Setters = { new Avalonia.Styling.Setter(ScrollViewer.MaxHeightProperty, MaxScrollHeight) },
        };
        menu.Styles.Add(style);
    }

    /// <summary>
    /// Bring the current/assigned-value entry into view once the menu has been laid out.
    /// </summary>
    internal static void ScrollFocusItemIntoView(ContextMenu menu, MenuItem? focusItem)
    {
        if (focusItem is null)
        {
            return;
        }

        EventHandler<Avalonia.Interactivity.RoutedEventArgs>? handler = null;
        handler = (_, _) =>
        {
            menu.Opened -= handler;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => focusItem.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
        };
        menu.Opened += handler;
    }
}
