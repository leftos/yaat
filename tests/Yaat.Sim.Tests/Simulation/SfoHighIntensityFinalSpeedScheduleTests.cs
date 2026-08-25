using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Headless re-simulation of S2-SFO-3 | High Intensity (ZOA): every arrival spawns <c>OnFinal</c> at 14 nm with
/// no controller speed instruction. Before the additive Vref speed schedule, jets spawned at 1.6·Vref (a B744 at
/// 251 kt — above the §91.117 250-kt limit) and held it unchanged from 14 nm to ~6.5 nm. Uncontrolled pilots
/// configure earlier than that: clean speed (≈Vref+70, capped by weight class) to ~9 nm, an approach-flap stage
/// (≈Vref+45) by ~9 nm, 1.3·Vref by 5 nm, Vref by 2–5 nm.
/// </summary>
public class SfoHighIntensityFinalSpeedScheduleTests
{
    private const string RecordingPath = "TestData/issue394-sfo-hs-spot17-recording.zip";
    private const double SimSeconds = 1300;

    private readonly ITestOutputHelper _output;

    public SfoHighIntensityFinalSpeedScheduleTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private sealed class Profile
    {
        public required string Callsign { get; init; }
        public required string Type { get; init; }
        public required double Vref { get; init; }
        public required double SpawnDistNm { get; init; }
        public required double SpawnIas { get; init; }
        public double MaxIas { get; set; }
        public SortedDictionary<double, double> IasAtNm { get; } = new();
    }

    private List<Profile>? RunScenario()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null || TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var engine = new SimulationEngine(new TestAirportGroundData());
        var errors = engine.LoadScenario(recording.ScenarioJson, 522837118);
        Assert.Empty(errors);
        engine.Scenario!.FinalApproachSpeedVarietyEnabled = true;

        double[] gates = [12, 10, 9, 8, 7, 6, 5, 4, 3, 2];
        var profiles = new Dictionary<string, Profile>();
        for (int t = 0; t < SimSeconds; t++)
        {
            engine.TickOneSecond();
            foreach (var a in engine.World.GetSnapshot())
            {
                if (a.IsOnGround || a.Phases?.CurrentPhase is not FinalApproachPhase || a.Phases.AssignedRunway is null)
                {
                    continue;
                }

                var rwy = a.Phases.AssignedRunway;
                double dist = GeoMath.DistanceNm(a.Position, new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude));
                if (!profiles.TryGetValue(a.Callsign, out var p))
                {
                    var category = AircraftCategorization.Categorize(a.AircraftType);
                    p = new Profile
                    {
                        Callsign = a.Callsign,
                        Type = a.AircraftType,
                        Vref = AircraftPerformance.ApproachSpeed(a.AircraftType, category),
                        SpawnDistNm = dist,
                        SpawnIas = a.IndicatedAirspeed,
                    };
                    profiles[a.Callsign] = p;
                }

                p.MaxIas = Math.Max(p.MaxIas, a.IndicatedAirspeed);
                foreach (var g in gates)
                {
                    if (dist <= g && !p.IasAtNm.ContainsKey(g))
                    {
                        p.IasAtNm[g] = a.IndicatedAirspeed;
                    }
                }
            }
        }

        foreach (var p in profiles.Values)
        {
            var row = string.Join(" ", gates.Select(g => p.IasAtNm.TryGetValue(g, out var v) ? $"{g}:{v:F0}" : $"{g}:-"));
            _output.WriteLine($"{p.Callsign, -8} {p.Type, -5} vref={p.Vref:F0} spawn@{p.SpawnDistNm:F1}nm {p.SpawnIas:F0}kt || {row}");
        }

        return profiles.Values.ToList();
    }

    [Fact]
    public void LongFinalJets_SpawnClean_ConfigureBySevenMiles_AndReachConfigSpeedByFive()
    {
        var profiles = RunScenario();
        if (profiles is null)
        {
            return;
        }

        var longFinalJets = profiles.Where(p => p.SpawnDistNm >= 10.5 && AircraftCategorization.Categorize(p.Type) == AircraftCategory.Jet).ToList();
        Assert.True(longFinalJets.Count >= 8, $"expected the scenario's 14-nm jet arrivals, got {longFinalJets.Count}");

        Assert.All(longFinalJets, p => Assert.True(p.MaxIas < 250, $"{p.Callsign} ({p.Type}) reached {p.MaxIas:F0} kt on final"));
        Assert.All(longFinalJets, p => Assert.True(p.SpawnIas <= 240, $"{p.Callsign} ({p.Type}) spawned at {p.SpawnIas:F0} kt"));
        // Approach-flap stage (≈Vref+45) is settled by a per-aircraft 9 ± 1.5 nm gate, so check just inside its far edge.
        Assert.All(
            longFinalJets.Where(p => p.IasAtNm.ContainsKey(7)),
            p => Assert.True(p.IasAtNm[7] <= p.Vref + 50, $"{p.Callsign} ({p.Type}) still {p.IasAtNm[7]:F0} kt at 7 nm (Vref {p.Vref:F0})")
        );
        Assert.All(
            longFinalJets.Where(p => p.IasAtNm.ContainsKey(5)),
            p => Assert.True(p.IasAtNm[5] <= (p.Vref * 1.3) + 3, $"{p.Callsign} ({p.Type}) {p.IasAtNm[5]:F0} kt at 5 nm (1.3·Vref {p.Vref * 1.3:F0})")
        );
    }

    [Fact]
    public void LongFinalJets_AreNotAllSlow_AtTenMiles()
    {
        // The balance the schedule is meant to strike: nobody near 250 at 10 nm, but not everyone at 180 either —
        // clean speed differs by weight class and the approach-flap stage is reached with per-aircraft variety.
        var profiles = RunScenario();
        if (profiles is null)
        {
            return;
        }

        var at10 = profiles.Where(p => p.SpawnDistNm >= 10.5 && p.IasAtNm.ContainsKey(10)).Select(p => p.IasAtNm[10]).ToList();
        Assert.True(at10.Count >= 8);
        Assert.Contains(at10, ias => ias >= 200);
        Assert.True(at10.Max() - at10.Min() >= 15, $"speeds at 10 nm collapsed to one value: {at10.Min():F0}-{at10.Max():F0}");
    }
}
