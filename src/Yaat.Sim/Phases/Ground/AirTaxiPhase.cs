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
        // Drop stale steering targets left by whatever the heli was doing before the air-taxi
        // (a prior FH/CM leaves TargetTrueHeading + AssignedMagneticHeading/AssignedAltitude set;
        // HelicopterTakeoff leaves a runway-heading target). Without this, FlightPhysics.UpdateHeading
        // keeps snapping the heli back to the old heading every tick, so it flies a frozen heading
        // straight past the spot instead of homing on it.
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.AssignedMagneticHeading = null;
        ctx.Targets.AssignedAltitude = null;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        // Field elevation comes from the airport (resolved in PhaseContext), not the current
        // altitude. An airborne heli given ATXI/LAND must descend to the pad at field level, not
        // hold an air-taxi altitude 100 ft above wherever it happened to be.
        double aglTarget = CategoryPerformance.AirTaxiAltitudeAgl(ctx.Category);
        _targetAltitude = ctx.FieldElevation + aglTarget;

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
                ctx.Targets.TargetTrueHeading = new TrueHeading(GeoMath.BearingTo(ctx.Aircraft.Position, target));
                return false;
            }
        }

        // Distance-aware brake ramp: cruise at AirTaxiSpeed while far from the
        // target, then linearly scale TargetSpeed to 0 across the brake zone so
        // the heli arrives at the destination with near-zero ground speed instead
        // of overshooting and coasting to a stop past the spot.
        ctx.Targets.TargetSpeed = (dist >= BrakeStartNm) ? maxSpeed : maxSpeed * (dist / BrakeStartNm);
        ctx.Targets.TargetAltitude = _targetAltitude;

        // Always steer toward the target via the standard heading target (FlightPhysics.UpdateHeading
        // turns the heli) so any overshoot self-corrects. Setting ctx.Aircraft.TrueHeading directly
        // here is fragile: a stale ControlTargets.TargetTrueHeading would override it every tick.
        double brg = GeoMath.BearingTo(ctx.Aircraft.Position, target);
        ctx.Targets.TargetTrueHeading = new TrueHeading(brg);

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
        // Any airborne maneuvering command (FH, turns, CM/DM, SPD, DCT, a new ATXI/LAND, DEL) pulls
        // the heli out of the air-taxi and hands control to the command queue. HPP (hover present
        // position) and a re-issued ATXI/LAND are routed by the dispatcher's tower-command path
        // before this gate; the ground HOLD/RES verbs don't apply to an airborne heli. The
        // dispatcher dry-runs the command before clearing the phase, so ground commands that can't
        // apply airborne (TAXI/PUSH) are rejected without orphaning the phase.
        return CommandAcceptance.ClearsPhase;
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
