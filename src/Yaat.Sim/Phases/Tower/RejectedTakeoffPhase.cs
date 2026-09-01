using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Rejected takeoff: keep accelerating for the crew's reaction window
/// (<see cref="RejectedTakeoff.ReactionSeconds"/> — sized from 14 CFR 25.109(a)(2)(iii)'s
/// 2-second distance element, modeled here as continued acceleration, which is slightly more
/// distance and therefore conservative), then brake on the centerline at
/// <see cref="CategoryPerformance.RejectedTakeoffDecelRate"/> to a stop. Completes into
/// <see cref="Ground.HoldingInPositionPhase"/> (installed alongside it by
/// <see cref="RejectedTakeoff.Install"/>) with a "stopped on the runway, standing by" call —
/// the aircraft holds where it stopped and awaits an exit or taxi instruction
/// (AIM 4-3-18.a: movement on the movement area needs ATC approval). If the stop cannot be
/// made on the remaining pavement the roll honestly continues past the departure end — the
/// case arresting systems exist for (P/CG ARRESTING SYSTEM: "aircraft cannot be stopped …
/// during aborted takeoff"; AIM 4-3-6.d declared distances) — and the overrun is surfaced to
/// the instructor and the solo evaluator.
/// </summary>
public sealed class RejectedTakeoffPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("RejectedTakeoffPhase");

    private const double CenterlineGainDegPerNm = 150.0;
    private const double MaxCenterlineCorrectionDeg = 10.0;

    private double _reactionRemainingSeconds = RejectedTakeoff.ReactionSeconds;
    private TrueHeading _runwayHeading;
    private double _thresholdLat;
    private double _thresholdLon;
    private double _pavementLengthFt;

    public override string Name => "Rejected Takeoff";

    /// <summary>True when the pilot initiated the reject (blocked runway), false for a CTOC-commanded abort — the solo evaluator scores only the former as a §3-9-6.a event.</summary>
    public bool AutoTriggered { get; set; }

    /// <summary>Callsign of the blocking occupant the predicted stop point runs past, or null when the stop fits — drives the evaluator's no-separation Safety finding.</summary>
    public string? CannotStopShortOf { get; set; }

    /// <summary>True once the still-moving aircraft has passed the departure end of the pavement.</summary>
    public bool OverrunReported { get; private set; }

    public override PhaseDto ToSnapshot() =>
        new RejectedTakeoffPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            ReactionRemainingSeconds = _reactionRemainingSeconds,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            PavementLengthFt = _pavementLengthFt,
            OverrunReported = OverrunReported,
            AutoTriggered = AutoTriggered,
            CannotStopShortOf = CannotStopShortOf,
        };

    public static RejectedTakeoffPhase FromSnapshot(RejectedTakeoffPhaseDto dto)
    {
        var phase = new RejectedTakeoffPhase();
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._reactionRemainingSeconds = dto.ReactionRemainingSeconds;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
        phase._pavementLengthFt = dto.PavementLengthFt;
        phase.OverrunReported = dto.OverrunReported;
        phase.AutoTriggered = dto.AutoTriggered;
        phase.CannotStopShortOf = dto.CannotStopShortOf;
        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        var rwy = ctx.Aircraft.Phases?.DepartureRunway ?? ctx.Runway;
        _runwayHeading = rwy?.TrueHeading ?? ctx.Aircraft.TrueHeading;
        _thresholdLat = rwy?.ThresholdLatitude ?? ctx.Aircraft.Position.Lat;
        _thresholdLon = rwy?.ThresholdLongitude ?? ctx.Aircraft.Position.Lon;
        _pavementLengthFt = rwy?.PavementLengthFt ?? double.MaxValue;

        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetTrueHeading = _runwayHeading;
        ctx.Targets.TargetSpeed = null;
        ctx.Targets.PreferredTurnDirection = null;

        Log.LogDebug(
            "[RejectedTakeoff] {Callsign}: aborting at {Gs:F0} kt groundspeed, {Reaction:F1}s reaction",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.GroundSpeed,
            _reactionRemainingSeconds
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Steer toward the centerline exactly as the takeoff roll does — a reject at speed must
        // not wander off the pavement laterally.
        double signedXte = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon), _runwayHeading);
        double correction = Math.Clamp(signedXte * CenterlineGainDegPerNm, -MaxCenterlineCorrectionDeg, MaxCenterlineCorrectionDeg);
        ctx.Targets.TargetTrueHeading = new TrueHeading(_runwayHeading.Degrees - correction);
        ctx.Targets.TargetSpeed = null;

        // The IAS field carries groundspeed on the ground; both the reaction-window
        // acceleration and the braking integrate that frame, like TakeoffPhase's roll.
        if (_reactionRemainingSeconds > 0)
        {
            _reactionRemainingSeconds -= ctx.DeltaSeconds;
            double accelRate = AircraftPerformance.GroundAccelRate(ctx.AircraftType, ctx.Category);
            ctx.Aircraft.IndicatedAirspeed += accelRate * ctx.DeltaSeconds;
            return false;
        }

        // Floored defensively: the category rate is 0 for Helicopter (which never enters this
        // phase), and a mis-mapped rotorcraft type must brake rather than roll forever.
        double decelRate = Math.Max(CategoryPerformance.RejectedTakeoffDecelRate(ctx.Category), 1.0);
        double speed = ctx.Aircraft.IndicatedAirspeed - (decelRate * ctx.DeltaSeconds);

        ReportOverrunOnce(ctx, speed);

        if (speed <= 0)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            Log.LogDebug("[RejectedTakeoff] {Callsign}: stopped on the runway", ctx.Aircraft.Callsign);
            Pilot.PilotResponder.RouteSoloOrRpoTransmission(
                ctx.Aircraft,
                ctx.SoloTrainingMode,
                ctx.RpoShowPilotSpeech,
                ctx.StudentPositionType,
                Pilot.PilotResponder.BuildStoppedOnRunway(ctx.Aircraft),
                Pilot.PilotResponder.SoloPositionsTower
            );
            return true;
        }

        ctx.Aircraft.IndicatedAirspeed = speed;
        return false;
    }

    /// <summary>
    /// Surfaces the overrun the moment the still-moving aircraft passes the departure end of the
    /// pavement. The physics is left honest — no teleport-stop at the end; the braking continues
    /// and the aircraft stops where v² says it stops.
    /// </summary>
    private void ReportOverrunOnce(PhaseContext ctx, double speedKts)
    {
        if (OverrunReported || (speedKts <= 0) || (_pavementLengthFt >= double.MaxValue))
        {
            return;
        }

        double alongFt =
            GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon), _runwayHeading) * GeoMath.FeetPerNm;
        if (alongFt <= _pavementLengthFt)
        {
            return;
        }

        OverrunReported = true;
        ctx.Aircraft.PendingWarnings.Add(
            $"{ctx.Aircraft.Callsign} overran the departure end during the rejected takeoff ({ctx.Aircraft.GroundSpeed:F0} kt at the end of the pavement)"
        );
        Log.LogDebug("[RejectedTakeoff] {Callsign}: overran the departure end at {Gs:F0} kt", ctx.Aircraft.Callsign, ctx.Aircraft.GroundSpeed);
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is rejecting the takeoff — braking to a stop on the runway; only DEL applies"),
        };
    }
}
