using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Generator + E2E tests for the ADD arrival-on-STAR spawn variant (issue #197). Spawns an IFR
/// aircraft already established on the OAKES THREE arrival into KOAK and verifies the descend-via
/// vs hold-level states, spawn position, computed default altitude, and dynamic descent behavior.
/// Skips offline when OAKES3 is not in the bundled CIFP cycle.
/// </summary>
[Collection("NavDbMutator")]
public class ArrivalStarSpawnTests
{
    private readonly ITestOutputHelper output;

    public ArrivalStarSpawnTests(ITestOutputHelper output)
    {
        this.output = output;
        // Pin the NavigationDatabase singleton before any test body reads it (static-singleton race rule).
        TestVnasData.EnsureInitialized();
    }

    private const string Airport = "KOAK";
    private const string StarId = "OAKES3";

    private record Fixture(string EntryFix, string Runway, LatLon EntryPos);

    /// <summary>Derives a spawn entry fix and a runway transition from OAKES3, or null if absent offline.</summary>
    private static Fixture? TryBuildFixture()
    {
        TestVnasData.EnsureInitialized();
        var navDb = NavigationDatabase.Instance;
        var star = navDb.GetStar(Airport, StarId);
        if (star is null || star.CommonLegs.Count == 0 || star.RunwayTransitions.Count == 0)
        {
            return null;
        }

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(new List<CifpLeg>(star.CommonLegs));
        if (targets.Count == 0)
        {
            return null;
        }

        var entryFix = targets[0].Name;
        var entryPos = navDb.GetFixPosition(entryFix);
        if (entryPos is null)
        {
            return null;
        }

        var rwKey = star.RunwayTransitions.Keys.First();
        var runway = rwKey.StartsWith("RW", StringComparison.Ordinal) ? rwKey[2..] : rwKey;

        return new Fixture(entryFix, runway, new LatLon(entryPos.Value.Lat, entryPos.Value.Lon));
    }

    private static SpawnRequest OnStarRequest(Fixture f, bool descendVia, double? altitude) =>
        new()
        {
            Rules = FlightRulesKind.Ifr,
            Weight = WeightClass.Heavy,
            Engine = EngineKind.Jet,
            PositionType = SpawnPositionType.OnStar,
            StarEntryFix = f.EntryFix,
            StarId = StarId,
            StarRunway = f.Runway,
            DescendVia = descendVia,
            StarAltitude = altitude,
            DestinationAirportId = Airport,
        };

    private static (AircraftState? State, string? Error) Generate(SpawnRequest req) =>
        AircraftGenerator.Generate(req, Airport, Array.Empty<AircraftState>(), groundLayout: null, new Random(1), new BeaconCodePool());

    [Fact]
    public void DescendVia_SpawnsEstablishedOnStar_WithConstraints()
    {
        var f = TryBuildFixture();
        if (f is null)
        {
            return;
        }

        var (state, error) = Generate(OnStarRequest(f, descendVia: true, altitude: 16000));

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal(StarId, state.Procedure.ActiveStarId);
        Assert.True(state.Procedure.StarViaMode, "descend-via spawn should enable StarViaMode");
        Assert.NotEmpty(state.Targets.NavigationRoute);
        Assert.Contains(state.Targets.NavigationRoute, t => t.AltitudeRestriction is not null);

        // Spawn position is the entry waypoint.
        Assert.True(GeoMath.DistanceNm(state.Position, f.EntryPos) < 1.0, "spawn position should be at the entry fix");
        Assert.Equal(16000, state.Altitude);
        Assert.False(state.IsOnGround);
    }

    [Fact]
    public void Level_BuildsLateralRouteOnly_NoConstraints_NoStarViaMode()
    {
        var f = TryBuildFixture();
        if (f is null)
        {
            return;
        }

        var (state, error) = Generate(OnStarRequest(f, descendVia: false, altitude: 12000));

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.False(state.Procedure.StarViaMode, "level spawn must NOT enable StarViaMode");
        Assert.Null(state.Procedure.ActiveStarId);
        Assert.NotEmpty(state.Targets.NavigationRoute);
        // No altitude constraints overlaid → descent planning stays inert and the aircraft holds level.
        Assert.DoesNotContain(state.Targets.NavigationRoute, t => t.AltitudeRestriction is not null);
        // STAR is in the filed route so a later DVIA can reactivate the descent profile.
        Assert.Contains(StarId, state.FlightPlan.Route);
        Assert.Equal(12000, state.Altitude);
    }

    [Fact]
    public void AltitudeOmitted_ComputesDefaultWithinBand()
    {
        var f = TryBuildFixture();
        if (f is null)
        {
            return;
        }

        var (state, error) = Generate(OnStarRequest(f, descendVia: true, altitude: null));

        Assert.Null(error);
        Assert.NotNull(state);
        output.WriteLine($"computed default altitude: {state.Altitude:F0}");
        // A published crossing at the entry fix is authoritative (may be a high enroute floor); derived
        // estimates are clamped to FL240. Either way the result is a sane, 1000-ft-snapped altitude.
        Assert.InRange(state.Altitude, 3000, 45000);
        Assert.Equal(0, state.Altitude % 1000); // snapped to a 1000-ft increment
    }

    [Fact]
    public void DescendVia_TicksDownTowardConstraints()
    {
        var f = TryBuildFixture();
        if (f is null)
        {
            return;
        }

        // FL240 is well above any OAKES3 crossing restriction, so descent is guaranteed.
        var (state, error) = Generate(OnStarRequest(f, descendVia: true, altitude: 24000));
        Assert.Null(error);
        Assert.NotNull(state);

        double initialAlt = state.Altitude;
        for (int t = 0; t < 180; t++)
        {
            FlightPhysics.Update(state, 1.0, null, null);
        }

        output.WriteLine($"alt {initialAlt:F0} -> {state.Altitude:F0} (target {state.Targets.TargetAltitude:F0})");
        bool descending = (state.Altitude < initialAlt) || (state.Targets.TargetAltitude < initialAlt);
        Assert.True(descending, $"descend-via aircraft should descend: {initialAlt:F0} -> {state.Altitude:F0}");
    }

    [Fact]
    public void Level_HoldsAltitude_ThenDviaReactivatesDescent()
    {
        var f = TryBuildFixture();
        if (f is null)
        {
            return;
        }

        var (state, error) = Generate(OnStarRequest(f, descendVia: false, altitude: 16000));
        Assert.Null(error);
        Assert.NotNull(state);

        double spawnAlt = state.Altitude;
        for (int t = 0; t < 120; t++)
        {
            FlightPhysics.Update(state, 1.0, null, null);
        }

        // Holds level while StarViaMode is off and the route carries no constraints.
        Assert.True(Math.Abs(state.Altitude - spawnAlt) < 500, $"level aircraft should hold ~{spawnAlt:F0}, was {state.Altitude:F0}");
        Assert.DoesNotContain(state.Targets.NavigationRoute, t => t.AltitudeRestriction is not null);

        // DVIA reactivates the filed STAR (ActiveStarId was null → TryActivateFiledStar reads FlightPlan.Route).
        var result = CommandDispatcher.Dispatch(new DescendViaCommand(null), state, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, $"DVIA should succeed via the filed route: {result.Message}");
        Assert.Equal(StarId, state.Procedure.ActiveStarId);
        Assert.True(state.Procedure.StarViaMode);
        Assert.Contains(state.Targets.NavigationRoute, t => t.AltitudeRestriction is not null);
    }
}
