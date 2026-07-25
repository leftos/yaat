using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests;

/// <summary>
/// A restored <see cref="HoldingShortPhase"/> must share the route's <see cref="HoldShortPoint"/>, not a detached copy.
///
/// The phase snapshot carries only the node id, runway and reason, so restore rebuilt a fresh point without
/// <c>TailOverRunwayNodeId</c>. That is the field <c>CLRWY</c> gates on, so after any rewind/replay the command was
/// permanently refused — while <c>SimulationEngine.BuildOccupiedHoldShortNodes</c> still read the route copy and kept
/// the runway hold-short marked occupied. The aircraft blocks the runway with no command able to move it.
/// </summary>
public sealed class HoldShortRestoreTests
{
    public HoldShortRestoreTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>Finds a hold-short node with a named taxiway edge to a neighbour, so a one-segment route can be built.</summary>
    private static (GroundNode HoldShort, GroundNode Neighbour, IGroundEdge Edge)? FindHoldShortWithEdge(AirportGroundLayout layout, string runwayId)
    {
        foreach (var holdShort in layout.GetRunwayHoldShortNodes(runwayId))
        {
            foreach (var edge in holdShort.Edges)
            {
                if (string.IsNullOrEmpty(edge.TaxiwayName))
                {
                    continue;
                }

                foreach (var node in edge.Nodes)
                {
                    if (node.Id != holdShort.Id)
                    {
                        return (holdShort, node, edge);
                    }
                }
            }
        }

        return null;
    }

    [Fact]
    public void RestoredHoldingShort_KeepsTailOverRunway_SoClrwyStaysAvailable()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var found = FindHoldShortWithEdge(layout, "28R");
        if (found is null)
        {
            return;
        }

        var (holdShortNode, neighbour, edge) = found.Value;

        // The issue-#172 "W2" state: holding short of a taxiway with the tail still over a runway.
        var holdShort = new HoldShortPoint
        {
            NodeId = holdShortNode.Id,
            Reason = HoldShortReason.RunwayCrossing,
            TargetName = "28R",
            TailOverRunwayNodeId = neighbour.Id,
        };

        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = edge.TaxiwayName, Edge = edge.Directed(neighbour, holdShortNode) }],
            HoldShortPoints = [holdShort],
        };

        var aircraft = new AircraftState
        {
            Callsign = "JBU577",
            AircraftType = "A320",
            Position = holdShortNode.Position,
            TrueHeading = new TrueHeading(280),
            Altitude = 9,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
        aircraft.Ground.AssignedTaxiRoute = route;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingShortPhase(holdShort));

        // Precondition: CLRWY is available before the round-trip, so this cannot pass vacuously.
        var live = (HoldingShortPhase)aircraft.Phases.CurrentPhase!;
        Assert.NotNull(live.HoldShort.TailOverRunwayNodeId);
        Assert.False(live.CanAcceptCommand(CanonicalCommandType.ClearRunway).IsRejected);

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), layout);
        var restoredPhase = Assert.IsType<HoldingShortPhase>(restored.Phases?.CurrentPhase);

        Assert.NotNull(restoredPhase.HoldShort.TailOverRunwayNodeId);
        Assert.False(
            restoredPhase.CanAcceptCommand(CanonicalCommandType.ClearRunway).IsRejected,
            "CLRWY was refused after a snapshot restore, stranding the aircraft over the runway"
        );
    }
}
