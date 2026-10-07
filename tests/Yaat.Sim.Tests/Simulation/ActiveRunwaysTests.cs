using Xunit;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

public sealed class ActiveRunwaysTests
{
    [Fact]
    public void With_ReplacesAirportList()
    {
        ActiveRunways runways = ActiveRunways
            .Empty.With("OAK", [new ActiveRunway("28L", ActiveRunwayUse.Both)])
            .With("OAK", [new ActiveRunway("28R", ActiveRunwayUse.Departure), new ActiveRunway("30", ActiveRunwayUse.Arrival)]);

        ActiveRunway[] expected = [new("28R", ActiveRunwayUse.Departure), new("30", ActiveRunwayUse.Arrival)];
        Assert.Equal(expected, runways.For("OAK"));
    }

    [Fact]
    public void With_EmptyListRemovesAirport()
    {
        ActiveRunways runways = ActiveRunways.Empty.With("OAK", [new ActiveRunway("28L", ActiveRunwayUse.Both)]).With("OAK", []);

        Assert.Empty(runways.For("OAK"));
        Assert.DoesNotContain("OAK", runways.Airports);
        Assert.Equal(ActiveRunways.Empty, runways);
    }

    [Fact]
    public void Equality_SameListsAreEqual()
    {
        ActiveRunways a = ActiveRunways
            .Empty.With("OAK", [new ActiveRunway("28L", ActiveRunwayUse.Both)])
            .With("SFO", [new ActiveRunway("10L", ActiveRunwayUse.Arrival), new ActiveRunway("10R", ActiveRunwayUse.Departure)]);
        ActiveRunways b = ActiveRunways
            .Empty.With("SFO", [new ActiveRunway("10L", ActiveRunwayUse.Arrival), new ActiveRunway("10R", ActiveRunwayUse.Departure)])
            .With("OAK", [new ActiveRunway("28L", ActiveRunwayUse.Both)]);

        // Airport order does not matter; the list order within an airport does, as does the use.
        Assert.Equal(a, b);
        Assert.NotEqual(a, ActiveRunways.Empty.With("OAK", [new ActiveRunway("28L", ActiveRunwayUse.Both)]));
        Assert.NotEqual(a, ActiveRunways.Empty.With("OAK", [new ActiveRunway("28L", ActiveRunwayUse.Departure)]));
        Assert.NotEqual(
            a,
            ActiveRunways
                .Empty.With("OAK", [new ActiveRunway("28L", ActiveRunwayUse.Both)])
                .With("SFO", [new ActiveRunway("10R", ActiveRunwayUse.Departure), new ActiveRunway("10L", ActiveRunwayUse.Arrival)])
        );
    }

    [Theory]
    [InlineData("OAK")]
    [InlineData("oak")]
    [InlineData("KOAK")]
    public void For_KeysEverySpellingToTheSameAirport(string airportId)
    {
        ActiveRunways runways = ActiveRunways.Empty.With("OAK", [new ActiveRunway("28L", ActiveRunwayUse.Both)]);

        Assert.Equal("28L", Assert.Single(runways.For(airportId)).Designator);
        string[] expected = ["OAK"];
        Assert.Equal(expected, runways.Airports);
    }

    [Theory]
    [InlineData("28L", ActiveRunwayUse.Both, "28L")]
    [InlineData("28L", ActiveRunwayUse.Departure, "D28L")]
    [InlineData("28R", ActiveRunwayUse.Arrival, "A28R")]
    [InlineData("30", ActiveRunwayUse.Both, "30")]
    [InlineData("1L", ActiveRunwayUse.Departure, "D01L")]
    public void ToToken_PrefixesByUse(string designator, ActiveRunwayUse use, string expected) =>
        Assert.Equal(expected, new ActiveRunway(designator, use).ToToken());
}
