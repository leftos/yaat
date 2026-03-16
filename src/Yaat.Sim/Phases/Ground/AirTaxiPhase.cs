using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Helicopter air taxi: lifts to AirTaxiAltitudeAgl (50ft AGL), flies direct
/// to destination at AirTaxiSpeed, then hovers over destination.
/// Per FAA 7110.65 §3-11-1.c: below 100ft AGL, above 20 KIAS.
/// </summary>
public sealed class AirTaxiPhase : Phase
{
    private const double ArrivalThresholdNm = 0.05;
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

        ctx.Logger.LogDebug(
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
        if (ctx.Aircraft.IsHeld)
        {
            ctx.Targets.TargetSpeed = 0;
            return false;
        }
        else
        {
            double maxSpeed = CategoryPerformance.AirTaxiSpeed(ctx.Category);
            if (ctx.Targets.TargetSpeed < maxSpeed && !_descending)
            {
                ctx.Targets.TargetSpeed = maxSpeed;
            }
        }

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);

        // Phase 1: lifting off
        if (_liftingOff)
        {
            double agl = ctx.Aircraft.Altitude - (_targetAltitude - CategoryPerformance.AirTaxiAltitudeAgl(ctx.Category));
            if (agl >= CategoryPerformance.AirTaxiAltitudeAgl(ctx.Category) * 0.8)
            {
                _liftingOff = false;
            }
            else
            {
                // Still climbing — don't navigate yet, just point toward destination
                double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);
                double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
                ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(ctx.Aircraft.Heading, bearing, maxTurn);
                return false;
            }
        }

        // Phase 3: arrived — hover/descend
        if (dist <= ArrivalThresholdNm)
        {
            if (!_descending)
            {
                _descending = true;
                ctx.Targets.TargetSpeed = 0;
                ctx.Logger.LogDebug("[AirTaxi] {Callsign}: arrived over {Dest}, hovering", ctx.Aircraft.Callsign, _destinationName ?? "destination");
            }

            // Complete when speed is near zero and hovering
            if (ctx.Aircraft.GroundSpeed <= 2.0)
            {
                // Snap position to destination — helicopter remains airborne (hovering at AGL)
                ctx.Aircraft.Latitude = _targetLat;
                ctx.Aircraft.Longitude = _targetLon;
                return true;
            }

            return false;
        }

        // Phase 2: navigate toward destination
        double brg = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);
        double turnRate = AircraftPerformance.TurnRate(ctx.AircraftType, ctx.Category);
        double maxTurnAmount = turnRate * ctx.DeltaSeconds;
        ctx.Aircraft.Heading = GeoMath.TurnHeadingToward(ctx.Aircraft.Heading, brg, maxTurnAmount);

        // Maintain target altitude
        ctx.Targets.TargetAltitude = _targetAltitude;

        // Periodic logging
        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            ctx.Logger.LogDebug(
                "[AirTaxi] {Callsign}: dist={Dist:F3}nm, hdg={Hdg:F0}, brg={Brg:F0}, alt={Alt:F0}, gs={Gs:F0}",
                ctx.Aircraft.Callsign,
                dist,
                ctx.Aircraft.Heading,
                brg,
                ctx.Aircraft.Altitude,
                ctx.Aircraft.GroundSpeed
            );
        }

        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        ctx.Logger.LogDebug("[AirTaxi] {Callsign}: ended ({Status})", ctx.Aircraft.Callsign, endStatus);
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
            _ => CommandAcceptance.Rejected,
        };
    }
}
