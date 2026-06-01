using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// ADD command spawning a departure lined up on a runway with a filed route. The route is a
/// dot-joined token after the runway (e.g. "NIMI6.OAK.SAU"), converted to a space-separated route so a
/// subsequent CTO flies the filed SID. A numeric second token is still an on-final distance.
/// </summary>
public class SpawnParserRunwayRouteTests
{
    [Fact]
    public void Parse_RunwayWithDotJoinedRoute_SetsRunwayPositionAndSpaceSeparatedRoute()
    {
        var (request, error) = SpawnParser.Parse("I S P 28R NIMI6.OAK.SAU");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.Runway, request.PositionType);
        Assert.Equal("28R", request.RunwayId);
        Assert.Equal("NIMI6 OAK SAU", request.Route);
    }

    [Fact]
    public void Parse_BareRunway_HasEmptyRoute()
    {
        var (request, error) = SpawnParser.Parse("I S P 28R");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.Runway, request.PositionType);
        Assert.Equal("", request.Route);
    }

    [Fact]
    public void Parse_RunwayWithNumericSecondToken_IsOnFinalNotRoute()
    {
        var (request, error) = SpawnParser.Parse("I S P 28R 5");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.OnFinal, request.PositionType);
        Assert.Equal(5, request.FinalDistanceNm);
        Assert.Equal("", request.Route);
    }

    [Fact]
    public void Parse_RunwayRouteWithTrailingType_KeepsRouteAndType()
    {
        var (request, error) = SpawnParser.Parse("I S P 28R NIMI6.OAK.SAU C421");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(SpawnPositionType.Runway, request.PositionType);
        Assert.Equal("NIMI6 OAK SAU", request.Route);
        Assert.Equal("C421", request.ExplicitType);
    }
}
