using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// An <c>ATXI</c> destination takes the same markers a <c>TAXI</c> destination does: <c>@</c> names a helipad or
/// a gate, <c>$</c> a taxi spot, and a bare token is a runway. MIA names its taxi spots 1–27, so a bare
/// <c>ATXI 27</c> or <c>ATXI 9</c> must still reach the runway's holding position, and the spot of the same name
/// is reached only through its <c>$</c>. A bare token that is not a runway at the airport is refused with the fix.
/// </summary>
[Collection("NavDbMutator")]
public class AirTaxiDestinationMarkerTests
{
    public AirTaxiDestinationMarkerTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("ATXI 27", "Air taxi to runway 27, holding short")]
    [InlineData("ATXI 9", "Air taxi to runway 9, holding short")]
    public void BareSpotNamedToken_IsTheRunway(string command, string expectedMessage)
    {
        (AirportGroundLayout layout, AircraftState heli) = MiaHeli(command["ATXI ".Length..]);

        CommandResult result = Dispatch(command, heli, layout);

        Assert.True(result.Success, result.Message);
        Assert.Equal(expectedMessage, result.Message);
        Assert.Null(heli.Ground.ParkingSpot);
    }

    [Theory]
    [InlineData("27", "27")]
    [InlineData("9", "09")]
    public void BareToken_ResolvesToTheRunwayHoldingPosition_NotTheSpot(string token, string runwayId)
    {
        (AirportGroundLayout layout, AircraftState _) = MiaHeli();
        GroundNode? spot = layout.FindSpotNodeByName(token);
        Assert.NotNull(spot);

        Assert.True(GroundCommandHandler.TryResolveAirTaxiDestination(layout, token, null, out GroundCommandHandler.AirTaxiDestination? resolved));

        Assert.Equal(GroundCommandHandler.AirTaxiDestinationKind.Runway, resolved.Kind);
        Assert.Equal(runwayId, resolved.RunwayId);
        Assert.NotEqual(spot.Position, resolved.Target);
    }

    [Theory]
    [InlineData("27")]
    [InlineData("9")]
    public void SpotMarker_ResolvesToTheSpot(string name)
    {
        (AirportGroundLayout layout, AircraftState heli) = MiaHeli("$" + name);
        GroundNode? spot = layout.FindSpotNodeByName(name);
        Assert.NotNull(spot);

        Assert.True(
            GroundCommandHandler.TryResolveAirTaxiDestination(layout, "$" + name, null, out GroundCommandHandler.AirTaxiDestination? resolved)
        );
        Assert.Equal(GroundCommandHandler.AirTaxiDestinationKind.Spot, resolved.Kind);
        Assert.Equal(spot.Position, resolved.Target);

        CommandResult result = Dispatch($"ATXI ${name}", heli, layout);
        Assert.True(result.Success, result.Message);
        Assert.Equal($"Air taxi to spot {name}", result.Message);
        Assert.Null(heli.Ground.ParkingSpot);
    }

    [Fact]
    public void StandMarker_ParksAtTheGate()
    {
        (AirportGroundLayout layout, AircraftState heli) = MiaHeli("@D1");
        Assert.NotNull(layout.FindParkingByName("D1"));

        CommandResult result = Dispatch("ATXI @D1", heli, layout);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Air taxi to D1", result.Message);
        Assert.Equal("D1", heli.Ground.ParkingSpot);
    }

    [Theory]
    [InlineData("D1")]
    [InlineData("5")]
    public void BareNonRunwayToken_IsRefused_WithTheMarkers(string name)
    {
        (AirportGroundLayout layout, AircraftState heli) = MiaHeli();
        Assert.True((layout.FindParkingByName(name) ?? layout.FindSpotNodeByName(name)) is not null);

        CommandResult result = Dispatch($"ATXI {name}", heli, layout);

        Assert.False(result.Success);
        Assert.Equal($"Unable, no runway {name}, use @{name} for a helipad or gate, ${name} for a spot", result.Message);
        Assert.Null(heli.Phases!.CurrentPhase);
    }

    [Fact]
    public void BareUnknownRunway_IsRefused_WithoutADash()
    {
        // OAK has no runway 27, so the bare token is refused with the markers that reach a pad, gate or spot.
        AirportGroundLayout? oak = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(oak);
        Assert.Null(oak.FindRunway("27"));
        GroundNode? heliPad = oak.FindSpotByName("HELI");
        Assert.NotNull(heliPad);
        AircraftState heli = HeliAt(oak, heliPad.Position, "KOAK");

        CommandResult result = GroundCommandHandler.TryAirTaxi(heli, "27", oak);

        Assert.False(result.Success);
        Assert.Equal("Unable, no runway 27, use @27 for a helipad or gate, $27 for a spot", result.Message);
    }

    [Theory]
    [InlineData("ATXI 27", "Air taxi to runway 27")]
    [InlineData("ATXI 28L@J", "Air taxi to runway 28L@J")]
    [InlineData("ATXI $27", "Air taxi to spot 27")]
    [InlineData("ATXI @FDX1", "Air taxi to FDX1")]
    public void Describer_NamesTheDestinationKind(string command, string expected)
    {
        AirTaxiCommand parsed = Assert.IsType<AirTaxiCommand>(CommandParser.Parse(command).Value);

        Assert.Equal(expected, CommandDescriber.DescribeNatural(parsed));
    }

    [Theory]
    [InlineData("ATXI @D1")]
    [InlineData("ATXI $27")]
    [InlineData("ATXI 27")]
    [InlineData("ATXI 28L@J")]
    public void Marker_SurvivesTheCanonicalRoundTrip(string command)
    {
        AirTaxiCommand parsed = Assert.IsType<AirTaxiCommand>(CommandParser.Parse(command).Value);

        Assert.Equal(command, CommandDescriber.DescribeCommand(parsed));
    }

    private static CommandResult Dispatch(string command, AircraftState heli, AirportGroundLayout layout)
    {
        ParsedCommand parsed = CommandParser.Parse(command).Value!;
        return CommandDispatcher.Dispatch(parsed, heli, TestDispatch.Context(Random.Shared, groundLayout: layout));
    }

    /// <summary>
    /// A heli at the first MIA stand or helipad with no runway between it and each of <paramref name="destinations"/>,
    /// the ones the row dispatches: the rows here pin how a marker resolves, so their exact result lines must not carry
    /// a crossing warning. No one stand clears runway 9's bar together with gate D1 or spot 9, so each row stages its
    /// own. Throws when no stand clears the list.
    /// </summary>
    private static (AirportGroundLayout Layout, AircraftState Heli) MiaHeli(params string[] destinations)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("MIA");
        Assert.NotNull(layout);
        return (layout, HeliAt(layout, AirTaxiStands.FirstClearOfRunways(layout, destinations).Position, "KMIA"));
    }

    private static AircraftState HeliAt(AirportGroundLayout layout, LatLon position, string departure)
    {
        var heli = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "EC35",
            Position = position,
            TrueHeading = new TrueHeading(90),
            Altitude = 8,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = departure },
        };
        heli.Ground.Layout = layout;
        heli.Phases = new PhaseList();
        return heli;
    }
}
