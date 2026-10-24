using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// Regression coverage for the snapshot-restore clear block: <see cref="SimulationEngine.RestoreFromSnapshot"/>
/// clears the IFR <c>Generators</c> list before re-adding the snapshot's copies, but a server rewind-from-snapshot
/// reloads the scenario first (which repopulates every generator list from the scenario config) and only then
/// restores. If the clear block omits <c>VfrArrivalGenerators</c> / <c>OverflightGenerators</c>, the snapshot's
/// generators are appended on top of the freshly loaded ones and the lists double — so a rewind makes VFR
/// arrivals and overflights spawn at twice their authored cadence and the replay diverges from the live session.
/// </summary>
public class VfrOverflightGeneratorRestoreTests(ITestOutputHelper output)
{
    private const string VfrArrivalScenario = """
        {
          "id": "01TEST00000000000000000011",
          "name": "VfrArrivalGeneratorRestore",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [],
          "vfrArrivalGenerators": [
            {
              "id": "vfr-south",
              "bearingFrom": 120,
              "bearingTo": 200,
              "initialDistance": 12,
              "maxDistance": 20,
              "altitudeMin": 2500,
              "altitudeMax": 3500,
              "intervalTime": 60
            }
          ]
        }
        """;

    private const string OverflightScenario = """
        {
          "id": "01TEST00000000000000000012",
          "name": "OverflightGeneratorRestore",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [],
          "overflightGenerators": [
            {
              "id": "of-eastwest",
              "fromBearingFrom": 80,
              "fromBearingTo": 100,
              "toBearingFrom": 260,
              "toBearingTo": 280,
              "initialDistance": 15,
              "maxDistance": 22,
              "altitudeMin": 4500,
              "altitudeMax": 7500,
              "exitDistance": 30,
              "intervalTime": 60
            }
          ]
        }
        """;

    private SimulationEngine? BuildEngine(string scenarioJson)
    {
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

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        foreach (string w in engine.LoadScenario(scenarioJson, rngSeed: 42, sessionStartUtc: MagneticDeclination.EvaluationDateUtc))
        {
            output.WriteLine($"[load-warn] {w}");
        }
        return engine;
    }

    [Fact]
    public void RestoreFromSnapshot_DoesNotDoubleVfrArrivalGenerators()
    {
        SimulationEngine? engine = BuildEngine(VfrArrivalScenario);
        if (engine?.Scenario is null)
        {
            return;
        }

        // The scenario load populated the list (as a server rewind's scenario reload does).
        Assert.Single(engine.Scenario.VfrArrivalGenerators);

        // The production rewind-from-snapshot sequence: scenario already loaded, now jump to a saved state.
        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        engine.RestoreFromSnapshot(snapshot);

        Assert.Single(engine.Scenario.VfrArrivalGenerators);
    }

    [Fact]
    public void RestoreFromSnapshot_DoesNotDoubleOverflightGenerators()
    {
        SimulationEngine? engine = BuildEngine(OverflightScenario);
        if (engine?.Scenario is null)
        {
            return;
        }

        Assert.Single(engine.Scenario.OverflightGenerators);

        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        engine.RestoreFromSnapshot(snapshot);

        Assert.Single(engine.Scenario.OverflightGenerators);
    }
}
