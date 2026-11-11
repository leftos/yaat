using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// What a TAXI to a stand or spot costs: how many candidate routes the missing-taxiway fallback resolves on the way to
/// its answer. The counts are route resolutions on the calling thread, never a duration, so they pin the work the tick
/// thread is spared.
/// </summary>
public class MissingTaxiwayFallbackCostTests(ITestOutputHelper output)
{
    /// <summary>
    /// SFO <c>TAXI F $16</c> from mid-F: F is not on the spot's reach and exactly one candidate is, so only that candidate
    /// is resolved beside the clearance.
    /// </summary>
    [Fact]
    public void TaxiF16FromMidF_ResolvesOnlyCandidatesTouchingTheSpot()
    {
        if (Spawning("F") is not { } spawned)
        {
            return;
        }

        int count = Dispatch(spawned, "TAXI F $16");
        Assert.True(count <= 2, $"TAXI F $16 from mid F resolved {count} routes, expected at most 2");
    }

    /// <summary>
    /// SFO <c>TAXI G $30</c> from mid-G: G does not lie on the spot's reach and no candidate does, so every candidate is
    /// skipped and only the clearance itself is resolved.
    /// </summary>
    [Fact]
    public void TaxiG30FromMidG_ResolvesOnce()
    {
        if (Spawning("G") is not { } spawned)
        {
            return;
        }

        int count = Dispatch(spawned, "TAXI G $30");
        Assert.Equal(1, count);
    }

    /// <summary>SFO <c>TAXI F $16</c> from mid-B: a few candidates lie on the spot's reach and are resolved, the rest are not.</summary>
    [Fact]
    public void TaxiF16FromMidB_ResolvesAtMostThree()
    {
        if (Spawning("B") is not { } spawned)
        {
            return;
        }

        int count = Dispatch(spawned, "TAXI F $16");
        Assert.True(count <= 3, $"TAXI F $16 from mid B resolved {count} routes, expected at most 3");
    }

    /// <summary>
    /// SFO <c>TAXI E $18</c> from mid-E still ends held short of C: the filter spares resolutions, never the answer.
    /// </summary>
    [Fact]
    public void TaxiE18FromMidE_StillHoldsShortOfC()
    {
        if (Spawning("E") is not { } spawned)
        {
            return;
        }

        CommandResult result = spawned.Ground.Engine.SendCommand("PRB1", "TAXI E $18");
        output.WriteLine($"TAXI E $18: {result.Success} — {result.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(spawned.Aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.NotEmpty(route.HoldShortPoints);
        HoldShortPoint last = route.HoldShortPoints[^1];
        Assert.Equal("C", Assert.IsType<string>(last.TargetName));
        Assert.Equal(HoldShortReason.RouteIncomplete, last.Reason);
    }

    /// <summary>
    /// SFO with an aircraft spawned in <see cref="HoldingInPositionPhase"/> at the middle of <paramref name="taxiway"/>,
    /// or null when the committed SFO layout is unavailable (the harness's silent skip).
    /// </summary>
    private (SfoGround Ground, AircraftState Aircraft)? Spawning(string taxiway)
    {
        if (SfoGroundHarness.Build(output, AutoCrossRunwayMode.All) is not { } ground)
        {
            return null;
        }

        (GroundNode node, TrueHeading heading) = MidNode(ground.Layout, taxiway);
        output.WriteLine($"mid {taxiway} is node {node.Id}");
        AircraftState aircraft = SfoGroundHarness.SpawnAt(ground, "PRB1", "B738", (node, heading), new HoldingInPositionPhase());
        return (ground, aircraft);
    }

    /// <summary>Sends <paramref name="command"/> and returns how many routes its resolution took on this thread.</summary>
    private int Dispatch((SfoGround Ground, AircraftState Aircraft) spawned, string command)
    {
        GroundCommandHandler.ResolveDirectCountForTests = 0;
        spawned.Ground.Engine.SendCommand("PRB1", command);
        int count = GroundCommandHandler.ResolveDirectCountForTests;
        output.WriteLine($"{command}: {count} route resolutions");
        return count;
    }

    /// <summary>The middle of a plain run of <paramref name="twy"/> — no runway hold-short, straight edges only — and the heading along it.</summary>
    private static (GroundNode Node, TrueHeading Heading) MidNode(AirportGroundLayout layout, string twy)
    {
        List<GroundNode> plain =
        [
            .. layout
                .GetNodesOnTaxiway(twy)
                .Where(n => n.Type != GroundNodeType.RunwayHoldShort)
                .Where(n => (n.Edges.Count == 2) && n.Edges.All(e => (e is not GroundArc) && e.MatchesTaxiway(twy)))
                .OrderBy(n => n.Position.Lon),
        ];
        Assert.True(plain.Count > 0, $"SFO layout has no plain run of taxiway {twy}");
        GroundNode node = plain[plain.Count / 2];
        return (node, new TrueHeading(GeoMath.BearingTo(node.Position, node.Edges[0].OtherNode(node).Position)));
    }
}
