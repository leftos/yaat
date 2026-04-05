// Yaat.Scratch — migrate v3 recordings to v4 (deduplicate GroundLayout)

using System.Text.Json;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

string testDataDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "Yaat.Sim.Tests", "TestData");
testDataDir = Path.GetFullPath(testDataDir);

var zips = Directory.GetFiles(testDataDir, "*.zip");
Console.WriteLine($"Migrating {zips.Length} recordings in {testDataDir}\n");

foreach (var zipPath in zips.OrderBy(f => f))
{
    string name = Path.GetFileName(zipPath);
    var originalSize = new FileInfo(zipPath).Length;

    // Read everything into memory first, then close the source file
    RecordingManifest manifest;
    string scenarioJson;
    string? weatherJson;
    List<RecordedAction> actions;
    List<TimedSnapshot> timedSnapshots = [];
    var layouts = new Dictionary<string, AirportGroundLayout>(StringComparer.OrdinalIgnoreCase);

    try
    {
        using var sourceArchive = RecordingArchive.Open(zipPath);
        manifest = sourceArchive.Manifest;

        if (manifest.LayoutAirportIds is { Count: > 0 })
        {
            Console.WriteLine($"SKIP {name} (already v4)");
            continue;
        }

        scenarioJson = sourceArchive.ReadScenarioJson();
        weatherJson = sourceArchive.ReadWeatherJson();
        actions = sourceArchive.ReadActions();

        for (int i = 0; i < manifest.Snapshots.Count; i++)
        {
            var timed = sourceArchive.ReadTimedSnapshot(i);
            timedSnapshots.Add(timed);

            // Extract layouts from first snapshot that has delayed spawns
            if (layouts.Count == 0 && timed.State.Scenario.DelayedQueue is { Count: > 0 } queue)
            {
                foreach (var delayed in queue)
                {
                    var aircraft = JsonSerializer.Deserialize<LoadedAircraft>(delayed.AircraftJson, RecordingJsonOptions.Default);
                    if (aircraft?.State.GroundLayout is { } layout && !layouts.ContainsKey(layout.AirportId))
                    {
                        layouts[layout.AirportId] = layout;
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SKIP {name} ({ex.Message})");
        continue;
    }

    // Source file is now closed. Re-serialize delayed queues to strip GroundLayout.
    foreach (var timed in timedSnapshots)
    {
        if (timed.State.Scenario.DelayedQueue is { Count: > 0 } queue)
        {
            for (int j = 0; j < queue.Count; j++)
            {
                var old = queue[j];
                var aircraft = JsonSerializer.Deserialize<LoadedAircraft>(old.AircraftJson, RecordingJsonOptions.Default)!;
                var newJson = JsonSerializer.Serialize(aircraft, RecordingJsonOptions.Default);
                queue[j] = new DelayedSpawnDto { AircraftJson = newJson, SpawnAtSeconds = old.SpawnAtSeconds };
            }
        }
    }

    // Write v4
    string tempPath = zipPath + ".v4.tmp";
    using (var outStream = File.Create(tempPath))
    using (var writer = new RecordingArchiveWriter(outStream))
    {
        writer.WriteScenario(scenarioJson);
        writer.WriteWeather(weatherJson);
        writer.WriteActions(actions);

        foreach (var layout in layouts.Values)
        {
            writer.WriteLayout(layout);
        }

        for (int i = 0; i < timedSnapshots.Count; i++)
        {
            var s = timedSnapshots[i];
            writer.WriteSnapshot(i, s.ElapsedSeconds, s.ActionIndex, s.State);
        }

        writer.Finish(
            manifest.ScenarioName,
            manifest.ScenarioId,
            manifest.ArtccId,
            manifest.RngSeed,
            manifest.TotalElapsedSeconds,
            manifest.RecordedAtUtc,
            manifest.RecordedBy
        );
    }

    var newSize = new FileInfo(tempPath).Length;
    File.Delete(zipPath);
    File.Move(tempPath, zipPath);

    double ratio = (1.0 - (double)newSize / originalSize) * 100;
    Console.WriteLine(
        $"{name}: {layouts.Count} layout(s) [{string.Join(", ", layouts.Keys)}] -> {originalSize / 1024.0 / 1024.0:F1}MB -> {newSize / 1024.0 / 1024.0:F1}MB ({ratio:F0}% smaller)"
    );
}

Console.WriteLine("\nDone.");
