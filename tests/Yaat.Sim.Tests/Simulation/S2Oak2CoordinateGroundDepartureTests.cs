using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for the S2-OAK-2 "IFR Takeoff/Landing" bug bundle. Three OAK departures
/// (TWY85, UPS2945, DAL2125) are authored at <c>Coordinates</c> sitting at field
/// elevation (9 ft) with a heading and a <c>TAXI</c> preset — the scenario-author
/// idiom for "ready to taxi from this surface point". They must spawn on the ground
/// and taxi via their preset, not fly off airborne in the spawn heading the moment
/// the simulation is unpaused.
///
/// The recording's own actions are the user's manual recovery (AmendFlightPlan +
/// WARPG + TAXI per aircraft), so replaying them would mask the bug. This loads the
/// real bundled scenario fresh (no actions) through the engine and asserts the spawn
/// outcome directly.
/// </summary>
[Collection("NavDbMutator")]
public class S2Oak2CoordinateGroundDepartureTests
{
    private const string RecordingPath = "TestData/s2oak2-coord-ground-departures-recording.yaat-bug-report-bundle.zip";

    private static readonly string[] GroundDepartures = ["TWY85", "UPS2945", "DAL2125"];

    private static SimulationEngine? LoadFreshScenario()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return null;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(recording.ScenarioJson, recording.RngSeed);
        return engine.Scenario is null ? null : engine;
    }

    [Fact]
    public void CoordinateDepartures_SpawnOnGround_WithTaxiRoute()
    {
        var engine = LoadFreshScenario();
        if (engine is null)
        {
            return;
        }

        foreach (var callsign in GroundDepartures)
        {
            var ac = engine.FindAircraft(callsign);
            Assert.NotNull(ac);
            Assert.True(ac.IsOnGround, $"{callsign} must spawn on the ground");
            Assert.NotNull(ac.Ground.Layout);
            // The TAXI preset only resolves a route when the aircraft is on the ground with a
            // layout loaded — the whole point of the fix (no manual APT/WARPG/TAXI needed).
            Assert.NotNull(ac.Ground.AssignedTaxiRoute);
        }
    }

    [Fact]
    public void CoordinateDepartures_DoNotFlyOff_OnUnpause()
    {
        var engine = LoadFreshScenario();
        if (engine is null)
        {
            return;
        }

        for (int t = 0; t < 10; t++)
        {
            engine.TickOneSecond();
        }

        foreach (var callsign in GroundDepartures)
        {
            var ac = engine.FindAircraft(callsign);
            Assert.NotNull(ac);
            Assert.True(ac.IsOnGround, $"{callsign} flew off the ground on unpause");
            Assert.True(ac.Altitude < 50, $"{callsign} climbed to {ac.Altitude:F0} ft — it should stay at field elevation");
        }
    }
}
