using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft crosses a runway at normal taxi speed (runway-crossing speed kept only
/// as a no-stop floor) by following the taxi line via a <see cref="GroundNavigator"/>
/// over the crossing slice of its source route (own path, else AssignedTaxiRoute). Each tick steers
/// via the navigator (which respects arcs, fillets and intermediate runway-centerline
/// nodes that the painted line traverses, and slows for any curve via its arc-speed
/// cap), then completes ½ aircraft length past the exit-side hold-short, following
/// the route's own onward segments to get there so the tail clears the runway without
/// leaving the painted line. Crossing without delay (7110.65 §3-7-2.j, "CROSS
/// (runway) … WITHOUT DELAY"; AIM 4-3-21.a) minimizes runway occupancy, so the aircraft
/// does not slow below its taxi speed for a straight crossing.
///
/// Earlier versions used a single straight-line beeline from the entry-side
/// hold-short to the exit-side hold-short, which cut diagonally across the
/// runway surface whenever the taxiway crossed via a fillet arc rather than
/// an exactly perpendicular straight line (e.g. SFO H crossing 01L/19R —
/// see GitHub issue #166).
/// </summary>
public sealed class CrossingRunwayPhase(int approachNodeId, int targetNodeId, string? runwayId) : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("CrossingRunwayPhase");

    private const double LogIntervalSeconds = 3.0;

    /// <summary>The largest turn (degrees) the layout tail-clearance walk takes from the segment it came along.</summary>
    private const double MaxTailWalkTurnDeg = 45.0;

    private readonly int _approachNodeId = approachNodeId;
    private readonly int _targetNodeId = targetNodeId;
    private readonly string? _runwayId = runwayId;

    // A crossing path handed to the phase instead of read from the aircraft's AssignedTaxiRoute (see OverOwnPath).
    // Null for the ordinary crossing, which slices the aircraft's own route.
    private TaxiRoute? _ownPath;

    // Built lazily in OnStart (or first OnTick after snapshot restore) by
    // slicing the source route (the own path, else AssignedTaxiRoute) between approach and target.
    private TaxiRoute? _crossingRoute;
    private GroundNavigator? _navigator;
    private bool _initialized;
    private double _timeSinceLastLog;

    // Where the slice sits in the source route (own path, else AssignedTaxiRoute), so the crossing can hand the
    // route back at the segment the aircraft is standing on. All three are recomputed by TryBuildCrossingRoute
    // from the restored source route, so none of them is snapshotted (see FromSnapshot).
    private int _exitRouteIndex = -1;
    private int _tailClearFullSegments;
    private bool _tailClearPartial;

    // The tail decision the slice was built from. Unlike the fields above it reads which holds the route had cleared at
    // build, so it is snapshotted, and a restore builds the slice from the stored decision (_restoredTailDecision) rather
    // than deciding again from holds cleared since. Null before the slice is built.
    private TailDecision? _tailDecision;
    private TailDecision? _restoredTailDecision;
    private int? _restoredHeldShortBarId;

    // The slice's own geometry the stop cap measures from: how far along the source route the slice ends, and whether it ends
    // the route. A pure function of the slice, recomputed by TryBuildCrossingRoute like the fields above, so not snapshotted.
    // The stop past the slice is searched afresh every tick (ApplyStopCap), never kept from the build.
    private double _sliceEndRouteFt;
    private bool _sliceEndsRoute;

    // Whether this crossing has logged its stop cap (StopCap) taking over from the speed floor.
    private bool _loggedStopCap;

    // For a crossing that ends at rest, the other runway's hold-short bar that set where the slice ends (the route stop's bar,
    // EndingAtRestStop, or the layout walk's bar cut), recorded when the slice is planned; null when no such bar set it. The route's
    // bar reads which holds the route had cleared, so the bar is snapshotted with the tail decision, and a restore that applies that
    // decision applies the bar with it (_restoredHeldShortBarId) rather than deriving it again.
    private GroundNode? _heldShortBar;

    // The pose the navigator set up the current slice segment at. The navigator builds the segment's primitive from the
    // pose it is set up at (an entry-alignment turn starts at the aircraft), so a restore sets the segment up again from
    // this pose rather than from where the aircraft stands at restore time. Snapshotted with the slice segment index.
    private (LatLon Position, TrueHeading Heading)? _segmentSetupPose;

    // Set by FromSnapshot and consumed (reset to null) by the first slice build after a restore.
    private int? _restoredSegmentIndex;
    private (LatLon Position, TrueHeading Heading)? _restoredSetupPose;
    private GroundNavigatorDto? _restoredNavigator;

    // The not-clear call (TickNotClearCall): how long the aircraft has been at rest, and whether the call was made this crossing.
    // Both snapshotted.
    private double _notClearRestSeconds;
    private bool _saidNotClear;

    // Set by a HOLD that breaks off the follow behind this crossing (EndAtRest): the crossing brakes to rest past its slice end
    // rather than hand over moving. Snapshotted.
    private bool _endsAtRest;

    public override string Name => "Crossing Runway";

    public string? RunwayId => _runwayId;

    /// <summary>The entry-side (near) bar node the crossing starts from.</summary>
    public int ApproachNodeId => _approachNodeId;

    /// <summary>The exit-side (far) bar node the crossing drives to.</summary>
    public int TargetNodeId => _targetNodeId;

    /// <summary>The path a crossing over its own path slices (<see cref="OverOwnPath"/>); null for a crossing of the aircraft's route.</summary>
    internal TaxiRoute? OwnPath => _ownPath;

    /// <summary>The crossing slice the navigator is playing back (entry bar → exit bar → tail-clearance). Null until built.</summary>
    internal TaxiRoute? CrossingRoute => _crossingRoute;

    /// <summary>
    /// How far (ft) an aircraft at <paramref name="position"/> still has to go along the crossing slice to where the crossing
    /// completes, its tail past the far holding-position marking (the slice's last node, <see cref="TaxiRoute.IsComplete"/>);
    /// measured as <see cref="TaxiRoute.RemainingDistanceFt"/> measures it. Zero once the slice is complete; null before it is built.
    /// </summary>
    public double? TailClearRemainingFt(LatLon position) =>
        _crossingRoute switch
        {
            null => null,
            { IsComplete: true } => 0.0,
            { } route => route.RemainingDistanceFt(position, route.Segments.Count),
        };

    /// <summary>The navigator's straight-segment speed cap (kts), or null before the crossing slice is built.</summary>
    public double? NavMaxSpeedKts => _navigator?.MaxSpeedKts;

    /// <summary>
    /// True when this crossing was released or pre-cleared "without delay" (<c>CROSS … EXP</c>, 7110.65 §3-7-2.j). Set at
    /// construction from the crossing's hold-short (<see cref="HoldShortPoint.IsExpedited"/>), and again when the phase starts,
    /// because a crossing is built when the aircraft arrives at the bar, before the <c>CROSS</c> that releases it. Snapshotted,
    /// so a restored crossing keeps the value it had rather than reading the route again.
    /// </summary>
    public bool IsExpedited
    {
        get => _expedited;
        init => _expedited = value;
    }

    private bool _expedited;

    /// <summary>
    /// A crossing that drives <paramref name="path"/> — a route from the entry-side bar across the runway to the
    /// exit-side one — rather than the aircraft's assigned taxi route, which it leaves untouched: neither read, nor
    /// replaced, nor its cursor moved. For a crossing whose onward movement is not the taxi route (a follow armed
    /// at the bar), where the route must keep describing the clearance the aircraft taxis under.
    /// </summary>
    public static CrossingRunwayPhase OverOwnPath(int approachNodeId, int targetNodeId, string? runwayId, TaxiRoute path, bool expedited) =>
        new(approachNodeId, targetNodeId, runwayId) { _ownPath = path, _expedited = expedited };

    /// <summary>The route the crossing slices: the path it was handed, else the aircraft's assigned taxi route.</summary>
    private TaxiRoute? SourceRoute(PhaseContext ctx) => _ownPath ?? ctx.Aircraft.Ground.AssignedTaxiRoute;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
        _expedited = _expedited || (SourceRoute(ctx)?.GetHoldShortAt(_approachNodeId)?.IsExpedited ?? false);

        TryBuildCrossingRoute(ctx, replan: false);

        Log.LogDebug(
            "[Crossing] {Callsign}: crossing runway {Rwy}, approach={Approach}, target={Target}, initialized={Init}",
            ctx.Aircraft.Callsign,
            _runwayId ?? "?",
            _approachNodeId,
            _targetNodeId,
            _initialized
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_initialized)
        {
            TryBuildCrossingRoute(ctx, replan: false);
        }

        TickNotClearCall(ctx);

        // A GIVEWAY does not act on the runway: the crossing runs on until the tail is past the far holding-position marking
        // (AIM 4-3-21.a/b) and the onward TaxiingPhase applies the give-way braking from there. A HOLD stops it here.
        bool held = ctx.Aircraft.Ground.IsImmobile && (ctx.Aircraft.Ground.Hold?.Kind != HoldKind.GiveWay);
        if (held && (!_initialized || _navigator is null || _crossingRoute is null))
        {
            PinHeldSpeed(ctx);
            return false;
        }

        if (!_initialized || _navigator is null || _crossingRoute is null)
        {
            // Degenerate fallback: no route slice available. Stop where we are
            // so the next phase can take over (or be inserted) without driving
            // the aircraft anywhere by guesswork. Should only happen if the
            // phase is constructed in a test without a source route (own path, else AssignedTaxiRoute).
            ctx.Targets.TargetSpeed = 0;
            HandRouteBack(ctx);
            return true;
        }

        if (_endsAtRest && _crossingRoute.IsComplete)
        {
            return TickBrakingToRest(ctx, held);
        }

        ApplySpeedCap(ctx, _navigator);
        StopCapState? stopCap = held ? null : ApplyStopCap(ctx, _navigator);
        bool isLastSegment = _crossingRoute.CurrentSegmentIndex + 1 >= _crossingRoute.Segments.Count;
        NavigatorResult result = _navigator.Tick(ctx, isLastSegment, _ => true);
        if (held)
        {
            return TickHeld(ctx, _navigator, _crossingRoute, result);
        }

        if (stopCap is { } cap)
        {
            ClampToStopCap(ctx, cap);
            if ((result != NavigatorResult.ArrivedAtNode) && RestsAtTheSliceEnd(ctx, cap))
            {
                if (_endsAtRest)
                {
                    return CompletesAtRest(ctx);
                }

                Log.LogDebug("[Crossing] {Callsign}: at a crawl at the slice end, handing the route back", ctx.Aircraft.Callsign);
                HandRouteBack(ctx);
                return true;
            }
        }

        if (result == NavigatorResult.ArrivedAtNode)
        {
            if (_crossingRoute.CurrentSegment is { } seg)
            {
                ctx.Aircraft.Ground.CurrentTaxiway = seg.TaxiwayName;
            }

            _crossingRoute.CurrentSegmentIndex += 1;
            if (_crossingRoute.CurrentSegmentIndex > _crossingRoute.Segments.Count)
            {
                _crossingRoute.CurrentSegmentIndex = _crossingRoute.Segments.Count;
            }

            if (_endsAtRest && _crossingRoute.IsComplete)
            {
                return TickBrakingToRest(ctx, held);
            }

            if (_crossingRoute.IsComplete)
            {
                HandRouteBack(ctx);
                return true;
            }

            SetupCurrentSegment(ctx, _navigator, _crossingRoute);
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            Log.LogTrace(
                "[Crossing] {Callsign}: seg={Idx}/{Count} gs={Gs:F1}kts",
                ctx.Aircraft.Callsign,
                _crossingRoute.CurrentSegmentIndex,
                _crossingRoute.Segments.Count,
                ctx.Aircraft.GroundSpeed
            );
        }

        return false;
    }

    /// <summary>Ground speed (kt) under which a crossing aircraft counts as at rest for the not-clear call.</summary>
    private const double NotClearRestKts = 1.0;

    /// <summary>How long (s) a crossing aircraft is at rest before it says it is not clear of the runway (owner ruling).</summary>
    private const double NotClearCallDelaySeconds = 3.0;

    /// <summary>
    /// An aircraft that has come to rest mid-crossing with part of it still inside the far holding-position marking, not for a
    /// controller's HOLD, says once per crossing, <see cref="NotClearCallDelaySeconds"/> after coming to rest, that it is holding
    /// position not clear of the runway with traffic ahead (<see cref="PilotResponder.BuildHoldingPositionNotClearTrafficAhead"/>,
    /// the end a pilot names it by, <see cref="RunwayCrossingEnd.Spoken"/>: the active end, else the nearest; AIM 2-3-5.a, 4-3-18.b), routed to a student on ground
    /// or as an RPO's pilot speech or warning. Moving again, or a HOLD, restarts the wait; a GIVEWAY does not, since it does not
    /// stop the aircraft on the runway.
    /// </summary>
    private void TickNotClearCall(PhaseContext ctx)
    {
        if (_saidNotClear)
        {
            return;
        }

        if ((ctx.Aircraft.GroundSpeed >= NotClearRestKts) || (ctx.Aircraft.Ground.Hold is { Kind: HoldKind.HoldPosition }))
        {
            _notClearRestSeconds = 0.0;
            return;
        }

        _notClearRestSeconds += ctx.DeltaSeconds;
        if ((_notClearRestSeconds < NotClearCallDelaySeconds) || (_runwayId is null) || !IsNotClearOfFarMarking(ctx.Aircraft, ctx.GroundLayout))
        {
            return;
        }

        _saidNotClear = true;
        string runway = RunwayCrossingEnd.Spoken(ctx.Aircraft, _runwayId, ctx.GroundLayout, ctx.ActiveRunways);
        Log.LogDebug("[Crossing] {Callsign}: at rest not clear of runway {Rwy}, traffic ahead", ctx.Aircraft.Callsign, runway);
        PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            PilotResponder.BuildHoldingPositionNotClearTrafficAhead(ctx.Aircraft, runway),
            PilotResponder.SoloPositionsGround
        );
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/> is on the runway side of the crossing with part of it still inside the far
    /// holding-position marking: its nose past the near marking (<see cref="NosePastNearMarkingFt"/>) and its tail short of the line
    /// through the exit-side bar node square to the way across.
    /// </summary>
    private bool IsNotClearOfFarMarking(AircraftState aircraft, AirportGroundLayout? layout)
    {
        if (
            (layout is null)
            || (NosePastNearMarkingFt(aircraft, layout) is not > 0.0)
            || !layout.Nodes.TryGetValue(_approachNodeId, out GroundNode? near)
            || !layout.Nodes.TryGetValue(_targetNodeId, out GroundNode? far)
        )
        {
            return false;
        }

        var acrossHeading = new TrueHeading(GeoMath.BearingTo(near.Position, far.Position));
        double centrePastFarFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, far.Position, acrossHeading) * GeoMath.FeetPerNm;
        return centrePastFarFt - (AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0) < 0.0;
    }

    /// <summary>
    /// The firm rate (kt/s) a piston or a helicopter brakes at for a HOLD on the runway, above its 2 kt/s taxi brake rate,
    /// which would leave a 20 kt light aircraft rolling some 170 ft along the runway (owner ruling).
    /// </summary>
    private const double LightHoldOnRunwayDecelKtsPerSec = 4.5;

    /// <summary>
    /// The rate (kt/s) a HOLD on the runway brakes at: the taxi brake rate for a jet or a turboprop, the firm
    /// <see cref="LightHoldOnRunwayDecelKtsPerSec"/> for a piston or a helicopter.
    /// </summary>
    internal static double HoldBrakeRateKtsPerSec(AircraftCategory category) =>
        category is AircraftCategory.Piston or AircraftCategory.Helicopter
            ? LightHoldOnRunwayDecelKtsPerSec
            : CategoryPerformance.TaxiDecelRate(category);

    /// <summary>
    /// A held tick: a HOLD brakes the aircraft to a stop where it is, on the runway (AIM 4-3-21.a: HOLD is the approval to stop
    /// there). The navigator has already ticked, so its curve playback stays with the aircraft while it brakes, and the speed
    /// target is pinned after it (docs/ground/navigator.md, "The hold contract"). An arrival at a node inside the slice (a runway
    /// centreline node) rolls on to the next slice segment at the brake rate. An arrival at the slice's last node, past the far
    /// marking with the tail clear, ends the crossing as an un-held arrival does (<see cref="HandRouteBack"/>), still braking:
    /// the onward taxi's held branch brakes the aircraft on from there. Returns whether the crossing is done.
    /// </summary>
    private bool TickHeld(PhaseContext ctx, GroundNavigator navigator, TaxiRoute crossingRoute, NavigatorResult result)
    {
        PinHeldSpeed(ctx);
        if (result != NavigatorResult.ArrivedAtNode)
        {
            return false;
        }

        if (crossingRoute.CurrentSegment is { } seg)
        {
            ctx.Aircraft.Ground.CurrentTaxiway = seg.TaxiwayName;
        }

        crossingRoute.CurrentSegmentIndex += 1;
        if (_endsAtRest && crossingRoute.IsComplete)
        {
            return CompletesAtRest(ctx);
        }

        if (crossingRoute.IsComplete)
        {
            HandRouteBack(ctx);
            return true;
        }

        SetupCurrentSegment(ctx, navigator, crossingRoute);
        PinHeldSpeed(ctx);
        return false;
    }

    /// <summary>
    /// A tick of a crossing that ends at rest (<see cref="EndAtRest"/>) once its slice is driven: with nothing left to steer
    /// along it brakes straight on, at the hold's rate while a HOLD stands (<see cref="PinHeldSpeed(PhaseContext)"/>), else at
    /// the taxi brake rate, and hands over at rest (<see cref="CompletesAtRest"/>). Returns whether the crossing is done.
    /// </summary>
    private bool TickBrakingToRest(PhaseContext ctx, bool held)
    {
        if (held)
        {
            PinHeldSpeed(ctx);
        }
        else
        {
            ctx.Targets.TargetSpeed = 0.0;
            ctx.Targets.DesiredDecelRate = null;
        }

        return CompletesAtRest(ctx);
    }

    /// <summary>
    /// Whether a crossing that ends at rest (<see cref="EndAtRest"/>) is done: once at rest (<see cref="FollowingPhase.IsAtRest"/>)
    /// it zeroes its speed and hands the route back (<see cref="HandRouteBack"/>), so the <see cref="HoldingInPositionPhase"/>
    /// after it starts at rest; until then it is not. A slice that ends before its tail and wingtips are past the far bar
    /// (<see cref="FollowingPhase.ShortOfBarFt"/>) still stops at its end, which it never drives past, with a warning.
    /// </summary>
    private bool CompletesAtRest(PhaseContext ctx)
    {
        if (!FollowingPhase.IsAtRest(ctx))
        {
            return false;
        }

        if ((FollowingPhase.ShortOfBarFt(ctx.GroundLayout, _targetNodeId, ctx.Aircraft) is { } shortFt) && (shortFt >= 0.0))
        {
            Log.LogWarning(
                "[Crossing] {Callsign}: the crossing's slice ends {Short:F1} ft short of clearing the far bar #{Bar}; stopped at its end",
                ctx.Aircraft.Callsign,
                shortFt,
                _targetNodeId
            );
        }

        Log.LogDebug("[Crossing] {Callsign}: at rest at the slice end, handing over to the hold", ctx.Aircraft.Callsign);
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        RecordWhereTheBreakOffHoldRests(ctx);
        HandRouteBack(ctx);
        return true;
    }

    /// <summary>
    /// Replaces the break-off hold (<see cref="HoldingInPositionPhase.EndsBrokenOffFollow"/>) next after this crossing with one that
    /// records where the aircraft came to rest
    /// (<see cref="HoldingInPositionPhase.AfterFollowBreakOff(string, string, IReadOnlyCollection{string})"/>): the crossed
    /// runway, its end nearest the aircraft, when the tail is still inside its far holding-position marking
    /// (<see cref="IsNotClearOfFarMarking"/>), and the other runway whose bar set where the slice ends (<see cref="_heldShortBar"/>)
    /// when the nose came to rest short of that bar (<see cref="IsNoseShortOfHeldShortBar"/>): braking from a HOLD can carry it past
    /// the stop the slice planned short of the bar, and a nose past the marking is not holding short of it. Any other next phase is
    /// left alone.
    /// </summary>
    private void RecordWhereTheBreakOffHoldRests(PhaseContext ctx)
    {
        if ((ctx.Aircraft.Phases is not { } phases) || (ctx.GroundLayout is not { } layout))
        {
            return;
        }

        int next = phases.CurrentIndex + 1;
        if ((next >= phases.Phases.Count) || (phases.Phases[next] is not HoldingInPositionPhase { EndsBrokenOffFollow: true }))
        {
            return;
        }

        string? tailEnd =
            (_runwayId is { } runwayId) && IsNotClearOfFarMarking(ctx.Aircraft, layout)
                ? RunwayCrossingEnd.Spoken(ctx.Aircraft, runwayId, layout, ctx.ActiveRunways)
                : null;
        RunwayInfo? reference = tailEnd is null ? null : NavigationDatabase.InstanceOrNull?.GetRunway(layout.AirportId, tailEnd);
        string? heldShort =
            (_heldShortBar is { RunwayId: not null } bar) && IsNoseShortOfHeldShortBar(ctx.Aircraft, layout, bar)
                ? RunwayTailClearance.HeldShortRunwayEnd(layout, bar, reference, ctx.Aircraft, ctx.ActiveRunways)
                : null;
        Log.LogDebug(
            "[Crossing] {Callsign}: at rest holding short of {HeldShort}, tail inside {TailInside}",
            ctx.Aircraft.Callsign,
            heldShort ?? "no runway",
            tailEnd ?? "no runway"
        );
        phases.Phases[next] = HoldingInPositionPhase.AfterFollowBreakOff(heldShort, tailEnd, PilotResponder.SoloPositionsGround);
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/> has its nose short of the holding position marking of <paramref name="bar"/>, the other
    /// runway's bar that set where the slice ends, judged as a <c>CLRWY</c> judges a nose against another runway's bar
    /// (<see cref="ClearRunwayPhase.IsNosePastBar"/>) from the crossed runway's far bar. False without that far bar in the layout.
    /// </summary>
    private bool IsNoseShortOfHeldShortBar(AircraftState aircraft, AirportGroundLayout layout, GroundNode bar)
    {
        if (!layout.Nodes.TryGetValue(_targetNodeId, out GroundNode? far))
        {
            Log.LogWarning(
                "[Crossing] {Callsign}: far bar #{Bar} not in the layout; not holding short of #{Other}",
                aircraft.Callsign,
                _targetNodeId,
                bar.Id
            );
            return false;
        }

        bool past = ClearRunwayPhase.IsNosePastBar(aircraft, far, bar);
        Log.LogDebug("[Crossing] {Callsign}: at rest with the nose {Where} bar #{Other}", aircraft.Callsign, past ? "past" : "short of", bar.Id);
        return !past;
    }

    /// <summary>Pins the held crossing's speed target and brake rate (<see cref="PinHeldSpeed(PhaseContext, double?)"/>).</summary>
    private void PinHeldSpeed(PhaseContext ctx) => PinHeldSpeed(ctx, NosePastNearMarkingFt(ctx.Aircraft, ctx.GroundLayout));

    /// <summary>
    /// Pins the speed target and brake rate of an aircraft a HOLD stops at a runway crossing, its nose
    /// <paramref name="nosePastFt"/> past the near holding-position marking (null when unknown): zero at
    /// <see cref="HoldBrakeRateKtsPerSec"/>, or, when that rate stops the nose at the marking (<see cref="HoldBrakesOntoMarking"/>),
    /// the braking curve onto the marking. The crossing pins a HOLD with it, and so does the taxi in the stretch before the
    /// crossing starts.
    /// </summary>
    internal static void PinHeldSpeed(PhaseContext ctx, double? nosePastFt)
    {
        double rate = HoldBrakeRateKtsPerSec(ctx.Category);
        ctx.Targets.DesiredDecelRate = rate;
        ctx.Targets.TargetSpeed = 0.0;
        if ((nosePastFt is { } pastFt) && HoldBrakesOntoMarking(ctx.Aircraft.GroundSpeed, rate, pastFt, ctx.DeltaSeconds))
        {
            ctx.Targets.TargetSpeed = Math.Min(ctx.Aircraft.GroundSpeed, GroundStopBraking.StopCurveKts(ctx, -pastFt, rate));
        }
    }

    /// <summary>
    /// How far (ft) <paramref name="aircraft"/>'s nose is past the near holding-position marking, negative while short of it:
    /// the marking is the line through the entry-side bar node square to the way across (the slice's first segment, else the
    /// line to the exit-side bar node), and the nose is half the fuselage ahead of the centre. Null when the layout lacks either
    /// bar node.
    /// </summary>
    public double? NosePastNearMarkingFt(AircraftState aircraft, AirportGroundLayout? layout)
    {
        if (
            (layout is null)
            || !layout.Nodes.TryGetValue(_approachNodeId, out GroundNode? near)
            || !layout.Nodes.TryGetValue(_targetNodeId, out GroundNode? far)
        )
        {
            return null;
        }

        double acrossDeg = _crossingRoute?.Segments[0].Edge.DepartureBearing ?? GeoMath.BearingTo(near.Position, far.Position);
        return NosePastMarkingFt(aircraft, near.Position, acrossDeg);
    }

    /// <summary>
    /// How far (ft) <paramref name="aircraft"/>'s nose is past a holding-position marking, negative while short of it: the
    /// marking is the line through <paramref name="nearBar"/> square to the way across, <paramref name="acrossDeg"/>, and the nose
    /// is half the fuselage ahead of the centre. The one measure the crossing, the taxi before it and the near-line gate share.
    /// </summary>
    internal static double NosePastMarkingFt(AircraftState aircraft, LatLon nearBar, double acrossDeg)
    {
        double centreFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, nearBar, new TrueHeading(acrossDeg)) * GeoMath.FeetPerNm;
        return centreFt + (AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0);
    }

    /// <summary>
    /// The segments the crossing drives from where it is, as indices to walk: the rest of its slice (entry bar → exit bar → tail
    /// clearance) from <c>SliceStart</c>, then, for a crossing of the aircraft's own route, <c>Onward</c> — that route — from
    /// <c>OnwardStart</c>, the first segment the tail clearance did not drive whole, so a route segment the tail clearance drove
    /// only part of comes back whole, a harmless overstatement of the path. The aircraft's route cursor stays on the entry segment
    /// for the whole crossing (<see cref="HandRouteBack"/> is its only writer), so a reader of the path ahead takes it from here.
    /// Null until the slice is built.
    /// </summary>
    public (TaxiRoute Slice, int SliceStart, TaxiRoute? Onward, int OnwardStart)? PathAhead(AircraftState aircraft) =>
        _crossingRoute is { } slice
            ? (
                slice,
                Math.Max(slice.CurrentSegmentIndex, 0),
                (_ownPath is null) && (_exitRouteIndex >= 0) ? aircraft.Ground.AssignedTaxiRoute : null,
                _exitRouteIndex + 1 + _tailClearFullSegments
            )
            : null;

    /// <summary>
    /// The runway end a HOLD on this crossing is read back as holding on — the end a pilot names it by
    /// (<see cref="RunwayCrossingEnd.Spoken"/>: the active end, else the nearest) — when the HOLD stops some part of the aircraft past the near holding-position
    /// marking (<see cref="HoldStopsOnRunway"/>). Null otherwise, or when the crossing names no runway.
    /// </summary>
    public string? HeldOnRunwayEnd(AircraftState aircraft, ActiveRunways activeRunways)
    {
        AirportGroundLayout? layout = aircraft.Ground.Layout;
        return (_runwayId is not null) && (NosePastNearMarkingFt(aircraft, layout) is { } nosePastFt) && HoldStopsOnRunway(aircraft, nosePastFt)
            ? RunwayCrossingEnd.Spoken(aircraft, _runwayId, layout, activeRunways)
            : null;
    }

    /// <summary>One phase tick (s): the phases run once per physics sub-tick.</summary>
    private const double PhaseTickSeconds = 1.0 / SimulationEngine.PhysicsSubTickRate;

    /// <summary>
    /// Whether a HOLD given now stops <paramref name="aircraft"/>, its nose <paramref name="nosePastFt"/> past the near
    /// holding-position marking, with some part of it past the marking (AIM 2-3-5.a.1): unless the HOLD's rate
    /// (<see cref="HoldBrakeRateKtsPerSec"/>) brakes it onto the marking (<see cref="HoldBrakesOntoMarking"/>, judged over the
    /// phase tick <see cref="PinHeldSpeed(PhaseContext, double?)"/> pins it on), it brakes at that rate from where it is, and the
    /// nose comes to rest its stopping distance on. The readback and the braking judge the same room, so they agree.
    /// </summary>
    internal static bool HoldStopsOnRunway(AircraftState aircraft, double nosePastFt)
    {
        double rate = HoldBrakeRateKtsPerSec(AircraftCategorization.Categorize(aircraft.AircraftType));
        double speedKts = aircraft.GroundSpeed;
        return !HoldBrakesOntoMarking(speedKts, rate, nosePastFt, PhaseTickSeconds)
            && (nosePastFt + GroundStopBraking.StoppingDistanceFt(speedKts, rate) > 0.0);
    }

    /// <summary>
    /// Whether a HOLD braking at <paramref name="rate"/> from <paramref name="speedKts"/> stops the nose, <paramref name="nosePastFt"/>
    /// past the near holding-position marking, at the marking on the braking curve onto it
    /// (<see cref="GroundStopBraking.StopCurveKts"/>): the nose is short of the marking and the stopping distance fits the room the
    /// curve has — the distance short, less <see cref="GroundNavigator.SetBackStopMarginFt"/> and one tick's
    /// (<paramref name="tickSeconds"/>) travel.
    /// </summary>
    private static bool HoldBrakesOntoMarking(double speedKts, double rate, double nosePastFt, double tickSeconds)
    {
        double roomFt = -nosePastFt - GroundNavigator.SetBackStopMarginFt - (speedKts * GroundStopBraking.FeetPerSecondPerKt * tickSeconds);
        return (nosePastFt < 0.0) && (GroundStopBraking.StoppingDistanceFt(speedKts, rate) <= roomFt);
    }

    // Speed targets are owned by the next phase. The typical successor is
    // TaxiingPhase (TaxiingPhase.cs BuildResumePhases) — zeroing IAS here
    // would force a stop the aircraft has to re-accelerate from. If the
    // route ends after the crossing, the inserted HoldingInPositionPhase
    // / AtParkingPhase will brake to zero on its own.
    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        Log.LogDebug("[Crossing] {Callsign}: OnEnd ({Status})", ctx.Aircraft.Callsign, endStatus);

        // The crossing's expedite acceleration ends with the crossing.
        ctx.Targets.DesiredAccelRate = null;
    }

    /// <summary>
    /// The crossing's straight-segment cap (7110.65 §3-7-2.j "CROSS … WITHOUT DELAY"): a commanded <c>SPD</c> wins, else the taxi
    /// speed, raised to the expedite cap while this crossing or the route is expedited, with the expedite acceleration published
    /// for as long. A Super/Heavy gets neither (<see cref="CategoryPerformance.IsSuperOrHeavy"/>).
    /// </summary>
    private void ApplySpeedCap(PhaseContext ctx, GroundNavigator navigator)
    {
        // A CROSS … EXP issued once the crossing is under way marks the crossing's hold-short, read here every tick.
        bool expedite =
            _expedited || (SourceRoute(ctx)?.GetHoldShortAt(_approachNodeId)?.IsExpedited ?? false) || ctx.Aircraft.Ground.IsExpeditingTaxi;
        bool superOrHeavy = CategoryPerformance.IsSuperOrHeavy(ctx.Aircraft.AircraftType, ctx.Category);
        double? commandedKts = ctx.Aircraft.Ground.CommandedTaxiSpeedKts;
        navigator.MaxSpeedKts = CategoryPerformance.TaxiSpeedCap(ctx.Category, commandedKts, expedite, superOrHeavy);
        ctx.Targets.DesiredAccelRate = CategoryPerformance.TaxiAccelOverride(ctx.Category, commandedKts, expedite, superOrHeavy);
    }

    /// <summary>
    /// The first binding stop past the crossing's near bar on the route it crosses: an uncleared hold short's placed stop
    /// (<paramref name="Bar"/>, whose node route segment <paramref name="BarSegmentIndex"/> ends at), or with no such bar the
    /// route's end. <paramref name="StopRouteFt"/> is how far along the route that stop lies from the route's start.
    /// </summary>
    private sealed record OnwardStop(double StopRouteFt, HoldShortPoint? Bar, int BarSegmentIndex);

    /// <summary>
    /// The distances (ft) one tick of the stop cap works from: <paramref name="GapFt"/>, how far past the slice end the onward
    /// stop lies (negative for a stop the slice reaches past); <paramref name="ToStopFt"/>, how far the aircraft has to go to that
    /// stop; and <paramref name="ToAimFt"/>, how far to the point the braking curve onto it reaches zero.
    /// </summary>
    private readonly record struct StopDistances(double GapFt, double ToStopFt, double ToAimFt);

    /// <summary>
    /// The stop cap one tick works to (<see cref="ApplyStopCap"/>): the speed (kt) the crossing is held to, the speed the onward
    /// stop is aimed at (zero for a stop), whether the cap is below the floor (and so lowers it), whether the stop is the slice
    /// end itself (a crossing that ends the route), and the distances it was weighed at.
    /// </summary>
    private readonly record struct StopCapState(double CapKts, double AimKts, bool BelowFloor, bool AimsAtSliceEnd, StopDistances Distances);

    /// <summary>
    /// The first binding stop on <paramref name="route"/> from the crossing's exit segment <paramref name="exitIdx"/> on, as a
    /// pure function of the route as it stands: the first uncleared bar, its stop the route distance to its node less its
    /// setback, or with none the route's end. A cleared bar never binds, so a crossing straight into a second cleared crossing
    /// keeps its speed. Traffic, a GIVEWAY or a FOLLOWG target is never this stop: the conflict ceiling already outranks the floor.
    /// The stop is never placed before the point where the tail clears the crossed runway's far marking: the hold-short
    /// annotator (<see cref="HoldShortAnnotator"/>) places a taxiway stop with the tail 10 ft past that marking, or clamps it
    /// at the bar when the gap is too short, and the slice is held at the stop (<see cref="AppendTailClearance"/>).
    /// </summary>
    private static OnwardStop FindOnwardStop(TaxiRoute route, int exitIdx)
    {
        for (int i = exitIdx; i < route.Segments.Count; i++)
        {
            if (route.GetHoldShortAt(route.Segments[i].ToNodeId) is { IsCleared: false } bar)
            {
                double stopFt = route.PrefixDistanceFt(i + 1) - (route.HoldShortSetbackNm(i, bar) * GeoMath.FeetPerNm);
                return new OnwardStop(stopFt, bar, i);
            }
        }

        return new OnwardStop(route.PrefixDistanceFt(route.Segments.Count), null, route.Segments.Count - 1);
    }

    /// <summary>
    /// Holds the crossing to the taxi-rate curve onto the first binding stop past it whatever set its target. The stop is
    /// searched every tick on the live assigned route (<see cref="FindOnwardStop"/>), so a hold cleared mid-crossing (scenario
    /// auto-cross, a takeoff clearance's pre-clear) stops binding on the next tick and a restored run sees the same stop as
    /// the original; only the slice end is kept from the slice build. Every tick the floor is
    /// <see cref="CategoryPerformance.RunwayCrossingSpeed"/>, lowered to the curve onto the stop's aim
    /// (<see cref="GroundStopBraking.StopCurveKts"/> at <see cref="StopAimFt"/>) wherever that curve is below it; and wherever
    /// the curve is below the published target, a target above the floor included (a taxi or commanded speed),
    /// <see cref="ClampToStopCap"/> clamps the target to it. A destination-runway bar the aircraft will go through on a stored
    /// takeoff or line-up clearance is aimed at <see cref="TaxiingPhase.RouteEndSpeedKts"/> rather than a stop: the curve to
    /// that speed is the stop curve lifted by it. With no stop, or the stop at or beyond <see cref="StopCapActivationFt"/> past
    /// the slice end for the fastest of the floor, the published target and the current speed, the cap is never weighed, so the
    /// crossing's speeds are the plain floor's bit for bit. A crossing that ends at rest (<see cref="EndAtRest"/>) brakes onto its
    /// slice end instead, its own path included (<see cref="ApplySliceEndStopCap"/>). Returns the cap whenever it is weighed, else
    /// null.
    /// </summary>
    private StopCapState? ApplyStopCap(PhaseContext ctx, GroundNavigator navigator)
    {
        double floorKts = CategoryPerformance.RunwayCrossingSpeed(ctx.Category);
        navigator.MinSpeedKts = floorKts;
        if (_endsAtRest)
        {
            return ApplySliceEndStopCap(ctx, navigator);
        }

        if (
            (_ownPath is not null)
            || (_exitRouteIndex < 0)
            || (ctx.Aircraft.Ground.AssignedTaxiRoute is not { } route)
            || (StopCapRemainingFt(ctx.Aircraft.Position) is not { } remainingFt)
        )
        {
            return null;
        }

        OnwardStop stop = FindOnwardStop(route, _exitRouteIndex);
        bool aimsAtSliceEnd = (stop.Bar is null) && _sliceEndsRoute;
        double gapFt = aimsAtSliceEnd ? 0.0 : (stop.StopRouteFt - _sliceEndRouteFt);
        double fastestKts = Math.Max(floorKts, Math.Max(ctx.Targets.TargetSpeed ?? 0.0, ctx.Aircraft.IndicatedAirspeed));
        if (gapFt >= StopCapActivationFt(fastestKts, ctx.Category))
        {
            return null;
        }

        double toStopFt = remainingFt + gapFt;
        double toAimFt = StopAimFt(route, stop, toStopFt);
        double aimKts = (stop.BarSegmentIndex == (route.Segments.Count - 1)) ? TaxiingPhase.RouteEndSpeedKts(ctx, route) : 0.0;
        return CapOntoStop(ctx, navigator, aimKts, aimsAtSliceEnd, new StopDistances(gapFt, toStopFt, toAimFt));
    }

    /// <summary>
    /// The stop cap of a crossing that ends at rest (<see cref="EndAtRest"/>): its stop is its slice end, the tail past the far
    /// holding-position marking, whatever route it was cut from or what lies past it, aimed at zero
    /// <see cref="GroundNavigator.SetBackStopMarginFt"/> beyond it as for a crossing that ends its route
    /// (<see cref="StopAimFt"/>). Always weighed, so it brakes onto the slice end at the taxi rate. Null before the slice is built.
    /// </summary>
    private StopCapState? ApplySliceEndStopCap(PhaseContext ctx, GroundNavigator navigator)
    {
        if (StopCapRemainingFt(ctx.Aircraft.Position) is not { } remainingFt)
        {
            return null;
        }

        var distances = new StopDistances(0.0, remainingFt, remainingFt + GroundNavigator.SetBackStopMarginFt);
        return CapOntoStop(ctx, navigator, 0.0, true, distances);
    }

    /// <summary>
    /// The cap onto a stop at <paramref name="distances"/>: the taxi-rate curve onto its aim, lifted by <paramref name="aimKts"/>
    /// when the stop is flown through at that speed; a cap below the crossing floor
    /// (<see cref="CategoryPerformance.RunwayCrossingSpeed"/>) lowers the navigator's floor to it.
    /// </summary>
    private static StopCapState CapOntoStop(PhaseContext ctx, GroundNavigator navigator, double aimKts, bool aimsAtSliceEnd, StopDistances distances)
    {
        double curveKts = GroundStopBraking.StopCurveKts(ctx, distances.ToAimFt, CategoryPerformance.TaxiDecelRate(ctx.Category));
        double capKts = (aimKts > 0.0) ? Math.Sqrt((aimKts * aimKts) + (curveKts * curveKts)) : curveKts;
        bool belowFloor = capKts < CategoryPerformance.RunwayCrossingSpeed(ctx.Category);
        if (belowFloor)
        {
            navigator.MinSpeedKts = capKts;
        }

        return new StopCapState(capKts, aimKts, belowFloor, aimsAtSliceEnd, distances);
    }

    /// <summary>
    /// Where the curve onto the onward stop, <paramref name="toStopFt"/> ahead, reaches zero: for a bar, exactly where the
    /// onward taxi aims (<see cref="TaxiingPhase.RoutineAimFt"/>), so the target carries no step when the crossing hands the
    /// route back; for the route's end, which the onward navigator aims at itself, <see cref="GroundNavigator.SetBackStopMarginFt"/>
    /// beyond it, as for a stop on its bar's own segment.
    /// </summary>
    private static double StopAimFt(TaxiRoute route, OnwardStop stop, double toStopFt) =>
        stop.Bar is { } bar
            ? TaxiingPhase.RoutineAimFt(route, stop.BarSegmentIndex, bar, toStopFt, farSideBlocked: false)
            : toStopFt + GroundNavigator.SetBackStopMarginFt;

    /// <summary>
    /// How near past the slice end (ft) the onward stop must lie for the stop cap to act: the taxi-rate stopping distance from
    /// <paramref name="speedKts"/>, plus <see cref="GroundNavigator.SetBackStopMarginFt"/> and one phase tick's travel at that
    /// speed. Beyond it the crossing's speeds are left exactly as the plain floor sets them. The caller passes the fastest of
    /// the floor, the published target and the current speed, so a crossing taxiing above the floor starts braking onto the
    /// stop as early as its own speed needs, not the floor's.
    /// </summary>
    private static double StopCapActivationFt(double speedKts, AircraftCategory category) =>
        GroundStopBraking.StoppingDistanceFt(speedKts, CategoryPerformance.TaxiDecelRate(category))
        + GroundNavigator.SetBackStopMarginFt
        + (speedKts * GroundStopBraking.FeetPerSecondPerKt * PhaseTickSeconds);

    /// <summary>
    /// Holds the published target to the stop cap once the navigator has ticked, arrival sub-ticks included, so the target is
    /// never above the taxi-rate curve onto the stop on any sub-tick: the navigator publishes no speed on the sub-tick it
    /// arrives at a node, and physics flies the crossing's last target on the sub-tick it hands the route back. A target left
    /// unset is capped from the current speed. A cap at or above both the floor and the target leaves the target untouched.
    /// Logs the first time the cap takes over.
    /// </summary>
    private void ClampToStopCap(PhaseContext ctx, StopCapState cap)
    {
        double targetKts = ctx.Targets.TargetSpeed ?? ctx.Aircraft.IndicatedAirspeed;
        if (!cap.BelowFloor && (cap.CapKts >= targetKts))
        {
            return;
        }

        ctx.Targets.TargetSpeed = Math.Min(targetKts, cap.CapKts);
        if (_loggedStopCap)
        {
            return;
        }

        _loggedStopCap = true;
        Log.LogDebug(
            "[Crossing] {Callsign}: braking onto the stop past the crossing: gap={Gap:F1}ft toStop={ToStop:F1}ft "
                + "floor={Floor:F1}kt curve={Curve:F1}kt target={Target:F1}kt",
            ctx.Aircraft.Callsign,
            cap.Distances.GapFt,
            cap.Distances.ToStopFt,
            CategoryPerformance.RunwayCrossingSpeed(ctx.Category),
            cap.CapKts,
            ctx.Targets.TargetSpeed
        );
    }

    /// <summary>
    /// The completion rule: braking onto a stop (not a flow-through destination bar), the aircraft is down to a crawl
    /// (<see cref="TaxiingPhase.StartNodeHoldArmSpeedKts"/>) within the take distance of the slice end or of the point its
    /// braking curve aims at, so the crossing hands the route back rather than come to rest inside it, where the not-clear call
    /// (<see cref="TickNotClearCall"/>) would fire. The take distance is <see cref="TaxiingPhase.SetBackStopTakeFt"/>, the
    /// onward taxi's own; for a crossing that ends the route, whose next phase stops it where it stands, it is
    /// <see cref="GroundNavigator.SetBackStopMarginFt"/>, so the tail rests no further short of its clearance than any set-back
    /// stop does. A crossing that ends at rest (<see cref="EndAtRest"/>) is not down to a crawl until it is at rest
    /// (<see cref="FollowingPhase.IsAtRest"/>).
    /// </summary>
    private bool RestsAtTheSliceEnd(PhaseContext ctx, StopCapState cap)
    {
        bool crawling = _endsAtRest ? FollowingPhase.IsAtRest(ctx) : (ctx.Aircraft.IndicatedAirspeed <= TaxiingPhase.StartNodeHoldArmSpeedKts);
        if ((cap.AimKts > 0.0) || !crawling)
        {
            return false;
        }

        double windowFt = cap.AimsAtSliceEnd ? GroundNavigator.SetBackStopMarginFt : TaxiingPhase.SetBackStopTakeFt;
        double toSliceEndFt = cap.Distances.ToStopFt - cap.Distances.GapFt;
        return (toSliceEndFt <= windowFt) || (cap.Distances.ToAimFt <= windowFt);
    }

    /// <summary>
    /// The slice remainder (ft) the stop cap measures from: <see cref="TailClearRemainingFt"/>, except that a position already
    /// past the in-progress segment's end node reads that overshoot as behind it, not still ahead. Physics can carry the
    /// aircraft past a node between the navigator's arrival checks (one sub-tick is 6 ft at 14 kt), and the straight reading of
    /// the in-progress leg is unsigned, so read straight the distance to the stop grew with the overshoot, the curve rose above
    /// the speed and the crossing lost a sub-tick of braking it could never win back at the taxi rate. Past the node the
    /// remainder is the rest of the slice less the overshoot along the segment's arrival bearing, so overshooting a node never
    /// lengthens the distance to the stop.
    /// </summary>
    private double? StopCapRemainingFt(LatLon position)
    {
        double? tailClearFt = TailClearRemainingFt(position);
        if ((tailClearFt is not { } remainingFt) || (_crossingRoute is not { IsComplete: false } slice))
        {
            return tailClearFt;
        }

        DirectionalEdge edge = slice.Segments[Math.Max(0, slice.CurrentSegmentIndex)].Edge;
        double pastEndNm = GeoMath.AlongTrackDistanceNm(position, edge.ToNode.Position, new TrueHeading(edge.ArrivalBearing));
        double toEndFt = GeoMath.DistanceNm(position, edge.ToNode.Position) * GeoMath.FeetPerNm;
        return (pastEndNm > 0.0) ? (remainingFt - toEndFt - (pastEndNm * GeoMath.FeetPerNm)) : remainingFt;
    }

    /// <summary>
    /// Ends this crossing's expedite: whatever ended the route expedite, or a <c>CROSS</c> without <c>EXP</c> naming it (YAAT-658).
    /// </summary>
    internal void EndExpedite() => _expedited = false;

    /// <summary>
    /// Makes this crossing the last move before a <see cref="HoldingInPositionPhase"/>, for a <c>HOLD</c> that breaks off the
    /// follow behind it: the crossing brakes at the taxi rate onto its slice end, the tail past the far holding-position marking,
    /// as a crossing that ends its route does (<see cref="ApplySliceEndStopCap"/>), and hands over only at rest
    /// (<see cref="CompletesAtRest"/>), so the hold after it starts from rest rather than stopping it in one tick. Called again, it
    /// does nothing. A restored crossing not yet ticked is first built as its first tick would build it, from the tail decision and
    /// held-short bar its snapshot carries; one never built and never restored only takes the flag, and is built ending at rest.
    /// A crossing whose slice is built re-plans it at once: it is put into the state a restore leaves it in
    /// (<see cref="FromSnapshot"/>) and built again at the segment, set-up pose and navigator playback it had reached, its tail decided
    /// afresh with the reach of a crossing that ends at rest (<see cref="TailReachFt"/>), so a live run and a restore plan the same
    /// slice. An aircraft already past the re-planned slice's end only brakes to rest (<see cref="TickBrakingToRest"/>). A re-plan
    /// that cannot build a slice puts the one it had back, with a warning, and still ends at rest.
    /// </summary>
    public void EndAtRest(PhaseContext ctx)
    {
        if (_endsAtRest)
        {
            return;
        }

        // A restored crossing not yet ticked is built first, as its first tick would build it, so a HOLD right after a restore re-plans
        // from the slice the live crossing had, as a live HOLD does.
        if (!_initialized && (_restoredNavigator is not null))
        {
            TryBuildCrossingRoute(ctx, replan: false);
        }

        _endsAtRest = true;
        if (!_initialized || (_navigator is not { } navigator) || (_crossingRoute is not { } crossingRoute))
        {
            return;
        }

        BuiltSlice kept = KeepBuiltSlice(navigator, crossingRoute);
        TearDownSlice(navigator, crossingRoute);
        TryBuildCrossingRoute(ctx, replan: true);
        if (!_initialized)
        {
            PutBackBuiltSlice(kept);
            Log.LogWarning(
                "[Crossing] {Callsign}: could not re-plan the crossing of runway {Rwy} to end at rest; it brakes to rest on the slice it had",
                ctx.Aircraft.Callsign,
                _runwayId ?? "?"
            );
            return;
        }

        if ((_crossingRoute is { IsComplete: false } slice) && (StopCapRemainingFt(ctx.Aircraft.Position) is < 0.0))
        {
            Log.LogDebug("[Crossing] {Callsign}: already past the re-planned slice end; braking to rest", ctx.Aircraft.Callsign);
            slice.CurrentSegmentIndex = slice.Segments.Count;
        }
    }

    /// <summary>A built slice's state, kept by <see cref="EndAtRest"/> so a re-plan that cannot build a slice can put it back.</summary>
    private sealed class BuiltSlice
    {
        public required GroundNavigator Navigator { get; init; }
        public required TaxiRoute CrossingRoute { get; init; }
        public required (LatLon Position, TrueHeading Heading)? SegmentSetupPose { get; init; }
        public required TailDecision? Tail { get; init; }
        public required GroundNode? HeldShortBar { get; init; }
        public required int ExitRouteIndex { get; init; }
        public required int TailClearFullSegments { get; init; }
        public required bool TailClearPartial { get; init; }
        public required double SliceEndRouteFt { get; init; }
        public required bool SliceEndsRoute { get; init; }
        public required bool LoggedStopCap { get; init; }
    }

    private BuiltSlice KeepBuiltSlice(GroundNavigator navigator, TaxiRoute crossingRoute) =>
        new()
        {
            Navigator = navigator,
            CrossingRoute = crossingRoute,
            SegmentSetupPose = _segmentSetupPose,
            Tail = _tailDecision,
            HeldShortBar = _heldShortBar,
            ExitRouteIndex = _exitRouteIndex,
            TailClearFullSegments = _tailClearFullSegments,
            TailClearPartial = _tailClearPartial,
            SliceEndRouteFt = _sliceEndRouteFt,
            SliceEndsRoute = _sliceEndsRoute,
            LoggedStopCap = _loggedStopCap,
        };

    /// <summary>
    /// Puts the built slice into the state a restore leaves it in (<see cref="FromSnapshot"/>): its segment, set-up pose and
    /// navigator kept for the next build, everything the build derives cleared, its tail left to be decided afresh.
    /// </summary>
    private void TearDownSlice(GroundNavigator navigator, TaxiRoute crossingRoute)
    {
        _restoredSegmentIndex = crossingRoute.CurrentSegmentIndex;
        _restoredSetupPose = _segmentSetupPose;
        _restoredNavigator = navigator.ToSnapshot();
        _restoredTailDecision = null;
        _restoredHeldShortBarId = null;
        _segmentSetupPose = null;
        _tailDecision = null;
        _heldShortBar = null;
        _exitRouteIndex = -1;
        _tailClearFullSegments = 0;
        _tailClearPartial = false;
        _sliceEndRouteFt = 0.0;
        _sliceEndsRoute = false;
        _loggedStopCap = false;
        _navigator = null;
        _crossingRoute = null;
        _initialized = false;
    }

    /// <summary>Puts back the slice <paramref name="kept"/> held, as it was before <see cref="TearDownSlice"/>.</summary>
    private void PutBackBuiltSlice(BuiltSlice kept)
    {
        _navigator = kept.Navigator;
        _crossingRoute = kept.CrossingRoute;
        _segmentSetupPose = kept.SegmentSetupPose;
        _tailDecision = kept.Tail;
        _heldShortBar = kept.HeldShortBar;
        _exitRouteIndex = kept.ExitRouteIndex;
        _tailClearFullSegments = kept.TailClearFullSegments;
        _tailClearPartial = kept.TailClearPartial;
        _sliceEndRouteFt = kept.SliceEndRouteFt;
        _sliceEndsRoute = kept.SliceEndsRoute;
        _loggedStopCap = kept.LoggedStopCap;
        _restoredSegmentIndex = null;
        _restoredSetupPose = null;
        _restoredNavigator = null;
        _restoredTailDecision = null;
        _restoredHeldShortBarId = null;
        _initialized = true;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is crossing a runway; only HOLD or a new TAXI apply until the crossing completes"),
        };
    }

    /// <summary>
    /// Slice the source route (own path, else <see cref="AircraftGroundState.AssignedTaxiRoute"/>) between the entry- and
    /// exit-side hold-short nodes, extend it past the exit for tail clearance, and hand the result to a new
    /// <see cref="GroundNavigator"/>. The extension runs ½ aircraft length along the route's own onward segments when it has
    /// any, else a straight-ahead walk on the layout (<see cref="AppendLayoutTailClearance"/>) for ½ length plus
    /// <see cref="RunwayTailClearance.MarginFt"/>. Idempotent — only builds the navigator once per phase instance, until a re-plan
    /// (<paramref name="replan"/>, <see cref="EndAtRest"/>) tears it down; a re-plan's navigator drops a playback the re-planned slice
    /// no longer has the segment for quietly (<see cref="GroundNavigator.ForReplan"/>).
    /// </summary>
    private void TryBuildCrossingRoute(PhaseContext ctx, bool replan)
    {
        if (_initialized || ctx.GroundLayout is null)
        {
            return;
        }

        TaxiRoute? route = SourceRoute(ctx);
        if (route is null || route.Segments.Count == 0)
        {
            return;
        }

        RunwayIdentifier? crossedRunway = _runwayId is { } runwayId ? RunwayIdentifier.Parse(runwayId) : null;
        var spec = new SliceSpec(_approachNodeId, _targetNodeId, _endsAtRest, crossedRunway);
        if (BuildSlice(ctx, route, spec, _restoredTailDecision, _restoredHeldShortBarId) is not { } plan)
        {
            Log.LogWarning(
                "[Crossing] {Callsign}: could not slice crossing segments from route (approach={Approach}, target={Target}, segments={Count})",
                ctx.Aircraft.Callsign,
                _approachNodeId,
                _targetNodeId,
                route.Segments.Count
            );
            return;
        }

        _exitRouteIndex = plan.ExitIndex;
        _tailClearFullSegments = plan.FullTailSegments;
        _tailClearPartial = plan.PartialTail;
        _tailDecision = plan.Tail;
        _heldShortBar = plan.HeldShortBar;
        _restoredTailDecision = null;
        _restoredHeldShortBarId = null;
        double tailFt = plan.Segments.Skip(plan.ExitIndex - plan.EntryIndex + 1).Sum(static s => s.Edge.DistanceNm) * GeoMath.FeetPerNm;
        _sliceEndRouteFt = route.PrefixDistanceFt(plan.ExitIndex + 1) + tailFt;
        _sliceEndsRoute = (plan.ExitIndex + 1 + plan.FullTailSegments) >= route.Segments.Count;
        WarnIfSliceEndsPastItsStop(ctx, route, plan.ExitIndex);
        // A crossing that ends at rest may be snapshotted braking past its driven slice, its segment index past the last one.
        int lastIndex = _endsAtRest ? plan.Segments.Count : plan.Segments.Count - 1;
        var crossingRoute = new TaxiRoute
        {
            Segments = plan.Segments,
            HoldShortPoints = [],
            CurrentSegmentIndex = Math.Clamp(_restoredSegmentIndex ?? 0, 0, lastIndex),
        };
        _crossingRoute = crossingRoute;

        // The saved set-up pose belongs to the saved slice segment: when the rebuilt slice no longer has that segment and
        // the index was clamped, the segment set up is a different one, and it is set up from the live pose instead.
        bool restoredIndexClamped = false;
        if (_restoredSegmentIndex is { } restoredIndex)
        {
            restoredIndexClamped = restoredIndex != crossingRoute.CurrentSegmentIndex;
            Log.LogDebug(
                "[Crossing] {Callsign}: restored slice segment {Index} applied (slice segment {Applied} of {Count})",
                ctx.Aircraft.Callsign,
                restoredIndex,
                crossingRoute.CurrentSegmentIndex,
                plan.Segments.Count
            );
            _restoredSegmentIndex = null;
        }

        // Cross at normal taxi speed and continue without delay (7110.65 §3-7-2.j, "CROSS (runway) at
        // (taxiway) WITHOUT DELAY"; AIM 4-3-21.a): a crossing is just taxiing across, so the cap is the
        // category taxi speed — not a slower "crossing speed". RunwayCrossingSpeed is kept only as a navigator
        // FLOOR so the aircraft never brakes toward a stop on the runway or at the (artificial) slice end, and
        // the off-centerline re-acquire gate can't cap it mid-crossing. Both the arc/turn-speed cap for a curve
        // in the painted line and a conflict/airport speed-limit ceiling still outrank that floor (see
        // GroundNavigator.ClampBySpeedLimit). The aircraft hands off to the onward TaxiingPhase moving; that
        // phase owns the real deceleration for the destination, except that a binding stop within braking distance
        // of the slice end lowers the floor onto the taxi-rate curve to it every tick (ApplyStopCap).
        // A restored crossing takes the navigator from the snapshot, so the set-up below resumes the primitive it was
        // playing at the progress it had reached (GroundNavigator.TryResumeRestoredPlayback).
        GroundNavigator navigator = _restoredNavigator switch
        {
            { } saved when replan => GroundNavigator.ForReplan(saved),
            { } saved => GroundNavigator.FromSnapshot(saved),
            null => new GroundNavigator(),
        };
        _navigator = navigator;
        ApplySpeedCap(ctx, navigator);
        navigator.MinSpeedKts = CategoryPerformance.RunwayCrossingSpeed(ctx.Category);
        _restoredNavigator = null;

        // A crossing that ends at rest restored past its driven slice has no segment left to set up; it only brakes
        // (TickBrakingToRest).
        bool sliceLeft = !crossingRoute.IsComplete;
        if (sliceLeft && (_restoredSetupPose is { } setupPose) && !restoredIndexClamped)
        {
            SetupRestoredSegment(ctx, navigator, crossingRoute, setupPose);
        }
        else if (sliceLeft)
        {
            SetupCurrentSegment(ctx, navigator, crossingRoute);
        }

        _restoredSetupPose = null;
        _initialized = true;

        Log.LogDebug(
            "[Crossing] {Callsign}: crossing route built, {SegCount} segments, exitRouteIdx={ExitIdx}, "
                + "tailClear={TailFull} full segment(s){Partial}, maxSpeed={Max:F0}kts floor={Floor:F0}kts",
            ctx.Aircraft.Callsign,
            plan.Segments.Count,
            _exitRouteIndex,
            _tailClearFullSegments,
            _tailClearPartial ? " + a partial cut" : "",
            _navigator.MaxSpeedKts,
            _navigator.MinSpeedKts
        );
    }

    /// <summary>
    /// Warns when the built slice ends more than <see cref="TaxiingPhase.SetBackStopTakeFt"/> past the first binding stop beyond
    /// the exit, a stop the slice is built never to pass (<see cref="AppendTailClearance"/>). A bar on the exit node itself is
    /// exempt: its stop sits back from the exit node the slice ends at, and the crossing aims at it there.
    /// </summary>
    private void WarnIfSliceEndsPastItsStop(PhaseContext ctx, TaxiRoute route, int exitIdx)
    {
        if ((_ownPath is not null) || (FindOnwardStop(route, exitIdx) is not { Bar: { } bar } stop))
        {
            return;
        }

        double gapFt = stop.StopRouteFt - _sliceEndRouteFt;
        if ((gapFt >= -TaxiingPhase.SetBackStopTakeFt) || (bar.NodeId == route.Segments[exitIdx].ToNodeId))
        {
            return;
        }

        Log.LogWarning(
            "[Crossing] {Callsign}: the crossing slice ends {Over:F1} ft past the stop for {Target} (#{Node}); it should end at or before it",
            ctx.Aircraft.Callsign,
            -gapFt,
            bar.TargetName ?? "?",
            bar.NodeId
        );
    }

    /// <summary>Set up the slice segment the crossing is on from the aircraft's pose, and remember that pose for a snapshot.</summary>
    private void SetupCurrentSegment(PhaseContext ctx, GroundNavigator navigator, TaxiRoute crossingRoute)
    {
        _segmentSetupPose = (ctx.Aircraft.Position, ctx.Aircraft.TrueHeading);
        navigator.SetupSegment(crossingRoute, ctx, _ => true);
        ApplyExitHoldShortOffset(ctx);
    }

    /// <summary>
    /// Set the restored slice segment up again from the pose the original set it up at, so the navigator plays back the
    /// same primitive (an entry-alignment turn is built from the aircraft's pose) and the restored crossing goes on as the
    /// original. The aircraft is put back where it stands before the first tick. A navigator restored with its playback
    /// resumes the saved primitive at its saved progress; one without it captures its progress on the primitive's first
    /// tick, as it does for any primitive.
    /// </summary>
    private void SetupRestoredSegment(
        PhaseContext ctx,
        GroundNavigator navigator,
        TaxiRoute crossingRoute,
        (LatLon Position, TrueHeading Heading) setupPose
    )
    {
        LatLon livePosition = ctx.Aircraft.Position;
        TrueHeading liveHeading = ctx.Aircraft.TrueHeading;
        ctx.Aircraft.Position = setupPose.Position;
        ctx.Aircraft.TrueHeading = setupPose.Heading;
        try
        {
            SetupCurrentSegment(ctx, navigator, crossingRoute);
        }
        finally
        {
            ctx.Aircraft.Position = livePosition;
            ctx.Aircraft.TrueHeading = liveHeading;
        }
    }

    /// <summary>
    /// The crossing slice and where it sits in the source route (own path, else AssignedTaxiRoute): the segments from the entry-side
    /// hold-short through the exit-side one plus the tail-clearance extension, the route index of the exit
    /// segment, the tail decision the extension was built from (<see cref="TailDecision"/>), and, for a crossing that ends at rest,
    /// the other runway's hold-short bar that set where the slice ends (null when none did).
    /// </summary>
    private sealed record CrossingSlice(List<TaxiRouteSegment> Segments, int EntryIndex, int ExitIndex, TailDecision Tail, GroundNode? HeldShortBar)
    {
        /// <summary>How many onward route segments the tail clearance consumed whole.</summary>
        public int FullTailSegments => Tail.FullSegments;

        /// <summary>Whether the tail clearance ends on a partial (virtual) cut inside the next onward segment.</summary>
        public bool PartialTail => Tail.CutFt > 0.0;
    }

    /// <summary>
    /// How the slice's tail clearance was decided at build: whether it is suppressed (the slice ends at the exit node), how many
    /// onward route segments it takes whole, and the length (ft) of the straight cut it ends on inside the next one, 0 for none.
    /// The decision reads which holds the route had cleared at build, so the crossing snapshots it and a restore applies it
    /// rather than deciding again (<see cref="AppendTailClearance"/>).
    /// </summary>
    private sealed record TailDecision(bool Suppressed, int FullSegments, double CutFt);

    /// <summary>
    /// The crossing a slice is built for: its entry-side and exit-side bar nodes, and whether it ends at rest
    /// (<see cref="EndAtRest"/>), which reaches its tail clearance <see cref="RunwayTailClearance.MarginFt"/> farther along its
    /// own route (<see cref="TailReachFt"/>) and stops it short of another runway's bar (<see cref="EndingAtRestStop"/>), and the
    /// runway it crosses. <c>CrossedRunway</c> is read only when <c>EndsAtRest</c>: <see cref="BuildSlice"/> resolves it then (the
    /// crossing's own runway, else its exit node's when that is a runway hold-short) and clears it otherwise, and the layout walk
    /// uses it when set, else the runway of the bars it crosses between.
    /// </summary>
    private sealed record SliceSpec(int ApproachNodeId, int TargetNodeId, bool EndsAtRest, RunwayIdentifier? CrossedRunway);

    /// <summary>
    /// How far (ft) past the exit node the tail clearance reaches along the route: ½ the aircraft's length, plus
    /// <see cref="RunwayTailClearance.MarginFt"/> for a crossing that ends at rest, which stops at its slice end rather than taxi
    /// on past it, so its own route carries it the margin the layout walk adds (<see cref="BuildTail"/>).
    /// </summary>
    private static double TailReachFt(PhaseContext ctx, bool endsAtRest) =>
        (AircraftLength.ResolveFt(ctx.Aircraft.AircraftType) / 2.0) + (endsAtRest ? RunwayTailClearance.MarginFt : 0.0);

    /// <summary>
    /// Build the crossing slice out of <paramref name="route"/>. Null when the route does not contain the
    /// crossing (no segment leaves the spec's approach node, or none reaches its target node after it). A pure function of the
    /// route, the layout, the aircraft's length, <paramref name="spec"/> and, for a restored crossing, the tail decision
    /// <paramref name="storedTail"/> its snapshot carries, applied with the held-short bar <paramref name="storedHeldShortBarId"/>
    /// planned with it; with none the tail and the bar are decided from the route's holds as they stand.
    /// </summary>
    private static CrossingSlice? BuildSlice(PhaseContext ctx, TaxiRoute route, SliceSpec spec, TailDecision? storedTail, int? storedHeldShortBarId)
    {
        int approachNodeId = spec.ApproachNodeId;
        int targetNodeId = spec.TargetNodeId;

        // Find the slice [entryIdx..exitIdx] in the existing route:
        //   entryIdx: first segment whose FromNodeId == approachNodeId
        //   exitIdx:  first segment at or after entryIdx whose ToNodeId == targetNodeId
        int entryIdx = -1;
        int exitIdx = -1;
        for (int i = 0; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            if (entryIdx < 0 && seg.FromNodeId == approachNodeId)
            {
                entryIdx = i;
            }
            if (entryIdx >= 0 && seg.ToNodeId == targetNodeId)
            {
                exitIdx = i;
                break;
            }
        }

        if (entryIdx < 0 || exitIdx < 0)
        {
            return null;
        }

        // The one source of the crossed runway for a crossing that ends at rest: its own, else its exit node's when that is a bar.
        RunwayIdentifier? exitBarRunway = route.Segments[exitIdx].Edge.ToNode is { Type: GroundNodeType.RunwayHoldShort, RunwayId: { } exitRunway }
            ? exitRunway
            : null;
        spec = spec with { CrossedRunway = spec.EndsAtRest ? (spec.CrossedRunway ?? exitBarRunway) : null };
        var slice = new List<TaxiRouteSegment>(capacity: exitIdx - entryIdx + 3);
        for (int i = entryIdx; i <= exitIdx; i++)
        {
            slice.Add(route.Segments[i]);
        }

        (List<TaxiRouteSegment> tail, TailDecision decision, GroundNode? walkBar, bool storedApplied) = AppendTailClearance(
            ctx,
            route,
            spec,
            exitIdx,
            storedTail
        );
        slice.AddRange(tail);
        GroundNode? heldShortBar = null;
        if (spec.EndsAtRest)
        {
            // A stored tail comes with the bar planned with it: the route's bar (RouteStopBar) reads holds cleared since.
            heldShortBar = storedApplied ? StoredHeldShortBar(ctx, storedHeldShortBarId) : (walkBar ?? RouteStopBar(ctx, route, exitIdx, spec));
        }

        return new CrossingSlice(slice, entryIdx, exitIdx, decision, heldShortBar);
    }

    /// <summary>
    /// The held-short bar <paramref name="nodeId"/> a snapshot carried with its tail decision; null when it carried none, and, with a
    /// warning, when the layout has no such node.
    /// </summary>
    private static GroundNode? StoredHeldShortBar(PhaseContext ctx, int? nodeId)
    {
        if (nodeId is not { } id)
        {
            return null;
        }

        if ((ctx.GroundLayout is { } layout) && layout.Nodes.TryGetValue(id, out GroundNode? bar))
        {
            return bar;
        }

        Log.LogWarning("[Crossing] {Callsign}: the stored held-short bar #{Bar} is not in the layout; planning with none", ctx.Aircraft.Callsign, id);
        return null;
    }

    /// <summary>
    /// The other runway's hold-short bar at the stop a crossing that ends at rest brakes onto along its route
    /// (<see cref="EndingAtRestStop"/>), when that stop lies inside the tail's reach past the exit (<see cref="TailReachFt"/>), so
    /// that it, not the reach, sets where the slice ends; else null. It reads which holds the route has cleared
    /// (<see cref="FindOnwardStop"/>), so a restore that applies its stored tail decision takes the bar its snapshot carries with it
    /// instead (<see cref="BuildSlice"/>).
    /// </summary>
    private static GroundNode? RouteStopBar(PhaseContext ctx, TaxiRoute route, int exitIdx, SliceSpec spec)
    {
        EndingStop stop = EndingAtRestStop(route, exitIdx, AircraftLength.ResolveFt(ctx.Aircraft.AircraftType), spec.CrossedRunway);
        double reachEndFt = route.PrefixDistanceFt(exitIdx + 1) + TailReachFt(ctx, endsAtRest: true);
        return (stop.StopFt < reachEndFt) ? stop.OtherRunwayBar : null;
    }

    /// <summary>
    /// The tail-clearance extension ½ an aircraft length past the exit node so the tail clears the runway, along the
    /// route's own onward segments when it has any, else a straight-ahead walk on the layout
    /// (<see cref="AppendLayoutTailClearance"/>). The route's own segments come first because the graph's straightest
    /// continuation leaves the painted line wherever the route turns right after the crossing (SFO G → B across 01L/19R: a
    /// 121 ft straight extension along G ran ~55 ft wide of the fillet the aircraft was about to fly). A straight segment the
    /// ½-length boundary falls inside is cut at the exact distance with a virtual node; an arc (or a straight carrying shape
    /// points) is taken whole, because a free-space cut across a curve puts the aircraft off the centerline again and the
    /// short overshoot is harmless. The ½ length is what it takes for the tail to cross the holding position marking: an
    /// aircraft is not clear of the runway until every part of it has crossed that marking (AIM 2-3-5.a.1, AIM 4-3-21.b).
    /// When the route ends on the crossed runway's far bar (a CROSS of the destination runway) or runs out inside the ½
    /// length past it, the walk continues from the route's last segment for what is left plus
    /// <see cref="RunwayTailClearance.MarginFt"/>. The extension never passes the first binding stop past the exit
    /// (<see cref="FindOnwardStop"/>): a straight segment is cut at it, and an arc or shaped segment whose end lies past it is
    /// not taken. A restored crossing applies the decision it was snapshotted with (<paramref name="storedTail"/>) rather
    /// than deciding again, since the holds it reads may have been cleared since; one that no longer fits the route is
    /// decided again, with a warning. Returns the extension's segments, the decision they were built from, and the other runway's
    /// bar the layout walk cut its end short of (null when it cut at none), and whether the stored decision was applied.
    /// </summary>
    private static (List<TaxiRouteSegment> Tail, TailDecision Decision, GroundNode? WalkBar, bool StoredApplied) AppendTailClearance(
        PhaseContext ctx,
        TaxiRoute route,
        SliceSpec spec,
        int exitIdx,
        TailDecision? storedTail
    )
    {
        if (storedTail is { } stored)
        {
            if (StoredTailFits(route, exitIdx, stored))
            {
                (List<TaxiRouteSegment> storedSegments, GroundNode? storedWalkBar) = BuildTail(ctx, route, spec, exitIdx, stored);
                return (storedSegments, stored, storedWalkBar, true);
            }

            Log.LogWarning(
                "[Crossing] {Callsign}: the restored tail clearance ({Full} whole segment(s), cut {Cut:F1} ft) no longer fits the route; "
                    + "deciding it again",
                ctx.Aircraft.Callsign,
                stored.FullSegments,
                stored.CutFt
            );
        }

        TailDecision decision = DecideTail(ctx, route, exitIdx, spec);
        (List<TaxiRouteSegment> segments, GroundNode? walkBar) = BuildTail(ctx, route, spec, exitIdx, decision);
        return (segments, decision, walkBar, false);
    }

    /// <summary>
    /// Whether a stored tail decision still fits <paramref name="route"/> past the exit segment <paramref name="exitIdx"/>: the
    /// whole segments it takes exist, and so does the straight segment its cut lies in.
    /// </summary>
    private static bool StoredTailFits(TaxiRoute route, int exitIdx, TailDecision tail)
    {
        int lastIndex = exitIdx + tail.FullSegments + ((tail.CutFt > 0.0) ? 1 : 0);
        return tail.Suppressed
            || (
                (tail.FullSegments >= 0)
                && (lastIndex < route.Segments.Count)
                && ((tail.CutFt <= 0.0) || (route.Segments[lastIndex].Edge.Edge is GroundEdge { IntermediatePoints.Count: 0 }))
            );
    }

    /// <summary>
    /// Decides the tail clearance from the route as it stands (see <see cref="AppendTailClearance"/>): suppressed, or how many
    /// onward segments it takes whole and the straight cut it ends on, reaching <see cref="TailReachFt"/> past the exit.
    /// </summary>
    private static TailDecision DecideTail(PhaseContext ctx, TaxiRoute route, int exitIdx, SliceSpec spec)
    {
        double lengthFt = AircraftLength.ResolveFt(ctx.Aircraft.AircraftType);
        if (TailClearanceSuppressed(ctx, route, exitIdx, lengthFt))
        {
            return new TailDecision(true, 0, 0.0);
        }

        // The slice never drives over the first binding stop past the exit: a segment whose end lies past that stop is cut at
        // it when straight, and ends the slice where it starts when it is an arc or a shaped segment, which is taken whole or
        // not at all. The onward taxi then brakes onto that stop. A crossing that ends at rest has its own stop.
        double stopRouteFt = spec.EndsAtRest
            ? EndingAtRestStop(route, exitIdx, lengthFt, spec.CrossedRunway).StopFt
            : FindOnwardStop(route, exitIdx).StopRouteFt;
        int fullSegments = 0;
        double remainingNm = TailReachFt(ctx, spec.EndsAtRest) / GeoMath.FeetPerNm;
        for (int i = exitIdx + 1; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            double segStartFt = route.PrefixDistanceFt(i);
            bool endsPastStop = route.PrefixDistanceFt(i + 1) > stopRouteFt;
            if ((seg.Edge.DistanceNm <= remainingNm) && !endsPastStop)
            {
                fullSegments++;
                remainingNm -= seg.Edge.DistanceNm;
                continue;
            }

            if (seg.Edge.Edge is GroundEdge { IntermediatePoints.Count: 0 })
            {
                double cutNm = endsPastStop ? Math.Min(remainingNm, (stopRouteFt - segStartFt) / GeoMath.FeetPerNm) : remainingNm;
                return new TailDecision(false, fullSegments, Math.Max(0.0, cutNm) * GeoMath.FeetPerNm);
            }

            return new TailDecision(false, endsPastStop ? fullSegments : fullSegments + 1, 0.0);
        }

        return new TailDecision(false, fullSegments, 0.0);
    }

    /// <summary>
    /// The stop (route ft) a crossing that ends at rest (<see cref="EndAtRest"/>) never drives its tail clearance past: the nearer of
    /// the first binding stop past the exit (<see cref="FindOnwardStop"/>) and the point where the nose, ½ the aircraft's
    /// <paramref name="lengthFt"/> ahead, is <see cref="GroundNavigator.SetBackStopMarginFt"/> short of the first hold-short bar from
    /// the exit node on of a runway other than <paramref name="crossed"/> (<see cref="IsOtherRunwaysBar"/>), cleared or not. The reach past the exit
    /// (<see cref="TailReachFt"/>) bounds it too. The crossing makes its best effort to clear the crossed runway, but its nose never
    /// passes another runway's hold bar: a type that fits between the bars ends clear of both, and one too long rests with its
    /// nose short of the next runway's bar and its tail over the crossed runway (owner ruling). Returns the stop and the other
    /// runway's hold-short bar that set it (<see cref="EndingStop"/>).
    /// </summary>
    private static EndingStop EndingAtRestStop(TaxiRoute route, int exitIdx, double lengthFt, RunwayIdentifier? crossed)
    {
        OnwardStop onward = FindOnwardStop(route, exitIdx);
        GroundNode? onwardBar =
            (onward.Bar is not null)
            && (route.Segments[onward.BarSegmentIndex].Edge.ToNode is { Type: GroundNodeType.RunwayHoldShort } onwardNode)
            && ((crossed is not { } onwardCrossed) || IsOtherRunwaysBar(onwardNode, onwardCrossed))
                ? onwardNode
                : null;
        var stop = new EndingStop(onward.StopRouteFt, onwardBar);
        if (crossed is { } crossedRunway)
        {
            int barIndex = route.Segments.FindIndex(exitIdx, s => IsOtherRunwaysBar(s.Edge.ToNode, crossedRunway));
            double barStopFt =
                (barIndex >= 0)
                    ? route.PrefixDistanceFt(barIndex + 1) - (lengthFt / 2.0) - GroundNavigator.SetBackStopMarginFt
                    : double.PositiveInfinity;
            if (barStopFt < stop.StopFt)
            {
                stop = new EndingStop(barStopFt, route.Segments[barIndex].Edge.ToNode);
            }
        }

        return stop;
    }

    /// <summary>
    /// The stop (route ft) of a crossing that ends at rest (<see cref="EndingAtRestStop"/>) and the hold-short bar of a runway other
    /// than the crossed one that set it: the route's binding stop at such a bar, or the nose's stop short of the first such bar past
    /// the exit. Null for a stop no other runway's bar set (a taxiway hold short, the route's end).
    /// </summary>
    private sealed record EndingStop(double StopFt, GroundNode? OtherRunwayBar);

    /// <summary>
    /// The tail-clearance segments <paramref name="tail"/> describes: its whole onward segments, then its straight cut, or,
    /// when every whole segment fit inside the ½ length and the route ran out past the crossed runway's far bar, the layout
    /// walk for what is left (<see cref="AppendLayoutTailClearance"/>). The walk is a pure function of the route and the
    /// layout, so it is built again rather than stored. Returns the segments and the other runway's bar the walk cut its end short of,
    /// null when it cut at none.
    /// </summary>
    private static (List<TaxiRouteSegment> Segments, GroundNode? WalkBar) BuildTail(
        PhaseContext ctx,
        TaxiRoute route,
        SliceSpec spec,
        int exitIdx,
        TailDecision tail
    )
    {
        List<TaxiRouteSegment> segments = [];
        if (tail.Suppressed)
        {
            return (segments, null);
        }

        double remainingNm = TailReachFt(ctx, spec.EndsAtRest) / GeoMath.FeetPerNm;
        bool allFit = true;
        for (int k = 0; k < tail.FullSegments; k++)
        {
            TaxiRouteSegment seg = route.Segments[exitIdx + 1 + k];
            allFit &= seg.Edge.DistanceNm <= remainingNm;
            remainingNm -= seg.Edge.DistanceNm;
            segments.Add(seg);
        }

        int nextIdx = exitIdx + 1 + tail.FullSegments;
        if (tail.CutFt > 0.0)
        {
            TaxiRouteSegment cut = route.Segments[nextIdx];
            segments.Add(CutStraight(cut.Edge, tail.CutFt / GeoMath.FeetPerNm, cut.TaxiwayName));
            return (segments, null);
        }

        // The route runs out within the tail-clearance distance past the crossed runway's far bar: walk the layout straight ahead.
        // Otherwise the route ends inside the tail-clearance distance, and the slice stops at its end rather than driving past it.
        if (
            allFit
            && (nextIdx >= route.Segments.Count)
            && (ctx.GroundLayout is { } layout)
            && (ExitIsCrossedRunwayBar(layout, spec.ApproachNodeId, route.Segments[exitIdx].ToNodeId) is { } crossed)
        )
        {
            // A crossing that ends at rest has the margin in its reach already (TailReachFt).
            double walkNm = spec.EndsAtRest ? remainingNm : remainingNm + (RunwayTailClearance.MarginFt / GeoMath.FeetPerNm);
            return (segments, AppendLayoutTailClearance(ctx, route.Segments[^1].Edge, walkNm, spec.CrossedRunway ?? crossed, segments));
        }

        return (segments, null);
    }

    /// <summary>
    /// The runway being crossed when the crossing's exit node is one of its hold-short bars (the same runway as the bar the
    /// crossing starts from), else null.
    /// </summary>
    private static RunwayIdentifier? ExitIsCrossedRunwayBar(AirportGroundLayout layout, int approachNodeId, int exitNodeId) =>
        (
            layout.Nodes.TryGetValue(approachNodeId, out GroundNode? approach)
            && layout.Nodes.TryGetValue(exitNodeId, out GroundNode? exit)
            && (exit.Type == GroundNodeType.RunwayHoldShort)
            && (approach.RunwayId is { } crossed)
            && (exit.RunwayId is { } exitRunway)
            && exitRunway.Equals(crossed)
        )
            ? crossed
            : null;

    /// <summary>
    /// Extend <paramref name="slice"/> from the end of <paramref name="arrivedOn"/> along the layout straight ahead for
    /// <paramref name="remainingNm"/>, so the tail ends <see cref="RunwayTailClearance.MarginFt"/> past the far bar
    /// (the YAAT-905 bare-crossing end's distance). Each step takes the edge whose whole heading change (to its start and
    /// to its end bearing) from the one arrived along is least, up to <see cref="MaxTailWalkTurnDeg"/>, never a runway edge.
    /// A straight the remaining distance falls inside is cut at it: a plain straight at the exact distance, a straight
    /// with shape points along its shape points (measured along them); an arc is taken whole. The walk stops short, leaving
    /// the aircraft where it is, rather than drive to a runway centerline node, or where no edge qualifies. It never ends with
    /// the nose, ½ the aircraft's length on along the walked path, past another runway's hold-short bar: it is cut that far and
    /// <see cref="GroundNavigator.SetBackStopMarginFt"/> more short of the bar, to nothing when that falls before its start, and an
    /// arc that would carry the nose past that point is not
    /// taken. The extra segments live only in the crossing's slice, never in the aircraft's route, so the slice is still
    /// recomputable from route, layout and length. Returns the other runway's bar the end was cut short of, null when none was.
    /// </summary>
    private static GroundNode? AppendLayoutTailClearance(
        PhaseContext ctx,
        DirectionalEdge arrivedOn,
        double remainingNm,
        RunwayIdentifier crossed,
        List<TaxiRouteSegment> slice
    )
    {
        double halfLengthNm = (AircraftLength.ResolveFt(ctx.Aircraft.AircraftType) / 2.0) / GeoMath.FeetPerNm;
        TailWalk walk = WalkAhead(ctx, arrivedOn, remainingNm, halfLengthNm, crossed);
        double endNm = walk.EndNm;
        double noseLimitNm = double.PositiveInfinity;
        GroundNode? cutAtBar = null;
        if ((walk.Bar is { } bar) && (walk.BarNm is { } barNm))
        {
            noseLimitNm = barNm - halfLengthNm - (GroundNavigator.SetBackStopMarginFt / GeoMath.FeetPerNm);
            if (noseLimitNm < endNm)
            {
                LogTailWalkBarStop(ctx, bar, remainingNm - Math.Max(0.0, noseLimitNm));
                endNm = noseLimitNm;
                cutAtBar = bar;
            }
        }

        AppendWalkedPath(walk.Path, endNm, noseLimitNm, slice);
        return cutAtBar;
    }

    /// <summary>
    /// A layout walk past the far bar (<see cref="WalkAhead"/>): the edges walked, how far along them the walk ends (its reach,
    /// or a stop node short of it), and the first hold-short bar of another runway it met, if any, with its distance along them
    /// (null when it met none).
    /// </summary>
    private sealed record TailWalk(List<DirectionalEdge> Path, double EndNm, GroundNode? Bar, double? BarNm);

    /// <summary>
    /// Walks the layout straight ahead from the end of <paramref name="arrivedOn"/> (<see cref="StraightestContinuation"/>) as far
    /// as the walk's reach, <paramref name="remainingNm"/>, and the nose <paramref name="halfLengthNm"/> beyond it, stopping at the
    /// first hold-short bar of a runway other than <paramref name="crossed"/> (<see cref="IsOtherRunwaysBar"/>), before an edge to a
    /// runway centerline node, and where no edge qualifies. A stop short of the reach ends the walk there, with a warning.
    /// </summary>
    private static TailWalk WalkAhead(PhaseContext ctx, DirectionalEdge arrivedOn, double remainingNm, double halfLengthNm, RunwayIdentifier crossed)
    {
        List<DirectionalEdge> path = [];
        double alongNm = 0.0;
        while (alongNm < (remainingNm + halfLengthNm))
        {
            GroundNode node = arrivedOn.ToNode;
            DirectionalEdge? next = StraightestContinuation(node, arrivedOn);
            if ((next is null) || IsOnRunwayCenterline(next.ToNode))
            {
                if (alongNm < remainingNm)
                {
                    LogTailWalkStop(ctx, node, remainingNm - alongNm, WalkStopReason(node, next));
                }

                return new TailWalk(path, Math.Min(alongNm, remainingNm), null, null);
            }

            path.Add(next);
            alongNm += WalkLengthNm(next);
            if (IsOtherRunwaysBar(next.ToNode, crossed))
            {
                return new TailWalk(path, remainingNm, next.ToNode, alongNm);
            }

            arrivedOn = next;
        }

        return new TailWalk(path, remainingNm, null, null);
    }

    /// <summary>
    /// Why the layout walk stops at <paramref name="node"/>: no edge qualifies there, or <paramref name="next"/> leads onto a runway.
    /// </summary>
    private static string WalkStopReason(GroundNode node, DirectionalEdge? next) =>
        next is not null ? $"the next node #{next.ToNodeId} is on a runway centerline"
        : VirtualNode.IsVirtualNode(node) ? "the route ends off the graph"
        : $"no edge within {MaxTailWalkTurnDeg:F0}° of straight ahead";

    /// <summary>
    /// Appends <paramref name="path"/> to <paramref name="slice"/> up to <paramref name="endNm"/> along it, cutting the edge the end
    /// falls inside (<see cref="AppendWalkCut"/>), except an arc, taken whole, whose end lies past <paramref name="noseLimitNm"/>.
    /// Appends nothing when <paramref name="endNm"/> is not past the start.
    /// </summary>
    private static void AppendWalkedPath(List<DirectionalEdge> path, double endNm, double noseLimitNm, List<TaxiRouteSegment> slice)
    {
        double doneNm = 0.0;
        foreach (DirectionalEdge edge in path)
        {
            if (doneNm >= endNm)
            {
                return;
            }

            double lengthNm = WalkLengthNm(edge);
            if (lengthNm > (endNm - doneNm))
            {
                if ((edge.Edge is GroundEdge) || ((doneNm + lengthNm) <= noseLimitNm))
                {
                    AppendWalkCut(edge, endNm - doneNm, slice);
                }

                return;
            }

            slice.Add(new TaxiRouteSegment { TaxiwayName = edge.TaxiwayName, Edge = edge });
            doneNm += lengthNm;
        }
    }

    /// <summary>
    /// The edge out of <paramref name="node"/> whose whole heading change from <paramref name="arrivedOn"/>'s arrival bearing is
    /// least, if that change is at most <see cref="MaxTailWalkTurnDeg"/>; never the edge arrived along, never a runway edge. An
    /// edge's change is the larger of the turns to its departure and to its arrival bearing: a fillet starts tangent to its
    /// taxiway, so its initial bearing alone ties it with the straight edge it leaves and hides the turn it ends in. An exact
    /// tie goes to the edge whose far node has the lower id, so the choice never depends on edge order between different end
    /// nodes.
    /// </summary>
    private static DirectionalEdge? StraightestContinuation(GroundNode node, DirectionalEdge arrivedOn)
    {
        DirectionalEdge? best = null;
        double bestTurnDeg = MaxTailWalkTurnDeg;
        foreach (IGroundEdge edge in node.Edges)
        {
            if (ReferenceEquals(edge, arrivedOn.Edge) || IsRunwayEdge(edge))
            {
                continue;
            }

            DirectionalEdge candidate = edge.Directed(node, edge.OtherNode(node));
            double turnDeg = Math.Max(
                GeoMath.AbsBearingDifference(arrivedOn.ArrivalBearing, candidate.DepartureBearing),
                GeoMath.AbsBearingDifference(arrivedOn.ArrivalBearing, candidate.ArrivalBearing)
            );
            bool better = (best is null) || (turnDeg < bestTurnDeg) || ((turnDeg == bestTurnDeg) && (candidate.ToNodeId < best.ToNodeId));
            if ((turnDeg <= MaxTailWalkTurnDeg) && better)
            {
                best = candidate;
                bestTurnDeg = turnDeg;
            }
        }

        return best;
    }

    /// <summary>An edge on a runway: a centerline segment, a <c>RWY…:link</c> crossing connector, or an arc onto either.</summary>
    private static bool IsRunwayEdge(IGroundEdge edge) =>
        edge switch
        {
            GroundArc arc => arc.TaxiwayNames.Any(static n => n.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)),
            _ => edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase),
        };

    /// <summary>Whether <paramref name="node"/> is a hold-short bar of a runway other than <paramref name="crossed"/>.</summary>
    private static bool IsOtherRunwaysBar(GroundNode node, RunwayIdentifier crossed) =>
        (node.Type == GroundNodeType.RunwayHoldShort) && !((node.RunwayId is { } runway) && runway.Equals(crossed));

    /// <summary>Whether <paramref name="node"/> lies on a runway centerline, so the layout walk must never drive to it.</summary>
    private static bool IsOnRunwayCenterline(GroundNode node) => node.Edges.Exists(static e => e.IsRunwayCenterline);

    /// <summary>
    /// How far the layout walk travels along <paramref name="edge"/>: a straight with shape points along its polyline, which is
    /// longer than its node-to-node <see cref="DirectionalEdge.DistanceNm"/>; any other edge its <c>DistanceNm</c>.
    /// </summary>
    private static double WalkLengthNm(DirectionalEdge edge) =>
        edge.Edge is GroundEdge { IntermediatePoints.Count: > 0 } shaped
            ? PolylineNm(EdgeGeometry.PointsFrom(shaped, edge.FromNode))
            : edge.DistanceNm;

    private static double PolylineNm(List<LatLon> points)
    {
        double totalNm = 0;
        for (int i = 1; i < points.Count; i++)
        {
            totalNm += GeoMath.DistanceNm(points[i - 1], points[i]);
        }

        return totalNm;
    }

    /// <summary>
    /// Append the part of <paramref name="edge"/> the remaining distance covers: a plain straight cut at it, a straight with
    /// shape points cut along its shape points, an arc whole.
    /// </summary>
    private static void AppendWalkCut(DirectionalEdge edge, double remainingNm, List<TaxiRouteSegment> slice)
    {
        switch (edge.Edge)
        {
            case GroundEdge { IntermediatePoints.Count: 0 }:
                slice.Add(CutStraight(edge, remainingNm, edge.TaxiwayName));
                return;
            case GroundEdge shaped:
                AppendShapedCut(shaped, edge, remainingNm, slice);
                return;
            default:
                slice.Add(new TaxiRouteSegment { TaxiwayName = edge.TaxiwayName, Edge = edge });
                return;
        }
    }

    /// <summary>
    /// Append <paramref name="edge"/> (a straight with shape points) cut <paramref name="remainingNm"/> along its shape points:
    /// one virtual segment per shape-point leg up to the leg the distance ends in, then a virtual segment to the cut point on
    /// that leg. The caller cuts only an edge whose polyline (<see cref="WalkLengthNm"/>) is longer than the distance, so the
    /// last leg always holds the cut; a zero-length leg (a duplicated shape point) is skipped.
    /// </summary>
    private static void AppendShapedCut(GroundEdge shaped, DirectionalEdge edge, double remainingNm, List<TaxiRouteSegment> slice)
    {
        List<LatLon> points = EdgeGeometry.PointsFrom(shaped, edge.FromNode);
        GroundNode from = edge.FromNode;
        double leftNm = remainingNm;
        for (int i = 1; i < points.Count; i++)
        {
            LatLon a = points[i - 1];
            LatLon b = points[i];
            double legNm = GeoMath.DistanceNm(a, b);
            if (legNm <= 0)
            {
                continue;
            }

            if ((legNm >= leftNm) || (i == points.Count - 1))
            {
                (double lat, double lon) = GeoMath.ProjectPointRaw(a.Lat, a.Lon, GeoMath.BearingTo(a, b), Math.Min(leftNm, legNm));
                slice.Add(VirtualNode.CreateSegment(from, VirtualNode.Create(lat, lon), edge.TaxiwayName));
                return;
            }

            GroundNode shapePoint = VirtualNode.Create(b.Lat, b.Lon);
            slice.Add(VirtualNode.CreateSegment(from, shapePoint, edge.TaxiwayName));
            from = shapePoint;
            leftNm -= legNm;
        }
    }

    /// <summary>A virtual segment along the plain straight <paramref name="edge"/> from its start, <paramref name="remainingNm"/> long.</summary>
    private static TaxiRouteSegment CutStraight(DirectionalEdge edge, double remainingNm, string taxiwayName)
    {
        GroundNode from = edge.FromNode;
        (double lat, double lon) = GeoMath.ProjectPointRaw(from.Position.Lat, from.Position.Lon, edge.DepartureBearing, remainingNm);
        return VirtualNode.CreateSegment(from, VirtualNode.Create(lat, lon), taxiwayName);
    }

    private static void LogTailWalkStop(PhaseContext ctx, GroundNode node, double remainingNm, string reason) =>
        Log.LogWarning(
            "[Crossing] {Callsign}: tail-clearance walk past the far bar stopped {Short:F0} ft short at node #{Node}: {Reason}",
            ctx.Aircraft.Callsign,
            remainingNm * GeoMath.FeetPerNm,
            node.Id,
            reason
        );

    private static void LogTailWalkBarStop(PhaseContext ctx, GroundNode bar, double remainingNm) =>
        Log.LogWarning(
            "[Crossing] {Callsign}: tail-clearance walk past the far bar stopped {Short:F0} ft short of another runway's bar #{BarId}",
            ctx.Aircraft.Callsign,
            remainingNm * GeoMath.FeetPerNm,
            bar.Id
        );

    /// <summary>
    /// Whether the ½-length tail-clearance extension must be suppressed, so the crossing ends at the exit
    /// node itself. Two cases, both hold-short compliance outranking tail clearance:
    /// a binding hold-short within a fuselage length past the exit (the aircraft cannot fit between the runway
    /// and that line — issue #172; overshooting would carry it past the hold line and force a ~180° reversal
    /// back to it), and an exit node that is itself a binding hold-short (see
    /// <see cref="ExitIsBindingHoldShort"/>).
    /// </summary>
    private static bool TailClearanceSuppressed(PhaseContext ctx, TaxiRoute route, int exitIdx, double lengthFt)
    {
        if (ctx.GroundLayout is null || !ctx.GroundLayout.Nodes.ContainsKey(route.Segments[exitIdx].ToNodeId))
        {
            return true;
        }

        return BindingHoldShortWithin(route, exitIdx, lengthFt / GeoMath.FeetPerNm, ctx.GroundLayout)
            || ExitIsBindingHoldShort(route, route.Segments[exitIdx].ToNodeId);
    }

    /// <summary>
    /// The index <see cref="TaxiRoute.CurrentSegmentIndex"/> will hold once the crossing from
    /// <paramref name="approachNodeId"/> to <paramref name="targetNodeId"/> completes — the first segment
    /// the tail-clearance extension does not finish. <see cref="TaxiingPhase"/>'s builders read this to
    /// decide whether a <see cref="TaxiingPhase"/> follows the crossing <i>without</i> moving the cursor;
    /// the crossing phase is the only writer (see <see cref="HandRouteBack"/>). Falls back to the current
    /// cursor when the route does not contain the crossing, so an unsliceable crossing still resumes taxiing.
    /// </summary>
    internal static int RouteIndexAfterCrossing(PhaseContext ctx, TaxiRoute route, int approachNodeId, int targetNodeId)
    {
        // A planning read for the phases that follow the crossing, made before it is built: it decides the tail afresh.
        var spec = new SliceSpec(approachNodeId, targetNodeId, EndsAtRest: false, CrossedRunway: null);
        if (BuildSlice(ctx, route, spec, storedTail: null, storedHeldShortBarId: null) is not { } plan)
        {
            return route.CurrentSegmentIndex;
        }

        return Math.Min(plan.ExitIndex + 1 + plan.FullTailSegments, route.Segments.Count);
    }

    /// <summary>
    /// Hand the aircraft's own route back at the segment it is standing on when the crossing ends: the exit
    /// segment plus every onward segment the tail-clearance extension drove to the end of. With a partial
    /// cut that is the segment the aircraft is standing on mid-way, which the navigator's I8 entry capture
    /// resumes from the live position. This is the ONLY write of
    /// <see cref="TaxiRoute.CurrentSegmentIndex"/> for a runway crossing — <see cref="TaxiingPhase"/>'s
    /// builders advance the cursor only past the segment the aircraft arrived at the end of, and never walk
    /// it across the crossing slice, so the cursor can no longer point at a segment already behind the nose.
    /// </summary>
    private void HandRouteBack(PhaseContext ctx)
    {
        // A crossing over its own path has no route of the aircraft's to hand back.
        if (_ownPath is not null)
        {
            return;
        }

        TaxiRoute? route = ctx.Aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return;
        }

        int exitIdx = _exitRouteIndex;
        if (exitIdx < 0)
        {
            // The slice never built (no layout, or a route that does not contain the crossing). Fall back to
            // the exit segment found by node id alone, so the onward taxi still starts past the runway.
            exitIdx = route.Segments.FindIndex(s => s.ToNodeId == _targetNodeId);
        }

        if (exitIdx < 0)
        {
            return;
        }

        route.CurrentSegmentIndex = Math.Min(exitIdx + 1 + _tailClearFullSegments, route.Segments.Count);
    }

    /// <summary>
    /// Whether the crossing's own exit node carries a binding (uncleared) hold-short — the parallel-runway case
    /// where the far side of the runway being crossed <i>is</i> the next runway's hold line. The ½-length tail-clearance overshoot
    /// must be suppressed there for the same
    /// reason it is for a binding taxiway hold-short: hold-short compliance outranks tail clearance, and
    /// overshooting puts the aircraft inside the holding position markings of a runway it has no clearance to
    /// enter (AIM 4-3-18.a.5). Tail-over-runway exposure is reported by <see cref="HoldShortPoint.TailOverRunwayNodeId"/>.
    /// </summary>
    private static bool ExitIsBindingHoldShort(TaxiRoute route, int targetNodeId) => route.GetHoldShortAt(targetNodeId) is { IsCleared: false };

    /// <summary>
    /// Snap the navigator's target to the exit hold-short's nose-at-line position once the crossing is aimed at
    /// it. <see cref="TaxiingPhase.SetupCurrentSegment"/> does the same on the ordinary arrival path; without it
    /// the crossing stops at the node itself, which sits on the painted bar rather than a fuselage short of it.
    /// </summary>
    private void ApplyExitHoldShortOffset(PhaseContext ctx)
    {
        if (_navigator is null || _navigator.TargetNodeId != _targetNodeId)
        {
            return;
        }

        TaxiRoute? route = SourceRoute(ctx);
        if (route is null)
        {
            return;
        }

        if (route.GetHoldShortAt(_targetNodeId) is { IsCleared: false, Latitude: { } lat, Longitude: { } lon })
        {
            _navigator.OverrideTargetPosition(ctx, lat, lon);
        }
    }

    /// <summary>
    /// Whether the route has a binding (uncleared) hold-short — taxiway or runway — within
    /// <paramref name="withinNm"/> downstream of the crossing exit (segment index <paramref name="exitIdx"/>).
    /// A hold-short this close means the aircraft cannot fit a full fuselage between the runway it just crossed
    /// and that line, so the ½-length tail-clearance overshoot must be suppressed. A runway holding position
    /// binds exactly as a taxiway one does: driving up to it, or taking the next segment whole, puts the nose
    /// inside the markings of a runway the aircraft has no clearance to enter (AIM 2-3-5.a.1, AIM 4-3-18.a.5).
    /// A cleared one — the next runway of a multi-runway crossing — does not bind, so that crossing proceeds.
    /// </summary>
    private static bool BindingHoldShortWithin(TaxiRoute route, int exitIdx, double withinNm, AirportGroundLayout layout)
    {
        double accumulated = 0;
        for (int i = exitIdx + 1; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            if (!layout.Nodes.TryGetValue(seg.FromNodeId, out GroundNode? from) || !layout.Nodes.TryGetValue(seg.ToNodeId, out GroundNode? to))
            {
                break;
            }

            accumulated += GeoMath.DistanceNm(from.Position, to.Position);

            HoldShortPoint? hs = route.GetHoldShortAt(seg.ToNodeId);
            if (hs is not null && !hs.IsCleared && accumulated <= withinNm)
            {
                return true;
            }

            if (accumulated > withinNm)
            {
                break;
            }
        }

        return false;
    }

    public override PhaseDto ToSnapshot() =>
        new CrossingRunwayPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            ApproachNodeId = _approachNodeId,
            TargetNodeId = _targetNodeId,
            CrossingRunwayId = _runwayId,
            Initialized = _initialized,
            TimeSinceLastLog = _timeSinceLastLog,
            Navigator = _navigator?.ToSnapshot() ?? _restoredNavigator,
            // A restored crossing not yet ticked has built no slice: it still holds the segment and set-up pose it was
            // restored with, which a snapshot taken now must carry on.
            CrossingRouteSegmentIndex = _crossingRoute?.CurrentSegmentIndex ?? _restoredSegmentIndex ?? 0,
            SegmentSetupLat = (_segmentSetupPose ?? _restoredSetupPose)?.Position.Lat,
            SegmentSetupLon = (_segmentSetupPose ?? _restoredSetupPose)?.Position.Lon,
            SegmentSetupHeadingDeg = (_segmentSetupPose ?? _restoredSetupPose)?.Heading.Degrees,
            OwnPath = _ownPath?.ToSnapshot(),
            NotClearRestSeconds = _notClearRestSeconds > 0.0 ? _notClearRestSeconds : null,
            SaidNotClear = _saidNotClear ? true : null,
            IsExpedited = _expedited ? true : null,
            TailClearanceSuppressed = (_tailDecision ?? _restoredTailDecision)?.Suppressed,
            TailFullSegmentCount = (_tailDecision ?? _restoredTailDecision)?.FullSegments,
            TailCutFt = (_tailDecision ?? _restoredTailDecision)?.CutFt,
            HeldShortBarNodeId = (_tailDecision is not null) ? _heldShortBar?.Id : _restoredHeldShortBarId,
            EndsAtRest = _endsAtRest ? true : null,
        };

    public static CrossingRunwayPhase FromSnapshot(CrossingRunwayPhaseDto dto, AirportGroundLayout? groundLayout)
    {
        var phase = new CrossingRunwayPhase(dto.ApproachNodeId, dto.TargetNodeId, dto.CrossingRunwayId)
        {
            _timeSinceLastLog = dto.TimeSinceLastLog,
            _ownPath = dto.OwnPath is { } ownPath ? TaxiRoute.FromSnapshot(ownPath, groundLayout) : null,
            _restoredSegmentIndex = dto.CrossingRouteSegmentIndex,
            _restoredSetupPose =
                (dto.SegmentSetupLat is { } lat) && (dto.SegmentSetupLon is { } lon) && (dto.SegmentSetupHeadingDeg is { } heading)
                    ? (new LatLon(lat, lon), new TrueHeading(heading))
                    : null,
            _restoredNavigator = dto.Navigator,
            _notClearRestSeconds = dto.NotClearRestSeconds ?? 0.0,
            _saidNotClear = dto.SaidNotClear ?? false,
            _expedited = dto.IsExpedited ?? false,
            _restoredTailDecision =
                (dto.TailClearanceSuppressed is { } suppressed) && (dto.TailFullSegmentCount is { } full) && (dto.TailCutFt is { } cutFt)
                    ? new TailDecision(suppressed, full, cutFt)
                    : null,
            _restoredHeldShortBarId = dto.HeldShortBarNodeId,
            _endsAtRest = dto.EndsAtRest ?? false,
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
        };
        phase.RestoreRequirements(dto.Requirements);
        // Leave _initialized=false so the first OnTick rebuilds the route slice from the restored source route (own path,
        // else AssignedTaxiRoute): BuildSlice derives the slice and its route index from that route, the layout and the
        // aircraft's length, and its tail clearance and held-short bar from the stored tail decision (decided afresh when the snapshot has
        // none). The slice segment the crossing was on is then set up again on the restored navigator from the pose the
        // original set it up at: the navigator resumes the primitive its snapshot carries, and a snapshot without one
        // (written before the navigator carried it) rebuilds the same primitive from that pose.
        _ = dto.Initialized;
        return phase;
    }
}
