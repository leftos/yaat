using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The silent simulated tower (YAAT-1180) for arrivals: where the student does not work the tower and no AI tower covers
/// the airport, an arrival is cleared to land automatically and then calls the student for taxi-in, and no tower-type call
/// reaches the student. N152SP (C172) on a 4 nm final to OAK 28R under a ZOA student on OAK ground.
/// </summary>
public class SimulatedTowerArrivalTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public SimulatedTowerArrivalTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void GroundStudent_ArrivalLandsUncleared_ThenCallsForTaxiIn()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = LoadUnderStudent("OAK_GND", "GND", AtcPositionLevel.Ground);
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign) ?? throw new InvalidOperationException("N152SP did not spawn");

        for (int second = 0; aircraft.Phases?.CurrentPhase is not HoldingAfterExitPhase; second++)
        {
            Assert.True(second < 400, $"never exited the runway (phase {aircraft.Phases?.CurrentPhase?.Name ?? "none"})");
            Assert.IsNotType<GoAroundPhase>(aircraft.Phases?.CurrentPhase);
            Assert.False(
                aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Landing or PilotPendingRequestKind.Takeoff },
                "a tower-type request reached the ground student"
            );
            AiTestFixture.Tick(engine, 1);
            aircraft = engine.FindAircraft(AiTestFixture.Callsign) ?? throw new InvalidOperationException("N152SP left the world");
        }

        Assert.Null(aircraft.Phases!.LandingClearance);
        aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi },
            10
        );
        Assert.Equal("Oakland Ground", aircraft.PendingPilotRequest!.FacilityCallName);
        Assert.NotNull(aircraft.PendingPilotRequest.ParkingName);
        Assert.True(aircraft.HasMadeInitialContact);
    }

    [Fact]
    public void NullRange_ArrivalWithoutClearance_GoesAround()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = LoadUnderStudent("OAK_GND", "GND", null);

        AircraftState aircraft = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is GoAroundPhase, 400);

        Assert.False(aircraft.IsOnGround);
    }

    [Fact]
    public void AiTower_IsNotAutoCleared()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = LoadUnderStudent("OAK_GND", "GND", AtcPositionLevel.Ground);
        engine.Scenario!.SetAiStaffedPositions([TestAiPositions.OakTower(_zoa)]);

        Assert.False(engine.Scenario.IsAutoClearedToLandFor(Arrival(engine)));
    }

    [Fact]
    public void TowerStudent_IsNotAutoCleared()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = LoadUnderStudent("OAK_TWR", "TWR", AtcPositionLevel.Ground);

        Assert.False(engine.Scenario!.IsAutoClearedToLandFor(Arrival(engine)));
    }

    [Fact]
    public void RoomToggle_StillAutoClears_ForATowerStudent()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = LoadUnderStudent("OAK_TWR", "TWR", AtcPositionLevel.Ground);
        engine.Scenario!.AutoClearedToLand = true;

        Assert.True(engine.Scenario.IsAutoClearedToLandFor(Arrival(engine)));
        AircraftState aircraft = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            ac => (ac.Phases?.CurrentPhase is HoldingAfterExitPhase) || (ac.Phases?.CurrentPhase is GoAroundPhase),
            400
        );
        Assert.IsType<HoldingAfterExitPhase>(aircraft.Phases!.CurrentPhase);
    }

    private static AircraftState Arrival(SimulationEngine engine)
    {
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign) ?? throw new InvalidOperationException("N152SP did not spawn");
        Assert.NotNull(aircraft.Phases?.AssignedRunway);
        return aircraft;
    }

    /// <summary>The OAK final in a solo room whose student works <paramref name="studentCallsign"/>, room auto cleared-to-land off.</summary>
    private SimulationEngine LoadUnderStudent(string studentCallsign, string studentType, AtcPositionLevel? lowest)
    {
        ArtccConfigRoot zoa = _zoa ?? throw new InvalidOperationException("the ZOA fixture did not load");
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        PositionConfig student = zoa.FindPositionByCallsign(studentCallsign, facilityHint: null)!;
        scenario.StudentPosition = zoa.ResolvePosition(student.Id);
        scenario.StudentPositionType = studentType;
        scenario.StudentLowestWorkedLevel = lowest;
        scenario.AutoClearedToLand = false;
        return engine;
    }
}
