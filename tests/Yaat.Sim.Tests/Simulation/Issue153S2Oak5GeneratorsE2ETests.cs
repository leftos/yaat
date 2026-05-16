using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #153: reporter claimed S2-OAK-5 (2) generators were "not working" and "missing the runway".
/// The bundle had 0 snapshots — the sim never actually ran — so the report combined (a) a real
/// cosmetic bug in the editor's Runway dropdown with (b) an unverified assumption that the
/// runtime was broken. This test exercises the runtime against the exact scenario JSON the
/// reporter loaded, with its specific generator configs (180s jet/large @ 30, 400s jet/smallplus
/// @ 28R), and asserts each generator actually produces arrivals.
/// </summary>
public class Issue153S2Oak5GeneratorsE2ETests(ITestOutputHelper output)
{
    private const string ScenarioPath = "TestData/issue153-s2-oak-5-2-scenario.json";

    [Fact]
    public void Issue153_ExactScenario_BothGeneratorsSpawnArrivals()
    {
        if (!File.Exists(ScenarioPath))
        {
            return;
        }
        var scenarioJson = File.ReadAllText(ScenarioPath);

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return;
        }

        var engine = new SimulationEngine(groundData);
        var warnings = engine.LoadScenario(scenarioJson, rngSeed: 42);
        foreach (var w in warnings)
        {
            output.WriteLine($"[load-warn] {w}");
        }

        Assert.NotNull(engine.Scenario);
        Assert.Equal(2, engine.Scenario.Generators.Count);

        var gen30 = engine.Scenario.Generators.Single(g => g.Config.Runway == "30");
        var gen28R = engine.Scenario.Generators.Single(g => g.Config.Runway == "28R");
        Assert.Equal(180, gen30.Config.IntervalTime);
        Assert.Equal("Jet", gen30.Config.EngineType);
        Assert.Equal("Large", gen30.Config.WeightCategory);
        Assert.Equal(400, gen28R.Config.IntervalTime);
        Assert.Equal("Jet", gen28R.Config.EngineType);
        Assert.Equal("SmallPlus", gen28R.Config.WeightCategory);

        // Generator-spawned aircraft come from AircraftInitializer.InitializeOnFinal, which
        // stamps PhaseList.AssignedRunway = the configured runway. That's the authoritative
        // discriminator — scenario delayed-spawn aircraft (parking + fix-spawned VFRs) do not
        // have AssignedRunway set this way. 60min / 180s = ~20 expected on rwy 30,
        // 60min / 400s = ~9 on rwy 28R, both ±25%.
        var preCallsigns = engine.World.GetSnapshot().Select(a => a.Callsign).ToHashSet();
        var spawnedOn30 = new List<(int T, string Callsign, string Type, double Alt, double Ias)>();
        var spawnedOn28R = new List<(int T, string Callsign, string Type, double Alt, double Ias)>();

        const int totalSeconds = 60 * 60;
        for (int t = 0; t < totalSeconds; t++)
        {
            engine.TickOneSecond();
            foreach (var ac in engine.World.GetSnapshot())
            {
                if (preCallsigns.Contains(ac.Callsign))
                {
                    continue;
                }
                preCallsigns.Add(ac.Callsign);

                // Generator arrivals are spawned airborne at glide-slope altitude with an
                // AssignedRunway. Scenario delayed-parking spawns also get an AssignedRunway
                // (via their TAXI preset) but they're on ground at field elevation; scenario
                // VFR Fix-spawns are airborne but have no AssignedRunway. Both filters in
                // conjunction match only genuine generator output.
                var assignedRwy = ac.Phases?.AssignedRunway?.Designator;
                if (assignedRwy is null || ac.IsOnGround || ac.Altitude < 1000)
                {
                    continue;
                }

                var sample = (T: t + 1, ac.Callsign, Type: ac.AircraftType, Alt: ac.Altitude, Ias: ac.IndicatedAirspeed);
                if (assignedRwy == "30")
                {
                    spawnedOn30.Add(sample);
                }
                else if (assignedRwy == "28R")
                {
                    spawnedOn28R.Add(sample);
                }
                output.WriteLine(
                    $"t={t + 1, 5}s spawn rwy={assignedRwy, -3} {ac.Callsign, -8} type={ac.AircraftType, -6} alt={ac.Altitude, 5:F0}ft ias={ac.IndicatedAirspeed, 3:F0}kts hdg={ac.TrueHeading.Degrees, 3:F0}°"
                );
            }
        }

        output.WriteLine($"Total generator arrivals: {spawnedOn30.Count} on RWY 30, {spawnedOn28R.Count} on RWY 28R");

        Assert.NotEmpty(spawnedOn30);
        Assert.NotEmpty(spawnedOn28R);
    }
}
