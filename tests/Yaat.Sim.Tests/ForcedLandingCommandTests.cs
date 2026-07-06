using System;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// CLANDF command wiring: parse/describe, the RPO-only solo gate, the clearance + ForceLanding
/// flag it sets, and the cancel paths (GA and CLC clear the override).
/// </summary>
public sealed class ForcedLandingCommandTests
{
    private static RunwayInfo MakeRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);

    private static AircraftState MakeOnFinal(RunwayInfo rwy)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt + 800,
            IndicatedAirspeed = 140,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KTEST" },
        };
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        ac.Phases.Add(new LandingPhase());
        return ac;
    }

    /// <summary>
    /// A VFR pattern aircraft mid-go-around: climbing out on runway heading, aligned with the
    /// assigned runway, with only a <see cref="GoAroundPhase"/> current (a go-around wipes every
    /// pending landing phase). This is the N500M bug state.
    /// </summary>
    private static AircraftState MakeInGoAround(RunwayInfo rwy)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "C182",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt + 350,
            IndicatedAirspeed = 80,
            VerticalSpeed = 1000,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KTEST" },
        };
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Left };
        ac.Phases.Add(new GoAroundPhase { ReenterPattern = true, NextLandingFullStop = false });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        return ac;
    }

    [Fact]
    public void Parse_Clandf_ProducesForceLandingCommand()
    {
        var result = CommandParser.Parse("CLANDF");

        var cmd = Assert.IsType<ForceLandingCommand>(result.Value);
        Assert.Equal("CLANDF", CommandDescriber.DescribeCommand(cmd));
        Assert.Equal("Force landing (override go-around)", CommandDescriber.DescribeNatural(cmd));
    }

    [Fact]
    public void Handler_RpoMode_GrantsClearanceAndSetsForceLanding()
    {
        var ac = MakeOnFinal(MakeRunway());

        var result = PatternCommandHandler.TryForceLanding(new ForceLandingCommand(), ac, TestDispatch.Context(new Random(1)));

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Phases!.ForceLanding);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);
        Assert.Equal("28", ac.Phases.ClearedRunwayId);
    }

    [Fact]
    public void Handler_DuringGoAround_ReversesToFinalAndForcesLanding()
    {
        var ac = MakeInGoAround(MakeRunway());
        Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);

        var result = PatternCommandHandler.TryForceLanding(new ForceLandingCommand(), ac, TestDispatch.Context(new Random(1)));

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Phases.ForceLanding);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);
        Assert.Equal("28", ac.Phases.ClearedRunwayId);

        // The go-around is cancelled: the aircraft is re-established on final with a pending
        // full-stop landing (not a touch-and-go), and is no longer in the go-around phase.
        Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
        Assert.Contains(ac.Phases.Phases, p => p is LandingPhase && p.Status == PhaseStatus.Pending);
        Assert.DoesNotContain(ac.Phases.Phases, p => p is GoAroundPhase && p.Status != PhaseStatus.Completed);
    }

    [Fact]
    public void Handler_SoloTraining_RejectedAsRpoOnly()
    {
        var ac = MakeOnFinal(MakeRunway());

        var result = PatternCommandHandler.TryForceLanding(
            new ForceLandingCommand(),
            ac,
            TestDispatch.Context(new Random(1), soloTrainingMode: true)
        );

        Assert.False(result.Success);
        Assert.Contains("RPO-only", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.False(ac.Phases!.ForceLanding);
    }

    [Fact]
    public void Handler_OnGround_Rejected()
    {
        var ac = MakeOnFinal(MakeRunway());
        ac.IsOnGround = true;

        var result = PatternCommandHandler.TryForceLanding(new ForceLandingCommand(), ac, TestDispatch.Context(new Random(1)));

        Assert.False(result.Success);
        Assert.False(ac.Phases!.ForceLanding);
    }

    [Fact]
    public void GoAround_ClearsForceLanding()
    {
        var ac = MakeOnFinal(MakeRunway());
        ac.Phases!.ForceLanding = true;
        ac.Phases.LandingClearance = ClearanceType.ClearedToLand;

        var result = PatternCommandHandler.TryGoAround(new GoAroundCommand(null, null, null), ac);

        Assert.True(result.Success, result.Message);
        Assert.False(ac.Phases.ForceLanding);
    }

    [Fact]
    public void CancelLandingClearance_ClearsForceLanding()
    {
        var ac = MakeOnFinal(MakeRunway());
        ac.Phases!.ForceLanding = true;
        ac.Phases.LandingClearance = ClearanceType.ClearedToLand;
        ac.Phases.ClearedRunwayId = "28";

        var result = PatternCommandHandler.TryCancelLandingClearance(ac);

        Assert.True(result.Success, result.Message);
        Assert.False(ac.Phases.ForceLanding);
        Assert.Null(ac.Phases.LandingClearance);
    }
}
