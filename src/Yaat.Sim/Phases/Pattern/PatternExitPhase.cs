using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Terminal leg of a VFR pattern-exit departure (CTO MRC/MRD/MLC/MLD). After the preceding
/// pattern legs (upwind, and crosswind for a downwind exit), the aircraft rolls out on the
/// exit-leg heading — crosswind heading for a crosswind departure, runway reciprocal for a
/// downwind departure — and departs the area, continuing the takeoff-rate climb toward its
/// assigned/cruise altitude. The phase completes once the aircraft is established on the exit
/// heading, handing it to free flight so the controller can vector, climb, or hand it off
/// (FAA 7110.65 3-9-3.b.1). There is no base/final/landing — the aircraft does not re-enter.
/// </summary>
public sealed class PatternExitPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("PatternExitPhase");

    private const double HeadingToleranceDeg = 5.0;

    /// <summary>True heading the aircraft rolls out on and departs (crosswind or downwind heading).</summary>
    public TrueHeading ExitHeading { get; set; }

    /// <summary>Pattern side, which determines the turn direction onto the exit leg.</summary>
    public PatternDirection Direction { get; set; }

    /// <summary>
    /// Fully-resolved continuous-climb target (ft MSL): the controller-assigned altitude, else the
    /// filed cruise altitude, else pattern altitude. A departing aircraft keeps climbing toward this
    /// and never levels at pattern altitude. Resolved by <see cref="PatternBuilder.BuildPatternExitCircuit"/>.
    /// </summary>
    public int ClimbTargetFt { get; set; }

    public override string Name => "PatternExit";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.TargetTrueHeading = ExitHeading;
        ctx.Targets.PreferredTurnDirection = Direction == PatternDirection.Right ? TurnDirection.Right : TurnDirection.Left;
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        }

        // Continue the takeoff-rate climb toward the assigned/cruise altitude — a departing
        // aircraft never levels at pattern altitude. Release the slower pattern speed target so
        // FlightPhysics accelerates to the normal climb-out speed for the altitude band.
        int climbTo = ClimbTargetFt;
        ctx.Targets.TargetAltitude = climbTo;
        ctx.Targets.DesiredVerticalRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        ctx.Targets.TargetSpeed = null;

        Log.LogDebug("[PatternExit] {Callsign}: departing on hdg={Hdg:F0}, climbTo={Alt}ft", ctx.Aircraft.Callsign, ExitHeading.Degrees, climbTo);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Hold the exit heading through the rollout. Once established, complete so the aircraft
        // free-flies straight out on that heading, climbing, until the controller acts.
        bool established = ctx.Aircraft.TrueHeading.AbsAngleTo(ExitHeading) < HeadingToleranceDeg;
        if (established)
        {
            Log.LogDebug("[PatternExit] {Callsign}: established on exit heading, departing the pattern", ctx.Aircraft.Callsign);
        }

        return established;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        // Clear the directional bias the exit turn set so a subsequent vector doesn't inherit it.
        ctx.Targets.PreferredTurnDirection = null;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Altitude and speed adjustments retarget without breaking the departure; any lateral
        // instruction (a vector or direct) ends the exit and hands the aircraft to free flight.
        if (IsAdditiveAirborneAdjustment(cmd))
        {
            return CommandAcceptance.Allowed;
        }

        return CommandAcceptance.ClearsPhase;
    }

    public override PhaseDto ToSnapshot() =>
        new PatternExitPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            ExitHeadingDeg = ExitHeading.Degrees,
            Direction = (int)Direction,
            ClimbTargetFt = ClimbTargetFt,
        };

    public static PatternExitPhase FromSnapshot(PatternExitPhaseDto dto)
    {
        var phase = new PatternExitPhase
        {
            ExitHeading = new TrueHeading(dto.ExitHeadingDeg),
            Direction = (PatternDirection)dto.Direction,
            ClimbTargetFt = dto.ClimbTargetFt ?? 0,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
