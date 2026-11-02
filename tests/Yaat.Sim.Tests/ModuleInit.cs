using System.Runtime.CompilerServices;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.MilitaryRoutes;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Runs once per test assembly load. Ensures current AIRAC CIFP is resolved (cache hit
/// or single FAA download) before any test method touches <see cref="TestVnasData.NavigationDb"/>, and registers the
/// real navigation database as the default <see cref="NavigationDatabase.Instance"/> loads on its first read when none is installed.
/// </summary>
internal static class ModuleInit
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Surface any pure-pursuit orbit (GroundNavigator circling a node it can't converge on) as a
        // hard test failure. The shipping app leaves this false and recovers gracefully instead.
        GroundNavigator.ThrowOnOrbit = true;

        // Same for an arc primitive writing an aircraft further than it drove in one sub-tick — a teleport.
        GroundNavigator.ThrowOnTeleport = true;

        string testDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        Yaat.Sim.Testing.TestVnasData.SetTestDataDir(testDataDir);

        // Load the aircraft tables before any test runs: AircraftCategorization.Categorize returns Jet for an empty table,
        // so a test that reads a category before its harness loads them would otherwise stage every type as a jet.
        Yaat.Sim.Testing.TestVnasData.EnsureAircraftDataInitialized();

        // CIFP is a precondition for the same reason NavData is: procedures, approaches and SIDs come
        // from it, and without them the tests that exercise them return early into a green run.
        //
        // The suite pins the committed bundle, so every machine runs the AIRAC cycle recorded in
        // TestData/cifp-manifest.json whatever its user cache holds. A new cycle reaches the tests
        // only through `python tools/refresh-navdata.py` and a commit.
        RequireCifp(CifpPathResolver.EnsureCurrentCycle(Yaat.Sim.Testing.TestVnasData.PinnedCifpOptions(testDataDir)));

        // Most of this suite asserts against real navdata, so running without it is not a degraded
        // run — it is a meaningless one. Resolve it, and make failure fatal for the assembly rather
        // than letting hundreds of tests silently `return;` into a green result.
        //
        // The pin also keeps the ~240 ms vNAS config fetch off every test process: a pinned resolve
        // never contacts vNAS, and `NavDataPathResolverTests` asserts the process makes no such call.
        string navDataPath = RequireNavData(NavDataPathResolver.EnsureCurrent(Yaat.Sim.Testing.TestVnasData.PinnedNavDataOptions(testDataDir)));

        // A class that reads NavigationDatabase.Instance without installing one gets the real database on its
        // first read, rather than only when an earlier class happened to call TestVnasData.EnsureInitialized().
        NavigationDatabase.SetTestDefaultLoader(() =>
            Yaat.Sim.Testing.TestVnasData.LoadRealDataWithoutInstalling()
            ?? throw new InvalidOperationException($"The real navigation database could not be loaded from the resolved NavData at {navDataPath}.")
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
                + "The suite pins the committed tests/Yaat.Sim.Tests/TestData/NavData.dat "
                + "(a missing one usually means an incomplete checkout). "
                + "Restore it with `git checkout -- tests/Yaat.Sim.Tests/TestData/NavData.dat`, or run "
                + "`python tools/refresh-navdata.py` to repin it."
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
                + "The suite pins the committed bundle at tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz "
                + "(a missing one usually means an incomplete checkout). "
                + "Restore it with `git checkout -- tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz`, or run "
                + "`python tools/refresh-navdata.py` to repin it."
        );
    }
}
