using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Tracks 3° glideslope from current position to threshold.
/// Checks PhaseList-level landing clearance (CTL can be issued on
/// downwind/base, well before this phase activates).
/// Auto-triggers go-around if no clearance by 200ft AGL.
/// Completes when crossing the threshold.
/// Checks for illegal approach intercept (7110.65 §5-9-1) on
/// first tick when aircraft is established on the localizer.
/// </summary>
public sealed class FinalApproachPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("FinalApproachPhase");

    private const double AutoGoAroundAgl = 200.0;
    private const double NoClearanceWarningDistNm = 1.0;
    private const double InterceptCrossTrackThresholdNm = 0.1;
    private const double InterceptHeadingThresholdDeg = 15.0;
    private const double AimPointMinNm = 0.1;
    private const double FasTransitionDistanceNm = 5.0;

    /// <summary>
    /// Maximum FAC-vs-runway-heading difference at which an approach is considered "aligned"
    /// for establishment-check fallback purposes. Below this, the runway-heading branch is
    /// allowed (Issue #101 mag-variation tolerance); above it, only the FAC counts as
    /// established. ~10° leaves headroom for the largest mag-variation discrepancies in CONUS
    /// while still excluding genuine offset approaches like KCCR S19R (~18° offset).
    /// </summary>
    private const double IsAlignedToleranceDeg = 10.0;

    private double _thresholdLat;
    private double _thresholdLon;
    private double _thresholdElevation;

    /// <summary>
    /// Physical runway heading — used only by the intercept-legality check (Issue #101 fallback).
    /// All lateral guidance uses <see cref="_finalApproachCourse"/>, which may differ for offset
    /// approaches (LDA, RNAV with offset CF leg, VOR offset).
    /// </summary>
    private TrueHeading _runwayHeading;

    /// <summary>
    /// Published final approach course in true degrees, derived from CIFP via
    /// <see cref="Data.Vnas.FinalApproachCourseExtractor"/>. Equals runway heading for
    /// aligned approaches.
    /// </summary>
    private TrueHeading _finalApproachCourse;

    /// <summary>
    /// Cross-track reference point. For ordinary approaches this is the runway threshold;
    /// for parallel-offset approaches (KDCA LDA-X 19) it is the published MAP fix coordinates.
    /// </summary>
    private double _anchorLat;

    private double _anchorLon;
    private double _gsAngleDeg;
    private bool _goAroundTriggered;
    private bool _noClearanceWarningIssued;
    private bool _interceptChecked;
    private bool _isPatternTraffic;
    private bool _tooHighGoAroundChecked;
    private bool _fasSet;
    private double _mapDistNm;

    /// <summary>
    /// When true, skips the illegal intercept distance check.
    /// Set for aircraft that spawn on final (not vectored by RPO).
    /// </summary>
    public bool SkipInterceptCheck { get; init; }

    public override string Name => "FinalApproach";

    public override PhaseDto ToSnapshot() =>
        new FinalApproachPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            SkipInterceptCheck = SkipInterceptCheck,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            ThresholdElevation = _thresholdElevation,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            FinalApproachCourseDeg = _finalApproachCourse.Degrees,
            AnchorLat = _anchorLat == _thresholdLat ? null : _anchorLat,
            AnchorLon = _anchorLon == _thresholdLon ? null : _anchorLon,
            GsAngleDeg = _gsAngleDeg,
            GoAroundTriggered = _goAroundTriggered,
            NoClearanceWarningIssued = _noClearanceWarningIssued,
            InterceptChecked = _interceptChecked,
            IsPatternTraffic = _isPatternTraffic,
            TooHighGoAroundChecked = _tooHighGoAroundChecked,
            FasSet = _fasSet,
            MapDistNm = _mapDistNm,
        };

    public static FinalApproachPhase FromSnapshot(FinalApproachPhaseDto dto)
    {
        var phase = new FinalApproachPhase { SkipInterceptCheck = dto.SkipInterceptCheck };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
        phase._thresholdElevation = dto.ThresholdElevation;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        // Pre-FAC-extractor snapshots only have RunwayHeadingDeg; treat that as the FAC.
        phase._finalApproachCourse = dto.FinalApproachCourseDeg is { } facDeg ? new TrueHeading(facDeg) : phase._runwayHeading;
        phase._anchorLat = dto.AnchorLat ?? phase._thresholdLat;
        phase._anchorLon = dto.AnchorLon ?? phase._thresholdLon;
        phase._gsAngleDeg = dto.GsAngleDeg;
        phase._goAroundTriggered = dto.GoAroundTriggered;
        phase._noClearanceWarningIssued = dto.NoClearanceWarningIssued;
        phase._interceptChecked = dto.InterceptChecked;
        phase._isPatternTraffic = dto.IsPatternTraffic;
        phase._tooHighGoAroundChecked = dto.TooHighGoAroundChecked;
        phase._fasSet = dto.FasSet;
        phase._mapDistNm = dto.MapDistNm;
        return phase;
    }

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

        // Pull the published final approach course and (optional) lateral anchor from the
        // active approach clearance. The clearance is populated by ApproachCommandHandler
        // (and the JFAC handler) via FinalApproachCourseExtractor. When the aircraft was
        // spawned directly into final approach without a clearance, fall back to runway
        // heading + threshold for both fields.
        var clearance = ctx.Aircraft.Phases?.ActiveApproach;
        _finalApproachCourse = clearance?.FinalApproachCourse ?? _runwayHeading;
        _anchorLat = clearance?.FinalApproachAnchorLat ?? _thresholdLat;
        _anchorLon = clearance?.FinalApproachAnchorLon ?? _thresholdLon;

        _gsAngleDeg = GlideSlopeGeometry.AngleForCategory(ctx.Category);
        _isPatternTraffic = ctx.Aircraft.Phases?.TrafficDirection is not null;
        _mapDistNm = clearance?.MapDistanceNm ?? 0.5;

        ctx.Targets.TargetTrueHeading = _finalApproachCourse;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        double approachSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);

        double startDist = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon));

        // Only set FAS immediately when already within transition distance.
        // Further out, keep the current speed (InterceptCoursePhase sets 1.3×FAS)
        // and let OnTick() apply FAS when distance drops below the threshold.
        if (startDist <= FasTransitionDistanceNm)
        {
            ctx.Targets.TargetSpeed = approachSpeed;
            _fasSet = true;
        }

        double startXte = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_anchorLat, _anchorLon), _finalApproachCourse);
        Log.LogDebug(
            "[FinalApproach] {Callsign}: started, fac={Fac:F0} (rwy {Rwy:F0}), dist={Dist:F1}nm, alt={Alt:F0}ft, apchSpd={Spd:F0}kts, fasSet={FasSet}, xte={Xte:F3}nm",
            ctx.Aircraft.Callsign,
            _finalApproachCourse.Degrees,
            _runwayHeading.Degrees,
            startDist,
            ctx.Aircraft.Altitude,
            approachSpeed,
            _fasSet,
            startXte
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (_goAroundTriggered)
        {
            return false;
        }

        double distNm = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon));

        // Decelerate to FAS when within transition distance
        if (!_fasSet && (distNm <= FasTransitionDistanceNm) && !ctx.Targets.HasExplicitSpeedCommand)
        {
            double fas = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            ctx.Targets.TargetSpeed = fas;
            _fasSet = true;
            Log.LogDebug("[FinalApproach] {Callsign}: slowing to FAS {Fas:F0}kts at {Dist:F1}nm", ctx.Aircraft.Callsign, fas, distNm);
        }

        // Follow speed adjustment (Vref floor — never below final approach speed)
        if (_fasSet && (ctx.Targets.TargetSpeed is { } currentFas))
        {
            double vref = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            var adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, currentFas, vref);
            if (adjusted is not null)
            {
                ctx.Targets.TargetSpeed = adjusted.Value;
            }
        }

        CheckInterceptDistance(ctx, distNm);

        // Lateral guidance: steer toward an aim point on the published final approach course.
        // The cross-track / along-track reference is the lateral anchor (runway threshold for
        // ordinary approaches, the published MAP fix for parallel-offset approaches like LDA).
        // Lead distance based on turn radius — the kinematically natural look-ahead.
        double signedXte = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_anchorLat, _anchorLon), _finalApproachCourse);
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

        double alongTrack = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_anchorLat, _anchorLon), _finalApproachCourse);
        double aimAlongTrack = Math.Min(alongTrack + leadNm, 0.0);

        TrueHeading reciprocal = _finalApproachCourse.ToReciprocal();
        var aimPoint = GeoMath.ProjectPoint(new LatLon(_anchorLat, _anchorLon), reciprocal, Math.Abs(aimAlongTrack));
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, aimPoint);
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

        // Auto go-around if no landing clearance by 200ft AGL
        double aglForClearance = ctx.Aircraft.Altitude - _thresholdElevation;
        if ((aglForClearance <= AutoGoAroundAgl) && !hasLandingClearance)
        {
            _goAroundTriggered = true;
            Log.LogDebug(
                "[FinalApproach] {Callsign}: go-around triggered (no landing clearance at {Agl:F0}ft AGL, {Dist:F2}nm)",
                ctx.Aircraft.Callsign,
                aglForClearance,
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
                Log.LogDebug(
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
            Log.LogDebug(
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
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_anchorLat, _anchorLon), _finalApproachCourse)
        );

        // Establishment check: aircraft must be aligned with the published final approach course.
        // For Issue #101 we kept a runway-heading fallback to absorb mag-variation noise where
        // the published FAC differs from the runway-number heading by ~5-10°. Apply that
        // fallback only when the FAC and runway heading are within IsAlignedToleranceDeg of
        // each other — for genuine offset approaches (LDA, RNAV with offset CF leg, VOR offset
        // like KCCR S19R) the runway heading must NOT be accepted as "established", or an
        // aircraft tracking the runway centerline would silently pass establishment without
        // ever flying the published course.
        double facDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(_finalApproachCourse);
        double facVsRwy = _finalApproachCourse.AbsAngleTo(_runwayHeading);
        double headingDiff = facVsRwy < IsAlignedToleranceDeg ? Math.Min(facDiff, ctx.Aircraft.TrueHeading.AbsAngleTo(_runwayHeading)) : facDiff;

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
        double interceptAngle =
            ctx.Aircraft.Phases?.ActiveApproach?.InterceptCaptureAngleDeg ?? ctx.Aircraft.TrueHeading.AbsAngleTo(_finalApproachCourse);

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
            EstablishedLat = ctx.Aircraft.Position.Lat,
            EstablishedLon = ctx.Aircraft.Position.Lon,
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

    private static void TriggerGoAround(PhaseContext ctx, string reason) => GoAroundHelper.Trigger(ctx, reason);

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.Follow => CommandAcceptance.Allowed,
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
