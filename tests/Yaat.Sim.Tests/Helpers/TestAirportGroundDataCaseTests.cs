using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Guards fixture lookup against filename case. Windows resolves paths case-insensitively, so a GeoJSON committed
/// under a case other than the lowercase convention resolves on a developer machine and disappears on the Linux CI
/// runner. That failure is silent by construction: <see cref="TestAirportGroundData.GetLayout"/> returns null for a
/// missing file and every consumer honours the harness's mandatory silent-skip convention, so the affected tests
/// report green while asserting nothing.
/// </summary>
public sealed class TestAirportGroundDataCaseTests
{
    private const string TestDataDir = "TestData";

    /// <summary>
    /// Every committed <c>*.geojson</c> must be reachable through the harness under the id derived from its filename,
    /// whatever case it was committed under. Reads the source rather than building the layout so the assertion stays
    /// cheap — this covers path resolution, which is where the case sensitivity bites.
    /// </summary>
    [Fact]
    public void EveryCommittedGeoJson_ResolvesRegardlessOfFilenameCase()
    {
        if (!Directory.Exists(TestDataDir))
        {
            return;
        }

        var data = new TestAirportGroundData();
        List<string> unresolved = [];

        foreach (string path in Directory.EnumerateFiles(TestDataDir, "*.geojson"))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            if (data.GetSourceGeoJson(id) is null)
            {
                unresolved.Add(Path.GetFileName(path));
            }
        }

        Assert.True(
            unresolved.Count == 0,
            $"These fixtures exist on disk but the harness cannot resolve them: {string.Join(", ", unresolved)}. "
                + "On a case-sensitive filesystem their consuming tests skip silently instead of running."
        );
    }
}
