namespace Yaat.Sim.Simulation.Replay;

/// <summary>
/// Outcome of a replay that compared engine state against bundled snapshots.
/// Each entry in <see cref="Drifts"/> represents one snapshot timestamp where
/// the live state diverged from the captured one. An empty list means
/// every checked snapshot matched within tolerance.
/// </summary>
public sealed record ReplayResult(IReadOnlyList<SnapshotDriftReport> Drifts);

public sealed record SnapshotDriftReport(double ElapsedSeconds, IReadOnlyList<AircraftDrift> AircraftDrifts);

public sealed record AircraftDrift(string Callsign, IReadOnlyList<FieldDrift> Fields);

/// <summary>
/// One field that diverged between the engine and the snapshot at a given
/// timestamp. <see cref="Detail"/> carries an optional human-readable note
/// (e.g. "0.62 nm" for a position drift, "+8.3°" for heading).
/// </summary>
public sealed record FieldDrift(string Field, string Expected, string Actual, string? Detail);
