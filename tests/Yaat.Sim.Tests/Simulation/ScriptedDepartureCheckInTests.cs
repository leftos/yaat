using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Engine-level coverage that scenario-scripted takeoff clearances do NOT establish student contact,
/// so the departure still makes its post-takeoff check-in. Complements the dispatcher-gate unit tests
/// by exercising the real production wiring: the preset dispatcher (<see cref="SimulationEngine.DispatchPresetCommands"/>)
/// and the hold-for-release auto-CTO (<see cref="SimulationEngine.ProcessReleasedGroundDepartures"/>).
/// Each case pairs the scripted dispatch with a live-controller control on an identical aircraft: the
/// control MUST establish contact, which proves the CTO actually succeeds (so the scripted assertion is
/// not vacuous) and that the only difference is the scripted origin.
/// Regression: "Z | S3-NCTA-1 | Area A Familiarization" bug bundle (QXE2274 departing KSJC).
/// </summary>
public class ScriptedDepartureCheckInTests
{
    public ScriptedDepartureCheckInTests() => TestVnasData.EnsureInitialized();

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

    private static SimScenarioState MinimalScenario(double elapsedSeconds) =>
        new()
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = elapsedSeconds,
        };

    private static AircraftState LinedUpIfrDeparture(RunwayInfo runway)
    {
        var ac = new AircraftState
        {
            Callsign = "AAL123",
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Altitude = PlannedAltitude.Ifr(5000),
                FlightRules = "IFR",
                HasFlightPlan = true,
            },
        };
        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new LinedUpAndWaitingPhase());
        phases.Add(new TakeoffPhase());
        phases.Add(new InitialClimbPhase { Departure = new DefaultDeparture(), CruiseAltitude = 5000 });
        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        return ac;
    }

    private static AircraftState HeldThenReleasedDeparture(RunwayInfo runway)
    {
        var ac = new AircraftState
        {
            Callsign = "AAL456",
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "KLAX",
                Altitude = PlannedAltitude.Ifr(5000),
                FlightRules = "IFR",
                HasFlightPlan = true,
            },
        };
        var phases = new PhaseList { AssignedRunway = runway };
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
        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        // Released by the student; the automated tower has not yet issued the takeoff clearance.
        ac.Ground.HeldForRelease = false;
        ac.Ground.ReleasedForDeparture = true;
        ac.Ground.ReleasedAtSeconds = 0;
        return ac;
    }

    private static void LiveCto(AircraftState ac)
    {
        var parsed = CommandParser.ParseCompound("CTO", ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse failed: {parsed.Reason}");
        var result = CommandDispatcher.DispatchCompound(
            parsed.Value!,
            ac,
            TestDispatch.Context(new SerializableRandom(7), isScenarioScripted: false)
        );
        Assert.True(result.Success, $"Live CTO should succeed: {result.Message}");
    }

    [Fact]
    public void PresetCto_ViaPresetDispatcher_DoesNotEstablishInitialContact()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = MinimalScenario(0) };
        var scripted = LinedUpIfrDeparture(Oak28R());

        engine.DispatchPresetCommands(
            new LoadedAircraft { State = scripted, PresetCommands = [new PresetCommand { Command = "CTO", TimeOffset = 0 }] }
        );

        Assert.False(scripted.HasMadeInitialContact, "A scripted CTO preset must not establish student contact.");

        // Control: same setup, live controller CTO — establishes contact (proves CTO succeeds on this setup).
        var live = LinedUpIfrDeparture(Oak28R());
        LiveCto(live);
        Assert.True(live.HasMadeInitialContact);
    }

    [Fact]
    public void AutoCto_OnHoldForReleaseRelease_DoesNotEstablishInitialContact()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        // Elapsed time well past the deterministic tower-readback jitter so the auto-CTO fires.
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = MinimalScenario(10_000) };
        var scripted = HeldThenReleasedDeparture(Oak28R());
        engine.World.AddAircraft(scripted);

        engine.ProcessReleasedGroundDepartures();

        Assert.False(scripted.Ground.ReleasedForDeparture, "The release loop must have processed this aircraft.");
        Assert.False(
            scripted.HasMadeInitialContact,
            "The auto-CTO is issued by the automated tower, not the student, so it must not establish contact."
        );

        // Control: same holding-short setup, live controller CTO — establishes contact (proves CTO succeeds).
        var live = HeldThenReleasedDeparture(Oak28R());
        LiveCto(live);
        Assert.True(live.HasMadeInitialContact);
    }
}
