using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Tests;

// Covers GroundViewModel.GetPushbackDirections / PushbackFacingAsync: the ground view's
// "Push back, face <taxiway>" menu items must emit PUSH FACE <cardinal>, which the server's
// GroundCommandParser.ParsePushback accepts. The earlier numeric form (PUSH 090) is refused
// outright ("PUSH no longer accepts numeric headings — use FACE/TAIL or </> with a cardinal"),
// so every one of those menu items silently failed server-side.
// The cardinal is an absolute MAGNETIC facing, so the edge's true bearing is converted
// true→magnetic before it is snapped to the nearest of the eight 45-degree compass points.
public class GroundViewModelPushbackDirectionTests
{
    private static readonly LatLon Center = new(37.620, -122.380);
    private const double SpokeLengthNm = 0.03;

    [Fact]
    public async Task GetPushbackDirections_DueEastEdge_SendsFaceCardinalNotNumericHeading()
    {
        var (vm, sent) = MakeViewModel();
        vm.SetLayoutForTesting(SpokeLayout(("T", 90.0)));
        var ac = MakeAircraft();

        var directions = vm.GetPushbackDirections(ac);

        var only = Assert.Single(directions);
        Assert.Equal("face T", only.Label);
        Assert.Equal("E", only.Cardinal);

        await vm.PushbackFacingAsync(ac.Callsign, "AB", only.Cardinal);
        Assert.Equal("PUSH FACE E", Assert.Single(sent));
    }

    [Fact]
    public void GetPushbackDirections_BearingsStraddlingBucketBoundary_RoundToNearerCardinal()
    {
        var (vm, _) = MakeViewModel();
        // The E/SE bucket boundary sits at magnetic 112.5. With ~13 degrees east variation in the
        // Bay Area these two true bearings land either side of it.
        vm.SetLayoutForTesting(SpokeLayout(("A", 118.0), ("B", 135.0)));
        var ac = MakeAircraft();

        var directions = vm.GetPushbackDirections(ac);

        Assert.Equal(2, directions.Count);
        Assert.Equal("E", CardinalFor(directions, "face A"));
        Assert.Equal("SE", CardinalFor(directions, "face B"));
    }

    [Fact]
    public void GetPushbackDirections_ConvertsTrueBearingToMagneticBeforeSnapping()
    {
        var (vm, _) = MakeViewModel();
        // True 072 snaps to E (bucket midpoint 090) if taken as-is, but east variation pulls the
        // magnetic bearing below the NE/E boundary at 67.5, so the correct answer is NE.
        vm.SetLayoutForTesting(SpokeLayout(("C", 72.0)));
        var ac = MakeAircraft();

        var directions = vm.GetPushbackDirections(ac);

        Assert.Equal("NE", CardinalFor(directions, "face C"));
    }

    [Fact]
    public void GetPushbackDirections_RampEdge_ProducesNoEntry()
    {
        var (vm, _) = MakeViewModel();
        vm.SetLayoutForTesting(SpokeLayout(("RAMP", 0.0), ("T", 90.0)));
        var ac = MakeAircraft();

        var directions = vm.GetPushbackDirections(ac);

        var only = Assert.Single(directions);
        Assert.Equal("face T", only.Label);
        Assert.DoesNotContain(directions, d => d.Label.Contains("RAMP", StringComparison.OrdinalIgnoreCase));
    }

    private static string CardinalFor(List<(string Label, string Cardinal)> directions, string label)
    {
        var match = directions.Find(d => d.Label == label);
        Assert.Equal(label, match.Label);
        return match.Cardinal;
    }

    private static (GroundViewModel Vm, List<string> Sent) MakeViewModel()
    {
        var sent = new List<string>();
        var connection = new ServerConnection();
        var vm = new GroundViewModel(
            connection,
            sendCommand: (_, command, _) =>
            {
                sent.Add(command);
                return Task.CompletedTask;
            }
        );
        return (vm, sent);
    }

    private static AircraftModel MakeAircraft() => new() { Callsign = "TST123", Position = Center };

    // A parking node at Center with one spoke per (taxiway, true bearing) pair.
    private static GroundLayoutDto SpokeLayout(params (string Taxiway, double TrueBearing)[] spokes)
    {
        var nodes = new List<GroundNodeDto> { new(1, Center.Lat, Center.Lon, "Parking", "A1", null, null) };
        var edges = new List<GroundEdgeDto>();
        int nextId = 2;
        foreach (var (taxiway, trueBearing) in spokes)
        {
            var end = GeoMath.ProjectPoint(Center, new TrueHeading(trueBearing), SpokeLengthNm);
            nodes.Add(new GroundNodeDto(nextId, end.Lat, end.Lon, "TaxiwayIntersection", null, null, null));
            edges.Add(new GroundEdgeDto(1, nextId, taxiway, SpokeLengthNm, null));
            nextId++;
        }

        return new GroundLayoutDto("TST", nodes, edges, null, null, null);
    }
}
