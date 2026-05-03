using System.Text.Json;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Loads an ARTCC configuration snapshot for tests that exercise replay-time
/// TCP/ERAM resolution. The snapshot is committed to TestData/ as a static JSON
/// file and refreshed via <c>tools/refresh-artcc-snapshot.py</c> when the
/// upstream config changes shape.
///
/// Tests that need an ArtccConfig should call <see cref="LoadZoa"/> before
/// replay and assign the result to <c>engine.Scenario!.ArtccConfig</c>. When
/// the snapshot file is absent (e.g. fresh checkout that hasn't run the
/// refresh script), <see cref="LoadZoa"/> returns null and tests should skip
/// silently — matching the <see cref="TestVnasData"/> pattern.
/// </summary>
public static class TestArtccConfig
{
    private const string ZoaSnapshotPath = "TestData/artcc-zoa-snapshot.json";

    private static readonly object Lock = new();
    private static ArtccConfigRoot? _zoa;
    private static bool _zoaLoadAttempted;

    public static ArtccConfigRoot? LoadZoa()
    {
        lock (Lock)
        {
            if (_zoaLoadAttempted)
            {
                return _zoa;
            }

            _zoaLoadAttempted = true;

            if (!File.Exists(ZoaSnapshotPath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(ZoaSnapshotPath);
                _zoa = JsonSerializer.Deserialize<ArtccConfigRoot>(json, RecordingJsonOptions.Default);
            }
            catch (JsonException)
            {
                _zoa = null;
            }

            return _zoa;
        }
    }
}
