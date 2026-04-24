using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// For turboprop/jet aircraft entering the pattern from the wrong side.
/// Inserted after <see cref="MidfieldCrossingPhase"/> to descend from
/// pattern altitude + 500 ft (the large/turbine crossing altitude per
/// AIM 4-3-3.1.b and AC 90-66B §11.4) to pattern altitude, rejoining
/// downwind at the midfield abeam point via a 45° intercept.
///
/// Geometry: three waypoints on a single outbound-then-inbound path.
/// 1. Outbound anchor — abeam + crosswind heading × outbound distance (pattern-side perpendicular, well clear).
/// 2. 45° lead-in — abeam + reverse-45°-entry heading × category lead-in distance (same as ChooseDownwindLeadIn floors).
/// 3. Abeam — the midfield abeam point itself.
///
/// Altitude restrictions on each waypoint give a linear descent from TPA+500
/// down to TPA. After the route drains, DownwindPhase takes over at abeam
/// with the aircraft already tracking the 45° intercept course.
///
/// Not inserted for pistons or helicopters — they cross at TPA (no teardrop needed).
/// </summary>
public sealed class TeardropReentryPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("TeardropReentryPhase");

    public required PatternWaypoints Waypoints { get; init; }

    private double _outboundLat;
    private double _outboundLon;
    private double _leadInLat;
    private double _leadInLon;

    public override string Name => "TeardropReentry";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        double downwindDeg = Waypoints.DownwindHeading.Degrees;
        double reverseEntryDeg = Waypoints.Direction == PatternDirection.Right ? downwindDeg + 45.0 + 180.0 : downwindDeg - 45.0 + 180.0;
        var reverseEntryHdg = new TrueHeading(reverseEntryDeg);

        double leadInNm = ctx.Category switch
        {
            AircraftCategory.Jet => 2.0,
            AircraftCategory.Turboprop => 1.5,
            _ => 1.0,
        };
        double outboundNm = ctx.Category switch
        {
            AircraftCategory.Jet => 3.0,
            AircraftCategory.Turboprop => 2.5,
            _ => 2.0,
        };

        var outbound = GeoMath.ProjectPoint(Waypoints.DownwindAbeamLat, Waypoints.DownwindAbeamLon, Waypoints.CrosswindHeading, outboundNm);
        var leadIn = GeoMath.ProjectPoint(Waypoints.DownwindAbeamLat, Waypoints.DownwindAbeamLon, reverseEntryHdg, leadInNm);

        _outboundLat = outbound.Lat;
        _outboundLon = outbound.Lon;
        _leadInLat = leadIn.Lat;
        _leadInLon = leadIn.Lon;

        double tpa = Waypoints.PatternAltitude;
        int anchorAlt = (int)(tpa + 250);
        int leadInAlt = (int)(tpa + 50);
        int abeamAlt = (int)tpa;

        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Position = new LatLon(_outboundLat, _outboundLon),
                Name = "TDROP-OUT",
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, anchorAlt),
            }
        );
        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Position = new LatLon(_leadInLat, _leadInLon),
                Name = "TDROP-LI",
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, leadInAlt),
            }
        );
        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Position = new LatLon(Waypoints.DownwindAbeamLat, Waypoints.DownwindAbeamLon),
                Name = "TDROP-ABM",
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, abeamAlt),
            }
        );

        ctx.Targets.TargetTrueHeading = null;
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = null;
        }
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.TargetAltitude = tpa;
        ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);

        Log.LogDebug(
            "[TeardropReentry] {Callsign}: descending TPA+500→TPA via outbound+45° (cat={Cat}, leadIn={Lead:F2}nm)",
            ctx.Aircraft.Callsign,
            ctx.Category,
            leadInNm
        );
    }

    public override bool OnTick(PhaseContext ctx) => ctx.Targets.NavigationRoute.Count == 0;

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new TeardropReentryPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Waypoints = Waypoints.ToSnapshot(),
            OutboundLat = _outboundLat,
            OutboundLon = _outboundLon,
            LeadInLat = _leadInLat,
            LeadInLon = _leadInLon,
        };

    public static TeardropReentryPhase FromSnapshot(TeardropReentryPhaseDto dto)
    {
        var phase = new TeardropReentryPhase { Waypoints = PatternWaypoints.FromSnapshot(dto.Waypoints) };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase._outboundLat = dto.OutboundLat;
        phase._outboundLon = dto.OutboundLon;
        phase._leadInLat = dto.LeadInLat;
        phase._leadInLon = dto.LeadInLon;
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
