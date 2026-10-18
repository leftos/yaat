using System.Text.Json;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Loads an ARTCC configuration snapshot for tests that exercise replay-time
/// TCP/ERAM resolution or the towered-airport set. The snapshots are committed to TestData/
/// as static JSON files and refreshed via <c>tools/refresh-artcc-snapshot.py</c> when the
/// upstream config changes shape.
///
/// Tests that need an ArtccConfig should call <see cref="LoadZoa"/> (or <see cref="LoadZla"/>,
/// ZOA's neighbour) before replay and assign the result to <c>engine.Scenario!.ArtccConfig</c>.
/// When the snapshot file is absent (e.g. fresh checkout that hasn't run the refresh script),
/// the loader returns null and tests should skip silently — matching the <see cref="TestVnasData"/> pattern.
/// </summary>
public static class TestArtccConfig
{
    private static readonly Lock Lock = new();
    private static readonly Dictionary<string, ArtccConfigRoot?> Loaded = [];

    public static ArtccConfigRoot? LoadZoa() => Load("TestData/artcc-zoa-snapshot.json");

    public static ArtccConfigRoot? LoadZla() => Load("TestData/artcc-zla-snapshot.json");

    private static ArtccConfigRoot? Load(string snapshotPath)
    {
        lock (Lock)
        {
            if (Loaded.TryGetValue(snapshotPath, out ArtccConfigRoot? cached))
            {
                return cached;
            }

            ArtccConfigRoot? config = null;
            if (File.Exists(snapshotPath))
            {
                try
                {
                    string json = File.ReadAllText(snapshotPath);
                    config = JsonSerializer.Deserialize<ArtccConfigRoot>(json, RecordingJsonOptions.Default);
                }
                catch (JsonException)
                {
                    config = null;
                }
            }

            Loaded[snapshotPath] = config;
            return config;
        }
    }
}
