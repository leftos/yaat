using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Approach;

/// <summary>
/// AIM 5-3-8 holding pattern with inbound course, turn direction, leg timing,
/// and automatic entry determination (Direct, Teardrop, Parallel).
/// State machine: Navigate → Entry → Outbound → TurnToInbound → Inbound → TurnToOutbound → repeat.
/// Never self-completes — most RPO commands exit via ClearsPhase; CM/DM/Speed are allowed without exiting.
/// </summary>
public sealed class HoldingPatternPhase : Phase
{
    private const double ArrivalNm = 0.5;
    private const double HeadingToleranceDeg = 5.0;
    private const double TeardropOffsetDeg = 30.0;

    // AIM 5-3-8(j)(8)(c): when outbound, triple the inbound drift correction.
    private const double TripleDriftFactor = 3.0;
    private const double MinOutboundSeconds = 20.0;
    private const double MaxOutboundSeconds = 300.0;

    public required string FixName { get; init; }
    public required double FixLat { get; init; }
    public required double FixLon { get; init; }
    public required int InboundCourse { get; init; }
    public required double LegLength { get; init; }
    public required bool IsMinuteBased { get; init; }
    public required TurnDirection Direction { get; init; }
    public HoldingEntry? Entry { get; init; }

    /// <summary>
    /// When set, the phase self-completes after this many circuits.
    /// Used for hold-in-lieu of procedure turn (1 circuit).
    /// When null, the hold continues indefinitely (standard behavior).
    /// </summary>
    public int? MaxCircuits { get; init; }

    private HoldState _state = HoldState.NavigatingToFix;
    private HoldingEntry _entry;
    private TrueHeading _outboundHeading;
    private TrueHeading _correctedOutboundHeading;
    private double _legTimerSeconds;
    private int _circuitsCompleted;

    public override string Name => "HoldingPattern";

    public override void OnStart(PhaseContext ctx)
    {
        _outboundHeading = new TrueHeading(InboundCourse + 180);

        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = FixName,
                Latitude = FixLat,
                Longitude = FixLon,
            }
        );

        _entry = Entry ?? HoldingEntryCalculator.ComputeEntry(ctx.Aircraft.TrueHeading, InboundCourse, Direction);

        ctx.Logger.LogDebug(
            "[HoldingPattern] {Callsign}: started at {Fix}, inbound={Crs:000}, {Dir}, entry={Entry}, maxCircuits={Max}",
            ctx.Aircraft.Callsign,
            FixName,
            InboundCourse,
            Direction,
            _entry,
            MaxCircuits?.ToString() ?? "unlimited"
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        switch (_state)
        {
            case HoldState.NavigatingToFix:
                TickNavigatingToFix(ctx);
                break;
            case HoldState.EntryOutbound:
                TickEntryOutbound(ctx);
                break;
            case HoldState.EntryReturn:
                TickEntryReturn(ctx);
                break;
            case HoldState.TurnToOutbound:
                TickTurnToOutbound(ctx);
                break;
            case HoldState.Outbound:
                TickOutbound(ctx);
                break;
            case HoldState.TurnToInbound:
                TickTurnToInbound(ctx);
                break;
            case HoldState.Inbound:
                if (TickInbound(ctx))
                {
                    return true;
                }
                break;
        }

        return false;
    }

    private void TickNavigatingToFix(PhaseContext ctx)
    {
        if (!AtFix(ctx))
        {
            return;
        }

        DecelerateToHoldingSpeed(ctx);
        ctx.Targets.NavigationRoute.Clear();
        ctx.Logger.LogDebug("[HoldingPattern] {Callsign}: at fix, entering via {Entry}", ctx.Aircraft.Callsign, _entry);

        switch (_entry)
        {
            case HoldingEntry.Direct:
                StartTurnToOutbound(ctx);
                break;
            case HoldingEntry.Teardrop:
                StartTeardropOutbound(ctx);
                break;
            case HoldingEntry.Parallel:
                StartParallelOutbound(ctx);
                break;
        }
    }

    private void TickEntryOutbound(PhaseContext ctx)
    {
        _legTimerSeconds -= ctx.DeltaSeconds;
        if (_legTimerSeconds <= 0)
        {
            StartEntryReturn(ctx);
        }
    }

    private void TickEntryReturn(PhaseContext ctx)
    {
        if (AtFix(ctx))
        {
            ctx.Targets.NavigationRoute.Clear();
            StartTurnToOutbound(ctx);
        }
    }

    private void TickTurnToOutbound(PhaseContext ctx)
    {
        if (IsHeadingClose(ctx.Aircraft.TrueHeading.Degrees, _correctedOutboundHeading.Degrees))
        {
            StartOutbound(ctx);
        }
    }

    private void TickOutbound(PhaseContext ctx)
    {
        if (IsMinuteBased)
        {
            _legTimerSeconds -= ctx.DeltaSeconds;
            if (_legTimerSeconds <= 0)
            {
                StartTurnToInbound(ctx);
            }
        }
        else
        {
            double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, FixLat, FixLon);
            if (dist >= LegLength)
            {
                StartTurnToInbound(ctx);
            }
        }
    }

    private void TickTurnToInbound(PhaseContext ctx)
    {
        if (IsHeadingClose(ctx.Aircraft.TrueHeading.Degrees, InboundCourse))
        {
            StartInbound(ctx);
        }
    }

    private bool TickInbound(PhaseContext ctx)
    {
        if (AtFix(ctx))
        {
            _circuitsCompleted++;
            ctx.Logger.LogDebug("[HoldingPattern] {Callsign}: circuit {N} complete", ctx.Aircraft.Callsign, _circuitsCompleted);

            if (MaxCircuits is { } max && _circuitsCompleted >= max)
            {
                ctx.Targets.NavigationRoute.Clear();
                ctx.Logger.LogDebug("[HoldingPattern] {Callsign}: max circuits reached, exiting", ctx.Aircraft.Callsign);
                return true;
            }

            ctx.Targets.NavigationRoute.Clear();
            StartTurnToOutbound(ctx);
        }

        return false;
    }

    private void StartTeardropOutbound(PhaseContext ctx)
    {
        _state = HoldState.EntryOutbound;
        _legTimerSeconds = GetLegTimerSeconds(ctx);

        double offset = Direction == TurnDirection.Right ? -TeardropOffsetDeg : TeardropOffsetDeg;
        TrueHeading teardropHeading = new TrueHeading(_outboundHeading.Degrees + offset);

        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.TargetTrueHeading = teardropHeading;
        ctx.Targets.PreferredTurnDirection = null;
    }

    private void StartParallelOutbound(PhaseContext ctx)
    {
        _state = HoldState.EntryOutbound;
        _legTimerSeconds = GetLegTimerSeconds(ctx);

        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.TargetTrueHeading = _outboundHeading;
        ctx.Targets.PreferredTurnDirection = null;
    }

    private void StartEntryReturn(PhaseContext ctx)
    {
        _state = HoldState.EntryReturn;

        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = FixName,
                Latitude = FixLat,
                Longitude = FixLon,
            }
        );

        if (_entry == HoldingEntry.Parallel)
        {
            ctx.Targets.PreferredTurnDirection = Direction;
        }
    }

    private void StartTurnToOutbound(PhaseContext ctx)
    {
        _state = HoldState.TurnToOutbound;
        _correctedOutboundHeading = ComputeOutboundHeading(ctx);
        ctx.Targets.TargetTrueHeading = _correctedOutboundHeading;
        ctx.Targets.PreferredTurnDirection = Direction;
    }

    private void StartOutbound(PhaseContext ctx)
    {
        _state = HoldState.Outbound;
        _legTimerSeconds = ComputeOutboundSeconds(ctx);
        ctx.Targets.TargetTrueHeading = _correctedOutboundHeading;
        ctx.Targets.PreferredTurnDirection = null;
    }

    private void StartTurnToInbound(PhaseContext ctx)
    {
        _state = HoldState.TurnToInbound;
        ctx.Targets.TargetTrueHeading = new TrueHeading(InboundCourse);
        ctx.Targets.PreferredTurnDirection = Direction;
    }

    private void StartInbound(PhaseContext ctx)
    {
        _state = HoldState.Inbound;

        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = FixName,
                Latitude = FixLat,
                Longitude = FixLon,
            }
        );
    }

    /// <summary>Entry leg timer (teardrop / parallel): nominal leg length, no wind adjustment.</summary>
    private double GetLegTimerSeconds(PhaseContext ctx)
    {
        if (IsMinuteBased)
        {
            return LegLength * 60.0;
        }

        // Distance-based: estimate time from current speed.
        double speedNmPerSec = ctx.Aircraft.GroundSpeed / 3600.0;
        return speedNmPerSec > 0 ? LegLength / speedNmPerSec : 60.0;
    }

    /// <summary>
    /// Standard outbound leg timer with wind compensation (AIM 5-3-8).
    /// Predictive: computes the outbound time needed so that the resulting inbound ground distance
    /// equals the target inbound duration at current inbound groundspeed.
    /// Distance-based holds are unchanged (distance determines the outbound endpoint).
    /// </summary>
    private double ComputeOutboundSeconds(PhaseContext ctx)
    {
        if (!IsMinuteBased)
        {
            double speedNmPerSec = ctx.Aircraft.GroundSpeed / 3600.0;
            return speedNmPerSec > 0 ? LegLength / speedNmPerSec : 60.0;
        }

        double targetInboundSeconds = LegLength * 60.0;

        if (ctx.Weather is null)
        {
            return targetInboundSeconds;
        }

        double tas = WindInterpolator.IasToTas(ctx.Aircraft.IndicatedAirspeed, ctx.Aircraft.Altitude);
        if (tas <= 0)
        {
            return targetInboundSeconds;
        }

        var wind = WindInterpolator.GetWindAt(ctx.Weather, ctx.Aircraft.Altitude);
        if (wind.SpeedKts <= 0)
        {
            return targetInboundSeconds;
        }

        // Positive headwind = wind opposing inbound direction = reduces inbound groundspeed.
        const double DegToRad = Math.PI / 180.0;
        double headwindInbound = wind.SpeedKts * Math.Cos((wind.DirectionDeg - InboundCourse) * DegToRad);
        double gsInbound = Math.Max(tas - headwindInbound, 1.0);

        // Outbound course is the reciprocal: what was a headwind inbound is a tailwind outbound.
        double gsOutbound = Math.Max(tas + headwindInbound, 1.0);

        // Distance covered at inbound groundspeed during the target inbound time.
        double inboundDistanceNm = gsInbound * (LegLength / 60.0);

        // Time to cover that same distance at outbound groundspeed.
        double outboundSeconds = (inboundDistanceNm / gsOutbound) * 3600.0;

        return Math.Clamp(outboundSeconds, MinOutboundSeconds, MaxOutboundSeconds);
    }

    /// <summary>
    /// Computes the outbound heading with AIM 5-3-8(j)(8)(c) triple-drift correction.
    /// Applies 3× the inbound WCA in the opposite sense to pre-compensate for crosswind
    /// on the outbound leg, so the inbound track stays close to the inbound course.
    /// </summary>
    private TrueHeading ComputeOutboundHeading(PhaseContext ctx)
    {
        if (ctx.Weather is null)
        {
            return _outboundHeading;
        }

        double tas = WindInterpolator.IasToTas(ctx.Aircraft.IndicatedAirspeed, ctx.Aircraft.Altitude);
        var wind = WindInterpolator.GetWindAt(ctx.Weather, ctx.Aircraft.Altitude);

        // inboundWca > 0 means crab right to maintain inbound track.
        // Triple-drift outbound: subtract 3× WCA from the outbound heading
        // (correcting in the opposite sense, tripled — AIM 5-3-8(j)(8)(c)).
        double inboundWca = WindInterpolator.ComputeWindCorrectionAngle(InboundCourse, tas, wind.DirectionDeg, wind.SpeedKts);
        return new TrueHeading(_outboundHeading.Degrees - (TripleDriftFactor * inboundWca));
    }

    private bool AtFix(PhaseContext ctx)
    {
        return GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, FixLat, FixLon) < ArrivalNm;
    }

    private static void DecelerateToHoldingSpeed(PhaseContext ctx)
    {
        double maxHold = AircraftPerformance.HoldingSpeed(ctx.AircraftType, ctx.Aircraft.Altitude);
        if (ctx.Targets.TargetSpeed is null || ctx.Targets.TargetSpeed > maxHold)
        {
            ctx.Targets.TargetSpeed = maxHold;
        }
    }

    private static bool IsHeadingClose(double current, double target)
    {
        double diff = ((current - target) % 360 + 360) % 360;
        if (diff > 180)
        {
            diff = 360 - diff;
        }

        return diff < HeadingToleranceDeg;
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

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }

    private enum HoldState
    {
        NavigatingToFix,
        EntryOutbound,
        EntryReturn,
        TurnToOutbound,
        Outbound,
        TurnToInbound,
        Inbound,
    }
}
