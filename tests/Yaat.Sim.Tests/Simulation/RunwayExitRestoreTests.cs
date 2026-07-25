using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// A snapshot taken while an aircraft is following its runway-exit path must restore into a phase that keeps
/// following it.
///
/// <c>ToSnapshot</c> persists the state, the waypoint node ids and the navigator, but the exit <em>route</em> is
/// rebuilt from the live ground layout and is not serialized. <c>TickFollowingExitPath</c> reads a null route as
/// "exit complete", so a restored phase ended immediately — bypassing <c>CompleteExit</c>, which is what inserts
/// <c>HoldingAfterExitPhase</c>, clears <c>IsExpeditingExit</c>, and marks the hold-short node occupied so another
/// arrival cannot plan the same exit.
/// </summary>
public sealed class RunwayExitRestoreTests
{
    public RunwayExitRestoreTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Picks a real hold-short node on <paramref name="runwayId"/> plus a neighbour joined by a named taxiway edge.
    /// Derived from the layout at runtime rather than hardcoded — fillet node ids are geometry-coupled and shift
    /// whenever the fixture is regenerated.
    /// </summary>
    private static (GroundNode Branch, GroundNode HoldShort, string Taxiway)? FindExitPair(AirportGroundLayout layout, string runwayId)
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
                        return (node, holdShort, edge.TaxiwayName);
                    }
                }
            }
        }

        return null;
    }

    [Fact]
    public void RestoredMidExit_ContinuesFollowingTheExitPath_InsteadOfReportingComplete()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return;
        }

        var (branch, holdShort, taxiway) = pair.Value;

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = holdShort.Id,
            ExitTaxiway = taxiway,
            RunwayId = "28R",
            ExitSpeed = 25.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = 281.0,
            ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
            ExitWaypointNodeIds = [branch.Id, holdShort.Id],
        };

        var phase = RunwayExitPhase.FromSnapshot(dto, layout);

        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = branch.Position,
            TrueHeading = new TrueHeading(281.0),
            Altitude = 9.0,
            IndicatedAirspeed = 25.0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
        aircraft.Phases = new PhaseList();

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            FieldElevation = 9.0,
            Logger = NullLogger.Instance,
        };

        bool completed = phase.OnTick(ctx);

        Assert.False(completed, "a phase restored mid-exit reported the exit complete on its first tick, skipping CompleteExit's cleanup");
    }
}
