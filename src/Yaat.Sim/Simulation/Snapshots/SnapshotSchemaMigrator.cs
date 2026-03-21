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
    public const int CurrentSchemaVersion = 1;

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
        // Example when we add version 2:
        //   if (snapshot.SchemaVersion < 2) MigrateV1ToV2(snapshot);
        //   if (snapshot.SchemaVersion < 3) MigrateV2ToV3(snapshot);

        snapshot.SchemaVersion = CurrentSchemaVersion;
    }

    // Migration steps go here as private static methods.
    // Each step mutates the DTO in place and bumps SchemaVersion.
    // Throw SnapshotSchemaException if migration is not possible.
    //
    // Example:
    // private static void MigrateV1ToV2(StateSnapshotDto snapshot)
    // {
    //     foreach (var ac in snapshot.Aircraft)
    //     {
    //         ac.NewField ??= ComputeDefault(ac);
    //     }
    //     snapshot.SchemaVersion = 2;
    // }
}
