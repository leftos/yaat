using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Which runway end an <c>HS</c> must name to arm a mid-runway crossing's bar: the ground menu names such a crossing by
/// both ends ("Runway 10L/28R") and sends one of them, so it measures here that either end binds the same near-side bar
/// on the real KOAK layout — taxiing south on G from the north ramp, across 28R mid-field toward 28L.
/// </summary>
public class HoldShortRunwayBothEndsTests
{
    [Theory]
    [InlineData("10L")]
    [InlineData("28R")]
    public void MidRunwayCrossing_EitherEnd_BindsTheNearSideBar(string end)
    {
        AirportGroundLayout layout = LoadOak();
        GroundNode nearBar = layout.Nodes.Values.Where(n => IsBar(n, "28R", "G")).MaxBy(n => n.Position.Lat)!;
        AircraftState ac = TaxiingSouthOnG(layout, nearBar);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;

        Assert.True(HoldShortTarget.TryParse(end, out HoldShortTarget target, out string? error), error);
        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, route, target);
        int boundNodeId = plan.Existing?.NodeId ?? plan.NodeId;
        HoldShortCommand hs = Assert.IsType<HoldShortCommand>(CommandParser.Parse($"HS {end}").Value);
        CommandResult result = GroundCommandHandler.TryHoldShort(ac, hs, layout);

        Assert.True(result.Success, result.Message);
        Assert.Contains(plan.Outcome, new[] { ExplicitHoldShortOutcome.ReArm, ExplicitHoldShortOutcome.Add });
        Assert.Equal(nearBar.Id, boundNodeId);
    }

    private static AirportGroundLayout LoadOak()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "oak.geojson");
        Assert.True(File.Exists(path), $"{path} is missing from the test output");
        return GeoJsonParser.Parse("OAK", File.ReadAllText(path), null);
    }

    private static bool IsBar(GroundNode node, string runwayEnd, string taxiway) =>
        (node.Type == GroundNodeType.RunwayHoldShort)
        && (node.RunwayId is { } id)
        && id.Contains(runwayEnd)
        && node.Edges.Any(e => e.TaxiwayName == taxiway);

    /// <summary>
    /// A C172 two G nodes north of <paramref name="nearBar"/>, facing it, cleared <c>TAXI G</c>. Each hop takes the
    /// northernmost G neighbour, away from the runway, and the start touches no runway link (centerline or junction arc).
    /// </summary>
    private static AircraftState TaxiingSouthOnG(AirportGroundLayout layout, GroundNode nearBar)
    {
        GroundNode current = nearBar;
        for (int hop = 0; hop < 2; hop++)
        {
            GroundNode from = current;
            current = from.Edges.Where(e => e.TaxiwayName == "G").Select(e => e.OtherNode(from)).MaxBy(n => n.Position.Lat)!;
            Assert.True(current.Position.Lat > from.Position.Lat, $"G runs no further north of node {from.Id}");
        }

        Assert.DoesNotContain(current.Edges, e => e.TaxiwayName.Contains("RWY", StringComparison.OrdinalIgnoreCase));

        var ac = new AircraftState
        {
            Callsign = "N459HS",
            AircraftType = "C172",
            Position = current.Position,
            TrueHeading = new TrueHeading(GeoMath.BearingTo(current.Position, nearBar.Position)),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
            Phases = new PhaseList(),
        };
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(CommandParser.Parse("TAXI G").Value);
        CommandResult taxied = GroundCommandHandler.TryTaxi(ac, taxi, layout);
        Assert.True(taxied.Success, taxied.Message);
        Assert.NotNull(ac.Ground.AssignedTaxiRoute);
        return ac;
    }
}
