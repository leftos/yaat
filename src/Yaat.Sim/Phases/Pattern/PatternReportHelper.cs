using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>Where on the downwind the aircraft is when its armed <c>REPORT MIDFIELD</c> report fires, which sets the wording.</summary>
public enum MidfieldReportPosition
{
    /// <summary>At midfield: "midfield {side} downwind".</summary>
    Midfield,

    /// <summary>Past midfield, short of abeam the threshold: "{side} downwind, past midfield".</summary>
    PastMidfield,

    /// <summary>At or past abeam the threshold: "{side} downwind, abeam the numbers".</summary>
    AbeamNumbers,
}

/// <summary>
/// Voices a controller-armed "turning {leg}" pilot report when a pattern leg begins. Called from each
/// pattern phase's <c>OnStart</c>: the report fires once per phase instance, which is once per circuit
/// — closed-traffic laps build fresh phase instances (<see cref="PatternBuilder.BuildNextCircuit"/>),
/// so the report naturally re-arms every round until the controller cancels it. Snapshot-safe: restore
/// rebuilds phases via <c>FromSnapshot</c> (not <c>OnStart</c>), so a restored mid-leg aircraft does
/// not re-voice a report it already made.
///
/// <para>The controller-armed midfield-downwind report (<c>REPORT MIDFIELD</c>) is the one report fired mid-leg: from
/// <see cref="DownwindPhase"/>'s <c>OnTick</c> at the midfield trigger (or its <c>OnStart</c> for a downwind joined at,
/// past or just short of it), through <see cref="EmitMidfieldDownwind"/>. It fires once per lap behind the leg's own
/// latch, <see cref="DownwindPhase.MidfieldReportIssued"/>, which is snapshotted and copied onto a downwind that
/// re-creates the one being flown on the same runway.</para>
/// </summary>
internal static class PatternReportHelper
{
    public static void EmitTurningLeg(PhaseContext ctx, ReportTrigger leg)
    {
        if (!IsLegArmed(ctx.Aircraft.Approach, leg))
        {
            return;
        }

        string runwayId = RunwayIdentifier.ToDisplayDesignator(ctx.Runway?.Designator ?? "unknown");
        PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            PilotResponder.BuildTurningLegReport(ctx.Aircraft, leg, runwayId),
            PilotResponder.SoloPositionsTowerApproach
        );
    }

    /// <summary>
    /// Voices the controller-armed midfield-downwind report (<c>REPORT MIDFIELD</c>). Called by
    /// <see cref="DownwindPhase"/> once per leg, when the aircraft reaches midfield or, armed late or joined past it, at
    /// once; the caller latches it and checks <see cref="IsLegArmed"/> first.
    /// </summary>
    public static void EmitMidfieldDownwind(PhaseContext ctx, PatternDirection side, MidfieldReportPosition position)
    {
        string runwayId = RunwayIdentifier.ToDisplayDesignator(ctx.Runway?.Designator ?? "unknown");
        PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            PilotResponder.BuildMidfieldDownwindReport(ctx.Aircraft, side, position, runwayId),
            PilotResponder.SoloPositionsTowerApproach
        );
    }

    public static bool IsLegArmed(AircraftApproachState approach, ReportTrigger leg) =>
        leg switch
        {
            ReportTrigger.Crosswind => approach.ReportArmedCrosswind,
            ReportTrigger.Downwind => approach.ReportArmedDownwind,
            ReportTrigger.Base => approach.ReportArmedBase,
            ReportTrigger.Final => approach.ReportArmedFinal,
            ReportTrigger.Midfield => approach.ReportArmedMidfield,
            _ => false,
        };
}
