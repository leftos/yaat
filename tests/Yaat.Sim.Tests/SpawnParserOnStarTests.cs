using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// ADD command spawning an IFR arrival already established on a STAR. The first position token is a
/// dotted <c>WAYPOINT.STAR[.RUNWAY]</c> route (e.g. "TBARR.TBARR4.34R"). Trailing args are
/// order-independent: a bare number = current altitude (shorthand via AltitudeResolver), "SP###" =
/// speed override, "LVL" = hold level (else descend via), an alphabetic token = destination airport,
/// plus the usual type / *airline overrides. Pure-parser tests — no NavigationDatabase required.
/// </summary>
public class SpawnParserOnStarTests
{
    [Fact]
    public void Parse_ThreePartToken_WithAltitude_DescendsViaByDefault()
    {
        var (request, error) = SpawnParser.Parse("I H J TBARR.TBARR4.34R 230");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.OnStar, request.PositionType);
        Assert.Equal("TBARR", request.StarEntryFix);
        Assert.Equal("TBARR4", request.StarId);
        Assert.Equal("34R", request.StarRunway);
        Assert.True(request.DescendVia);
        Assert.Equal(23000, request.StarAltitude!.Value);
        Assert.Null(request.StarSpeedKts);
        Assert.Null(request.DestinationAirportId);
    }

    [Fact]
    public void Parse_TwoPartToken_HasNullRunway()
    {
        var (request, error) = SpawnParser.Parse("I H J TBARR.TBARR4 230");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.OnStar, request.PositionType);
        Assert.Equal("TBARR", request.StarEntryFix);
        Assert.Equal("TBARR4", request.StarId);
        Assert.Null(request.StarRunway);
    }

    [Fact]
    public void Parse_AltitudeOmitted_LeavesAltitudeNull()
    {
        var (request, error) = SpawnParser.Parse("I H J TBARR.TBARR4.34R");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.OnStar, request.PositionType);
        Assert.Null(request.StarAltitude);
        Assert.True(request.DescendVia);
    }

    [Fact]
    public void Parse_AltitudeShorthand_ExpandsToHundreds()
    {
        var (request, _) = SpawnParser.Parse("I H J TBARR.TBARR4.34R 110");
        Assert.Equal(11000, request!.StarAltitude!.Value);
    }

    [Fact]
    public void Parse_AltitudeFullFeet_KeptAsIs()
    {
        var (request, _) = SpawnParser.Parse("I H J TBARR.TBARR4.34R 11000");
        Assert.Equal(11000, request!.StarAltitude!.Value);
    }

    [Fact]
    public void Parse_LvlKeyword_DisablesDescendVia()
    {
        var (request, error) = SpawnParser.Parse("I H J TBARR.TBARR4.34R 110 LVL");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.False(request.DescendVia);
        Assert.Equal(11000, request.StarAltitude!.Value);
    }

    [Fact]
    public void Parse_SpeedPrefix_SetsSpeedOverride()
    {
        var (request, error) = SpawnParser.Parse("I H J TBARR.TBARR4.34R 110 SP250");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(250, request.StarSpeedKts!.Value);
        Assert.Equal(11000, request.StarAltitude!.Value);
    }

    [Fact]
    public void Parse_TrailingAirport_SetsDestination()
    {
        var (request, error) = SpawnParser.Parse("I H J TBARR.TBARR4.34R 110 KOAK");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("KOAK", request.DestinationAirportId);
    }

    [Fact]
    public void Parse_AllTrailingArgs_AnyOrder_AllParsed()
    {
        var (request, error) = SpawnParser.Parse("I H J TBARR.TBARR4.34R SP250 110 LVL KOAK B738");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.OnStar, request.PositionType);
        Assert.Equal(11000, request.StarAltitude!.Value);
        Assert.Equal(250, request.StarSpeedKts!.Value);
        Assert.False(request.DescendVia);
        Assert.Equal("KOAK", request.DestinationAirportId);
        Assert.Equal("B738", request.ExplicitType);
    }

    [Fact]
    public void Parse_TypeOverride_NotMistakenForAirport()
    {
        var (request, error) = SpawnParser.Parse("I H J TBARR.TBARR4.34R 110 B738");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("B738", request.ExplicitType);
        Assert.Null(request.DestinationAirportId);
    }

    [Fact]
    public void Parse_AirlineOverride_Parsed()
    {
        var (request, error) = SpawnParser.Parse("I H J TBARR.TBARR4.34R 110 *UAL");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("UAL", request.ExplicitAirline);
    }

    [Fact]
    public void Parse_VfrArrival_Rejected()
    {
        var (request, error) = SpawnParser.Parse("V H J TBARR.TBARR4.34R 230");

        Assert.NotNull(error);
        Assert.Null(request);
    }

    [Fact]
    public void Parse_EmptyStarComponent_Rejected()
    {
        var (request, error) = SpawnParser.Parse("I H J TBARR. 230");

        Assert.NotNull(error);
        Assert.Null(request);
    }

    [Fact]
    public void Parse_TooManyDotParts_Rejected()
    {
        var (request, error) = SpawnParser.Parse("I H J TBARR.TBARR4.34R.EXTRA 230");

        Assert.NotNull(error);
        Assert.Null(request);
    }

    [Fact]
    public void Parse_EntryFixAndStarUppercased()
    {
        var (request, _) = SpawnParser.Parse("i h j tbarr.tbarr4.34r 230");

        Assert.NotNull(request);
        Assert.Equal("TBARR", request!.StarEntryFix);
        Assert.Equal("TBARR4", request.StarId);
        Assert.Equal("34R", request.StarRunway);
    }
}
