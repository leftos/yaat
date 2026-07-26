using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// An <c>EL</c>/<c>ER</c>/<c>EXIT &lt;twy&gt;</c> issued after <see cref="RunwayExitPhase"/> has already
/// committed a route to the navigator used to return a success echo — "Exit at T" — while the aircraft
/// carried on turning off at the taxiway it had already chosen. The phase's mid-tick preference re-check
/// is unreachable once the state is <c>FollowingExitPath</c>, and nothing clears the committed route, so
/// the instruction was acknowledged and dropped.
///
/// The controller now gets told the aircraft is unable, which is what a pilot already established in the
/// turn would say. While the aircraft is still tracking the centerline the change is honored as before —
/// that path was never broken.
/// </summary>
public class LateExitChangeTests(ITestOutputHelper output)
{
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

    private static AircraftState? BuildCommittedExit(RunwayExitPhase.ExitState state)
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return null;
        }

        var pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return null;
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
            ExitStateValue = (int)state,
            ExitWaypointNodeIds = [branch.Id, holdShort.Id],
        };

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
        aircraft.Phases.Add(RunwayExitPhase.FromSnapshot(dto, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.LayoutAirportId = "OAK";
        return aircraft;
    }

    [Fact]
    public void ExitChange_AfterTheTurnIsCommitted_IsRefusedNotEchoed()
    {
        var aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.FollowingExitPath);
        if (aircraft is null)
        {
            return;
        }

        var committed = Assert.IsType<RunwayExitPhase>(aircraft.Phases!.CurrentPhase);
        Assert.NotNull(committed.CommittedExitTaxiway);

        var before = aircraft.Phases.RequestedExit;
        var result = GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Taxiway = "B" }, noDelete: false, expedite: false);

        output.WriteLine($"committed={committed.CommittedExitTaxiway} result={result.Success} msg={result.Message}");

        Assert.False(result.Success);
        Assert.Contains(committed.CommittedExitTaxiway!, result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, aircraft.Phases.RequestedExit);
    }

    [Fact]
    public void ExitChange_WhileStillOnTheCenterline_IsAccepted()
    {
        var aircraft = BuildCommittedExit(RunwayExitPhase.ExitState.RollingOnCenterline);
        if (aircraft is null)
        {
            return;
        }

        var result = GroundCommandHandler.TryExitCommand(aircraft, new ExitPreference { Taxiway = "B" }, noDelete: false, expedite: false);

        output.WriteLine($"result={result.Success} msg={result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Equal("B", aircraft.Phases!.RequestedExit?.Taxiway);
    }
}
