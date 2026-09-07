using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Crosses midfield from the wrong side to the correct pattern side. Per
/// AIM 4-3-3.1.b and AC 90-66B §11.3-§11.4: small pistons/helicopters cross
/// at pattern altitude (1000 AGL); large and turbine-powered aircraft cross
/// at pattern altitude + 500 ft. Turboprops/jets are handed off to
/// <see cref="TeardropReentryPhase"/> afterward to descend to TPA; pistons
/// and helicopters drop directly into <see cref="DownwindPhase"/>.
/// </summary>
public sealed class MidfieldCrossingPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("MidfieldCrossingPhase");

    private const double ArrivalNm = 0.5;
    private const double LargeTurbineAltitudeOffsetFt = 500.0;

    private double _targetLat;
    private double _targetLon;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// When set, the initial join turn is biased in this direction rather than taking the
    /// geometric shortest way. The cross-runway departure join sets it toward the assigned
    /// pattern side (climb out on the departure runway, then turn the correct way onto the
    /// pattern runway's downwind); an in-pattern crossover sets it toward the field. Left null
    /// for arrival / wrong-side joins, which keep their established shortest-turn behavior.
    /// </summary>
    public TurnDirection? InitialTurn { get; init; }

    /// <summary>
    /// When true, the crossing is flown at pattern altitude regardless of category. An aircraft
    /// already established in the pattern crosses at the altitude it is already at — the AIM 4-3-3.1.b
    /// large/turbine "pattern altitude + 500 ft" is an <em>entry</em> rule for aircraft arriving from
    /// outside the pattern.
    /// </summary>
    public bool CrossAtPatternAltitude { get; init; }

    public override string Name => "MidfieldCrossing";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        // Target: midfield point on the correct side (midpoint of downwind leg)
        _targetLat = (Waypoints.DownwindStartLat + Waypoints.DownwindAbeamLat) / 2.0;
        _targetLon = (Waypoints.DownwindStartLon + Waypoints.DownwindAbeamLon) / 2.0;

        // Set heading toward midfield target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));
        ctx.Targets.TargetTrueHeading = new TrueHeading(bearing);
        // Bias the initial turn so the aircraft turns the way the join requires instead of the
        // geometric shortest way (which can roll across the extended centerline / departure path
        // the wrong direction — AIM 4-3-3, 4-3-5). Released in OnTick once roughly pointed at the
        // target; DownwindPhase clears it on entry regardless. Arrival / wrong-side joins leave it
        // unset and keep their established shortest-turn behavior.
        ctx.Targets.PreferredTurnDirection = InitialTurn;
        ctx.Targets.NavigationRoute.Clear();

        // Large/turbine cross at TPA+500 (AIM 4-3-3.1.b); pistons/helicopters
        // cross at pattern altitude (AC 90-66B §11.3-§11.4). An aircraft already
        // in the pattern crosses at pattern altitude whatever its category — the
        // +500 ft belongs to an entry from outside the pattern.
        double crossingAlt =
            !CrossAtPatternAltitude && ctx.Category is AircraftCategory.Jet or AircraftCategory.Turboprop
                ? Waypoints.PatternAltitude + LargeTurbineAltitudeOffsetFt
                : Waypoints.PatternAltitude;
        ctx.Targets.TargetAltitude = crossingAlt;
        ctx.Targets.DesiredVerticalRate = null;

        // Downwind speed for the category. A controller speed assignment outranks it (7110.65 §5-7-4).
        if (!ctx.Targets.HasExplicitSpeedCommand)
        {
            ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);
        }

        Log.LogDebug("[MidfieldCrossing] {Callsign}: started, cat={Cat}, crossingAlt={Alt:F0}ft", ctx.Aircraft.Callsign, ctx.Category, crossingAlt);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Lead-not-found / lead-on-ground / lost-visual watchdog for a follow accepted
        // mid-crossing. A cancel can replace or clear this phase list mid-tick — bail out
        // when it fires. See DownwindPhase.OnTick for the full rationale.
        if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
        {
            return false;
        }

        // A follow accepted during the crossing engages free-flight spacing immediately
        // (mirrors PatternEntryPhase): the aircraft isn't on a pattern leg yet, so use the
        // wider free-flight desired distance, and only ever SLOW below the crossing baseline.
        if (ctx.Aircraft.Approach.FollowingCallsign is not null)
        {
            double normalSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);
            double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            var adjusted = AirborneFollowHelper.GetAdjustedSpeedFreeFlight(ctx, normalSpeed, minSpeed);
            if (adjusted is not null)
            {
                ctx.Targets.TargetSpeed = Math.Min(adjusted.Value, normalSpeed);
            }
        }

        // Continuously update heading toward the midfield target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));
        ctx.Targets.TargetTrueHeading = new TrueHeading(bearing);

        // Once roughly pointed at the target, release the initial-turn bias so ongoing
        // small corrections take the natural (shortest) direction and a moving bearing
        // can't force a wrong-way loop.
        if (
            (ctx.Targets.PreferredTurnDirection is not null)
            && (Math.Abs(GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, bearing)) < 30.0)
        )
        {
            ctx.Targets.PreferredTurnDirection = null;
        }

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));

        bool complete = dist < ArrivalNm;
        if (complete)
        {
            Log.LogDebug("[MidfieldCrossing] {Callsign}: midfield reached, transitioning to downwind", ctx.Aircraft.Callsign);
        }

        return complete;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Speed adjustments are additive — they retarget without breaking the crossing, which carries the rest of the
        // circuit in its phase list, so clearing it on a plain SPD tore down Downwind → Base → FinalApproach →
        // Landing. The crossing speed is set once in OnStart, so an adjustment issued mid-phase stands.
        //
        // Altitude (CM/DM) still clears, matching PatternEntryPhase: a climb or descend during an entry maneuver
        // usually means the aircraft is no longer being sequenced into this pattern, and the RPO has to be told the
        // entry was cancelled.
        if (IsSpeedFamilyCommand(cmd))
        {
            return CommandAcceptance.Allowed;
        }

        // FOLLOW is additive during a same-runway crossing: it records the traffic to sequence
        // behind, and the DownwindPhase this crossing feeds runs all the spacing holds. Clearing
        // the crossing here would discard the wrong-side entry and free-pursue instead (#352).
        return cmd switch
        {
            CanonicalCommandType.Follow => CommandAcceptance.Allowed,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new MidfieldCrossingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Waypoints = Waypoints?.ToSnapshot(),
            TargetLat = _targetLat,
            TargetLon = _targetLon,
            InitialTurn = InitialTurn is { } turn ? (int)turn : null,
            CrossAtPatternAltitude = CrossAtPatternAltitude,
        };

    public static MidfieldCrossingPhase FromSnapshot(MidfieldCrossingPhaseDto dto)
    {
        var phase = new MidfieldCrossingPhase
        {
            Waypoints = dto.Waypoints is not null ? PatternWaypoints.FromSnapshot(dto.Waypoints) : null,
            InitialTurn = dto.InitialTurn is { } turn ? (TurnDirection)turn : null,
            CrossAtPatternAltitude = dto.CrossAtPatternAltitude ?? false,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase._targetLat = dto.TargetLat;
        phase._targetLon = dto.TargetLon;
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
