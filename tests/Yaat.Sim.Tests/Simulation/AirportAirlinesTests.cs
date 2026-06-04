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

    // Guards the carrier-crosswalk fix: OpenFlights reuses 2-letter IATA codes across defunct and current
    // carriers, so a naive last-wins map relabeled real US operators as defunct foreign airlines whose ICAO
    // collided with a different airline-fleets.json entry (8C/Air Transport Intl -> Shanxi -> CXI/Corendon;
    // N3 -> Omskavia -> OMS/SalamAir; E7 -> Estafeta -> European Aviation -> EAF/Electra). If a regenerated
    // fixture reintroduces any of these ICAOs, the build crosswalk guard regressed.
    [Theory]
    [InlineData("OMS")] // SalamAir (Oman) — Omskavia code collision
    [InlineData("CXI")] // Corendon (Malta) — Shanxi code collision
    [InlineData("EAF")] // Electra (Bulgaria) — European Aviation code collision
    public void Fixture_DoesNotContainMislabeledCarrier(string icao)
    {
        var hits = new List<string>();
        foreach (var airport in new[] { "OAK", "LAX", "MIA", "ATL", "CLT", "DEN", "DFW", "SFO", "SEA", "ORD", "PHX" })
        {
            if (AirportAirlines.TryGetAirlinesForAirport(airport, out var airlines) && airlines.Any(a => a.Icao == icao))
            {
                hits.Add(airport);
            }
        }

        Assert.True(hits.Count == 0, $"mislabeled carrier {icao} reappeared at: {string.Join(", ", hits)}");
    }

    [Fact]
    public void Fixture_MapsRealUsCargoCarrier_NotForeignCollision()
    {
        // 8C's real operator is Air Transport International (ATN), recovered by the crosswalk fix.
        Assert.True(AirportAirlines.TryGetAirlinesForAirport("SEA", out var sea));
        Assert.Contains(sea, a => a.Icao == "ATN" && a.Arrivals > 100);
    }
}
