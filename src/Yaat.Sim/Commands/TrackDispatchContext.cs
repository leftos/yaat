using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// Bundle of context required to dispatch a track command through <see cref="TrackEngine.Dispatch"/>: who is acting,
/// the scenario the positions and STARS configuration come from, where a handoff or point-out to an unattended TCP
/// lands, and the engine's conflict-alert set.
///
/// All fields are positional and required — every run kind (live room, replay, server reconstruction, bare test
/// engine) constructs a context explicitly, so a future addition breaks at the compiler instead of silently passing
/// null.
///
/// <para><see cref="Identity"/> is nullable: ownership and point-out verbs infer the acting position from track state
/// (<c>TrackEngine.RequiresIdentity</c>), so a preset or a triggered chained block dispatches with none.</para>
///
/// <para><see cref="Redirect"/> is nullable: null means "never redirect", which is what a preset or chained track
/// block dispatches with — only a run that can answer attendance builds one.</para>
///
/// <para><see cref="Conflicts"/> is the engine-level set <c>CAACK</c> acknowledges into; every other track verb
/// mutates the aircraft alone.</para>
/// </summary>
public sealed record TrackDispatchContext(
    TrackOwner? Identity,
    SimScenarioState Scenario,
    ConsolidationRedirect? Redirect,
    ConflictAlertState Conflicts
);
