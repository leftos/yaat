using System.Runtime.CompilerServices;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.MilitaryRoutes;
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

        // AllowDownload: false unconditionally. Resolving NavData otherwise fetches the vNAS config
        // over HTTPS on every test process (~240 ms of TLS handshake, ~10% of the fixed cost of a
        // filtered run) and makes the suite depend on VATSIM infrastructure being reachable. Tests
        // run against the cached or bundled TestData copy; `python tools/refresh-navdata.py` is what
        // moves that copy forward. NavDataPathResolverTests asserts the process makes no such call.
        NavDataPathResolver.EnsureCurrent(
            new NavDataResolveOptions(
                BundledPath: Path.Combine(testDataDir, "NavData.dat"),
                BundledManifestPath: Path.Combine(testDataDir, "navdata-manifest.json"),
                AllowDownload: false
            )
        );

        // Warm the lazily-loaded static databases off the test threads, as the server does at
        // startup (ServerApp): otherwise the first test to tick physics pays the airspace GeoJSON
        // parse (~0.75 s) inside its own timing, and the MTR load lands on whichever test asks first.
        _ = Task.Run(static () =>
        {
            _ = AirspaceDatabase.Default;
            _ = MilitaryRouteDatabase.Default;
        });
    }

    private static bool IsCifpDownloadSkipped()
    {
        var v = Environment.GetEnvironmentVariable("YAAT_SKIP_CIFP_DOWNLOAD");
        return string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
