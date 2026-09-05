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

        // CIFP is a precondition for the same reason NavData is: procedures, approaches and SIDs
        // come from it, and without them the tests that exercise them return early into a green run.
        //
        // No offline-first switch is needed here, unlike NavData: CifpPathResolver.ResolveCore
        // already returns a current-cycle cache hit before considering the network, which is why it
        // costs ~9 ms rather than ~240 ms. Downloads stay enabled by default deliberately — CIFP is
        // AIRAC-cycle-specific and the committed bundle goes stale every 28 days, so fetching the
        // current cycle when it is not cached is worth a one-off round trip.
        var allowCifpDownload = !IsCifpDownloadSkipped();
        RequireCifp(
            CifpPathResolver.EnsureCurrentCycle(
                new CifpResolveOptions(
                    BundledGzPath: Path.Combine(testDataDir, "FAACIFP18.gz"),
                    BundledManifestPath: Path.Combine(testDataDir, "cifp-manifest.json"),
                    AllowDownload: allowCifpDownload
                )
            )
        );

        // Most of this suite asserts against real navdata, so running without it is not a degraded
        // run — it is a meaningless one. Resolve it, and make failure fatal for the assembly rather
        // than letting hundreds of tests silently `return;` into a green result.
        //
        // Download only when there is nothing on disk to use. Resolving with downloads enabled
        // fetches the vNAS config over HTTPS first — ~240 ms of TLS handshake on EVERY test process
        // (~10% of the fixed cost of a filtered run) — and `TestData/NavData.dat` is committed, so
        // the normal path never needs it. `NavDataPathResolverTests` asserts a healthy checkout
        // makes no such call.
        var navOptions = new NavDataResolveOptions(
            BundledPath: Path.Combine(testDataDir, "NavData.dat"),
            BundledManifestPath: Path.Combine(testDataDir, "navdata-manifest.json"),
            AllowDownload: false
        );
        if (!NavDataPathResolver.HasLocalCopy(navOptions))
        {
            navOptions = navOptions with { AllowDownload = true };
        }

        RequireNavData(NavDataPathResolver.EnsureCurrent(navOptions));

        // Warm the lazily-loaded static databases off the test threads, as the server does at
        // startup (ServerApp): otherwise the first test to tick physics pays the airspace GeoJSON
        // parse (~0.75 s) inside its own timing, and the MTR load lands on whichever test asks first.
        _ = Task.Run(static () =>
        {
            _ = AirspaceDatabase.Default;
            _ = MilitaryRouteDatabase.Default;
        });
    }

    /// <summary>
    /// Fails the whole assembly when NavData could not be resolved. Real navdata is a precondition
    /// of this suite, not an optional enrichment: without it the tests that depend on it return
    /// early and the run reports green having proved nothing.
    /// </summary>
    /// <param name="resolvedPath">The path <see cref="NavDataPathResolver.EnsureCurrent"/> returned.</param>
    /// <returns>The verified path.</returns>
    /// <exception cref="InvalidOperationException">No usable NavData.dat could be resolved.</exception>
    internal static string RequireNavData(string? resolvedPath)
    {
        if (resolvedPath is not null && File.Exists(resolvedPath))
        {
            return resolvedPath;
        }

        throw new InvalidOperationException(
            "NavData.dat could not be resolved, so the Yaat.Sim test suite cannot run: most of it asserts against real navdata. "
                + "Expected a copy at %LOCALAPPDATA%/yaat/cache/NavData.dat or tests/Yaat.Sim.Tests/TestData/NavData.dat "
                + "(committed — a missing one usually means an incomplete checkout). "
                + "Restore it with `git checkout -- tests/Yaat.Sim.Tests/TestData/NavData.dat`, or run "
                + "`python tools/refresh-navdata.py` to fetch a fresh pin from vNAS."
        );
    }

    /// <summary>
    /// Fails the whole assembly when CIFP could not be resolved, for the same reason as
    /// <see cref="RequireNavData"/>: procedures, approaches and SIDs come from it, and the tests
    /// that exercise them skip silently in its absence rather than failing.
    /// </summary>
    /// <param name="resolvedPath">The path <see cref="CifpPathResolver.EnsureCurrentCycle"/> returned.</param>
    /// <returns>The verified path.</returns>
    /// <exception cref="InvalidOperationException">No usable CIFP could be resolved.</exception>
    internal static string RequireCifp(string? resolvedPath)
    {
        if (resolvedPath is not null && File.Exists(resolvedPath))
        {
            return resolvedPath;
        }

        throw new InvalidOperationException(
            "CIFP could not be resolved, so the Yaat.Sim test suite cannot run: procedures, approaches and SIDs come from it. "
                + "Expected a current-cycle copy under %LOCALAPPDATA%/yaat/cache/cifp/, or the committed bundle at "
                + "tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz (a missing one usually means an incomplete checkout). "
                + "Restore it with `git checkout -- tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz`, or allow the download by "
                + "clearing YAAT_SKIP_CIFP_DOWNLOAD so the current AIRAC cycle can be fetched and cached."
        );
    }

    private static bool IsCifpDownloadSkipped()
    {
        var v = Environment.GetEnvironmentVariable("YAAT_SKIP_CIFP_DOWNLOAD");
        return string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
