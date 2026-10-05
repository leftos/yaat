using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;

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
        ContextMenu menu = AircraftMenuBuilder.Build(ac, new MenuClick("N784ME", null, null, []), new RecordingMenuHost(""), _ => []);

        ItemCollection commandTree = menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader).Items;
        var crossItems = commandTree
            .OfType<MenuItem>()
            .Where(m => m.Header is string s && s.StartsWith("Cross ", StringComparison.Ordinal))
            .Select(m => (string)m.Header!)
            .ToList();

        Assert.Equal(new[] { "Cross 15" }, crossItems);
        Assert.DoesNotContain(commandTree.OfType<MenuItem>(), m => m.Header is string s && s.Contains("28R", StringComparison.Ordinal));
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => m.Header is string s && s.Contains("28R", StringComparison.Ordinal));
    }
}
