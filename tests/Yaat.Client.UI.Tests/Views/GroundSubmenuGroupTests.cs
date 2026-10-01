using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using CatalogMenuView = Yaat.Client.ContextMenus.MenuView;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Core hold-short and follow groups over a <see cref="RecordingMenuHost"/>: they build each submenu only from the
/// host's answers, send the host's finished commands or <c>FOLLOWG</c> / <c>GW</c> with the host's callsigns in the
/// host's order, and preview only the choices that carry a route.
/// </summary>
public class GroundSubmenuGroupTests
{
    private const string Callsign = "SWA104";
    private const string Initials = "AB";

    private static readonly MenuContext Context = new(Callsign, Initials, null, false, VfrCommandsForIfr.None, CatalogMenuView.Ground);

    private static AircraftModel Taxiing() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "Taxiing",
            HasActiveTaxiRoute = true,
        };

    private static ContextMenu BuildTaxiGroups(RecordingMenuHost host)
    {
        AircraftModel ac = Taxiing();
        var menu = new ContextMenu();
        SharedMenuGroups.AddGroundHoldShort(menu.Items, ac, Context, host);
        SharedMenuGroups.AddGroundFollowAndGiveWay(menu.Items, ac, Context, host, GroundFollowPosition.Taxi);
        return menu;
    }

    [AvaloniaFact]
    public void EmptyHostAnswers_BuildNoHoldShortFollowOrGiveWaySubmenu()
    {
        var host = new RecordingMenuHost("");

        ContextMenu menu = BuildTaxiGroups(host);

        Assert.Empty(menu.Items);
    }

    [AvaloniaFact]
    public void HoldShort_OnlyAChoiceWithAPreviewSetsTheRoutePreview()
    {
        var route = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        var host = new RecordingMenuHost("");
        host.HoldShortChoices.Add(new MenuCommandChoice("Runway 12", "HS 12", null));
        host.HoldShortChoices.Add(new MenuCommandChoice("Runway 30", "HS 30", route));

        ContextMenu menu = BuildTaxiGroups(host);
        MenuItem holdShort = Item(menu.Items, "Hold short of...");
        Assert.Equal(["Runway 12", "Runway 30"], Headers(holdShort.Items));

        RaisePointerEntered(Item(holdShort.Items, "Runway 12"));
        Assert.Empty(host.RoutePreviews);

        RaisePointerEntered(Item(holdShort.Items, "Runway 30"));
        Assert.Equal([route], host.RoutePreviews);

        Click(Item(holdShort.Items, "Runway 12"));
        Click(Item(holdShort.Items, "Runway 30"));
        Assert.Equal([(Callsign, "HS 12", Initials), (Callsign, "HS 30", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void FollowAndGiveWay_ListTheHostTrafficInOrder_AndSendFOLLOWGAndGW()
    {
        string[] traffic = ["SWA200", "AAL1", "DAL300"];
        var host = new RecordingMenuHost("");
        host.GroundTraffic.AddRange(traffic);

        ContextMenu menu = BuildTaxiGroups(host);
        Assert.Equal(["Follow...", "Give way to..."], Headers(menu.Items));

        MenuItem follow = Item(menu.Items, "Follow...");
        MenuItem giveWay = Item(menu.Items, "Give way to...");
        Assert.Equal(traffic, Headers(follow.Items));
        Assert.Equal(traffic, Headers(giveWay.Items));

        foreach (string other in traffic)
        {
            Click(Item(follow.Items, other));
        }

        foreach (string other in traffic)
        {
            Click(Item(giveWay.Items, other));
        }

        Assert.Equal(
            [.. traffic.Select(t => (Callsign, $"FOLLOWG {t}", Initials)), .. traffic.Select(t => (Callsign, $"GW {t}", Initials))],
            host.Sent
        );
    }

    private static List<string> Headers(ItemCollection items) =>
        [.. items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!)];

    private static MenuItem Item(ItemCollection items, string header)
    {
        MenuItem? item = items.OfType<MenuItem>().FirstOrDefault(m => m.Header is string s && s == header);
        Assert.NotNull(item);
        return item;
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    /// <summary>
    /// Raises a pointer enter on <paramref name="item"/> with a real <see cref="PointerEventArgs"/>, which the typed
    /// <see cref="InputElement.PointerEntered"/> handler requires (see <c>GroundSubmenuCharacterizationTests.RaisePointer</c>).
    /// </summary>
    private static void RaisePointerEntered(MenuItem item) =>
        item.RaiseEvent(
            new PointerEventArgs(
                InputElement.PointerEnteredEvent,
                item,
                new Pointer(0, PointerType.Mouse, isPrimary: true),
                rootVisual: null,
                rootVisualPosition: default,
                timestamp: 0,
                properties: default,
                modifiers: KeyModifiers.None
            )
        );
}
