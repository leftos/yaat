using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Pilot;

/// <summary>
/// What <see cref="PilotResponder.RouteByTunedPosition"/> needs to decide who hears a sim-initiated pilot report: whether the room
/// is solo training, whether an RPO room shows pilot speech, the student's position, and the positions that answer pilots this
/// session (<see cref="PilotContactRoster"/>), whose AI-staffed entries hear a pilot tuned to them.
/// </summary>
public sealed record PilotReportRouting(bool SoloTrainingMode, bool RpoShowPilotSpeech, TrackOwner? StudentPosition, PilotContactRoster Contacts)
{
    /// <summary>The routing a phase's tick reads from its context.</summary>
    public static PilotReportRouting From(PhaseContext ctx) =>
        new(ctx.SoloTrainingMode, ctx.RpoShowPilotSpeech, ctx.StudentPosition, ctx.PilotContacts);

    /// <summary>The routing a per-aircraft tick with no phase context reads from the scenario.</summary>
    public static PilotReportRouting From(SimScenarioState scenario) =>
        new(scenario.SoloTrainingMode, scenario.RpoShowPilotSpeech, scenario.StudentPosition, scenario.PilotContacts);

    /// <summary>The routing a command handler reads from its dispatch context, for a pilot answer said at once.</summary>
    public static PilotReportRouting From(DispatchContext ctx) =>
        new(ctx.SoloTrainingMode, ctx.RpoShowPilotSpeech, ctx.StudentPosition, ctx.PilotContacts);
}
