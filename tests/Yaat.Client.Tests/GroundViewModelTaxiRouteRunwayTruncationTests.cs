using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests;

/// <summary>
/// Regression coverage for the Ground View taxi-route overlay reconstruction
/// (<see cref="GroundViewModel.ResolveRemainingRoute"/>). A departure taxiing to hold short of a
/// runway must have its drawn route truncated at the runway hold-short, not walked to the full
/// physical extent of the last taxiway.
///
/// The client never receives route geometry over the wire — it re-runs the pathfinder locally. The
/// DTO taxiway string ("D C B") omits the held-short runway, so the reconstruction must source the
/// destination runway from <see cref="AircraftModel.AssignedRunway"/>. Without that hint the resolver
/// treats the route as "end of last taxiway" and highlights all of taxiway B past the B∩28R
/// intersection (the reported bug).
///
/// Uses the real OAK layout (parsed from the shared oak.geojson) because the runway truncation
/// depends on real hold-short / runway nodes.
/// </summary>
public class GroundViewModelTaxiRouteRunwayTruncationTests
{
    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
    }

    private static AirportGroundLayout? LoadOakLayout()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("OAK", File.ReadAllText(path), null, FilletMode.Standard) : null;
    }

    /// <summary>The 28R hold-short node that sits on taxiway B.</summary>
    private static GroundNode HoldShort28ROnB(AirportGroundLayout layout) =>
        layout.Nodes.Values.First(n =>
            n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId is { } r && r.Contains("28R") && n.Edges.Any(e => e.TaxiwayName == "B")
        );

    /// <summary>
    /// A start node a couple of hops back along taxiway B from the hold-short, on the interior
    /// (taxiway) side — never stepping onto a runway node.
    /// </summary>
    private static GroundNode InteriorStartOnB(GroundNode holdShort)
    {
        var current = holdShort;
        var previous = holdShort;
        for (int hop = 0; hop < 2; hop++)
        {
            var next = current
                .Edges.Where(e => e.TaxiwayName == "B")
                .Select(e => e.OtherNode(current))
                .FirstOrDefault(n => n.Id != previous.Id && n.Type != GroundNodeType.RunwayHoldShort);
            if (next is null)
            {
                break;
            }

            previous = current;
            current = next;
        }

        return current;
    }

    [Fact]
    public void TaxiToRunway_OverlayTruncatesAtHoldShort_NotFullTaxiway()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return; // test data absent — skip (matches AirportE2ETests convention)
        }

        var vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        var holdShort = HoldShort28ROnB(layout);
        var start = InteriorStartOnB(holdShort);

        var ac = new AircraftModel
        {
            Callsign = "N346G",
            Position = start.Position,
            CurrentTaxiway = "B",
            TaxiRoute = "D C B",
            AssignedRunway = "28R",
        };

        var route = vm.ResolveRemainingRoute(ac);

        Assert.NotNull(route);
        Assert.NotEmpty(route!.Segments);

        var terminus = layout.Nodes[route.Segments[^1].ToNodeId];
        Assert.Equal(GroundNodeType.RunwayHoldShort, terminus.Type);
        Assert.True(
            terminus.RunwayId is { } rwyId && rwyId.Contains("28R"),
            $"route ended at node {terminus.Id} ({terminus.Type}), expected a 28R hold-short"
        );
    }

    [Fact]
    public void TaxiWithoutAssignedRunway_WalksPastHoldShort()
    {
        // Documents the pre-fix behavior and the reason the runway hint is required: with no assigned
        // runway (empty), the reconstruction has no destination and walks B to its full extent,
        // continuing past the 28R hold-short.
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        var holdShort = HoldShort28ROnB(layout);
        var start = InteriorStartOnB(holdShort);

        var ac = new AircraftModel
        {
            Callsign = "N346G",
            Position = start.Position,
            CurrentTaxiway = "B",
            TaxiRoute = "D C B",
            AssignedRunway = "",
        };

        var route = vm.ResolveRemainingRoute(ac);

        Assert.NotNull(route);
        var terminus = layout.Nodes[route!.Segments[^1].ToNodeId];
        bool endsAtHoldShort28R = terminus.Type == GroundNodeType.RunwayHoldShort && (terminus.RunwayId?.Contains("28R") ?? false);
        Assert.False(endsAtHoldShort28R, "without an assigned runway the overlay should walk past the hold-short, not stop at it");
    }
}
