using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public class GroundRunwayEndDesignatorsTests
{
    [Fact]
    public void EndDesignators_OakRunways_SplitsEachNameIntoBothEnds()
    {
        var groundData = new TestAirportGroundData();
        var oak = groundData.GetLayout("OAK");
        if (oak is null)
        {
            return;
        }

        var byName = oak.Runways.ToDictionary(r => r.Name, r => r.EndDesignators, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(new[] { "30", "12" }, byName["30 - 12"]);
        Assert.Equal(new[] { "28L", "10R" }, byName["28L - 10R"]);
        Assert.Equal(new[] { "28R", "10L" }, byName["28R - 10L"]);
        Assert.Equal(new[] { "15", "33" }, byName["15 - 33"]);
    }

    [Fact]
    public void EndDesignators_NameWithoutSpaces_StillSplits()
    {
        var rwy = new GroundRunway
        {
            Name = "28R-10L",
            Coordinates = [(37.0, -122.0), (37.01, -122.0)],
            WidthFt = 150,
        };
        Assert.Equal(new[] { "28R", "10L" }, rwy.EndDesignators);
    }
}
