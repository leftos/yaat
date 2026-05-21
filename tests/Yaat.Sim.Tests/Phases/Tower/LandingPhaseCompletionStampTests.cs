using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests.LandingPhaseTests;

/// <summary>
/// Direct test for M12.4's LandingPhase touchdown completion stamp. The unit-level
/// AircraftDebriefDataTests cover the state mutations and registry behavior, but the
/// landing path itself only had implicit coverage via the broader OAK lifecycle suite.
/// Codifies the contract: after the phase transitions into the Touchdown state and ticks,
/// the aircraft's completion fields reflect <c>Landed</c> on the assigned runway.
/// </summary>
public class LandingPhaseCompletionStampTests
{
    public LandingPhaseCompletionStampTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo Oak28R() =>
        TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: 280,
            elevationFt: 9
        );

    [Fact]
    public void LandingPhase_TickTouchdown_StampsLandedCompletionWithRunway()
    {
        var runway = Oak28R();
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 25,
            IndicatedAirspeed = 80,
            IsOnGround = false,
        };
        var phases = new PhaseList { AssignedRunway = runway };
        var landing = new LandingPhase();
        phases.Add(landing);
        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            ScenarioElapsedSeconds = 245.0,
        };

        landing.OnStart(ctx);
        // OnStart selected a state from current AGL; force Touchdown to test the stamp
        // path deterministically. This is the same approach the snapshot round-trip uses
        // to restore a phase to a specific sub-state.
        landing.CurrentState = LandingPhase.State.Touchdown;

        landing.OnTick(ctx);

        Assert.Equal(CompletionReason.Landed, ac.CompletionReason);
        Assert.Equal(245.0, ac.CompletedAtSeconds);
        Assert.Equal("28R", ac.CompletionDetail);
    }

    [Fact]
    public void LandingPhase_AlreadyCompletedAircraft_DoesNotOverwriteOnTouchdown()
    {
        // A controller could conceivably issue CT before landing and then the aircraft
        // touches down anyway. First-write-wins keeps the canonical completion record.
        var runway = Oak28R();
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 25,
            IndicatedAirspeed = 80,
            IsOnGround = false,
            CompletedAtSeconds = 100,
            CompletionReason = CompletionReason.HandedOff,
            CompletionDetail = "NCT_F_APP",
        };
        var phases = new PhaseList { AssignedRunway = runway };
        var landing = new LandingPhase();
        phases.Add(landing);
        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            ScenarioElapsedSeconds = 300.0,
        };

        landing.OnStart(ctx);
        landing.CurrentState = LandingPhase.State.Touchdown;
        landing.OnTick(ctx);

        Assert.Equal(CompletionReason.HandedOff, ac.CompletionReason);
        Assert.Equal(100.0, ac.CompletedAtSeconds);
        Assert.Equal("NCT_F_APP", ac.CompletionDetail);
    }
}
