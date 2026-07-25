using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// The post-touchdown go-around energy gate must hold on the real dispatch path, not just when
/// <c>CanAcceptCommand</c> is called directly.
///
/// <c>DispatchWithPhase</c> runs <c>TryApplyTowerCommand</c> before consulting the phase's acceptance, and GA is a
/// tower command — so <see cref="LandingPhase"/>'s rejection below <c>RejectedLandingMinSpeed</c> was unreachable in
/// production. A GA on a slow rollout installed <see cref="GoAroundPhase"/>, which clears <c>IsOnGround</c> and
/// commands an initial climb, so the aircraft climbed away from the runway below its own go-around threshold.
/// </summary>
public sealed class GoAroundEnergyGateDispatchTests
{
    private static AircraftState MakeRollingOutAircraft(double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(280),
            Altitude = 100,
            IndicatedAirspeed = ias,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static (AircraftState Aircraft, LandingPhase Phase) RollingOut(double ias)
    {
        var rwy = TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);
        var ac = MakeRollingOutAircraft(ias);
        ac.Phases!.AssignedRunway = rwy;

        var phase = new LandingPhase();
        ac.Phases.Add(phase);

        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);
        Assert.Equal(LandingPhase.State.Rollout, phase.CurrentState);

        // The first rollout tick is what populates the phase's go-around energy flag.
        phase.OnTick(ctx);
        return (ac, phase);
    }

    [Fact]
    public void GoAroundBelowEnergyGate_IsRejectedThroughTheDispatcher_AndAircraftStaysOnGround()
    {
        var (aircraft, phase) = RollingOut(ias: 30);

        // Precondition: the phase really is refusing GA, so this cannot pass vacuously.
        Assert.True(phase.CanAcceptCommand(CanonicalCommandType.GoAround).IsRejected);

        var parsed = CommandParser.ParseCompound("GA");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("go-around speed gate", result.Message);
        Assert.True(aircraft.IsOnGround);
        Assert.IsNotType<GoAroundPhase>(aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// <c>docs/flight-physics.md</c> states the contract directly: <c>DesiredDecelRate</c> "must be cleared on phase
    /// transition or firm braking leaks into the next phase". <c>TickRollout</c> writes a ground braking rate every
    /// tick and only <c>TickHandoff</c> clears it, so breaking a rollout off with <c>GA</c> carried that rate onto the
    /// re-flown circuit — every deceleration down to the FAS bleed then ran at braking rate rather than airborne rate.
    /// </summary>
    [Fact]
    public void GoAroundFromRollout_ClearsTheRolloutBrakingRate()
    {
        var (aircraft, _) = RollingOut(ias: 80);

        // Precondition: the rollout tick really did write a braking rate, so this cannot pass vacuously.
        Assert.NotNull(aircraft.Targets.DesiredDecelRate);

        var parsed = CommandParser.ParseCompound("GA");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success, result.Message);

        Assert.Null(aircraft.Targets.DesiredDecelRate);
    }

    [Fact]
    public void GoAroundAboveEnergyGate_StillWorksThroughTheDispatcher()
    {
        var (aircraft, phase) = RollingOut(ias: 80);

        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.GoAround));

        var parsed = CommandParser.ParseCompound("GA");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, result.Message);
    }
}
