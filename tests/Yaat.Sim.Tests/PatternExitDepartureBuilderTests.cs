using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for the pattern-exit departure circuit builder and clearance phrasing
/// (CTO MRC/MRD/MLC/MLD). The end-to-end behavior is covered by
/// <see cref="Simulation.CtoMrdPatternExitTests"/>.
/// </summary>
public class PatternExitDepartureBuilderTests
{
    private static RunwayInfo Oak28R() =>
        TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72481,
            thresholdLon: -122.20471,
            endLat: 37.73047,
            endLon: -122.22218,
            heading: 292,
            elevationFt: 9
        );

    [Fact]
    public void BuildPatternExitCircuit_DownwindExit_UpwindCrosswindExit_NoLandingTail()
    {
        var phases = PatternBuilder.BuildPatternExitCircuit(
            Oak28R(),
            AircraftCategory.Piston,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            assignedAltitude: null,
            cruiseAltitude: 11500,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            airportRunways: null
        );

        Assert.Collection(phases, p => Assert.IsType<UpwindPhase>(p), p => Assert.IsType<CrosswindPhase>(p), p => Assert.IsType<PatternExitPhase>(p));

        // A departure has no re-entry: no base / final / landing.
        Assert.DoesNotContain(phases, p => p is BasePhase or FinalApproachPhase or TouchAndGoPhase or LandingPhase);

        // The continuous-climb target (cruise, since no assigned altitude) is propagated to the legs.
        Assert.Equal(11500, ((UpwindPhase)phases[0]).DepartureClimbTargetFt);
        Assert.Equal(11500, ((CrosswindPhase)phases[1]).DepartureClimbTargetFt);
    }

    [Fact]
    public void BuildPatternExitCircuit_NoCruiseNoAssigned_ClimbsToPatternAltitude_NotZero()
    {
        // Regression (issue #241): a VFR aircraft with no filed cruise altitude
        // (FlightPlan.CruiseAltitude defaults to 0) and no assigned altitude must climb toward
        // pattern altitude, never 0 ft MSL — otherwise the legs target 0 and fly the climb rate
        // straight into the ground.
        var phases = PatternBuilder.BuildPatternExitCircuit(
            Oak28R(),
            AircraftCategory.Piston,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            assignedAltitude: null,
            cruiseAltitude: 0,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            airportRunways: null
        );

        // OAK 28R piston pattern altitude = field elev 9 ft + 1000 ft AGL = 1009 ft MSL.
        const int patternAlt = 1009;
        Assert.Equal(patternAlt, ((UpwindPhase)phases[0]).DepartureClimbTargetFt);
        Assert.Equal(patternAlt, ((CrosswindPhase)phases[1]).DepartureClimbTargetFt);
        Assert.Equal(patternAlt, ((PatternExitPhase)phases[2]).ClimbTargetFt);
    }

    [Fact]
    public void BuildPatternExitCircuit_CrosswindExit_UpwindThenExit_NoCrosswindLeg()
    {
        var phases = PatternBuilder.BuildPatternExitCircuit(
            Oak28R(),
            AircraftCategory.Piston,
            PatternDirection.Left,
            PatternEntryLeg.Crosswind,
            assignedAltitude: 3000,
            cruiseAltitude: 11500,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            airportRunways: null
        );

        Assert.Collection(phases, p => Assert.IsType<UpwindPhase>(p), p => Assert.IsType<PatternExitPhase>(p));

        // An assigned altitude wins as the climb target.
        Assert.Equal(3000, ((UpwindPhase)phases[0]).DepartureClimbTargetFt);
        var exit = Assert.IsType<PatternExitPhase>(phases[1]);
        Assert.Equal(3000, exit.ClimbTargetFt);
        Assert.Equal(PatternDirection.Left, exit.Direction);
    }

    [Theory]
    [InlineData(PatternEntryLeg.Crosswind, PatternDirection.Right, ", right crosswind departure")]
    [InlineData(PatternEntryLeg.Crosswind, PatternDirection.Left, ", left crosswind departure")]
    [InlineData(PatternEntryLeg.Downwind, PatternDirection.Right, ", right downwind departure")]
    [InlineData(PatternEntryLeg.Downwind, PatternDirection.Left, ", left downwind departure")]
    public void FormatDepartureInstructionSuffix_PatternExit_VernacularPhrasing(PatternEntryLeg leg, PatternDirection dir, string expected)
    {
        var suffix = DepartureClearanceHandler.FormatDepartureInstructionSuffix(new PatternExitDeparture(leg, dir));
        Assert.Equal(expected, suffix);
    }

    [Theory]
    [InlineData("CTO MRC", PatternEntryLeg.Crosswind, PatternDirection.Right)]
    [InlineData("CTO MRD", PatternEntryLeg.Downwind, PatternDirection.Right)]
    [InlineData("CTO MLC", PatternEntryLeg.Crosswind, PatternDirection.Left)]
    [InlineData("CTO MLD", PatternEntryLeg.Downwind, PatternDirection.Left)]
    public void Cto_NamedPatternDeparture_RoundTripsToCanonical(string input, PatternEntryLeg leg, PatternDirection dir)
    {
        var parsed = CommandParser.Parse(input);
        var cto = Assert.IsType<ClearedForTakeoffCommand>(parsed.Value);
        var ped = Assert.IsType<PatternExitDeparture>(cto.Departure);
        Assert.Equal(leg, ped.ExitLeg);
        Assert.Equal(dir, ped.Direction);

        // Canonical form must round-trip back to the same token (recordings depend on this).
        Assert.Equal(input, CommandDescriber.DescribeCommand(cto));
    }
}
