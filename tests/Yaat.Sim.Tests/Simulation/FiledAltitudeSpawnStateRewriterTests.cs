using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <see cref="FiledAltitudeSpawnStateRewriter.SeedRecordedSpawnStates"/> on a tape written before snapshot schema 55: each
/// recorded spawn with no pilot's filed altitude (a live-traffic shadow's spawn state, an <c>ADD</c>'s spawned aircraft, a
/// generated aircraft's spawn) takes its plan's altitude, and nothing else changes. No <c>ADD</c> path files a plan
/// altitude, so the <c>ADD</c> case is a defensive seed.
/// </summary>
public class FiledAltitudeSpawnStateRewriterTests
{
    [Theory]
    [InlineData(54, true)]
    [InlineData(SnapshotSchemaMigrator.CurrentSchemaVersion, false)]
    public void PredatesFiledCruiseAltitude_OnlyBelowSchema55(int schemaVersion, bool expected) =>
        Assert.Equal(expected, FiledAltitudeSpawnStateRewriter.PredatesFiledCruiseAltitude(schemaVersion));

    [Fact]
    public void SeedRecordedSpawnStates_ASecondPassIsANoOp()
    {
        RecordedLiveTrafficSample sample = ShadowSpawnSamples.ShadowSpawnSample(planAltitudeFt: 23000);
        List<RecordedAction> actions = [sample];

        Assert.True(FiledAltitudeSpawnStateRewriter.SeedRecordedSpawnStates(actions));
        Assert.False(FiledAltitudeSpawnStateRewriter.SeedRecordedSpawnStates(actions));

        Assert.Equal(23000, sample.SpawnState!.FiledCruiseAltitudeFt);
    }

    [Fact]
    public void SeedRecordedSpawnStates_ASpawnStateWithNoPlanAltitude_StaysNull()
    {
        RecordedLiveTrafficSample sample = ShadowSpawnSamples.ShadowSpawnSample(planAltitudeFt: null);

        Assert.False(FiledAltitudeSpawnStateRewriter.SeedRecordedSpawnStates([sample]));

        Assert.Null(sample.SpawnState!.FiledCruiseAltitudeFt);
    }

    [Fact]
    public void SeedRecordedSpawnStates_AnAlreadySetField_IsKept()
    {
        RecordedLiveTrafficSample sample = ShadowSpawnSamples.ShadowSpawnSample(planAltitudeFt: 23000);
        sample.SpawnState!.FiledCruiseAltitudeFt = 31000;

        Assert.False(FiledAltitudeSpawnStateRewriter.SeedRecordedSpawnStates([sample]));

        Assert.Equal(31000, sample.SpawnState.FiledCruiseAltitudeFt);
    }

    /// <summary>A pre-55 <c>ADD</c> recorded the aircraft it spawned with no pilot's filed altitude: it takes its plan's.</summary>
    [Fact]
    public void SeedRecordedSpawnStates_SeedsAnAddsSpawnedAircraftFromItsPlan()
    {
        AircraftSnapshotDto spawned = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "VFR",
                Departure = "KOAK",
                Destination = "KSAC",
                Altitude = PlannedAltitude.Vfr(5500),
            },
        }.ToSnapshot();
        Assert.Null(spawned.FiledCruiseAltitudeFt);
        var add = new RecordedCommand(0, "", "ADD V S P @NEW1", "XX", "conn-1") { SpawnedAircraft = spawned };

        Assert.True(FiledAltitudeSpawnStateRewriter.SeedRecordedSpawnStates([add]));

        Assert.Equal(5500, spawned.FiledCruiseAltitudeFt);
    }

    /// <summary>A pre-55 generated-aircraft spawn recorded no pilot's filed altitude: its aircraft takes its plan's.</summary>
    [Fact]
    public void SeedRecordedSpawnStates_SeedsAGeneratedSpawnsAircraftFromItsPlan()
    {
        RecordedAircraftSpawn spawn = ShadowSpawnSamples.GeneratedSpawn(23000);

        Assert.True(FiledAltitudeSpawnStateRewriter.SeedRecordedSpawnStates([spawn]));

        Assert.Equal(23000, spawn.Aircraft.FiledCruiseAltitudeFt);
    }

    /// <summary>
    /// No checked-in non-zip tape holds a recorded spawn (a live-traffic shadow spawn state, an <c>ADD</c>'s spawned
    /// aircraft, a generated-aircraft spawn). <see cref="RecordingLoader"/> returns a non-zip tape as it was written,
    /// without the pre-55 seeding a server load or an archive read gives it, so such a tape would replay its spawns with no
    /// pilot's filed altitude.
    /// </summary>
    [Fact]
    public void NoCheckedInNonZipTape_HoldsARecordedSpawn()
    {
        List<string> tapes =
        [
            .. Directory.GetFiles("TestData", "*-recording.*").Where(path => !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)),
        ];
        Assert.NotEmpty(tapes);

        foreach (string tape in tapes)
        {
            SessionRecording recording = Assert.IsType<SessionRecording>(RecordingLoader.Load(tape));
            Assert.False(
                recording.Actions.Any(action =>
                    (action is RecordedLiveTrafficSample { SpawnState: not null })
                    || (action is RecordedCommand { SpawnedAircraft: not null })
                    || (action is RecordedAircraftSpawn)
                ),
                $"{tape} holds a recorded spawn: RecordingLoader must seed it "
                    + "(FiledAltitudeSpawnStateRewriter.SeedRecordedSpawnStates, gated on its first snapshot's schema) before it replays"
            );
        }
    }
}
