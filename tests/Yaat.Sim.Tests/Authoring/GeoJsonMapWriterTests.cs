using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring.AirportMap;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="GeoJsonMapWriter.WriteFile"/> writes a map through a temp file and a replacing move, so a failed write
/// leaves the old file whole, and it never writes into the vNAS airport cache.
/// </summary>
public sealed class GeoJsonMapWriterTests : IDisposable
{
    private static readonly string OakPath = Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaat-sim-tests", "map-writer", Guid.NewGuid().ToString("N"));

    public GeoJsonMapWriterTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Write_ThenRead_IsTheSameDocument()
    {
        var document = AirportMapDocument.Read(File.ReadAllText(OakPath));
        string path = Path.Combine(_dir, "OAK.geojson");

        GeoJsonMapWriter.WriteFile(document, path);
        var reread = AirportMapDocument.Read(File.ReadAllText(path));

        Assert.Equal(document.Features.Count, reread.Features.Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(document.Write()), JsonNode.Parse(reread.Write())));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Write_WhenTargetLocked_LeavesTargetIntactAndNoTempFile()
    {
        // Proves the failure path: the old file is left whole and no *.tmp file survives. It does not prove the write is
        // atomic, since a plain File.WriteAllText fails at open under the same lock before writing a byte.
        // FileShare.None is a mandatory lock only on Windows; Unix file locks are advisory, so the move would succeed.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding the target open blocks the replacing move only on Windows.");
        var document = AirportMapDocument.Read(File.ReadAllText(OakPath));
        string path = Path.Combine(_dir, "OAK.geojson");
        const string OldContent = "{\"type\": \"FeatureCollection\", \"features\": []}";
        File.WriteAllText(path, OldContent);

        Exception? failure;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failure = Record.Exception(() => GeoJsonMapWriter.WriteFile(document, path));
        }

        Assert.True(failure is IOException or UnauthorizedAccessException, $"expected the replacing move to fail, got {failure}");
        Assert.Equal(OldContent, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Write_RefusesCachePath()
    {
        var document = AirportMapDocument.Read(File.ReadAllText(OakPath));
        string name = $"{Guid.NewGuid():N}.geojson";
        string cachePath = YaatPaths.Combine("cache", "airports", name);
        try
        {
            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => GeoJsonMapWriter.WriteFile(document, cachePath));

            Assert.Contains(cachePath, refused.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(cachePath));
            Assert.Throws<InvalidOperationException>(() => GeoJsonMapWriter.WriteFile(document, YaatPaths.Combine("CACHE", "Airports", name)));
        }
        finally
        {
            // A regressed refusal would write here; never leave that file inside the real per-user cache.
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
    }

    [Fact]
    public void Write_UsesLfLineEndings()
    {
        string written = AirportMapDocument.Read(File.ReadAllText(OakPath)).Write();

        Assert.Contains("\n", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", written, StringComparison.Ordinal);
    }
}
