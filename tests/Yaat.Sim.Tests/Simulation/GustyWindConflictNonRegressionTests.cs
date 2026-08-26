using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Conflict-detector non-regression under variable wind: a dense traffic picture that is
/// conflict-free with steady wind must stay conflict-free when the same session flies in
/// gusty, direction-variable wind. Conflict prediction consumes groundspeed and track —
/// both of which now wobble — so this pins that realistic wobble amplitudes do not push
/// stable geometry across alert thresholds (per this project's experience that
/// conflict-detector changes have emergent timing).
/// </summary>
[Collection("NavDbMutator")]
public class GustyWindConflictNonRegressionTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/4d4344011a72.zip";
    private const int RestoreAtSeconds = 785;
    private const int LiveSeconds = 120;

    [Fact]
    public void ParallelFinals_GustyWind_NoNewConflictAlerts()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var steadyPairs = RunAndCollectConflicts(gusty: false);
        var gustyPairs = RunAndCollectConflicts(gusty: true);
        if (steadyPairs is null || gustyPairs is null)
        {
            return;
        }

        output.WriteLine($"Steady-wind conflicts: {steadyPairs.Count}; gusty-wind conflicts: {gustyPairs.Count}");
        var newPairs = gustyPairs.Except(steadyPairs).ToList();
        Assert.True(newPairs.Count == 0, $"Gusty wind introduced conflict pairs the steady baseline did not have: {string.Join(", ", newPairs)}");
    }

    private HashSet<string>? RunAndCollectConflicts(bool gusty)
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return null;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = new SimulationEngine(new TestAirportGroundData());
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(RestoreAtSeconds);
            if (snapshot is null)
            {
                output.WriteLine($"No snapshot near t={RestoreAtSeconds} — skipping");
                return null;
            }

            engine.RestoreFromSnapshot(snapshot.State);

            if (gusty)
            {
                engine.World.Weather = new WeatherProfile
                {
                    WindLayers =
                    [
                        new WindLayer
                        {
                            Altitude = 0,
                            Direction = 300,
                            Speed = 15,
                            Gusts = 25,
                            DirectionVariabilityDeg = 30,
                        },
                    ],
                };
            }

            var corridors = ConflictAlertDetector.BuildCorridors(["OAK"], NavigationDatabase.Instance);
            Assert.NotEmpty(corridors);

            var pairs = new HashSet<string>(StringComparer.Ordinal);
            for (int second = 0; second < LiveSeconds; second++)
            {
                // Full engine seconds so ElapsedSeconds advances and the wind field evolves.
                engine.TickOneSecond();

                var aircraft = engine.World.GetSnapshot();
                foreach (var pair in ConflictAlertDetector.Detect(aircraft, new ConflictAlertContext([], corridors)))
                {
                    var key =
                        string.CompareOrdinal(pair.CallsignA, pair.CallsignB) <= 0
                            ? pair.CallsignA + "/" + pair.CallsignB
                            : pair.CallsignB + "/" + pair.CallsignA;
                    pairs.Add(key);
                }
            }

            return pairs;
        }
    }
}
