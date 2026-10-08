using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// <see cref="AirportSidecarHash.For"/> over a scratch ARTCCs tree: it moves when any sidecar file of the airport moves,
/// whatever its file name or id spelling, and ignores every other airport's files. The files only need an
/// <c>airportId</c> to be attributed, so they are written here rather than copied from the shipped sidecars.
/// </summary>
public class AirportSidecarHashTests
{
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void ChangesWhenAnyFileOfTheAirportChanges_IgnoresOtherAirports()
    {
        string root = NewRoot();
        try
        {
            string oak = Sidecar(root, "ZOA", "oak.json", "KOAK", "[]");
            string oakExtra = Sidecar(root, "ZXX", "extra.json", "oak", "[]");
            string sfo = Sidecar(root, "ZOA", "sfo.json", "KSFO", "[]");
            Directory.CreateDirectory(Path.Combine(root, "ZAB", "Airports"));
            Directory.CreateDirectory(Path.Combine(root, "ZLA"));

            string baseline = AirportSidecarHash.For(root, "KOAK");
            Assert.Equal(64, baseline.Length);
            Assert.NotEqual(EmptySha256, baseline);
            Assert.Equal(baseline, AirportSidecarHash.For(root, "OAK"));
            Assert.Equal(baseline, AirportSidecarHash.For(root, "koak"));

            File.WriteAllText(sfo, Body("KSFO", "[\"A\"]"));
            Assert.Equal(baseline, AirportSidecarHash.For(root, "KOAK"));

            File.WriteAllText(oakExtra, Body("oak", "[\"B\"]"));
            string afterExtra = AirportSidecarHash.For(root, "KOAK");
            Assert.NotEqual(baseline, afterExtra);

            File.WriteAllText(oak, Body("KOAK", "[\"C\"]"));
            Assert.NotEqual(afterExtra, AirportSidecarHash.For(root, "KOAK"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NoSidecar_IsStableConstant()
    {
        string root = NewRoot();
        try
        {
            Sidecar(root, "ZOA", "sfo.json", "KSFO", "[]");

            Assert.Equal(EmptySha256, AirportSidecarHash.For(root, "KOAK"));
            Assert.Equal(EmptySha256, AirportSidecarHash.For(root, "KOAK"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingFolder_Throws()
    {
        string root = NewRoot();

        DirectoryNotFoundException ex = Assert.Throws<DirectoryNotFoundException>(() => AirportSidecarHash.For(root, "KOAK"));
        Assert.Contains(root, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidJson_IsSkipped() => AssertSkipped("{ not json", "is not valid JSON");

    [Fact]
    public void NoAirportId_IsSkipped() => AssertSkipped("{\"avoidTaxiways\": []}\n", "names no airportId");

    [Fact]
    public void NonStringAirportId_IsSkippedWithAWarning() =>
        AssertSkipped("{\"airportId\": 42, \"avoidTaxiways\": []}\n", "has a Number airportId, not a string");

    /// <summary>A KOAK sidecar beside a file with <paramref name="body"/> hashes as the KOAK sidecar alone, with a warning naming the file.</summary>
    private static void AssertSkipped(string body, string warning)
    {
        string root = NewRoot();
        try
        {
            Sidecar(root, "ZOA", "oak.json", "KOAK", "[]");
            string alone = AirportSidecarHash.For(root, "KOAK");
            string bad = Path.Combine(root, "ZOA", "Airports", "bad.json");
            File.WriteAllText(bad, body);
            var capture = WarningLogCapture.Install();

            Assert.Equal(alone, AirportSidecarHash.For(root, "KOAK"));
            Assert.Contains(
                capture.Warnings,
                line => line.Contains(bad, StringComparison.Ordinal) && line.Contains(warning, StringComparison.Ordinal)
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Sidecar(string root, string artcc, string fileName, string airportId, string avoid)
    {
        string directory = Path.Combine(root, artcc, "Airports");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, Body(airportId, avoid));
        return path;
    }

    private static string Body(string airportId, string avoid) => $"{{\"airportId\": \"{airportId}\", \"avoidTaxiways\": {avoid}}}\n";

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "sidecar-hash-" + Guid.NewGuid());
}
