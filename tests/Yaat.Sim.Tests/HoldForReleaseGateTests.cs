using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public class HoldForReleaseGateTests
{
    private static AircraftState HoldingShortDeparture(string callsign, bool held)
    {
        var phases = new PhaseList();
        phases.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "28R",
                }
            )
        );
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSJC",
                Destination = "KLAX",
                FlightRules = "IFR",
            },
            Phases = phases,
        };
        ac.Ground.HeldForRelease = held;
        return ac;
    }

    private static CommandResult Dispatch(string command, AircraftState ac)
    {
        var parsed = CommandParser.ParseCompound(command, ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var ctx = TestDispatch.Context(new System.Random(0));
        return CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
    }

    [Fact]
    public void Cto_RejectedWhileHeldForRelease()
    {
        var ac = HoldingShortDeparture("N1", held: true);

        var result = Dispatch("CTO", ac);

        Assert.False(result.Success);
        Assert.Contains("held for release", result.Message);
    }

    [Fact]
    public void Luaw_RejectedWhileHeldForRelease()
    {
        var ac = HoldingShortDeparture("N2", held: true);

        var result = Dispatch("LUAW", ac);

        Assert.False(result.Success);
        Assert.Contains("held for release", result.Message);
    }

    [Fact]
    public void Cto_NotBlockedByGateWhenNotHeld()
    {
        var ac = HoldingShortDeparture("N3", held: false);

        var result = Dispatch("CTO", ac);

        // The hold-for-release gate must not fire — any rejection here would be for other reasons.
        Assert.DoesNotContain("held for release", result.Message ?? string.Empty);
    }
}
