using Microsoft.Extensions.Logging;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Seeds the pilot's filed altitude (<see cref="Snapshots.AircraftSnapshotDto.FiledCruiseAltitudeFt"/>) on the live-traffic
/// shadow spawn states (<see cref="RecordedLiveTrafficSample.SpawnState"/>), the <c>ADD</c> spawns
/// (<see cref="RecordedCommand.SpawnedAircraft"/>) and the generated-aircraft spawns
/// (<see cref="RecordedAircraftSpawn.Aircraft"/>, real or synthetic) of a tape written before snapshot schema
/// <see cref="FiledCruiseAltitudeSchemaVersion"/>, which carried none: each takes its plan's altitude (null without one), as
/// <see cref="Snapshots.SnapshotSchemaMigrator"/> seeds the snapshots, so a replayed shadow spawns with the altitude it was
/// filed at and the assume keeps it, a recorded <c>ADD</c> agrees with the generator's derivation, which seeds its own, and a
/// replayed generated aircraft climbs to the altitude a seek to its snapshot gives it.
/// No <c>ADD</c> path files a plan altitude, so the <c>ADD</c> seed is defensive.
///
/// <para>Not safe on a tape written from that schema on, where a null is what was recorded (the feed filed no altitude when
/// the shadow spawned): callers gate it on <see cref="PredatesFiledCruiseAltitude"/> of the schema the tape's snapshot was
/// written at. A second pass is a no-op.</para>
/// </summary>
public static class FiledAltitudeSpawnStateRewriter
{
    /// <summary>
    /// The snapshot schema from which a recorded spawn (a shadow spawn state, an <c>ADD</c>'s spawned aircraft, a generated
    /// aircraft's spawn) carries the pilot's filed altitude.
    /// </summary>
    private const int FiledCruiseAltitudeSchemaVersion = 55;

    private static readonly ILogger Log = SimLog.CreateLogger("FiledAltitudeSpawnStateRewriter");

    /// <summary>
    /// Whether a tape recorded beside a snapshot written at <paramref name="schemaVersion"/> predates the pilot's filed
    /// altitude, so it must go through <see cref="SeedRecordedSpawnStates"/> before it replays.
    /// </summary>
    public static bool PredatesFiledCruiseAltitude(int schemaVersion) => schemaVersion < FiledCruiseAltitudeSchemaVersion;

    /// <summary>
    /// Seeds every recorded shadow spawn state, every <c>ADD</c>'s spawned aircraft (<see cref="RecordedCommand.SpawnedAircraft"/>)
    /// and every generated-aircraft spawn (<see cref="RecordedAircraftSpawn.Aircraft"/>) with no filed altitude from its
    /// plan's altitude; returns whether any changed.
    /// </summary>
    public static bool SeedRecordedSpawnStates(List<RecordedAction> actions)
    {
        int seeded = 0;
        foreach (RecordedAction action in actions)
        {
            AircraftSnapshotDto? spawn = action switch
            {
                RecordedLiveTrafficSample sample => sample.SpawnState,
                RecordedCommand command => command.SpawnedAircraft,
                RecordedAircraftSpawn generated => generated.Aircraft,
                _ => null,
            };
            if (SeedFromPlan(spawn))
            {
                seeded++;
            }
        }

        if ((seeded > 0) && Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Seeded the filed altitude of {Count} pre-55 recorded spawn(s) from their plans", seeded);
        }

        return seeded > 0;
    }

    /// <summary>Gives a recorded spawn with no filed altitude its plan's altitude; returns whether it set one.</summary>
    private static bool SeedFromPlan(AircraftSnapshotDto? spawn)
    {
        if ((spawn is not { FiledCruiseAltitudeFt: null }) || (spawn.FlightPlan?.AltitudeCruiseFeet is not { } filed))
        {
            return false;
        }

        spawn.FiledCruiseAltitudeFt = filed;
        return true;
    }
}
