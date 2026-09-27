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
/// </summary>
public sealed record EramEntryContext(TrackOwner? Identity, SimScenarioState? Scenario, ConsolidationRedirect? Redirect);
