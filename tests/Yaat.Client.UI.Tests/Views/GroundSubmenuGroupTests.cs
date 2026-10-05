using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The builder's hold-short, follow, pushback and taxi-route items over a <see cref="RecordingMenuHost"/>: they build
/// each submenu and the flat face items only from the host's answers, send the host's finished commands or
/// <c>FOLLOWG</c> / <c>GW</c> with the host's callsigns in the host's order, preview only the choices that carry a
/// route, and hand push route and taxi-route drawing to the host.
/// </summary>
public class GroundSubmenuGroupTests
{
    private const string Callsign = "SWA104";
    private const string Initials = "AB";

    /// <summary>The whole aircraft menu for <paramref name="ac"/> over <paramref name="host"/>, with no view section.</summary>
    private static ContextMenu BuildMenu(RecordingMenuHost host, AircraftModel ac) =>
        AircraftMenuBuilder.Build(ac, new MenuClick(Callsign, null, null, []), host, _ => []);

    /// <summary>The top-level headers of <paramref name="menu"/> that <paramref name="keep"/> picks, in order.</summary>
    private static List<string> HeadersWhere(ContextMenu menu, Func<string, bool> keep) => [.. Headers(CommandTree(menu)).Where(keep)];

    private static bool IsTaxiGroup(string header) => header is "Hold short of…" or "Follow…" or "Give way to…";

    private static bool IsPushItem(string header) => header.StartsWith("Push", StringComparison.Ordinal);

    private static bool IsTaxiRouteItem(string header) => header is "Preset taxi route" or "Draw taxi route…";

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

    private static ContextMenu BuildTaxiGroups(RecordingMenuHost host) => BuildMenu(host, Taxiing());

    [AvaloniaFact]
    public void EmptyHostAnswers_BuildNoHoldShortFollowOrGiveWaySubmenu()
    {
        var host = new RecordingMenuHost("");

        ContextMenu menu = BuildTaxiGroups(host);

        Assert.Empty(HeadersWhere(menu, IsTaxiGroup));
    }

    [AvaloniaFact]
    public void HoldShort_OnlyAChoiceWithAPreviewSetsTheRoutePreview()
    {
        var route = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        var host = new RecordingMenuHost("");
        host.HoldShortChoices.Add(new MenuCommandChoice("Runway 12", "HS 12", null, []));
        host.HoldShortChoices.Add(new MenuCommandChoice("Runway 30", "HS 30", route, []));

        ContextMenu menu = BuildTaxiGroups(host);
        MenuItem holdShort = Item(CommandTree(menu), "Hold short of…");
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
        Assert.Equal(["Follow…", "Give way to…"], HeadersWhere(menu, IsTaxiGroup));

        MenuItem follow = Item(CommandTree(menu), "Follow…");
        MenuItem giveWay = Item(CommandTree(menu), "Give way to…");
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

    // --- The pushback and taxi-route blocks --------------------------------------------------

    private static AircraftModel Parked() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            FlightRules = "IFR",
            IsOnGround = true,
            CurrentPhase = "At Parking",
        };

    private static ContextMenu BuildPushbackGroup(RecordingMenuHost host) => BuildMenu(host, Parked());

    [AvaloniaFact]
    public void Pushback_FaceItemsFollowPushBack_AndSendTheHostsCommands()
    {
        var host = new RecordingMenuHost("");
        host.PushbackFaceChoices.Add(new MenuCommandChoice("Push back, face W1", "PUSH FACE N", null, []));
        host.PushbackFaceChoices.Add(new MenuCommandChoice("Push back, face W2", "PUSH FACE SE", null, []));

        ContextMenu menu = BuildPushbackGroup(host);
        Assert.Equal(["Push back", "Push back, face W1", "Push back, face W2", "Push route…"], HeadersWhere(menu, IsPushItem));

        Click(Item(CommandTree(menu), "Push back, face W1"));
        Click(Item(CommandTree(menu), "Push back, face W2"));
        Assert.Equal([(Callsign, "PUSH FACE N", Initials), (Callsign, "PUSH FACE SE", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Pushback_PushBackToListsTheHostStandsInOrder_AndSendsTheirCommands()
    {
        var host = new RecordingMenuHost("");
        host.PushbackToChoices.Add(new MenuCommandChoice("1", "PUSH $1", null, []));
        host.PushbackToChoices.Add(new MenuCommandChoice("32", "PUSH @32", null, []));

        ContextMenu menu = BuildPushbackGroup(host);
        Assert.Equal(["Push back", "Push back to…", "Push route…"], HeadersWhere(menu, IsPushItem));

        MenuItem pushTo = Item(CommandTree(menu), "Push back to…");
        Assert.Equal(["1", "32"], Headers(pushTo.Items));

        Click(Item(pushTo.Items, "32"));
        Click(Item(pushTo.Items, "1"));
        Assert.Equal([(Callsign, "PUSH @32", Initials), (Callsign, "PUSH $1", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void Pushback_PushRouteEntersPushRouteDrawing_AndSendsNothing()
    {
        var host = new RecordingMenuHost("");

        Click(Item(CommandTree(BuildPushbackGroup(host)), "Push route…"));

        Assert.Equal([Callsign], host.PushRouteCallsigns);
        Assert.Empty(host.Sent);
    }

    [AvaloniaFact]
    public void Pushback_EmptyHostAnswers_BuildNoFaceItemsOrPushBackToSubmenu()
    {
        var host = new RecordingMenuHost("");

        Assert.Equal(["Push back", "Push route…"], HeadersWhere(BuildPushbackGroup(host), IsPushItem));
    }

    [AvaloniaFact]
    public void Pushback_NotOfferedWhileTaxiing()
    {
        var host = new RecordingMenuHost("");
        host.PushbackFaceChoices.Add(new MenuCommandChoice("Push back, face W1", "PUSH FACE N", null, []));
        host.PushbackToChoices.Add(new MenuCommandChoice("1", "PUSH $1", null, []));

        Assert.Empty(HeadersWhere(BuildMenu(host, Taxiing()), IsPushItem));
    }

    [AvaloniaFact]
    public void TaxiRoutes_PresetsSendTheHostsCommands_AndDrawTaxiRouteEntersDrawing()
    {
        var host = new RecordingMenuHost("");
        host.PresetTaxiChoices.Add(new MenuCommandChoice("TERMINAL to 30", "TAXI T U W RWY 30", null, []));
        host.PresetTaxiChoices.Add(new MenuCommandChoice("TERMINAL to 28R", "TAXI B C RWY 28R", null, []));

        ContextMenu menu = BuildMenu(host, Taxiing());
        Assert.Equal(["Preset taxi route", "Draw taxi route…"], HeadersWhere(menu, IsTaxiRouteItem));

        MenuItem presets = Item(CommandTree(menu), "Preset taxi route");
        Assert.Equal(["TERMINAL to 30", "TERMINAL to 28R"], Headers(presets.Items));
        Click(Item(presets.Items, "TERMINAL to 28R"));
        Assert.Equal([(Callsign, "TAXI B C RWY 28R", Initials)], host.Sent);

        Click(Item(CommandTree(menu), "Draw taxi route…"));
        Assert.Equal([Callsign], host.DrawRouteCallsigns);
    }

    [AvaloniaFact]
    public void TaxiRoutes_NoPresets_BuildsOnlyDrawTaxiRoute()
    {
        var host = new RecordingMenuHost("");

        ContextMenu menu = BuildMenu(host, Taxiing());

        Assert.Equal(["Draw taxi route…"], HeadersWhere(menu, IsTaxiRouteItem));
    }

    [AvaloniaFact]
    public void TaxiRoutes_NotOfferedAirborne()
    {
        var host = new RecordingMenuHost("");
        host.PresetTaxiChoices.Add(new MenuCommandChoice("TERMINAL to 30", "TAXI T U W RWY 30", null, []));
        AircraftModel airborne = Taxiing();
        airborne.IsOnGround = false;
        airborne.CurrentPhase = "ApproachNav";

        Assert.Empty(HeadersWhere(BuildMenu(host, airborne), IsTaxiRouteItem));
    }

    private static List<string> Headers(ItemCollection items) =>
        [.. items.OfType<MenuItem>().Where(m => m.Header is string).Select(m => (string)m.Header!)];

    /// <summary>The items of the menu's All Commands submenu, where the ground block lives.</summary>
    private static ItemCollection CommandTree(ContextMenu menu) => Item(menu.Items, AircraftMenuBuilder.AllCommandsHeader).Items;

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
