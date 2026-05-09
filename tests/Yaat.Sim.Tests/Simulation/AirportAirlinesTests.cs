using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests.Simulation;

public class AirportAirlinesTests
{
    [Fact]
    public void FixtureLoadsAirportAirlineMap()
    {
        Assert.True(AirportAirlines.AirportCount >= 60);
        Assert.Equal("OAK", AirportAirlines.NormalizeAirportId("KOAK"));
        Assert.True(AirportAirlines.TryGetAirlinesForAirport("OAK", out var oakAirlines));
        Assert.True(AirportAirlines.TryGetAirlinesForAirport("KOAK", out var koakAirlines));
        Assert.Same(oakAirlines, koakAirlines);
        Assert.Contains(oakAirlines, a => a.Icao == "SWA" && a.Arrivals > 1000 && a.Confidence == "regular");
    }

    [Fact]
    public void FixtureUsesRecentBtsPeriod()
    {
        Assert.Equal("2025-02", AirportAirlines.PeriodStart);
        Assert.Equal("2026-01", AirportAirlines.PeriodEnd);
        Assert.Contains("BTS T-100", AirportAirlines.SourceDescription, StringComparison.OrdinalIgnoreCase);
    }
}
