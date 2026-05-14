using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Helicopter air taxi: lifts to AirTaxiAltitudeAgl (100ft AGL), flies direct
/// to destination at AirTaxiSpeed, then hovers over destination.
/// Per FAA 7110.65 §3-11-1.c: below 100ft AGL, above 20 KIAS.
/// </summary>
public sealed class AirTaxiPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("AirTaxiPhase");

    private const double ArrivalThresholdNm = 0.01;
    private const double BrakeStartNm = 0.10;
    private const double LogIntervalSeconds = 3.0;

    private readonly double _targetLat;
    private readonly double _targetLon;
    private readonly string? _destinationName;

    private double _targetAltitude;
    private bool _liftingOff;
    private bool _descending;
    private double _timeSinceLastLog;

    public override string Name => "AirTaxi";

    public AirTaxiPhase(double targetLat, double targetLon, string? destinationName)
    {
        _targetLat = targetLat;
        _targetLon = targetLon;
        _destinationName = destinationName;
    }

    public override void OnStart(PhaseContext ctx)
    {
        double fieldElev = ctx.Aircraft.Phases?.AssignedRunway?.ElevationFt ?? ctx.Aircraft.Altitude;
        double aglTarget = CategoryPerformance.AirTaxiAltitudeAgl(ctx.Category);
        _targetAltitude = fieldElev + aglTarget;

        double maxSpeed = CategoryPerformance.AirTaxiSpeed(ctx.Category);
        ctx.Targets.TargetSpeed = maxSpeed;

        if (ctx.Aircraft.IsOnGround)
        {
            _liftingOff = true;
            ctx.Aircraft.IsOnGround = false;
            ctx.Targets.TargetAltitude = _targetAltitude;
            ctx.Targets.DesiredVerticalRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        }
        else
        {
            ctx.Targets.TargetAltitude = _targetAltitude;
        }

        Log.LogDebug(
            "[AirTaxi] {Callsign}: started → {Dest} at ({Lat:F6},{Lon:F6}), targetAlt={Alt:F0}, speed={Spd:F0}",
            ctx.Aircraft.Callsign,
            _destinationName ?? "direct",
            _targetLat,
            _targetLon,
            _targetAltitude,
            maxSpeed
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.Ground.IsImmobile)
        {
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));
        var target = new LatLon(_targetLat, _targetLon);
        double maxSpeed = CategoryPerformance.AirTaxiSpeed(ctx.Category);

        // Phase 1: lifting off — climb in place toward AirTaxiAltitudeAgl. Don't
        // start cruise navigation or brake ramp until we're at ~80% of target alt.
        if (_liftingOff)
        {
            double agl = ctx.Aircraft.Altitude - (_targetAltitude - CategoryPerformance.AirTaxiAltitudeAgl(ctx.Category));
            if (agl >= CategoryPerformance.AirTaxiAltitudeAgl(ctx.Category) * 0.8)
            {
                _liftingOff = false;
            }
            else
            {
                double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, target);
                double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
                ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearing, maxTurn);
                return false;
            }
        }

        // Distance-aware brake ramp: cruise at AirTaxiSpeed while far from the
        // target, then linearly scale TargetSpeed to 0 across the brake zone so
        // the heli arrives at the destination with near-zero ground speed instead
        // of overshooting and coasting to a stop past the spot.
        ctx.Targets.TargetSpeed = (dist >= BrakeStartNm) ? maxSpeed : maxSpeed * (dist / BrakeStartNm);
        ctx.Targets.TargetAltitude = _targetAltitude;

        // Always navigate toward the target so any overshoot self-corrects.
        double brg = GeoMath.BearingTo(ctx.Aircraft.Position, target);
        double turnRate = AircraftPerformance.TurnRate(ctx.AircraftType, ctx.Category);
        double maxTurnAmount = turnRate * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, brg, maxTurnAmount);

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            Log.LogTrace(
                "[AirTaxi] {Callsign}: dist={Dist:F3}nm, hdg={Hdg:F0}, brg={Brg:F0}, alt={Alt:F0}, gs={Gs:F0}, tgtSpd={Tgt:F0}",
                ctx.Aircraft.Callsign,
                dist,
                ctx.Aircraft.TrueHeading.Degrees,
                brg,
                ctx.Aircraft.Altitude,
                ctx.Aircraft.GroundSpeed,
                ctx.Targets.TargetSpeed ?? 0
            );
        }

        // Complete when at the spot AND stopped.
        if (dist <= ArrivalThresholdNm && ctx.Aircraft.GroundSpeed <= 2.0)
        {
            if (!_descending)
            {
                _descending = true;
                Log.LogDebug("[AirTaxi] {Callsign}: arrived over {Dest}, hovering", ctx.Aircraft.Callsign, _destinationName ?? "destination");
            }
            return true;
        }

        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        Log.LogDebug("[AirTaxi] {Callsign}: ended ({Status})", ctx.Aircraft.Callsign, endStatus);
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("helicopter is air-taxiing; only HOLD/RES, a new ATXI/LAND, or DEL apply"),
        };
    }

    public override PhaseDto ToSnapshot() =>
        new AirTaxiPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            TargetLat = _targetLat,
            TargetLon = _targetLon,
            DestinationName = _destinationName,
            TargetAltitude = _targetAltitude,
            LiftingOff = _liftingOff,
            Descending = _descending,
            TimeSinceLastLog = _timeSinceLastLog,
        };

    public static AirTaxiPhase FromSnapshot(AirTaxiPhaseDto dto)
    {
        var phase = new AirTaxiPhase(dto.TargetLat, dto.TargetLon, dto.DestinationName);
        phase._targetAltitude = dto.TargetAltitude;
        phase._liftingOff = dto.LiftingOff;
        phase._descending = dto.Descending;
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
