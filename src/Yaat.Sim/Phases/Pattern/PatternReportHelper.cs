using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Voices a controller-armed "turning {leg}" pilot report when a pattern leg begins. Called from each
/// pattern phase's <c>OnStart</c>: the report fires once per phase instance, which is once per circuit
/// — closed-traffic laps build fresh phase instances (<see cref="PatternBuilder.BuildNextCircuit"/>),
/// so the report naturally re-arms every round until the controller cancels it. Snapshot-safe: restore
/// rebuilds phases via <c>FromSnapshot</c> (not <c>OnStart</c>), so a restored mid-leg aircraft does
/// not re-voice a report it already made.
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

    private static bool IsLegArmed(AircraftApproachState approach, ReportTrigger leg) =>
        leg switch
        {
            ReportTrigger.Crosswind => approach.ReportArmedCrosswind,
            ReportTrigger.Downwind => approach.ReportArmedDownwind,
            ReportTrigger.Base => approach.ReportArmedBase,
            ReportTrigger.Final => approach.ReportArmedFinal,
            _ => false,
        };
}
