namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Thrown when a snapshot's schema version cannot be upgraded to the current version.
/// Callers should fall back to command replay (v1 mode) for this recording.
/// </summary>
public sealed class SnapshotSchemaException : Exception
{
    public int SnapshotVersion { get; }
    public int RequiredVersion { get; }

    public SnapshotSchemaException(int snapshotVersion, int requiredVersion)
        : base(
            $"Snapshot schema version {snapshotVersion} cannot be migrated to version {requiredVersion}. "
                + "Use command replay mode for this recording."
        )
    {
        SnapshotVersion = snapshotVersion;
        RequiredVersion = requiredVersion;
    }
}

/// <summary>
/// Upgrades <see cref="StateSnapshotDto"/> instances from older schema versions
/// to <see cref="CurrentSchemaVersion"/> via a sequential migration chain.
/// Each migration step handles one version increment (e.g., 1→2, 2→3).
/// When a breaking change makes migration impossible, the step throws
/// <see cref="SnapshotSchemaException"/>.
/// </summary>
public static class SnapshotSchemaMigrator
{
    public const int CurrentSchemaVersion = 9;

    /// <summary>
    /// Migrates a snapshot to <see cref="CurrentSchemaVersion"/> in place.
    /// No-op if already current. Throws <see cref="SnapshotSchemaException"/>
    /// if the schema is too old or too new to migrate.
    /// </summary>
    public static void Migrate(StateSnapshotDto snapshot)
    {
        if (snapshot.SchemaVersion == CurrentSchemaVersion)
        {
            return;
        }

        if (snapshot.SchemaVersion > CurrentSchemaVersion)
        {
            throw new SnapshotSchemaException(snapshot.SchemaVersion, CurrentSchemaVersion);
        }

        // Sequential migration chain: apply each step in order.
        // V1→V2: Added ServerSnapshotDto (consolidation, conflicts, beacon pool).
        //   No data transformation — V1 snapshots have Server = null, which
        //   RestoreFromSnapshot handles gracefully by clearing state.
        // V2→V3: Added AircraftFlightPlanDto.CreatedByOwner. No data transformation —
        //   legacy snapshots default to null, which makes ProcessFlightPlanCreatorAutoTrack
        //   a no-op for those aircraft (preserving prior replay behavior).
        // V3→V4: Split actual vs filed aircraft type — added AircraftFlightPlanDto.AircraftType.
        //   Legacy snapshots default the new field to "". Seed it from the parent aircraft's
        //   AircraftType so STARS/ASDE-X/FP-Editor still display a type when replaying old
        //   recordings (where filed and actual were always the same single field).
        // V4→V5: Added AircraftSnapshotDto.PendingPilotRequest. No data transformation —
        //   older snapshots default to null, meaning no unsatisfied pilot request was active.
        // V5→V6: Added AircraftSnapshotDto.HasLeftStudentFrequency. No data transformation —
        //   older snapshots default to false, preserving previous in-service behavior.
        // V6→V7: Added HoldShortPointDto.ClearedByAutoCross to track AutoCross-driven
        //   clearance. No data transformation — older snapshots default to false, which
        //   matches pre-feature semantics: a subsequent AutoCross-OFF toggle on replay
        //   will not revert any of their pre-cleared hold-shorts.
        // V7→V8: Added AircraftSnapshotDto.SpawnedAtSeconds / CompletedAtSeconds /
        //   CompletionReasonValue / CompletionDetail for M12.4 per-aircraft debrief.
        //   No data transformation — older snapshots default to spawn-at-0 / not-completed,
        //   which makes time-on-frequency report from session start and shows the aircraft
        //   as Active in the debrief tab until current-session lifecycle hooks fire.
        // V8→V9: Added AircraftGhostTrackDto.IsOverlay so the operator-facing Aircraft List
        //   can keep ghost overlays on real scenario aircraft visible while still hiding
        //   pure phantom DA/VP data blocks. No data transformation — older snapshots
        //   default to false; for replay correctness that is the safe choice because the
        //   field only affects the YAAT Aircraft List filter, not simulation behavior.
        if (snapshot.SchemaVersion < 4)
        {
            foreach (var ac in snapshot.Aircraft)
            {
                if (string.IsNullOrEmpty(ac.FlightPlan.AircraftType))
                {
                    ac.FlightPlan.AircraftType = ac.AircraftType;
                }
            }
        }

        snapshot.SchemaVersion = CurrentSchemaVersion;
    }
}
