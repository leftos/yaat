using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// The tug brakes a cleared tow to rest. A command that ends a <see cref="PushbackPhase"/> while the aircraft is still
/// moving — a <c>TAXI</c>, <c>TAXIAUTO</c>, <c>RTAXI</c>, <c>AIRTAXI</c>, <c>LAND</c> or a new <c>PUSHM</c> — leaves the
/// tug attached (<see cref="AircraftGroundOps.TowbarTrueHeading"/> still set), and <see cref="PhaseList.Start"/> puts
/// this phase ahead of the new chain. It sheds the speed at <see cref="CategoryPerformance.TugDecelRate"/> along the
/// direction the cleared move left (<see cref="AircraftGroundOps.PushbackTrueHeading"/> on a push, the nose on a pull),
/// steering nothing, and completes once the aircraft is at rest; the next phase then starts from a standstill with no
/// towbar-disconnect dwell. It shows as "Pushback" until then.
///
/// <para>A second clearing command during the brake ends this phase while still moving and leaves the tug state for
/// the release that command's chain installs, which brakes on from the lower speed. <c>DEL</c> stops the aircraft dead
/// instead (<see cref="StopDead"/>).</para>
/// </summary>
public sealed class TugReleasePhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("TugReleasePhase");

    /// <summary>Ground speed at or below which the tow counts as at rest, knots; the same threshold the tug move uses.</summary>
    public const double AtRestKts = 0.01;

    /// <summary>Feet per second per knot.</summary>
    private const double FtPerSecPerKt = GeoMath.FeetPerNm / 3600.0;

    /// <summary>Push (tail-first) or pull (nose-first): how the cleared move was moving the aircraft.</summary>
    public required PushbackLegKind Kind { get; init; }

    public override string Name => "Pushback";

    /// <summary>
    /// The phase the command that cleared the tow installed behind this release, which starts once the aircraft is at
    /// rest; null when nothing follows. A command the release does not answer itself is judged by it
    /// (<see cref="CanAcceptCommand"/>), so a follow-up to a TAXI (HS, CROSS, SPD, FOLLOWG) is taken during the brake.
    /// </summary>
    public Phase? Next { get; internal set; }

    /// <summary>
    /// The release a chain installed with the tug still attached needs: a push when the aircraft is travelling tail-first
    /// (<see cref="AircraftGroundOps.PushbackTrueHeading"/> set), a pull otherwise.
    /// </summary>
    /// <param name="aircraft">The aircraft the tug still holds.</param>
    /// <param name="next">The phase that starts once the aircraft is at rest, or null.</param>
    /// <returns>The release phase.</returns>
    public static TugReleasePhase For(AircraftState aircraft, Phase? next) =>
        new() { Kind = aircraft.Ground.PushbackTrueHeading is not null ? PushbackLegKind.Push : PushbackLegKind.Pull, Next = next };

    /// <summary>
    /// Whether <paramref name="current"/> is a taxi, or a release a taxi starts behind: the taxi's own commands (SPD, a
    /// speed resume) apply to it either way.
    /// </summary>
    /// <param name="current">The aircraft's current phase.</param>
    /// <returns>True when the aircraft is taxiing or about to.</returns>
    public static bool IsTaxiingOrReleasingIntoTaxi(Phase? current) => current is TaxiingPhase or TugReleasePhase { Next: TaxiingPhase };

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.TargetTrueHeading = null;
        Brake(ctx);
        Log.LogDebug(
            "[Push] {Callsign}: tow cleared at {Gs:F2} kt, braking {Kind} to rest at {Decel:F1} kt/s",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.GroundSpeed,
            Kind,
            CategoryPerformance.TugDecelRate(ctx.Category)
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.GroundSpeed <= AtRestKts)
        {
            return true;
        }

        // A controller hold (AircraftGroundOps.IsImmobile) asks for nothing the release is not already doing: it brakes at
        // the towbar rate either way, and it still ends at rest so the phase behind it starts under the hold and stays put.
        Brake(ctx);
        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        // A second clearing command mid-brake hands the tug state to the release its own chain installs.
        if ((endStatus != PhaseStatus.Completed) && (ctx.Aircraft.GroundSpeed > AtRestKts))
        {
            return;
        }

        Log.LogDebug("[Push] {Callsign}: cleared tow at rest ({Status}), tug released", ctx.Aircraft.Callsign, endStatus);
        StopDead(ctx.Aircraft);
    }

    /// <summary>
    /// Takes the tug off and stops the aircraft where it stands: both tug headings and both towbar rates go, and the
    /// speed and its target are zeroed. The end of every release, and what a cleared chain with nothing behind it
    /// (<c>DEL</c>) gets in place of one.
    /// </summary>
    /// <param name="aircraft">The aircraft the tug lets go of.</param>
    public static void StopDead(AircraftState aircraft)
    {
        aircraft.Ground.PushbackTrueHeading = null;
        aircraft.Ground.TowbarTrueHeading = null;
        aircraft.Targets.DesiredAccelRate = null;
        aircraft.Targets.DesiredDecelRate = null;
        aircraft.IndicatedAirspeed = 0;
        aircraft.Targets.TargetSpeed = 0;
    }

    /// <summary>
    /// Where a tow braked to rest from now stops: the live pose advanced along the direction of travel
    /// (<see cref="AircraftGroundOps.PushbackTrueHeading"/> on a push, the nose on a pull) by <c>v² / 2a</c>, <c>v</c> the
    /// ground speed and <c>a</c> the type's <see cref="CategoryPerformance.TugDecelRate"/>; the live pose itself at rest or
    /// with no tug attached (nothing brakes it at the towbar rate then). The nose does not turn while the tug brakes.
    /// </summary>
    /// <param name="aircraft">The aircraft under tow.</param>
    /// <returns>The predicted rest pose.</returns>
    public static PushbackPose PredictedStopPose(AircraftState aircraft)
    {
        var pose = new PushbackPose(aircraft.Position, aircraft.TrueHeading.Degrees);
        double speedKts = aircraft.GroundSpeed;
        if ((speedKts <= AtRestKts) || (aircraft.Ground.TowbarTrueHeading is null))
        {
            return pose;
        }

        double decelKtPerSec = CategoryPerformance.TugDecelRate(AircraftCategorization.Categorize(aircraft.AircraftType));
        double distanceFt = speedKts * speedKts / (2.0 * decelKtPerSec) * FtPerSecPerKt;
        TrueHeading travel = aircraft.Ground.PushbackTrueHeading ?? aircraft.TrueHeading;
        return pose with { Position = GeoMath.ProjectPoint(aircraft.Position, travel, distanceFt / GeoMath.FeetPerNm) };
    }

    /// <summary>
    /// Publishes the towbar rates and a zero target, every tick: physics nulls a zero target once the speed reaches it,
    /// and nothing a command left behind may brake the tow at the aircraft's own rate.
    /// </summary>
    private static void Brake(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.DesiredAccelRate = CategoryPerformance.TugAccelRate(ctx.Category);
        ctx.Targets.DesiredDecelRate = CategoryPerformance.TugDecelRate(ctx.Category);
        ctx.Targets.TargetSpeed = 0;
    }

    /// <summary>
    /// The release answers the commands that clear or hold a tow; any other is judged by <see cref="Next"/>, the phase
    /// the aircraft is about to start, so a taxi's follow-ups are taken during the brake.
    /// </summary>
    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd) => OwnAcceptance(cmd) ?? NextAcceptance(cmd);

    private CommandAcceptance NextAcceptance(CanonicalCommandType cmd) =>
        Next?.CanAcceptCommand(cmd)
        ?? CommandAcceptance.Rejected("the tug is stopping; only HOLD, RES, PUSHM, TAXI, TAXIAUTO, RTAXI, ATXI, LAND or DEL apply");

    private static CommandAcceptance? OwnAcceptance(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto or CanonicalCommandType.ReRouteTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.Pushback or CanonicalCommandType.ForcedPushback => CommandAcceptance.Allowed,
            CanonicalCommandType.PushbackMulti or CanonicalCommandType.ForcedPushbackMulti => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => null,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new TugReleasePhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            Kind = Kind,
        };

    public static TugReleasePhase FromSnapshot(TugReleasePhaseDto dto)
    {
        var phase = new TugReleasePhase
        {
            Kind = dto.Kind,
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
        };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
