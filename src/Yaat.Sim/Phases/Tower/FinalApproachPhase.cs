using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Tracks 3° glideslope from current position to threshold.
/// Checks PhaseList-level landing clearance (CTL can be issued on
/// downwind/base, well before this phase activates).
/// Auto-triggers go-around if no clearance by 0.5nm from threshold.
/// Completes when crossing the threshold.
/// Checks for illegal approach intercept (7110.65 §5-9-1) on
/// first tick when aircraft is established on the localizer.
/// </summary>
public sealed class FinalApproachPhase : Phase
{
    private const double AutoGoAroundDistNm = 0.5;
    private const double NoClearanceWarningDistNm = 1.0;
    private const double InterceptCrossTrackThresholdNm = 0.1;
    private const double InterceptHeadingThresholdDeg = 15.0;

    private double _thresholdLat;
    private double _thresholdLon;
    private double _thresholdElevation;
    private double _runwayHeading;
    private bool _goAroundTriggered;
    private bool _noClearanceWarningIssued;
    private bool _interceptChecked;
    private bool _isPatternTraffic;

    /// <summary>
    /// When true, skips the illegal intercept distance check.
    /// Set for aircraft that spawn on final (not vectored by RPO).
    /// </summary>
    public bool SkipInterceptCheck { get; init; }

    public override string Name => "FinalApproach";

    public override void OnStart(PhaseContext ctx)
    {
        if (ctx.Runway is null)
        {
            return;
        }

        _thresholdLat = ctx.Runway.ThresholdLatitude;
        _thresholdLon = ctx.Runway.ThresholdLongitude;
        _thresholdElevation = ctx.Runway.ElevationFt;
        _runwayHeading = ctx.Runway.TrueHeading;
        _isPatternTraffic = ctx.Aircraft.Phases?.TrafficDirection is not null;

        // Set heading toward threshold
        ctx.Targets.TargetHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        // Set approach speed
        double approachSpeed = CategoryPerformance.ApproachSpeed(ctx.Category);
        ctx.Targets.TargetSpeed = approachSpeed;

        double startDist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _thresholdLat, _thresholdLon);
        ctx.Logger.LogDebug(
            "[FinalApproach] {Callsign}: started, rwy hdg={Hdg:F0}, dist={Dist:F1}nm, alt={Alt:F0}ft, apchSpd={Spd:F0}kts",
            ctx.Aircraft.Callsign,
            _runwayHeading,
            startDist,
            ctx.Aircraft.Altitude,
            approachSpeed
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (_goAroundTriggered)
        {
            return false;
        }

        double distNm = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _thresholdLat, _thresholdLon);

        CheckInterceptDistance(ctx, distNm);

        // Target: threshold elevation (descend all the way to the runway)
        ctx.Targets.TargetAltitude = _thresholdElevation;

        // Compute descent rate proportionally: lose all remaining altitude
        // over the remaining distance. This naturally handles above/below
        // glideslope — steeper when high, shallower when low.
        double timeToThresholdSec = ctx.Aircraft.GroundSpeed > 1 ? distNm / (ctx.Aircraft.GroundSpeed / 3600.0) : 1.0;
        double altToLose = ctx.Aircraft.Altitude - _thresholdElevation;
        double requiredFpm = timeToThresholdSec > 1 ? (altToLose / timeToThresholdSec) * 60.0 : altToLose * 60.0;

        // Clamp to reasonable range (don't dive or climb on approach)
        double maxFpm = distNm > 2.0 ? 2500 : 1500;
        double clampedFpm = Math.Clamp(requiredFpm, 200, maxFpm);
        ctx.Targets.DesiredVerticalRate = -clampedFpm;

        // Check landing clearance from PhaseList (set earlier by CTL command)
        bool hasLandingClearance = HasLandingClearance(ctx);

        // Warn at 1nm if no landing clearance (only when auto-CTL is off)
        if (distNm <= NoClearanceWarningDistNm && !hasLandingClearance && !ctx.AutoClearedToLand && !_noClearanceWarningIssued)
        {
            _noClearanceWarningIssued = true;
            ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} is 1nm from the threshold without a landing clearance");
        }

        // Auto go-around if no landing clearance by 0.5nm
        if (distNm <= AutoGoAroundDistNm && !hasLandingClearance)
        {
            _goAroundTriggered = true;
            ctx.Logger.LogDebug("[FinalApproach] {Callsign}: go-around triggered (no landing clearance at {Dist:F2}nm)", ctx.Aircraft.Callsign, distNm);
            TriggerGoAround(ctx);
            return false;
        }

        // Phase complete at threshold
        double agl = ctx.Aircraft.Altitude - _thresholdElevation;
        bool complete = distNm < 0.05 || agl < 5;
        if (complete)
        {
            ctx.Logger.LogDebug(
                "[FinalApproach] {Callsign}: crossing threshold, dist={Dist:F3}nm, agl={Agl:F0}ft, gs={Gs:F0}kts",
                ctx.Aircraft.Callsign,
                distNm,
                agl,
                ctx.Aircraft.GroundSpeed
            );
        }

        return complete;
    }

    private void CheckInterceptDistance(PhaseContext ctx, double distNm)
    {
        if (_interceptChecked || SkipInterceptCheck || ctx.Runway is null)
        {
            return;
        }

        double crossTrack = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _thresholdLat, _thresholdLon, _runwayHeading)
        );

        double headingDiff = Math.Abs(FlightPhysics.NormalizeAngle(ctx.Aircraft.Heading - _runwayHeading));

        if (crossTrack >= InterceptCrossTrackThresholdNm || headingDiff >= InterceptHeadingThresholdDeg)
        {
            return;
        }

        // Aircraft is established on the localizer — check distance
        _interceptChecked = true;

        double minIntercept = ApproachGateDatabase.GetMinInterceptDistanceNm(ctx.Runway.AirportId, ctx.Runway.Designator);
        double interceptAngle = Math.Abs(FlightPhysics.NormalizeAngle(ctx.Aircraft.Heading - _runwayHeading));

        bool isDistanceLegal = distNm >= minIntercept;
        if (!isDistanceLegal && !_isPatternTraffic)
        {
            ctx.Aircraft.PendingWarnings.Add(
                $"Illegal intercept: turned on final {distNm:F1}nm " + $"from threshold (min {minIntercept:F1}nm) " + "[7110.65 §5-9-1]"
            );
        }

        // TBL 5-9-1: max intercept angle depends on distance to approach gate
        double distToGate = distNm - minIntercept;
        double maxAngle = distToGate < 2.0 ? 20.0 : 30.0;
        bool isAngleLegal = interceptAngle <= maxAngle;

        // Glideslope deviation at establishment
        double gsAltitude = GlideSlopeGeometry.AltitudeAtDistance(distNm, _thresholdElevation);
        double gsDeviation = ctx.Aircraft.Altitude - gsAltitude;

        // Speed at intercept
        double speedAtIntercept = ctx.Aircraft.IndicatedAirspeed > 0 ? ctx.Aircraft.IndicatedAirspeed : ctx.Aircraft.GroundSpeed;

        // Was this a forced approach clearance?
        bool wasForced = ctx.Aircraft.Phases?.ActiveApproach?.Force ?? false;

        // Capture approach score
        var approachId = ctx.Aircraft.Phases?.ActiveApproach?.ApproachId ?? "";
        var airportCode = ctx.Aircraft.Phases?.ActiveApproach?.AirportCode ?? ctx.Runway.AirportId;

        var score = new ApproachScore
        {
            Callsign = ctx.Aircraft.Callsign,
            AircraftType = ctx.Aircraft.AircraftType,
            ApproachId = approachId,
            RunwayId = ctx.Runway.Designator,
            AirportCode = airportCode,
            InterceptAngleDeg = interceptAngle,
            InterceptDistanceNm = distNm,
            MinInterceptDistanceNm = minIntercept,
            GlideSlopeDeviationFt = gsDeviation,
            SpeedAtInterceptKts = speedAtIntercept,
            WasForced = wasForced,
            IsPatternTraffic = _isPatternTraffic,
            MaxAllowedAngleDeg = maxAngle,
            IsInterceptAngleLegal = isAngleLegal,
            IsInterceptDistanceLegal = isDistanceLegal,
            EstablishedAtSeconds = ctx.ScenarioElapsedSeconds,
            EstablishedLat = ctx.Aircraft.Latitude,
            EstablishedLon = ctx.Aircraft.Longitude,
        };

        ctx.Aircraft.ActiveApproachScore = score;
        ctx.Aircraft.PendingApproachScores.Add(score);
    }

    private static bool HasLandingClearance(PhaseContext ctx)
    {
        if (ctx.AutoClearedToLand)
        {
            return true;
        }

        var phases = ctx.Aircraft.Phases;
        if (phases is null)
        {
            return false;
        }

        return phases.LandingClearance
            is ClearanceType.ClearedToLand
                or ClearanceType.ClearedForOption
                or ClearanceType.ClearedTouchAndGo
                or ClearanceType.ClearedStopAndGo
                or ClearanceType.ClearedLowApproach;
    }

    private void TriggerGoAround(PhaseContext ctx)
    {
        if (ctx.Aircraft.Phases is null)
        {
            return;
        }

        ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} is going around (no landing clearance)");

        // VFR aircraft without a pattern direction default to left traffic
        if (ctx.Aircraft.IsVfr && ctx.Aircraft.Phases.TrafficDirection is null)
        {
            ctx.Aircraft.Phases.TrafficDirection = PatternDirection.Left;
        }

        bool isPattern = ctx.Aircraft.Phases.TrafficDirection is not null;

        var goAround = new GoAroundPhase
        {
            TargetAltitude = isPattern ? (int?)(ctx.Runway?.ElevationFt + CategoryPerformance.PatternAltitudeAgl(ctx.Category)) : null,
        };

        ctx.Aircraft.Phases.ReplaceUpcoming([goAround]);
        ctx.Aircraft.Phases.AdvanceToNext(ctx);
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        // No per-phase requirements — clearance is tracked at PhaseList level
        return [];
    }
}
