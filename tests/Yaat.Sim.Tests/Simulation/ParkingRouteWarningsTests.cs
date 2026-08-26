using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A taxi clearance to a parking destination must surface the pathfinder's warnings the same way a
/// runway-destination clearance does. <c>GroundCommandHandler.ResolveParkingRoute</c> rebuilds the
/// <see cref="TaxiRoute"/> after appending the parking extension and used to construct it from
/// segments and hold-shorts only, so every materialiser warning — "HS … not applied", "taxiing via X —
/// not in the route issued", mandatory-connector notices — vanished from the route and from the TAXI
/// echo for <c>@gate</c> / <c>$spot</c> clearances.
///
/// Scripted on the real SFO layout: from gate F5, <c>TAXI T7A A @F10 HS 28L</c> never meets a 28L hold
/// line, so the materialiser warns that the hold-short was not applied.
/// </summary>
public class ParkingRouteWarningsTests(ITestOutputHelper output)
{
    private static AirportGroundLayout? LoadSfo()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("SFO", File.ReadAllText(path), null) : null;
    }

    [Fact]
    public void ParkingDestination_KeepsMaterialiserWarnings()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var f5 = layout.FindParkingByName("F5");
        Assert.NotNull(f5);
        Assert.NotNull(layout.FindParkingByName("F10"));

        var ac = new AircraftState
        {
            Callsign = "SKW6887",
            AircraftType = "CRJ7",
            Position = f5.Position,
            TrueHeading = new TrueHeading(349),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "SFO" },
        };
        ac.Phases = new PhaseList();

        var parsed = CommandParser.Parse("TAXI T7A A @F10 HS 28L");
        Assert.True(parsed.IsSuccess, $"parse failed: {parsed.Reason}");
        var taxi = Assert.IsType<TaxiCommand>(parsed.Value);

        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout);
        Assert.True(result.Success, $"TAXI failed: {result.Message}");
        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"message={result.Message}");
        output.WriteLine($"warnings=[{string.Join(" | ", route.Warnings)}]");

        Assert.Equal("F10", route.DestinationParking);
        Assert.DoesNotContain(route.HoldShortPoints, hs => hs.TargetName is { } n && RunwayIdentifier.Parse(n).Contains("28L"));

        Assert.Contains(route.Warnings, w => w.Contains("HS 28L not applied", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("HS 28L not applied", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
