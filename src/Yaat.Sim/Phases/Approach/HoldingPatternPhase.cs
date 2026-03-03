using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Approach;

/// <summary>
/// AIM 5-3-8 holding pattern with inbound course, turn direction, leg timing,
/// and automatic entry determination (Direct, Teardrop, Parallel).
/// State machine: Navigate → Entry → Outbound → TurnToInbound → Inbound → TurnToOutbound → repeat.
/// Never self-completes — any RPO command exits via ClearsPhase.
/// </summary>
public sealed class HoldingPatternPhase : Phase
{
    private const double ArrivalNm = 0.5;
    private const double HeadingToleranceDeg = 5.0;
    private const double TeardropOffsetDeg = 30.0;

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
    private double _outboundHeading;
    private double _legTimerSeconds;
    private int _circuitsCompleted;

    public override string Name => "HoldingPattern";

    public override void OnStart(PhaseContext ctx)
    {
        _outboundHeading = (InboundCourse + 180) % 360;

        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = FixName,
                Latitude = FixLat,
                Longitude = FixLon,
            }
        );

        _entry = Entry ?? HoldingEntryCalculator.ComputeEntry(ctx.Aircraft.Heading, InboundCourse, Direction);
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
        if (IsHeadingClose(ctx.Aircraft.Heading, _outboundHeading))
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
        if (IsHeadingClose(ctx.Aircraft.Heading, InboundCourse))
        {
            StartInbound(ctx);
        }
    }

    private bool TickInbound(PhaseContext ctx)
    {
        if (AtFix(ctx))
        {
            _circuitsCompleted++;
            if (MaxCircuits is { } max && _circuitsCompleted >= max)
            {
                ctx.Targets.NavigationRoute.Clear();
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
        double teardropHeading = ((_outboundHeading + offset) % 360 + 360) % 360;

        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.TargetHeading = teardropHeading;
        ctx.Targets.PreferredTurnDirection = null;
    }

    private void StartParallelOutbound(PhaseContext ctx)
    {
        _state = HoldState.EntryOutbound;
        _legTimerSeconds = GetLegTimerSeconds(ctx);

        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.TargetHeading = _outboundHeading;
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
        ctx.Targets.TargetHeading = _outboundHeading;
        ctx.Targets.PreferredTurnDirection = Direction;
    }

    private void StartOutbound(PhaseContext ctx)
    {
        _state = HoldState.Outbound;
        _legTimerSeconds = GetLegTimerSeconds(ctx);
        ctx.Targets.TargetHeading = _outboundHeading;
        ctx.Targets.PreferredTurnDirection = null;
    }

    private void StartTurnToInbound(PhaseContext ctx)
    {
        _state = HoldState.TurnToInbound;
        ctx.Targets.TargetHeading = InboundCourse;
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

    private double GetLegTimerSeconds(PhaseContext ctx)
    {
        if (IsMinuteBased)
        {
            return LegLength * 60.0;
        }

        // Distance-based: estimate time from current speed
        double speedNmPerSec = ctx.Aircraft.GroundSpeed / 3600.0;
        return speedNmPerSec > 0 ? LegLength / speedNmPerSec : 60.0;
    }

    private bool AtFix(PhaseContext ctx)
    {
        return GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, FixLat, FixLon) < ArrivalNm;
    }

    private static void DecelerateToHoldingSpeed(PhaseContext ctx)
    {
        double maxHold = CategoryPerformance.MaxHoldingSpeed(ctx.Aircraft.Altitude);
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
        return CommandAcceptance.ClearsPhase;
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
