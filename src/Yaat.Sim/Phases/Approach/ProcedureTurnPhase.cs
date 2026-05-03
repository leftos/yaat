using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Approach;

/// <summary>
/// AIM 5-4-9 procedure turn course reversal anchored at a published fix.
///
/// Six-state machine:
///   1. NavigateToFix    — fly direct to anchor fix.
///   2. Outbound         — after crossing fix, fly outbound radial (FAC + 180°).
///   3. TurnToPtOutbound — turn to the published 45°-offset PT heading.
///   4. PtOutbound       — fly the 45° leg until distance / time / altitude gate met.
///   5. TurnToInbound    — 180° turn in OneEightyTurnDirection back toward FAC.
///   6. InterceptInbound — fly heading toward fix; complete when established on FAC.
///
/// Constructed from a CIFP PI leg in <see cref="ApproachCommandHandler"/>.
/// </summary>
public sealed class ProcedureTurnPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("ProcedureTurnPhase");

    private const double ArrivalNm = 0.5;
    private const double HeadingToleranceDeg = 5.0;
    private const double InterceptToleranceDeg = 5.0;
    private const double DefaultPtOutboundSeconds = 60.0;
    private const double MinOutboundSeparationNm = 1.0;

    // AIM 5-4-9 max PT IAS (200 KIAS — protects PT-area airspace).
    private const double MaxPtIasKts = 200.0;

    // Reserve nm to start the 180° turn before the published distance cap so the
    // turn radius itself stays inside protected airspace.
    private const double TurnRadiusReserveNm = 2.0;

    // Aircraft must be within this lateral distance of the inbound course before
    // handing off to FinalApproachPhase.
    private const double InterceptLateralToleranceNm = 1.0;

    public required string FixName { get; init; }
    public required double FixLat { get; init; }
    public required double FixLon { get; init; }

    /// <summary>Final approach course (true degrees, inbound to fix). Aircraft re-establishes here at end.</summary>
    public required double InboundCourseDeg { get; init; }

    /// <summary>Published 45°-offset PT outbound heading (true degrees) — the heading flown on the 45° leg.</summary>
    public required double PtOutboundCourseDeg { get; init; }

    /// <summary>Maximum distance from fix at which to begin the 180° turn (CIFP LegDistanceNm; typically 10 nm).</summary>
    public required double MaxOutboundDistanceNm { get; init; }

    /// <summary>Direction of the 180° turn back to inbound (CIFP TurnDirection on the PI leg).</summary>
    public required TurnDirection OneEightyTurnDirection { get; init; }

    /// <summary>Minimum altitude during the PT (typically the AtOrAbove constraint on the PI leg).</summary>
    public required int MinAltitudeFt { get; init; }

    private PtState _state = PtState.NavigateToFix;
    private double _ptOutboundTimerSeconds;

    public override string Name => "ProcedureTurn";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.NavigationRoute.Add(new NavigationTarget { Name = FixName, Position = new LatLon(FixLat, FixLon) });
        ClampPtSpeed(ctx);
        Log.LogDebug(
            "[ProcedureTurn] {Callsign}: navigating to {Fix} (inbound={Crs:000}T, ptOut={Pt:000}T, 180°{Dir}, ≥{Alt}ft)",
            ctx.Aircraft.Callsign,
            FixName,
            InboundCourseDeg,
            PtOutboundCourseDeg,
            OneEightyTurnDirection,
            MinAltitudeFt
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        switch (_state)
        {
            case PtState.NavigateToFix:
                TickNavigateToFix(ctx);
                break;
            case PtState.Outbound:
                TickOutbound(ctx);
                break;
            case PtState.TurnToPtOutbound:
                TickTurnToPtOutbound(ctx);
                break;
            case PtState.PtOutbound:
                TickPtOutbound(ctx);
                break;
            case PtState.TurnToInbound:
                TickTurnToInbound(ctx);
                break;
            case PtState.InterceptInbound:
                if (TickInterceptInbound(ctx))
                {
                    return true;
                }
                break;
        }

        return false;
    }

    private void TickNavigateToFix(PhaseContext ctx)
    {
        if (GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(FixLat, FixLon)) > ArrivalNm)
        {
            return;
        }

        EnsureMinimumAltitudeTarget(ctx);
        ctx.Targets.NavigationRoute.Clear();

        var outboundHeading = new TrueHeading(InboundCourseDeg + 180.0);
        ctx.Targets.TargetTrueHeading = outboundHeading;
        ctx.Targets.PreferredTurnDirection = null;
        _state = PtState.Outbound;

        Log.LogDebug(
            "[ProcedureTurn] {Callsign}: at {Fix}, flying outbound heading {Hdg:000}T",
            ctx.Aircraft.Callsign,
            FixName,
            outboundHeading.Degrees
        );
    }

    private void TickOutbound(PhaseContext ctx)
    {
        EnsureMinimumAltitudeTarget(ctx);
        ClampPtSpeed(ctx);

        double distFromFix = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(FixLat, FixLon));

        // Distance cap during the radial outbound leg too — if a slow turn or wide arc
        // would push us past the published PT distance before we ever reach the 45° leg,
        // start the inbound turn now so the entire maneuver stays within protected airspace.
        if (distFromFix >= MaxOutboundDistanceNm - TurnRadiusReserveNm)
        {
            BeginTurnToInbound(ctx, distFromFix, "distance cap on outbound radial");
            return;
        }

        var outboundHeading = new TrueHeading(InboundCourseDeg + 180.0);
        bool established = ctx.Aircraft.TrueHeading.IsCloseTo(outboundHeading, HeadingToleranceDeg);
        bool clearOfFix = distFromFix >= MinOutboundSeparationNm;
        if (!established || !clearOfFix)
        {
            return;
        }

        ctx.Targets.TargetTrueHeading = new TrueHeading(PtOutboundCourseDeg);
        ctx.Targets.PreferredTurnDirection = OneEightyTurnDirection == TurnDirection.Left ? TurnDirection.Right : TurnDirection.Left;
        _state = PtState.TurnToPtOutbound;

        Log.LogDebug(
            "[ProcedureTurn] {Callsign}: outbound established, turning to PT heading {Hdg:000}T",
            ctx.Aircraft.Callsign,
            PtOutboundCourseDeg
        );
    }

    private void TickTurnToPtOutbound(PhaseContext ctx)
    {
        EnsureMinimumAltitudeTarget(ctx);
        ClampPtSpeed(ctx);

        if (!ctx.Aircraft.TrueHeading.IsCloseTo(new TrueHeading(PtOutboundCourseDeg), HeadingToleranceDeg))
        {
            return;
        }

        ctx.Targets.PreferredTurnDirection = null;
        _ptOutboundTimerSeconds = DefaultPtOutboundSeconds;
        _state = PtState.PtOutbound;

        Log.LogDebug("[ProcedureTurn] {Callsign}: PT outbound established, holding for {Sec}s", ctx.Aircraft.Callsign, _ptOutboundTimerSeconds);
    }

    private void TickPtOutbound(PhaseContext ctx)
    {
        EnsureMinimumAltitudeTarget(ctx);
        ClampPtSpeed(ctx);

        _ptOutboundTimerSeconds -= ctx.DeltaSeconds;
        double distFromFix = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(FixLat, FixLon));
        bool altitudeMet = ctx.Aircraft.Altitude >= MinAltitudeFt - 100;
        bool timerDone = _ptOutboundTimerSeconds <= 0;

        // Distance cap is the hard limit per AIM 5-4-9.a.3 — turn back early enough that
        // the 180° turn radius still finishes inside MaxOutboundDistanceNm of the fix.
        bool distanceCap = distFromFix >= MaxOutboundDistanceNm - TurnRadiusReserveNm;

        if (distanceCap)
        {
            BeginTurnToInbound(ctx, distFromFix, "distance cap on PT outbound");
            return;
        }

        // Continue outbound if altitude still being chased.
        if (!altitudeMet)
        {
            return;
        }

        if (!timerDone)
        {
            return;
        }

        BeginTurnToInbound(ctx, distFromFix, "PT outbound timer expired");
    }

    private void BeginTurnToInbound(PhaseContext ctx, double distFromFix, string reason)
    {
        ctx.Targets.TargetTrueHeading = new TrueHeading(InboundCourseDeg);
        ctx.Targets.PreferredTurnDirection = OneEightyTurnDirection;
        _state = PtState.TurnToInbound;

        Log.LogDebug(
            "[ProcedureTurn] {Callsign}: starting 180° {Dir} turn back to inbound {Crs:000}T ({Reason}, dist={Dist:F1}nm, alt={Alt:F0})",
            ctx.Aircraft.Callsign,
            OneEightyTurnDirection,
            InboundCourseDeg,
            reason,
            distFromFix,
            ctx.Aircraft.Altitude
        );
    }

    private void TickTurnToInbound(PhaseContext ctx)
    {
        EnsureMinimumAltitudeTarget(ctx);
        ClampPtSpeed(ctx);

        if (!ctx.Aircraft.TrueHeading.IsCloseTo(new TrueHeading(InboundCourseDeg), HeadingToleranceDeg + 5))
        {
            return;
        }

        ctx.Targets.PreferredTurnDirection = null;
        _state = PtState.InterceptInbound;

        Log.LogDebug("[ProcedureTurn] {Callsign}: turn complete, intercepting inbound", ctx.Aircraft.Callsign);
    }

    private bool TickInterceptInbound(PhaseContext ctx)
    {
        EnsureMinimumAltitudeTarget(ctx);
        ClampPtSpeed(ctx);

        bool onCourse = ctx.Aircraft.TrueHeading.IsCloseTo(new TrueHeading(InboundCourseDeg), InterceptToleranceDeg + 10);
        if (!onCourse)
        {
            return false;
        }

        // Lateral intercept gate: don't hand off to FinalApproach until the aircraft is
        // also laterally on the FAC. Heading-only would let a 5° heading match with a
        // 2 nm cross-track error pass FinalApproach an off-course aircraft.
        double crossTrackNm = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(FixLat, FixLon), new TrueHeading(InboundCourseDeg))
        );
        if (crossTrackNm > InterceptLateralToleranceNm)
        {
            return false;
        }

        // Hand off to the next phase (ApproachNavigation / FinalApproach) — clear our nav.
        ctx.Targets.NavigationRoute.Clear();
        Log.LogDebug("[ProcedureTurn] {Callsign}: established inbound (xtrack={XT:F2}nm), exiting", ctx.Aircraft.Callsign, crossTrackNm);
        return true;
    }

    private void EnsureMinimumAltitudeTarget(PhaseContext ctx)
    {
        if (ctx.Targets.TargetAltitude is null || ctx.Targets.TargetAltitude < MinAltitudeFt)
        {
            ctx.Targets.TargetAltitude = MinAltitudeFt;
        }
    }

    /// <summary>
    /// AIM 5-4-9.a.3 caps PT IAS at 200 KIAS to keep the maneuver inside protected airspace.
    /// Clamp <see cref="ControlTargets.SpeedCeiling"/> for the duration of the phase.
    /// </summary>
    private static void ClampPtSpeed(PhaseContext ctx)
    {
        if (ctx.Targets.SpeedCeiling is null || ctx.Targets.SpeedCeiling > MaxPtIasKts)
        {
            ctx.Targets.SpeedCeiling = MaxPtIasKts;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClimbMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.DescendMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.Speed => CommandAcceptance.Allowed,
            CanonicalCommandType.Mach => CommandAcceptance.Allowed,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new ProcedureTurnPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            FixName = FixName,
            FixLat = FixLat,
            FixLon = FixLon,
            InboundCourseDeg = InboundCourseDeg,
            PtOutboundCourseDeg = PtOutboundCourseDeg,
            MaxOutboundDistanceNm = MaxOutboundDistanceNm,
            OneEightyTurnDirection = (int)OneEightyTurnDirection,
            MinAltitudeFt = MinAltitudeFt,
            State = (int)_state,
            PtOutboundTimerSeconds = _ptOutboundTimerSeconds,
        };

    public static ProcedureTurnPhase FromSnapshot(ProcedureTurnPhaseDto dto)
    {
        var phase = new ProcedureTurnPhase
        {
            FixName = dto.FixName,
            FixLat = dto.FixLat,
            FixLon = dto.FixLon,
            InboundCourseDeg = dto.InboundCourseDeg,
            PtOutboundCourseDeg = dto.PtOutboundCourseDeg,
            MaxOutboundDistanceNm = dto.MaxOutboundDistanceNm,
            OneEightyTurnDirection = (TurnDirection)dto.OneEightyTurnDirection,
            MinAltitudeFt = dto.MinAltitudeFt,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase._state = (PtState)dto.State;
        phase._ptOutboundTimerSeconds = dto.PtOutboundTimerSeconds;
        return phase;
    }

    private enum PtState
    {
        NavigateToFix,
        Outbound,
        TurnToPtOutbound,
        PtOutbound,
        TurnToInbound,
        InterceptInbound,
    }
}
