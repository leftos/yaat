using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Regression coverage for the <c>$</c> taxi-spot sigil in TAXI commands. Outside an <c>HS</c>
/// clause a <c>$spot</c> / <c>@parking</c> token is the taxi destination; inside one, <c>$spot</c>
/// is a hold-short target (issue #394 — <c>TAXI T421 C Z B M1 1L HS $17</c>) and <c>@parking</c> is
/// rejected. The client-side ground-draw builder emits the destination before <c>HS</c>.
/// </summary>
public class GroundCommandParserTaxiSpotTests
{
    [Fact]
    public void ParseTaxi_DollarSpotBeforeHs_Sets_DestinationSpot()
    {
        var result = GroundCommandParser.ParseTaxi("T9 A F $I8L HS 01L");

        Assert.True(result.IsSuccess, result.Reason);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal("I8L", taxi.DestinationSpot);
        Assert.Null(taxi.DestinationParking);
        Assert.Equal(["T9", "A", "F"], taxi.Path);
        Assert.Equal(["01L"], taxi.HoldShorts.Select(h => h.ToCanonical()));
    }

    [Fact]
    public void ParseTaxi_AtParkingBeforeHs_Sets_DestinationParking()
    {
        var result = GroundCommandParser.ParseTaxi("T9 A F @A12 HS 01L");

        Assert.True(result.IsSuccess, result.Reason);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal("A12", taxi.DestinationParking);
        Assert.Null(taxi.DestinationSpot);
        Assert.Equal(["01L"], taxi.HoldShorts.Select(h => h.ToCanonical()));
    }

    /// <summary>
    /// TAXIAUTO takes the same destination sigils as TAXI. TAXIALL routes each parked aircraft through the
    /// TAXIAUTO arm, so a <c>$spot</c> destination has to survive both the parse and the canonical round trip.
    /// </summary>
    [Fact]
    public void ParseTaxiAuto_DollarSpot_Sets_DestinationSpot()
    {
        var result = GroundCommandParser.ParseTaxiAuto("$I8L");

        Assert.True(result.IsSuccess, result.Reason);
        var taxiAuto = Assert.IsType<TaxiAutoCommand>(result.Value);

        Assert.Equal("I8L", taxiAuto.DestinationSpot);
        Assert.Null(taxiAuto.DestinationParking);
        Assert.Null(taxiAuto.DestinationRunway);
    }

    [Theory]
    [InlineData("TAXIAUTO $I8L", "TAXIAUTO $I8L")]
    [InlineData("TAXIAUTO @A12", "TAXIAUTO @A12")]
    [InlineData("TAXIAUTO 28R", "TAXIAUTO 28R")]
    public void TaxiAutoCanonical_RoundTripsEveryDestination(string input, string expected)
    {
        var parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        Assert.Equal(expected, CommandDescriber.DescribeCommand(parsed.Value!));
    }

    /// <summary>A TAXIAUTO to a spot routes there, the same as the equivalent TAXI destination.</summary>
    [Fact]
    public void TryTaxiAuto_DollarSpot_RoutesToTheSpot()
    {
        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var spot = layout.Nodes.Values.OrderBy(n => n.Id).First(n => (n.Type == GroundNodeType.Spot) && (n.Name is { Length: > 0 }));
        var parking = layout.Nodes.Values.First(n =>
            (n.Type == GroundNodeType.Parking) && string.Equals(n.Name, "NEW7", StringComparison.OrdinalIgnoreCase)
        );

        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = parking.Position,
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
        };
        ac.Ground.Layout = layout;
        ac.Phases = new PhaseList();

        var result = GroundCommandHandler.TryTaxiAuto(ac, new TaxiAutoCommand(null, null, spot.Name), layout);

        Assert.True(result.Success, result.Message);
        Assert.Equal(spot.Name, ac.Ground.AssignedTaxiRoute?.DestinationSpot);
    }

    [Fact]
    public void ParseTaxi_BareDollarSpot_Sets_DestinationSpot()
    {
        var result = GroundCommandParser.ParseTaxi("$I8L");

        Assert.True(result.IsSuccess, result.Reason);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal("I8L", taxi.DestinationSpot);
        Assert.Null(taxi.DestinationParking);
        Assert.Empty(taxi.Path);
    }

    /// <summary>The issue #394 preset: the spot after HS is a hold-short target, not the destination.</summary>
    [Fact]
    public void ParseTaxi_DollarSpotInsideHs_IsSpotHoldShort()
    {
        var result = GroundCommandParser.ParseTaxi("T421 C Z B M1 1L HS $17");

        Assert.True(result.IsSuccess, result.Reason);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal(["T421", "C", "Z", "B", "M1"], taxi.Path);
        Assert.Equal("1L", taxi.DestinationRunway);
        Assert.Null(taxi.DestinationSpot);
        Assert.Null(taxi.DestinationParking);

        var hs = Assert.Single(taxi.HoldShorts);
        Assert.True(hs.IsSpot);
        Assert.Equal("17", hs.Target);
        Assert.Null(hs.OnTaxiway);
        Assert.Equal("$17", hs.ToCanonical());
        Assert.Equal("spot 17", hs.ToNatural());
    }

    /// <summary>The BOS corpus form: taxi TO spot 8 and hold short of it.</summary>
    [Fact]
    public void ParseTaxi_DestinationSpotThenHsSameSpot_KeepsBoth()
    {
        var result = GroundCommandParser.ParseTaxi("K $8 HS $8");

        Assert.True(result.IsSuccess, result.Reason);
        var taxi = Assert.IsType<TaxiCommand>(result.Value);

        Assert.Equal(["K"], taxi.Path);
        Assert.Equal("8", taxi.DestinationSpot);
        Assert.Equal(["$8"], taxi.HoldShorts.Select(h => h.ToCanonical()));
    }

    [Fact]
    public void ParseTaxi_ParkingInsideHs_Fails()
    {
        var result = GroundCommandParser.ParseTaxi("T9 A F HS 01L @A12");

        Assert.False(result.IsSuccess);
        Assert.Contains("cannot be a hold-short target", result.Reason);
    }

    [Theory]
    [InlineData("RES HS $17")]
    [InlineData("CROSS 28R HS $17")]
    [InlineData("HS $17")]
    public void Parse_SpotHoldShort_InEveryHsClause(string input)
    {
        var parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, $"'{input}' failed to parse: {parsed.Reason}");

        IReadOnlyList<HoldShortTarget> holdShorts = parsed.Value switch
        {
            ResumeCommand res => res.HoldShorts,
            CrossRunwayCommand cross => cross.HoldShorts,
            HoldShortCommand hs => [hs.Target],
            _ => throw new Xunit.Sdk.XunitException($"unexpected command type {parsed.Value?.GetType().Name}"),
        };

        var target = Assert.Single(holdShorts);
        Assert.True(target.IsSpot);
        Assert.Equal("17", target.Target);
        Assert.Equal(input, CommandDescriber.DescribeCommand(parsed.Value!));
    }

    [Theory]
    [InlineData("HS $17@Z")]
    [InlineData("HS $")]
    [InlineData("RES HS @A12")]
    public void Parse_MalformedSpotHoldShort_Fails(string input)
    {
        Assert.False(CommandParser.Parse(input).IsSuccess, $"'{input}' should fail to parse");
    }
}
