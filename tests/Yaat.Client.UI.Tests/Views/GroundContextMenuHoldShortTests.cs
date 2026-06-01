using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// Regression for the reported bug: N784ME holding short of 15/33 on taxiway C at OAK had
// four duplicate "Cross 28R" entries in the ground-map right-click menu. The menu scanned
// every hold-short node within 0.1nm and emitted one "Cross X" per node with no dedup; the
// aircraft sat ~0.07nm from four 28R/10L hold-short bars. The menu must offer to cross only
// the runway being held (15), once.
public class GroundContextMenuHoldShortTests
{
    private const double Lat = 37.620;
    private const double Lon = -122.380;

    [AvaloniaFact]
    public void HoldingShort_OffersOnlyHeldRunwayCrossing_NotNearbyParallelRunway()
    {
        var vm = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask);

        // One 15/33 hold-short bar (the runway being held) and four 28R/10L bars, all within
        // 0.1nm of the aircraft — mirroring OAK taxiway C near the 10L threshold.
        var dto = new GroundLayoutDto(
            "TST",
            [
                new GroundNodeDto(1, Lat, Lon, "TaxiwayIntersection", null, null, null),
                new GroundNodeDto(2, Lat + 0.0001, Lon, "RunwayHoldShort", null, null, "15/33"),
                new GroundNodeDto(3, Lat + 0.0010, Lon, "RunwayHoldShort", null, null, "28R/10L"),
                new GroundNodeDto(4, Lat + 0.0011, Lon, "RunwayHoldShort", null, null, "28R/10L"),
                new GroundNodeDto(5, Lat + 0.0012, Lon, "RunwayHoldShort", null, null, "28R/10L"),
                new GroundNodeDto(6, Lat + 0.0013, Lon, "RunwayHoldShort", null, null, "28R/10L"),
            ],
            [
                new GroundEdgeDto(1, 2, "C", DistanceNm: 0.01, IntermediatePoints: null),
                new GroundEdgeDto(1, 3, "C", DistanceNm: 0.06, IntermediatePoints: null),
                new GroundEdgeDto(1, 4, "C", DistanceNm: 0.06, IntermediatePoints: null),
                new GroundEdgeDto(1, 5, "C", DistanceNm: 0.07, IntermediatePoints: null),
                new GroundEdgeDto(1, 6, "C", DistanceNm: 0.07, IntermediatePoints: null),
            ],
            null,
            null
        );
        vm.SetLayoutForTesting(dto);

        var ac = new AircraftModel
        {
            Callsign = "N784ME",
            Position = new LatLon(Lat, Lon),
            CurrentPhase = "Holding Short 15/33",
            HasActiveTaxiRoute = true,
        };

        var menu = new ContextMenu();
        GroundView.AddHoldShortCrossingItems(menu, vm, ac, "Holding Short 15/33", "N784ME", "AB");

        var crossItems = menu
            .Items.OfType<MenuItem>()
            .Where(m => m.Header is string s && s.StartsWith("Cross ", StringComparison.Ordinal))
            .Select(m => (string)m.Header!)
            .ToList();

        Assert.Equal(new[] { "Cross 15" }, crossItems);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => m.Header is string s && s.Contains("28R", StringComparison.Ordinal));
    }
}
