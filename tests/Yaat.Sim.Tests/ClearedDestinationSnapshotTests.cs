using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="AircraftState.ClearedDestination"/> is the airport the pilot flies direct to when its route runs out. It
/// must survive a rewind, a restore and a replay, and a pre-V53 snapshot, which carries none, is seeded from the filed
/// destination so the restored aircraft flies its route end as a live one would.
/// </summary>
public class ClearedDestinationSnapshotTests
{
    private static AircraftState Arrival(bool hasFlightPlan) =>
        new()
        {
            Callsign = "SWA22",
            AircraftType = "B738",
            IsOnGround = false,
            Altitude = 11000,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan { HasFlightPlan = hasFlightPlan, Destination = "KOAK" },
        };

    private static StateSnapshotDto LegacySnapshot(AircraftSnapshotDto aircraft) =>
        new()
        {
            SchemaVersion = 52,
            ElapsedSeconds = 0,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [aircraft],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "cleared-destination",
                ScenarioName = "cleared destination migration",
                RngSeed = 42,
                ElapsedSeconds = 0,
                AutoClearedToLand = false,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
                ValidateDctFixes = false,
                IsPaused = false,
                SimRate = 1.0,
                AutoAcceptDelaySeconds = 0,
                IsStudentTowerPosition = false,
            },
        };

    [Fact]
    public void ClearedDestination_SurvivesSnapshotRoundTrip()
    {
        AircraftState aircraft = Arrival(hasFlightPlan: true);
        aircraft.ClearedDestination = "KSJC";

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), groundLayout: null);

        Assert.Equal("KSJC", restored.ClearedDestination);
    }

    [Fact]
    public void PreV53Snapshot_WithFlightPlan_MigratesToTheFiledDestination()
    {
        AircraftSnapshotDto dto = Arrival(hasFlightPlan: true).ToSnapshot();
        dto.ClearedDestination = null;
        StateSnapshotDto snapshot = LegacySnapshot(dto);

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal("KOAK", snapshot.Aircraft[0].ClearedDestination);
        Assert.Equal("KOAK", AircraftState.FromSnapshot(snapshot.Aircraft[0], groundLayout: null).ClearedDestination);
    }

    [Fact]
    public void ClearanceLimitDirectFlag_RoundTripsInMemoryAndThroughJson()
    {
        var direct = new NavigationTarget
        {
            Name = "KOAK",
            Position = new LatLon(37.72, -122.22),
            IsClearanceLimitDirect = true,
        };

        Assert.True(NavigationTarget.FromSnapshot(direct.ToSnapshot()).IsClearanceLimitDirect);

        string json = JsonSerializer.Serialize(direct.ToSnapshot());
        Assert.Contains("\"IsClearanceLimitDirect\":true", json, StringComparison.Ordinal);
        NavigationTargetDto? read = JsonSerializer.Deserialize<NavigationTargetDto>(json);
        Assert.NotNull(read);
        Assert.True(NavigationTarget.FromSnapshot(read).IsClearanceLimitDirect);
    }

    [Fact]
    public void OrdinaryTarget_WritesNoClearanceLimitDirectMember()
    {
        var fix = new NavigationTarget { Name = "BERKS", Position = new LatLon(37.86, -122.21) };

        string json = JsonSerializer.Serialize(fix.ToSnapshot());

        Assert.DoesNotContain("IsClearanceLimitDirect", json, StringComparison.Ordinal);
        Assert.False(NavigationTarget.FromSnapshot(JsonSerializer.Deserialize<NavigationTargetDto>(json)!).IsClearanceLimitDirect);
    }

    [Fact]
    public void PreV53Snapshot_WithNullFlightPlan_MigratesToNone()
    {
        JsonNode node = JsonNode.Parse(JsonSerializer.Serialize(Arrival(hasFlightPlan: true).ToSnapshot()))!;
        node["FlightPlan"] = null;
        node.AsObject().Remove("ClearedDestination");
        AircraftSnapshotDto dto = node.Deserialize<AircraftSnapshotDto>()!;
        Assert.Null(dto.FlightPlan);
        StateSnapshotDto snapshot = LegacySnapshot(dto);

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal("", snapshot.Aircraft[0].ClearedDestination);
    }

    [Fact]
    public void PreV53Snapshot_WithoutFlightPlan_MigratesToNone()
    {
        AircraftSnapshotDto dto = Arrival(hasFlightPlan: false).ToSnapshot();
        dto.ClearedDestination = null;
        StateSnapshotDto snapshot = LegacySnapshot(dto);

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal("", snapshot.Aircraft[0].ClearedDestination);
    }
}
