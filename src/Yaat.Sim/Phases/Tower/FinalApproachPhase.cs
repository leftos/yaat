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
    private const double AimPointMinNm = 0.1;

    private double _thresholdLat;
    private double _thresholdLon;
    private double _thresholdElevation;
    private TrueHeading _runwayHeading;
    private double _gsAngleDeg;
    private bool _goAroundTriggered;
    private bool _noClearanceWarningIssued;
    private bool _interceptChecked;
    private bool _isPatternTraffic;
    private bool _tooHighGoAroundChecked;
    private double _mapDistNm;

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
        _gsAngleDeg = GlideSlopeGeometry.AngleForCategory(ctx.Category);
        _isPatternTraffic = ctx.Aircraft.Phases?.TrafficDirection is not null;
        _mapDistNm = ctx.Aircraft.Phases?.ActiveApproach?.MapDistanceNm ?? AutoGoAroundDistNm;

        ctx.Targets.TargetTrueHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        // Set approach speed (per-type if available)
        double approachSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        ctx.Targets.TargetSpeed = approachSpeed;

        double startDist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _thresholdLat, _thresholdLon);
        double startXte = GeoMath.SignedCrossTrackDistanceNm(
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            _thresholdLat,
            _thresholdLon,
            _runwayHeading
        );
        ctx.Logger.LogDebug(
            "[FinalApproach] {Callsign}: started, rwy hdg={Hdg:F0}, dist={Dist:F1}nm, alt={Alt:F0}ft, apchSpd={Spd:F0}kts, xte={Xte:F3}nm",
            ctx.Aircraft.Callsign,
            _runwayHeading.Degrees,
            startDist,
            ctx.Aircraft.Altitude,
            approachSpeed,
            startXte
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

        // Lateral guidance: steer toward an aim point on the extended centerline.
        // Lead distance based on turn radius — the kinematically natural look-ahead.
        // Far from centerline: lead ≈ turnRadius (smooth arc the aircraft can fly).
        // Near centerline: lead → absXte (heading converges to runway heading).
        double signedXte = GeoMath.SignedCrossTrackDistanceNm(
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            _thresholdLat,
            _thresholdLon,
            _runwayHeading
        );
        double absXte = Math.Abs(signedXte);

        // Turn radius in nm: R = V_kts / (ω_deg/s × 20π)
        double turnRate = _isPatternTraffic
            ? CategoryPerformance.PatternTurnRate(ctx.Category)
            : (ctx.Aircraft.Targets.TurnRateOverride ?? AircraftPerformance.TurnRate(ctx.AircraftType, ctx.Category));
        double turnRadiusNm = ctx.Aircraft.GroundSpeed / (turnRate * 62.832);

        // Blend: at large XTE, lead = turnRadius (kinematic intercept arc).
        // At small XTE, lead → proportional to turn radius (prevents heading oscillation).
        // Floor at 30% of turn radius keeps correction angles gentle near centerline.
        double xteRatio = turnRadiusNm > 0.01 ? Math.Clamp(absXte / turnRadiusNm, 0.0, 1.0) : 1.0;
        double minLead = Math.Max(turnRadiusNm * 0.3, AimPointMinNm);
        double leadNm = Math.Max(turnRadiusNm * xteRatio + absXte * (1.0 - xteRatio), minLead);

        double alongTrack = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _thresholdLat, _thresholdLon, _runwayHeading);
        double aimAlongTrack = Math.Min(alongTrack + leadNm, 0.0);

        TrueHeading reciprocal = _runwayHeading.ToReciprocal();
        var aimPoint = GeoMath.ProjectPoint(_thresholdLat, _thresholdLon, reciprocal, Math.Abs(aimAlongTrack));
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, aimPoint.Lat, aimPoint.Lon);
        ctx.Targets.TargetTrueHeading = new TrueHeading(bearing);

        // Target: glideslope altitude at current distance (true 3°/6° path)
        double gsAltitude = GlideSlopeGeometry.AltitudeAtDistance(distNm, _thresholdElevation, _gsAngleDeg);
        ctx.Targets.TargetAltitude = gsAltitude;

        // Descent rate: geometry-based convergence when above GS, gentle recovery when below
        double standardFpm = GlideSlopeGeometry.RequiredDescentRate(ctx.Aircraft.GroundSpeed, _gsAngleDeg);
        double deviation = ctx.Aircraft.Altitude - gsAltitude;
        double maxFpm = distNm > 2.0 ? Math.Max(Math.Min(2500, standardFpm * 2.0), 200) : 1500;

        double fpm;
        if (deviation > 50)
        {
            // Above GS: compute FPM needed to reach GS altitude at a convergence point ahead.
            // Convergence point = min(distNm - 1.0, distNm × 0.7) nm from threshold, floored at 0.5nm.
            double convergeDistNm = Math.Max(Math.Min(distNm - 1.0, distNm * 0.7), 0.5);
            double convergeGsAlt = GlideSlopeGeometry.AltitudeAtDistance(convergeDistNm, _thresholdElevation, _gsAngleDeg);
            double altToLose = ctx.Aircraft.Altitude - convergeGsAlt;
            double distToConverge = distNm - convergeDistNm;

            // Time to convergence point at current groundspeed (minutes)
            double gs = Math.Max(ctx.Aircraft.GroundSpeed, 60);
            double minutesToConverge = (distToConverge / gs) * 60.0;

            if (minutesToConverge > 0.01)
            {
                fpm = altToLose / minutesToConverge;
            }
            else
            {
                fpm = maxFpm;
            }
        }
        else
        {
            // On or below GS: gentle scaling (0.5–1.0×)
            double scale = Math.Clamp(1.0 + deviation / 1000.0, 0.5, 1.0);
            fpm = standardFpm * scale;
        }

        ctx.Targets.DesiredVerticalRate = -Math.Clamp(fpm, 200, maxFpm);

        // Check landing clearance from PhaseList (set earlier by CTL command)
        bool hasLandingClearance = HasLandingClearance(ctx);

        // Warn at 1nm if no landing clearance (only when auto-CTL is off)
        if ((distNm <= NoClearanceWarningDistNm) && !hasLandingClearance && !ctx.AutoClearedToLand && !_noClearanceWarningIssued)
        {
            _noClearanceWarningIssued = true;
            ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} is 1nm from the threshold without a landing clearance");
        }

        // Auto go-around if no landing clearance by 0.5nm from threshold
        if ((distNm <= AutoGoAroundDistNm) && !hasLandingClearance)
        {
            _goAroundTriggered = true;
            ctx.Logger.LogDebug(
                "[FinalApproach] {Callsign}: go-around triggered (no landing clearance at {Dist:F2}nm)",
                ctx.Aircraft.Callsign,
                distNm
            );
            TriggerGoAround(ctx, "no landing clearance");
            return false;
        }

        // Go-around if too high at the MAP to make it down safely
        if ((distNm <= _mapDistNm) && !_tooHighGoAroundChecked && hasLandingClearance)
        {
            _tooHighGoAroundChecked = true;
            int mapAlt = ctx.Aircraft.Phases?.ActiveApproach?.MapAltitudeFt ?? (int)(_thresholdElevation + 200);
            if (ctx.Aircraft.Altitude > mapAlt + 200)
            {
                _goAroundTriggered = true;
                ctx.Logger.LogDebug(
                    "[FinalApproach] {Callsign}: go-around triggered (too high at MAP: {Alt:F0}ft, MAP alt {MapAlt}ft, at {Dist:F2}nm)",
                    ctx.Aircraft.Callsign,
                    ctx.Aircraft.Altitude,
                    mapAlt,
                    distNm
                );
                TriggerGoAround(ctx, "too high at missed approach point");
                return false;
            }
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

        // VFR aircraft are not included in the approach report at all
        if (ctx.Aircraft.IsVfr)
        {
            _interceptChecked = true;
            return;
        }

        double crossTrack = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _thresholdLat, _thresholdLon, _runwayHeading)
        );

        double headingDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(_runwayHeading);

        if (crossTrack >= InterceptCrossTrackThresholdNm || headingDiff >= InterceptHeadingThresholdDeg)
        {
            return;
        }

        // Aircraft is established on the localizer — check distance
        _interceptChecked = true;

        // Use the capture distance (when InterceptCoursePhase recorded it) for distance-based
        // checks. The capture moment is when the aircraft actually turned onto the localizer;
        // FinalApproachPhase's stricter establishment criteria fire later, closer in.
        double captureDistNm = ctx.Aircraft.Phases?.ActiveApproach?.InterceptCaptureDistanceNm ?? distNm;

        // Visual approaches are not subject to 7110.65 §5-9-1 intercept rules
        bool isVisualApproach = ctx.Aircraft.Phases?.ActiveApproach?.ApproachId.StartsWith("VIS", StringComparison.Ordinal) == true;

        double minIntercept = ApproachGateDatabase.GetMinInterceptDistanceNm(ctx.Runway.AirportId, ctx.Runway.Designator);

        // Use the capture angle (recorded at the actual intercept moment) when available.
        // At establishment time the aircraft is already aligned (< 15°), making the current
        // heading diff meaningless for scoring.
        double interceptAngle = ctx.Aircraft.Phases?.ActiveApproach?.InterceptCaptureAngleDeg ?? ctx.Aircraft.TrueHeading.AbsAngleTo(_runwayHeading);

        // Distance legality is checked at capture time (InterceptCoursePhase.Capture),
        // but recorded on the score for the approach report.
        bool isDistanceLegal = isVisualApproach || captureDistNm >= minIntercept;

        // TBL 5-9-1: max intercept angle depends on distance to approach gate
        // Approach gate = minIntercept - 2nm (the 2nm padding is from gate to min intercept)
        double approachGate = minIntercept - 2.0;
        double distToGate = captureDistNm - approachGate;
        double maxAngle = distToGate < 2.0 ? 20.0 : 30.0;
        bool isAngleLegal = isVisualApproach || interceptAngle <= maxAngle;

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
            InterceptDistanceNm = captureDistNm,
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

    private void TriggerGoAround(PhaseContext ctx, string reason)
    {
        if (ctx.Aircraft.Phases is null)
        {
            return;
        }

        ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} is going around ({reason})");

        // VFR aircraft without a pattern direction default to left traffic
        if (ctx.Aircraft.IsVfr && ctx.Aircraft.Phases.TrafficDirection is null)
        {
            ctx.Aircraft.Phases.TrafficDirection = PatternDirection.Left;
        }

        bool isPattern = ctx.Aircraft.Phases.TrafficDirection is not null;

        // For instrument approaches with MAP data, use MAP altitude and queue MAP phases
        var mapPhases = isPattern ? [] : ApproachCommandHandler.BuildMissedApproachPhases(ctx.Aircraft);
        int? targetAlt;
        if (mapPhases.Count > 0)
        {
            var mapFixes = ctx.Aircraft.Phases.ActiveApproach!.MissedApproachFixes;
            targetAlt = ApproachCommandHandler.GetMissedApproachAltitude(mapFixes);
        }
        else if (isPattern)
        {
            targetAlt = (int?)(ctx.Runway?.ElevationFt + CategoryPerformance.PatternAltitudeAgl(ctx.Category));
        }
        else
        {
            targetAlt = null;
        }

        var goAround = new GoAroundPhase { TargetAltitude = targetAlt, ReenterPattern = isPattern };

        var phases = new List<Phase> { goAround };
        phases.AddRange(mapPhases);

        ctx.Aircraft.Phases.ReplaceUpcoming(phases);
        ctx.Aircraft.Phases.AdvanceToNext(ctx);
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
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
