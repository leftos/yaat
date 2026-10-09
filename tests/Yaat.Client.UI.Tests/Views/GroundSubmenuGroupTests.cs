using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
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
    public void HoldShort_RouteLineThenOneRowPerBar_EachShowsItsBarPreviewsItsRouteAndSendsItsCommand()
    {
        var toS1 = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        var toRunway = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        var host = new RecordingMenuHost("") { HoldShortRouteLine = "route S T V W4 · RWY 30" };
        host.HoldShortChoices.Add(
            new HoldShortChoice(new HoldShortRowLabel(HoldShortChoice.TaxiwayBadge, "S1", "crossing on S", 1500), "HS S1@S", toS1)
        );
        host.HoldShortChoices.Add(
            new HoldShortChoice(new HoldShortRowLabel(HoldShortChoice.RunwayBadge, "Runway 30", "at W4, end of route", 5300), "HS 30", toRunway)
        );

        ContextMenu menu = BuildTaxiGroups(host);
        MenuItem holdShort = Item(CommandTree(menu), "Hold short of…");

        MenuItem line = Assert.IsType<MenuItem>(holdShort.Items[0]);
        Assert.Equal("route S T V W4 · RWY 30", line.Header);
        Assert.False(line.IsEnabled);
        Assert.IsType<Separator>(holdShort.Items[1]);
        List<MenuItem> rows = HoldShortRows(holdShort);
        Assert.Equal(
            ["TW S1 · crossing on S · ~1,500 ft — HS S1@S", "RW Runway 30 · at W4, end of route · ~5,300 ft — HS 30"],
            rows.Select(AutomationProperties.GetName)
        );
        Assert.Equal(["TW", "S1", " · crossing on S", "~1,500 ft", "HS S1@S"], RowTexts(rows[0]));
        Assert.Equal(["RW", "Runway 30", " · at W4, end of route", "~5,300 ft", "HS 30"], RowTexts(rows[1]));

        RaisePointerEntered(rows[1]);
        RaisePointerEntered(rows[0]);
        Assert.Equal([toRunway, toS1], host.RoutePreviews);

        Click(rows[0]);
        Click(rows[1]);
        Assert.Equal([(Callsign, "HS S1@S", Initials), (Callsign, "HS 30", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void HoldShort_NoRouteLine_ListsOnlyTheRows()
    {
        var host = new RecordingMenuHost("");
        host.HoldShortChoices.Add(
            new HoldShortChoice(
                new HoldShortRowLabel(HoldShortChoice.TaxiwayBadge, "B", "crossing on T", 300),
                "HS B",
                new TaxiRoute { Segments = [], HoldShortPoints = [] }
            )
        );

        MenuItem holdShort = Item(CommandTree(BuildTaxiGroups(host)), "Hold short of…");

        MenuItem row = Assert.IsType<MenuItem>(Assert.Single(holdShort.Items));
        Assert.Equal("TW B · crossing on T · ~300 ft — HS B", AutomationProperties.GetName(row));
    }

    /// <summary>The Hold short of… submenu's rows: every item but the route line.</summary>
    private static List<MenuItem> HoldShortRows(MenuItem holdShort) => [.. holdShort.Items.OfType<MenuItem>().Where(m => m.Header is not string)];

    /// <summary>The texts <paramref name="row"/>'s header template shows, in layout order.</summary>
    private static List<string> RowTexts(MenuItem row) => [.. TextBlocks(RowView(row)).Select(block => block.Text ?? "")];

    [AvaloniaFact]
    public void MenuCommandRowTemplate_HoldShortAndGroundTraffic_KeepTheirLayouts()
    {
        var host = new RecordingMenuHost("");
        host.HoldShortChoices.Add(
            new HoldShortChoice(
                new HoldShortRowLabel(HoldShortChoice.TaxiwayBadge, "S1", "crossing on S", 1500),
                "HS S1@S",
                new TaxiRoute { Segments = [], HoldShortPoints = [] }
            )
        );
        host.GroundTraffic.Add(RecordingMenuHost.ParkedRow("SWA200"));
        ItemCollection tree = CommandTree(BuildTaxiGroups(host));

        Control holdShortView = RowView(HoldShortRows(Item(tree, "Hold short of…"))[0]);
        TextBlock where = TextWithContent(holdShortView, " · crossing on S");
        Assert.Equal(12, where.FontSize);
        StackPanel holdShortName = Assert.IsType<StackPanel>(where.Parent);
        Assert.Equal(Orientation.Horizontal, holdShortName.Orientation);
        TextBlock holdShortBar = Assert.IsType<TextBlock>(holdShortName.Children[0]);
        Assert.Equal("S1", holdShortBar.Text);
        Assert.Equal(FontWeight.SemiBold, holdShortBar.FontWeight);
        Assert.NotEmpty(holdShortView.GetVisualDescendants().OfType<Border>());

        Control groundView = RowView(FollowRow(Item(tree, "Follow…"), "SWA200"));
        TextBlock state = TextWithContent(groundView, "at parking · gate 1");
        Assert.Equal(11, state.FontSize);
        StackPanel groundName = Assert.IsType<StackPanel>(state.Parent);
        Assert.Equal(Orientation.Vertical, groundName.Orientation);
        TextBlock groundCallsign = Assert.IsType<TextBlock>(groundName.Children[0]);
        Assert.Equal("SWA200 · B738", groundCallsign.Text);
        Assert.NotEqual(FontWeight.SemiBold, groundCallsign.FontWeight);
        Assert.Empty(groundView.GetVisualDescendants().OfType<Border>());
    }

    /// <summary>The view <paramref name="row"/>'s header template builds from its header.</summary>
    private static Control RowView(MenuItem row) => row.HeaderTemplate!.Build(row.Header)!;

    /// <summary>The one text block in <paramref name="view"/> that shows <paramref name="text"/>.</summary>
    private static TextBlock TextWithContent(Control view, string text) => Assert.Single(TextBlocks(view), block => block.Text == text);

    /// <summary>Every text block in <paramref name="view"/>, in layout order.</summary>
    private static List<TextBlock> TextBlocks(Control view)
    {
        List<TextBlock> blocks = [];
        CollectTextBlocks(view, blocks);
        return blocks;
    }

    private static void CollectTextBlocks(Control control, List<TextBlock> blocks)
    {
        switch (control)
        {
            case TextBlock text:
                blocks.Add(text);
                break;
            case Border { Child: { } child }:
                CollectTextBlocks(child, blocks);
                break;
            case Panel panel:
                foreach (Control child in panel.Children)
                {
                    CollectTextBlocks(child, blocks);
                }

                break;
        }
    }

    [AvaloniaFact]
    public void FollowAndGiveWay_ListTheHostTrafficInOrder_AndSendFOLLOWGAndGW()
    {
        string[] traffic = ["SWA200", "AAL1", "DAL300"];
        var host = new RecordingMenuHost("");
        host.GroundTraffic.AddRange(traffic.Select(RecordingMenuHost.ParkedRow));

        ContextMenu menu = BuildTaxiGroups(host);
        Assert.Equal(["Follow…", "Give way to…"], HeadersWhere(menu, IsTaxiGroup));

        MenuItem follow = Item(CommandTree(menu), "Follow…");
        MenuItem giveWay = Item(CommandTree(menu), "Give way to…");
        Assert.Equal(
            ["Parked or holding", .. traffic.Select(cs => $"{cs} · B738 · at parking · gate 1 · ~600 ft — FOLLOWG {cs}")],
            follow.Items.OfType<MenuItem>().Select(item => item.Header?.ToString())
        );
        Assert.Equal(
            ["Parked or holding", .. traffic.Select(cs => $"{cs} · B738 · at parking · gate 1 · ~600 ft — GW {cs}")],
            giveWay.Items.OfType<MenuItem>().Select(item => item.Header?.ToString())
        );

        RaisePointerEntered(FollowRow(follow, traffic[0]));
        Assert.Equal(["SWA200"], host.Highlights);

        foreach (string other in traffic)
        {
            Click(follow.Items.OfType<MenuItem>().First(item => item.Header?.ToString()?.StartsWith($"{other} ·", StringComparison.Ordinal) == true));
        }

        foreach (string other in traffic)
        {
            Click(
                giveWay.Items.OfType<MenuItem>().First(item => item.Header?.ToString()?.StartsWith($"{other} ·", StringComparison.Ordinal) == true)
            );
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
        host.PresetTaxiChoices.Add(RecordingMenuHost.PresetRow("TERMINAL to 30", "TAXI T U W RWY 30"));
        host.PresetTaxiChoices.Add(RecordingMenuHost.PresetRow("TERMINAL to 28R", "TAXI B C RWY 28R"));

        ContextMenu menu = BuildMenu(host, Taxiing());
        Assert.Equal(["Preset taxi route", "Draw taxi route…"], HeadersWhere(menu, IsTaxiRouteItem));

        MenuItem presets = Item(CommandTree(menu), "Preset taxi route");
        List<MenuItem> presetRows = [.. presets.Items.OfType<MenuItem>()];
        Assert.Equal(["TERMINAL to 30", "TERMINAL to 28R"], presetRows.Select(row => Assert.IsType<MenuCommandRow>(row.Header).Name));
        Click(presetRows.Single(row => ((MenuCommandRow)row.Header!).Name == "TERMINAL to 28R"));
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
        host.PresetTaxiChoices.Add(RecordingMenuHost.PresetRow("TERMINAL to 30", "TAXI T U W RWY 30"));
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

    /// <summary>The traffic row in <paramref name="menu"/> whose one-line header starts with <paramref name="callsign"/>.</summary>
    private static MenuItem FollowRow(MenuItem menu, string callsign) =>
        menu.Items.OfType<MenuItem>().First(item => item.Header?.ToString()?.StartsWith($"{callsign} ·", StringComparison.Ordinal) == true);

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
