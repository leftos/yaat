using System.Runtime.CompilerServices;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Runs once per test assembly load. Ensures current AIRAC CIFP is resolved (cache hit
/// or single FAA download) before any test method touches <see cref="TestVnasData.NavigationDb"/>.
/// </summary>
internal static class ModuleInit
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Surface any pure-pursuit orbit (GroundNavigator circling a node it can't converge on) as a
        // hard test failure. The shipping app leaves this false and recovers gracefully instead.
        GroundNavigator.ThrowOnOrbit = true;

        var testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        Yaat.Sim.Testing.TestVnasData.SetTestDataDir(testDataDir);

        var allowCifpDownload = !IsCifpDownloadSkipped();
        CifpPathResolver.EnsureCurrentCycle(
            new CifpResolveOptions(
                BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
                BundledManifestPath: Path.Combine(testDataDir, "cifp-manifest.json"),
                AllowDownload: allowCifpDownload
            )
        );

        var allowNavDownload = !IsNavDataDownloadSkipped();
        NavDataPathResolver.EnsureCurrent(
            new NavDataResolveOptions(
                BundledPath: Path.Combine(testDataDir, "NavData.dat"),
                BundledManifestPath: Path.Combine(testDataDir, "navdata-manifest.json"),
                AllowDownload: allowNavDownload
            )
        );
    }

    private static bool IsCifpDownloadSkipped()
    {
        var v = Environment.GetEnvironmentVariable("YAAT_SKIP_CIFP_DOWNLOAD");
        return string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNavDataDownloadSkipped()
    {
        var v = Environment.GetEnvironmentVariable("YAAT_SKIP_NAVDATA_DOWNLOAD");
        return string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
