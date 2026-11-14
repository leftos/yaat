using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.Views;

// Regression for GitHub issue #229: the ground-map right-click "Cleared for takeoff" and
// "Line up and wait" items clear the aircraft for the runway it is assigned, which the
// server resolves itself. Both send the bare verb (CTO, LUAW) and show the runway in the
// label only: a runway in the sent text ("CTO 28R") names a runway for the server to
// judge against the assignment, which these items never mean to do.
public class Issue229TakeoffMenuRunwayTests
{
    private const string Callsign = "EJA921";
    private const string Initials = "GG";

    /// <summary>A recording host whose session sends with this test's initials.</summary>
    private static RecordingMenuHost Host() =>
        new("")
        {
            Session = new MenuSession(
                Initials,
                false,
                VfrCommandsForIfr.EnterFinalOnly,
                QuickCommandDefaults.For,
                SoloAdvisoryPrefill.TrueValues,
                false,
                CommandScheme.Default(),
                ShowQuickCommandIcons: true,
                ActiveTcp: null,
                ActivePositionCallsign: null,
                ActiveTrackFacilityId: null
            ),
        };

    /// <summary>The Tower submenu's items for an aircraft holding short of 28R/10L, built over <paramref name="host"/>.</summary>
    private static ItemCollection BuildTowerItems(RecordingMenuHost host)
    {
        var ac = new AircraftModel
        {
            Callsign = Callsign,
            IsOnGround = true,
            CurrentPhase = "Holding Short 28R/10L",
        };
        ContextMenu menu = AircraftMenuBuilder.Build(ac, new MenuClick(Callsign, null, null, []), host, _ => []);
        return AllCommandsPath.Group(menu, MenuCategory.Runway).Items;
    }

    private static MenuItem FindItem(ItemCollection items, string header) =>
        items.OfType<MenuItem>().Single(m => m.Header is string s && s == header);

    [AvaloniaFact]
    public void ClearedForTakeoffDefault_SendsBareCto_NotRunwayArgument()
    {
        RecordingMenuHost host = Host();
        ItemCollection tower = BuildTowerItems(host);

        MenuItem ctoParent = FindItem(tower, "Cleared for takeoff 28R");
        MenuItem defaultItem = ctoParent.Items.OfType<MenuItem>().Single(m => m.Header is TakeoffFlyoutRow { Label: "Cleared for takeoff" });
        defaultItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal([(Callsign, "CTO", Initials)], host.Sent);
    }

    [AvaloniaFact]
    public void LineUpAndWait_SendsBareLuaw_NotRunwayArgument()
    {
        RecordingMenuHost host = Host();
        ItemCollection tower = BuildTowerItems(host);

        MenuItem luawItem = FindItem(tower, "Line up and wait 28R");
        luawItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal([(Callsign, "LUAW", Initials)], host.Sent);
    }
}
