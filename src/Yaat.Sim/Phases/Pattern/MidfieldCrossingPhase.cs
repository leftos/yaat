using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Crosses midfield from the wrong side to the correct pattern side. Pistons and helicopters cross at
/// pattern altitude (AC 90-66B §11.3-§11.4). A jet or turboprop <em>entering from outside the pattern</em>
/// crosses at the AIM 4-3-3.a.2 entry height — the higher of its own pattern altitude and the field
/// elevation plus 1,500 ft. At an unauthored field those are the same number (the turbine TPA is itself
/// 1,500 ft AGL), so the entry crossing is usually flown at TPA; a lower authored pattern (OAK 28L's
/// 600 ft AGL) is where the two diverge and the crossing really is above the circuit.
///
/// <para>An aircraft already established in the pattern, and a departure crossing into another runway's
/// pattern (<see cref="CrossAtPatternAltitude"/>), cross at pattern altitude whatever their category, as
/// does any aircraft flying a controller-assigned pattern altitude (AIM 4-4-7.b).
/// <see cref="CrossesAtPatternAltitude"/> is the single predicate for all of that, and the one the
/// builders key the follow-on <see cref="TeardropReentryPhase"/> off: the teardrop exists to shed the
/// entry height on an outbound leg and a 45° re-entry to abeam, so only an entry-height crossing gets
/// one. Everything else drops straight into <see cref="DownwindPhase"/>.</para>
/// </summary>
public sealed class MidfieldCrossingPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("MidfieldCrossingPhase");

    private const double ArrivalNm = 0.5;

    /// <summary>
    /// Minimum crossing height above the field (ft) for a jet/turboprop entering from outside the
    /// pattern — AIM 4-3-3.a.2's "not less than 1,500 feet AGL". Adding 500 ft to the pattern altitude
    /// instead would stack on top of a turbine TPA that is already 1,500 ft AGL (or authored + 500),
    /// putting the crossing at 2,000 ft AGL; the AIM's two figures are alternatives, not a sum.
    /// </summary>
    private const double TurbineEntryCrossingHeightAglFt = 1500.0;

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
    /// already established in the pattern crosses at the altitude it is already at — the AIM 4-3-3.a.2
    /// large/turbine crossing height is an <em>entry</em> rule for aircraft arriving from outside the
    /// pattern.
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

        // A jet/turboprop entering from outside the pattern crosses at or above 1,500 ft AGL
        // (AIM 4-3-3.a.2); pistons/helicopters cross at pattern altitude (AC 90-66B §11.3-§11.4).
        // An aircraft already in the pattern crosses at pattern altitude whatever its category —
        // the entry height belongs to an entry from outside the pattern.
        double crossingAlt = ResolveCrossingAltitude(ctx, Waypoints.PatternAltitude);
        ctx.Targets.TargetAltitude = crossingAlt;
        ctx.Targets.DesiredVerticalRate = null;

        // Downwind speed for the category. A controller speed assignment outranks it (7110.65 §5-7-4).
        if (!ctx.Targets.HasExplicitSpeedCommand)
        {
            ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);
        }

        Log.LogDebug("[MidfieldCrossing] {Callsign}: started, cat={Cat}, crossingAlt={Alt:F0}ft", ctx.Aircraft.Callsign, ctx.Category, crossingAlt);
    }

    /// <summary>
    /// The altitude (ft MSL) this crossing is flown at. An in-pattern crossover and a piston/helicopter
    /// entry cross at <paramref name="patternAltitude"/>; a jet/turboprop entry crosses at the higher of
    /// the pattern altitude and 1,500 ft above the field (AIM 4-3-3.a.2). Field elevation comes from the
    /// runway (<see cref="RunwayInfo.AirportElevationFt"/> is the field's, not a threshold's); with no
    /// runway in context there is nothing to measure AGL from, so the pattern altitude stands.
    ///
    /// <para>A <em>controller-assigned</em> pattern altitude
    /// (<see cref="AircraftPattern.AltitudeOverrideFt"/>, from an <c>MLT</c>/<c>MRT</c>/<c>CTO</c>
    /// altitude argument) outranks the entry floor for every category: it is an altitude assignment the
    /// pilot reads back and flies (AIM 4-4-7.b), not a recommended entry height to be raised.</para>
    /// </summary>
    private double ResolveCrossingAltitude(PhaseContext ctx, double patternAltitude) =>
        ResolveCrossingAltitude(
            CrossAtPatternAltitude,
            ctx.Category,
            ctx.Aircraft.Pattern.AltitudeOverrideFt,
            patternAltitude,
            ctx.Runway?.AirportElevationFt
        );

    /// <summary>
    /// The rule of <see cref="ResolveCrossingAltitude(PhaseContext, double)"/> as a pure function, so
    /// the phase builders can ask what a crossing they are about to construct will be flown at.
    /// </summary>
    public static double ResolveCrossingAltitude(
        bool crossAtPatternAltitude,
        AircraftCategory category,
        double? altitudeOverrideFt,
        double patternAltitude,
        double? airportElevationFt
    )
    {
        if (CrossesAtPatternAltitude(crossAtPatternAltitude, category, altitudeOverrideFt))
        {
            return patternAltitude;
        }

        return airportElevationFt is { } elevation ? Math.Max(patternAltitude, elevation + TurbineEntryCrossingHeightAglFt) : patternAltitude;
    }

    /// <summary>
    /// Whether this crossing is flown at pattern altitude rather than at the AIM 4-3-3.a.2 entry height:
    /// an aircraft that never left the pattern (<paramref name="crossAtPatternAltitude"/> — an
    /// in-pattern crossover, or a departure crossing into another runway's pattern), one flying a
    /// controller-assigned pattern altitude (AIM 4-4-7.b outranks a recommended entry height), and any
    /// piston or helicopter. The builders read this to decide whether a
    /// <c>TeardropReentryPhase</c> has anything to do: it exists to shed the entry height, so a crossing
    /// already at pattern altitude never gets one.
    /// </summary>
    public static bool CrossesAtPatternAltitude(bool crossAtPatternAltitude, AircraftCategory category, double? altitudeOverrideFt) =>
        crossAtPatternAltitude || (altitudeOverrideFt is not null) || (category is not (AircraftCategory.Jet or AircraftCategory.Turboprop));

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
