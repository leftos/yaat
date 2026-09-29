using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// The QT Coast Track state (<see cref="AircraftEramState.IsCoastTrack"/> and its anchor) survives a snapshot, so a
/// rewind or a bundle restore shows the coasted track where the live room showed it; schema 29 added it, and an older
/// snapshot restores with no coast.
/// </summary>
public class EramCoastSnapshotTests
{
    private static readonly TrackOwner Sector44 = TrackOwner.CreateEram("ZOA_44_CTR", "ZOA", "44");

    private static AircraftState CoastedAircraft(string entry)
    {
        var ac = new AircraftState
        {
            Callsign = "UAL1",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            TrueTrack = new TrueHeading(092),
            Altitude = 11_250,
            IndicatedAirspeed = 280,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { CruiseSpeed = 440 },
        };
        CommandResult result = EramEntryEngine.Apply(
            ac,
            entry,
            new EramEntryContext(Sector44, Scenario: null, Redirect: null, new EramConflictState())
        );
        Assert.True(result.Success, result.Message);
        return ac;
    }

    private static AircraftState RestoreThroughJson(AircraftState ac)
    {
        var snapshot = new StateSnapshotDto
        {
            SchemaVersion = SnapshotSchemaMigrator.CurrentSchemaVersion,
            ElapsedSeconds = 30,
            Rng = new RngState(0, 0, 0, 0),
            Aircraft = [ac.ToSnapshot()],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "s",
                ScenarioName = "s",
                RngSeed = 0,
                ElapsedSeconds = 30,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = true,
                IsPaused = false,
                SimRate = 1,
                AutoAcceptDelaySeconds = 5,
                IsStudentTowerPosition = false,
            },
        };
        string json = JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default);
        StateSnapshotDto read = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;
        SnapshotSchemaMigrator.Migrate(read);
        return AircraftState.FromSnapshot(Assert.Single(read.Aircraft), null);
    }

    [Theory]
    [InlineData("COAST T30 @37.6,-122.1 S400 A170 H27")]
    [InlineData("COAST T30 S400 R37.7,-122.0 R38.0,-122.0")]
    public void CoastTrack_RoundTripsThroughASnapshot_AndShowsTheSamePosition(string entry)
    {
        AircraftState live = CoastedAircraft(entry);

        AircraftState restored = RestoreThroughJson(live);

        Assert.True(restored.Eram.IsCoastTrack);
        Assert.Equal(live.Eram.CoastLat, restored.Eram.CoastLat);
        Assert.Equal(live.Eram.CoastLon, restored.Eram.CoastLon);
        Assert.Equal(live.Eram.CoastStartSeconds, restored.Eram.CoastStartSeconds);
        Assert.Equal(live.Eram.CoastAltitude, restored.Eram.CoastAltitude);
        Assert.Equal(live.Eram.CoastSpeed, restored.Eram.CoastSpeed);
        Assert.Equal(live.Eram.CoastTrueCourse, restored.Eram.CoastTrueCourse);
        Assert.Equal(live.Eram.CoastRoute, restored.Eram.CoastRoute);
        foreach (double t in new[] { 30.0, 95.5, 400.0, 3600.0 })
        {
            Assert.Equal(live.Eram.CoastPositionAt(t), restored.Eram.CoastPositionAt(t));
        }
    }

    [Fact]
    public void NotCoasted_RoundTripsWithNoCoastFields()
    {
        AircraftEramStateDto dto = new AircraftEramState().ToSnapshot();

        Assert.False(dto.IsCoastTrack);
        Assert.Null(dto.CoastRoute);
        Assert.False(AircraftEramState.FromSnapshot(dto).IsCoastTrack);
    }

    [Fact]
    public void SchemaV28Snapshot_MigratesToV29_WithNoCoast()
    {
        // A schema-28 aircraft's ERAM state carries no coast fields.
        const string json = """
            {
              "SchemaVersion": 28,
              "ElapsedSeconds": 10,
              "Rng": { "S0": 1, "S1": 2, "S2": 3, "S3": 4 },
              "Aircraft": [ { "Callsign": "AAL1", "AircraftType": "B738", "Eram": { "IsFrozen": false, "LeaderLength": 2 } } ],
              "Scenario": { "ScenarioId": "t", "ScenarioName": "T", "RngSeed": 1, "ElapsedSeconds": 10, "SimRate": 1 }
            }
            """;
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        var eram = AircraftEramState.FromSnapshot(Assert.Single(snapshot.Aircraft).Eram);
        Assert.False(eram.IsCoastTrack);
        Assert.Empty(eram.CoastRoute);
        Assert.Null(eram.CoastPositionAt(60));
        Assert.Empty(eram.SectorDisplays); // the legacy per-aircraft LeaderLength names no sector, so it is dropped
    }
}
