using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// For turboprop/jet aircraft entering the pattern from the wrong side.
/// Inserted after <see cref="MidfieldCrossingPhase"/> to descend from the
/// turbine entry crossing height (the higher of pattern altitude and 1,500 ft
/// above the field, AIM 4-3-3.a.2 — see
/// <see cref="MidfieldCrossingPhase"/>) to pattern altitude, rejoining
/// downwind at the abeam point (abeam the threshold) via a 45° intercept.
///
/// Geometry: three waypoints on a single outbound-then-inbound path.
/// 1. Outbound anchor — abeam + crosswind heading × outbound distance (pattern-side perpendicular, well clear).
/// 2. 45° lead-in — abeam + reverse-45°-entry heading × category lead-in distance (same as ChooseDownwindLeadIn floors).
/// 3. Abeam — the downwind abeam point itself (abeam the threshold, not midfield).
///
/// Altitude restrictions on each waypoint step the aircraft down to TPA from
/// whatever height it crossed at. After the route drains, DownwindPhase takes over at abeam
/// with the aircraft already tracking the 45° intercept course.
///
/// Not inserted when the crossing is flown at pattern altitude: always for pistons and helicopters, and
/// for a turbine entry whenever the AIM 4-3-3.a.2 entry height is not above the field's pattern altitude
/// (<c>PatternBuilder.BuildFieldCrossingPrefix</c> compares the resolved crossing altitude to
/// <see cref="PatternWaypoints.PatternAltitude"/>).
/// </summary>
public sealed class TeardropReentryPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("TeardropReentryPhase");

    public required PatternWaypoints Waypoints { get; set; }

    private double _outboundLat;
    private double _outboundLon;
    private double _leadInLat;
    private double _leadInLon;

    /// <summary>Set when a boundary hold ended with the aircraft past the abeam point; read by the next tick.</summary>
    private bool _completeAtAbeamOnResume;

    public override string Name => "TeardropReentry";
    public override bool ManagesSpeed => true;

    /// <summary>Name of the re-entry's last navigation target, the downwind abeam point where the downwind is joined.</summary>
    internal const string AbeamTargetName = "TDROP-ABM";

    /// <summary>Name of the re-entry's outbound anchor navigation target, the fix the teardrop turns out to.</summary>
    internal const string TeardropOutboundTargetName = "TDROP-OUT";

    /// <summary>Name of the re-entry's 45° lead-in navigation target.</summary>
    internal const string TeardropLeadInTargetName = "TDROP-LI";

    /// <summary>
    /// The re-entry's outbound anchor (TDROP-OUT) and 45° lead-in (TDROP-LI) on <paramref name="waypoints"/> for a
    /// <paramref name="category"/> aircraft. The route's third fix is the downwind abeam point itself.
    /// </summary>
    /// <param name="waypoints">The pattern the re-entry joins.</param>
    /// <param name="category">The aircraft's category, which sets the outbound and lead-in distances.</param>
    /// <returns>The outbound anchor and the lead-in, in the order they are flown.</returns>
    public static (LatLon Outbound, LatLon LeadIn) ReentryFixes(PatternWaypoints waypoints, AircraftCategory category)
    {
        double downwindDeg = waypoints.DownwindHeading.Degrees;
        double reverseEntryDeg = waypoints.Direction == PatternDirection.Right ? downwindDeg + 45.0 + 180.0 : downwindDeg - 45.0 + 180.0;
        var reverseEntryHdg = new TrueHeading(reverseEntryDeg);

        (double Lat, double Lon) outbound = GeoMath.ProjectPoint(
            waypoints.DownwindAbeamLat,
            waypoints.DownwindAbeamLon,
            waypoints.CrosswindHeading,
            OutboundNm(category)
        );
        (double Lat, double Lon) leadIn = GeoMath.ProjectPoint(
            waypoints.DownwindAbeamLat,
            waypoints.DownwindAbeamLon,
            reverseEntryHdg,
            LeadInNm(category)
        );
        return (new LatLon(outbound.Lat, outbound.Lon), new LatLon(leadIn.Lat, leadIn.Lon));
    }

    /// <summary>Distance (nm) from the abeam point back out to the 45° lead-in, by category.</summary>
    private static double LeadInNm(AircraftCategory category) =>
        category switch
        {
            AircraftCategory.Jet => 2.0,
            AircraftCategory.Turboprop => 1.5,
            _ => 1.0,
        };

    /// <summary>Distance (nm) from the abeam point out to the outbound anchor, by category.</summary>
    private static double OutboundNm(AircraftCategory category) =>
        category switch
        {
            AircraftCategory.Jet => 3.0,
            AircraftCategory.Turboprop => 2.5,
            _ => 2.0,
        };

    public override void OnStart(PhaseContext ctx)
    {
        (LatLon outbound, LatLon leadIn) = ReentryFixes(Waypoints, ctx.Category);
        double leadInNm = LeadInNm(ctx.Category);

        _outboundLat = outbound.Lat;
        _outboundLon = outbound.Lon;
        _leadInLat = leadIn.Lat;
        _leadInLon = leadIn.Lon;

        double tpa = Waypoints.PatternAltitude;

        // The crossing hands the aircraft in at the AIM 4-3-3.a.2 entry height (the higher of TPA and
        // field + 1,500 ft); this re-entry only ever *sheds* that height down to TPA on the outbound leg.
        // Cap each step at the crossing altitude so the teardrop never climbs above the height it entered
        // at: the phase's contract is to descend to pattern altitude; climbing above the circuit
        // contradicts AIM 4-3-3.a's recommendation that pattern altitude be maintained. A teardrop is only
        // inserted when the crossing is above pattern altitude (PatternBuilder.BuildFieldCrossingPrefix),
        // so pass crossAtPatternAltitude: false here.
        double crossingAlt = MidfieldCrossingPhase.ResolveCrossingAltitude(
            crossAtPatternAltitude: false,
            ctx.Category,
            ctx.Aircraft.Pattern.AltitudeOverrideFt,
            tpa,
            ctx.Runway?.AirportElevationFt
        );
        int anchorAlt = (int)Math.Min(tpa + 250, crossingAlt);
        int leadInAlt = (int)Math.Min(tpa + 50, crossingAlt);
        int abeamAlt = (int)tpa;

        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Position = new LatLon(_outboundLat, _outboundLon),
                Name = TeardropOutboundTargetName,
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, anchorAlt)
                {
                    Source = CifpAltitudeRestrictionSource.System,
                },
            }
        );
        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Position = new LatLon(_leadInLat, _leadInLon),
                Name = TeardropLeadInTargetName,
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, leadInAlt)
                {
                    Source = CifpAltitudeRestrictionSource.System,
                },
            }
        );
        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Position = new LatLon(Waypoints.DownwindAbeamLat, Waypoints.DownwindAbeamLon),
                Name = AbeamTargetName,
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, abeamAlt)
                {
                    Source = CifpAltitudeRestrictionSource.System,
                },
            }
        );

        ctx.Targets.TargetTrueHeading = null;
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = null;
        }
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.TargetAltitude = tpa;
        // A controller speed assignment outranks the leg baseline (7110.65 §5-7-4).
        if (!ctx.Targets.HasExplicitSpeedCommand)
        {
            ctx.Targets.TargetSpeed = PatternCompression.DownwindSpeed(Waypoints, ctx.AircraftType, ctx.Category);
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "[TeardropReentry] {Callsign}: descending to TPA via outbound+45° (cat={Cat}, leadIn={Lead:F2}nm)",
                ctx.Aircraft.Callsign,
                ctx.Category,
                leadInNm
            );
        }
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Lead-not-found / lead-on-ground / lost-visual watchdog for a follow accepted
        // mid-reentry. A cancel can replace or clear this phase list mid-tick — bail out
        // when it fires. See DownwindPhase.OnTick for the full rationale.
        if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
        {
            return false;
        }

        if (TryCompleteAtAbeamOnResume(ctx))
        {
            return true;
        }

        // A follow accepted during the re-entry engages free-flight spacing immediately
        // (mirrors PatternEntryPhase): wider free-flight desired distance, and only ever
        // SLOW below the re-entry baseline.
        if (ctx.Aircraft.Approach.FollowingCallsign is not null)
        {
            double normalSpeed = PatternCompression.DownwindSpeed(Waypoints, ctx.AircraftType, ctx.Category);
            double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            double? adjusted = AirborneFollowHelper.GetAdjustedSpeedFreeFlight(ctx, normalSpeed, minSpeed);
            if (adjusted is not null)
            {
                ctx.Targets.TargetSpeed = Math.Min(adjusted.Value, normalSpeed);
            }
        }

        return ctx.Targets.NavigationRoute.Count == 0;
    }

    /// <inheritdoc/>
    public override bool CanSuspendForBoundaryHold(PhaseContext ctx) => true;

    /// <summary>
    /// In place: the hold puts the remaining fixes back. When the orbit has left the aircraft past the abeam point, the next
    /// tick ends the re-entry if the abeam point is the only fix left, rather than doubling back to it
    /// (<see cref="TryCompleteAtAbeamOnResume"/>).
    /// </summary>
    public override HoldResume ResumeAfterBoundaryHold(PhaseContext ctx)
    {
        double pastAbeamNm = GeoMath.AlongTrackDistanceNm(
            ctx.Aircraft.Position,
            new LatLon(Waypoints.DownwindAbeamLat, Waypoints.DownwindAbeamLon),
            Waypoints.DownwindHeading
        );
        _completeAtAbeamOnResume = pastAbeamNm > 0;
        return HoldResume.InPlace;
    }

    /// <summary>
    /// The first tick after a boundary hold that ended past the abeam point, with the abeam point the only fix left: the
    /// teardrop is complete and the pattern continues on the downwind, which re-intercepts its line from where the aircraft is
    /// (<see cref="DownwindPhase.RejoinTrack"/>) and starts its descent at its first abeam check.
    /// </summary>
    /// <returns>True when the re-entry ends this tick.</returns>
    private bool TryCompleteAtAbeamOnResume(PhaseContext ctx)
    {
        if (!_completeAtAbeamOnResume)
        {
            return false;
        }

        _completeAtAbeamOnResume = false;
        List<NavigationTarget> route = ctx.Targets.NavigationRoute;
        if ((route.Count != 1) || (route[0].Name != AbeamTargetName))
        {
            return false;
        }

        route.Clear();
        if (ctx.Aircraft.Phases?.Phases.Skip(ctx.Aircraft.Phases.CurrentIndex + 1).OfType<DownwindPhase>().FirstOrDefault() is { } downwind)
        {
            downwind.RejoinTrack = true;
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("[TeardropReentry] {Callsign}: past abeam after the boundary hold, teardrop complete", ctx.Aircraft.Callsign);
        }

        return true;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Speed adjustments are additive — they retarget without breaking the re-entry, which carries the rest of the
        // circuit in its phase list, so clearing it on a plain SPD tore the whole thing down. The entry speed is set
        // once in OnStart, so an adjustment issued mid-phase stands.
        //
        // Altitude (CM/DM) still clears, matching PatternEntryPhase. That is also the honest answer here: this phase
        // navigates a waypoint route whose legs carry `At` altitude restrictions, and ApplyFixConstraints rewrites
        // TargetAltitude on every sequencing — so accepting a CM/DM would discard it at the next waypoint rather than
        // fly it.
        if (IsSpeedFamilyCommand(cmd))
        {
            return CommandAcceptance.Allowed;
        }

        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.ForceLanding => CommandAcceptance.Allowed,
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            // FOLLOW is additive during a same-runway re-entry: it records the traffic to
            // sequence behind; the DownwindPhase this re-entry feeds runs the spacing holds (#352).
            CanonicalCommandType.Follow => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new TeardropReentryPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? [.. Requirements.Select(r => r.ToSnapshot())] : null,
            Waypoints = Waypoints.ToSnapshot(),
            OutboundLat = _outboundLat,
            OutboundLon = _outboundLon,
            LeadInLat = _leadInLat,
            LeadInLon = _leadInLon,
            CompleteAtAbeamOnResume = _completeAtAbeamOnResume ? true : null,
        };

    public static TeardropReentryPhase FromSnapshot(TeardropReentryPhaseDto dto)
    {
        var phase = new TeardropReentryPhase
        {
            Waypoints = PatternWaypoints.FromSnapshot(dto.Waypoints),
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            _outboundLat = dto.OutboundLat,
            _outboundLon = dto.OutboundLon,
            _leadInLat = dto.LeadInLat,
            _leadInLon = dto.LeadInLon,
            _completeAtAbeamOnResume = dto.CompleteAtAbeamOnResume ?? false,
        };
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements() => [];
}
