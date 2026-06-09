using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies the shipping SFO sidecar (<c>Data/ARTCCs/ZOA/Airports/sfo.json</c>) loads its blocked turn and
/// that the committed coordinates resolve, against the real SFO layout, to the L/F apex corner — i.e. the
/// authored data is correct end-to-end (loader → catalog → resolver), not just the resolver in isolation.
/// </summary>
public class BlockedTurnSidecarTests
{
    public BlockedTurnSidecarTests() => TestVnasData.EnsureInitialized();

    private static AirportGroundLayout? LoadSfo()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("SFO", File.ReadAllText(path), null) : null;
    }

    [Fact]
    public void Sfo_ShippingSidecar_LoadsTheLFBlockedTurn()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var turns = NavigationDatabase.Instance.AirportSidecars.GetBlockedTurns("SFO");
        Assert.NotEmpty(turns);
        // Same data resolves via the ICAO form.
        Assert.NotEmpty(NavigationDatabase.Instance.AirportSidecars.GetBlockedTurns("KSFO"));
    }

    [Fact]
    public void Sfo_ShippingSidecar_ResolvesToTheLFApexCornerArc()
    {
        var layout = LoadSfo();
        if (layout is null || TestVnasData.NavigationDb is null)
        {
            return;
        }

        var turns = NavigationDatabase.Instance.AirportSidecars.GetBlockedTurns("SFO");
        var result = BlockedTurnResolver.Resolve(layout, turns);

        Assert.NotEmpty(result.HiddenArcPairs);
        Assert.NotEmpty(result.ForbiddenTurns);

        // Every hidden corner arc bridges L and F (the blocked apex), and only a few arcs are hidden.
        foreach (var (a, b) in result.HiddenArcPairs)
        {
            var arc = layout.Arcs.FirstOrDefault(x => (x.Nodes[0].Id == a && x.Nodes[1].Id == b) || (x.Nodes[0].Id == b && x.Nodes[1].Id == a));
            Assert.NotNull(arc);
            Assert.True(arc!.MatchesTaxiway("L") && arc.MatchesTaxiway("F"));
        }

        var lfArcs = layout.Arcs.Where(x => x.MatchesTaxiway("L") && x.MatchesTaxiway("F")).ToList();
        int hiddenLf = lfArcs.Count(x => result.IsHiddenArc(x.Nodes[0].Id, x.Nodes[1].Id));
        Assert.True(lfArcs.Count > hiddenLf, "other L/F corner arcs at the junction must remain drawn");
    }
}
