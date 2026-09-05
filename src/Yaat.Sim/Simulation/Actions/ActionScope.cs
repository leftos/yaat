namespace Yaat.Sim.Simulation.Actions;

/// <summary>
/// What a controller action is addressed to, resolved by the router <em>before</em> the action's arm runs. The
/// recorded callsign is not the discriminator: a global command is recorded with an empty callsign, and an applier
/// that resolves an aircraft first drops every one of them at its not-found guard — which is how <c>SQALL</c>,
/// <c>TAXIALL</c> and <c>ADD</c> silently no-op'd on every replay until the 2026-09-05 audit. The scope is a property
/// of the <see cref="RecordedCommandKind"/>, so a new kind has to say what it is addressed to before it can route.
/// </summary>
public enum ActionScope
{
    /// <summary>
    /// Addressed to the room or the world — nothing is resolved (<c>SQALL</c>, <c>TAXIALL</c>, <c>ADD</c>, <c>CON</c>, <c>HFR</c>).
    /// </summary>
    Global,

    /// <summary>
    /// Addressed to a callsign the arm resolves itself, because the target may be queued, not yet spawned, or about to
    /// be created (<c>DEL</c>, <c>SPAWN</c>, <c>GHOST</c>, <c>TIMER</c>, the strip verbs, <c>DA</c> on a new callsign).
    /// </summary>
    Callsign,

    /// <summary>
    /// Addressed to an aircraft that must exist; the router resolves it and refuses identically on every run kind when
    /// it does not.
    /// </summary>
    Aircraft,

    /// <summary>
    /// Addressed to a controller position — an acting identity is resolved, no aircraft (<c>AS</c>, <c>ACCEPTALL</c>,
    /// <c>HOALL</c>, <c>RDAUTO</c>).
    /// </summary>
    Position,
}
