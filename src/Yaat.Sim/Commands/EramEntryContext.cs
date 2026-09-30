using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// What <see cref="EramEntryEngine.Apply"/> needs beyond the aircraft and the entry: the acting ERAM position the record
/// names (null when it names none or the name no longer resolves), and for a <c>HANDOFF</c> the scenario whose positions
/// the TCP code resolves against and where a handoff to an unattended position lands.
///
/// <para><see cref="Scenario"/> is null only when no scenario is loaded. <see cref="Redirect"/> is the consolidation
/// redirect: a handoff to an unattended position lands on the attended position it is consolidated under. It is null
/// when the run cannot answer attendance, and then the handoff goes to the named position itself.</para>
///
/// <para><see cref="EramConflicts"/> is the engine's ERAM conflict-alert set, which the <c>CO</c> entry suppresses and
/// restores alerts in.</para>
///
/// <para><see cref="SweptPosition"/> is the target pose the display shows (the record's last ERAM sweep or coast point),
/// where a <c>COAST</c> with no <c>@</c> starts a track that is neither frozen nor coasting; null falls back to the live
/// target at the entry's time.</para>
/// </summary>
public sealed record EramEntryContext(
    TrackOwner? Identity,
    SimScenarioState? Scenario,
    ConsolidationRedirect? Redirect,
    EramConflictState EramConflicts,
    SweptPose? SweptPosition
);

/// <summary>A target pose as the ERAM display shows it: its position, its true ground track, and the sim time it belongs to.</summary>
public readonly record struct SweptPose(LatLon Position, double TrackDeg, double SimSeconds);
