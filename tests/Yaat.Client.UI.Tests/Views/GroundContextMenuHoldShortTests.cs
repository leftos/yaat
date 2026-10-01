using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using CatalogMenuView = Yaat.Client.ContextMenus.MenuView;

namespace Yaat.Client.UI.Tests.Views;

// Regression for the reported bug: N784ME holding short of 15/33 on taxiway C at OAK had
// four duplicate "Cross 28R" entries in the ground-map right-click menu. The menu scanned
// every hold-short node within 0.1nm and emitted one "Cross X" per node with no dedup; the
// aircraft sat ~0.07nm from four 28R/10L hold-short bars, and 28R was its departure runway.
// The menu must offer to cross only the runway being held (15), once, whatever runway it is
// assigned; the held runway comes from the phase alone, so no layout is consulted.
public class GroundContextMenuHoldShortTests
{
    [AvaloniaFact]
    public void HoldingShort_OffersOnlyHeldRunwayCrossing_NotNearbyParallelRunway()
    {
        var ac = new AircraftModel
        {
            Callsign = "N784ME",
            IsOnGround = true,
            CurrentPhase = "Holding Short 15/33",
            AssignedRunway = "28R",
            HasActiveTaxiRoute = true,
        };
        var context = new MenuContext("N784ME", "AB", null, false, VfrCommandsForIfr.EnterFinalOnly, CatalogMenuView.Ground);

        var menu = new ContextMenu();
        SharedMenuGroups.AddGroundClearances(menu.Items, ac, context, new RecordingMenuHost(""));

        var crossItems = menu
            .Items.OfType<MenuItem>()
            .Where(m => m.Header is string s && s.StartsWith("Cross ", StringComparison.Ordinal))
            .Select(m => (string)m.Header!)
            .ToList();

        Assert.Equal(new[] { "Cross 15" }, crossItems);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => m.Header is string s && s.Contains("28R", StringComparison.Ordinal));
    }
}
