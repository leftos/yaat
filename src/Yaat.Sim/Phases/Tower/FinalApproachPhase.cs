using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Tracks 3° glideslope from current position to threshold.
/// Checks PhaseList-level landing clearance (CTL can be issued on
/// downwind/base, well before this phase activates).
/// Auto-triggers go-around if no clearance by published DA/MDA when available,
/// otherwise by 200ft AGL.
/// Completes when crossing the threshold.
/// Checks for illegal approach intercept (7110.65 §5-9-1) on
/// first tick when aircraft is established on the localizer.
/// </summary>
public sealed class FinalApproachPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("FinalApproachPhase");

    private const double AutoGoAroundAgl = 200.0;
    private const double NoClearanceWarningDistNm = 1.0;
    private const double MinimumsNoClearanceWarningBufferFt = 1000.0;
    private const double InterceptCrossTrackThresholdNm = 0.1;
    private const double InterceptHeadingThresholdDeg = 15.0;

    /// <summary>
    /// Lateral-established gate for starting the glideslope descent: the aircraft must be within
    /// this many degrees of the final approach course (well inside the 7110.65 §5-9-2 intercept
    /// ceiling) before glideslope capture, so it does not descend while still slewing onto course.
    /// </summary>
    private const double GsEstablishedHeadingDeg = 5.0;

    /// <summary>Cross-track tolerance for the glideslope lateral-established gate ("on centerline").</summary>
    private const double GsEstablishedCrossTrackNm = 0.15;

    /// <summary>
    /// A recorded intercept capture angle steeper than the normal 30° bust-through gate marks a
    /// force-captured intercept (PTACF / implied-PTAC) that will S-turn onto course — it bypasses
    /// the lateral glideslope gate (which would otherwise leave it high while it recovers).
    /// </summary>
    private const double ForcedInterceptCaptureAngleDeg = 30.0;
    private const double AimPointMinNm = 0.1;

    /// <summary>
    /// Distance from threshold by which the aircraft must be settled at FAS. Anchored
    /// between the VMC 500-ft (~1.6 NM on a 3° GS) and IMC 1000-ft (~3.2 NM) stabilized
    /// approach gates per FAA AC 120-71 / InFO 11009. The kinematic FAS trigger is
    /// computed as <c>FasReachGateNm + bleedDistance</c> so the aircraft hits FAS at this
    /// distance regardless of how much speed it had to bleed.
    /// </summary>
    private const double FasReachGateNm = 2.0;

    /// <summary>
    /// Upper bound on the kinematic FAS trigger: never start the FAS deceleration
    /// earlier than this. Preserves the prior fixed 5.0 NM behavior as a safety cap
    /// for unusually fast or unusually slow-decelerating aircraft.
    /// </summary>
    private const double MaxFasTriggerNm = 5.0;

    /// <summary>
    /// Distance from threshold by which the aircraft must be settled at configuration
    /// speed (1.3·Vref). Sized so heavies arriving hot (typically at 1.6·Vref from the
    /// OnFinal spawn formula or InterceptCoursePhase handoff) finish their first-stage
    /// bleed before the stabilized-approach window begins (FAA AC 120-71 / InFO 11009),
    /// leaving the second-stage FAS bleed to settle inside <see cref="FasReachGateNm"/>.
    /// Mirrors the <c>1.3·FAS</c> step that <see cref="Approach.InterceptCoursePhase"/>
    /// applies for vectored aircraft — re-applied here for OnFinal spawns and for any
    /// aircraft that reach FinalApproachPhase still above the configuration band.
    /// </summary>
    private const double ConfigReachGateNm = 5.0;

    /// <summary>
    /// Upper bound on the kinematic configuration trigger. Caps the bleed start so
    /// even a slow-decelerating heavy doesn't begin the configuration bleed beyond a
    /// reasonable approach-management distance (typical FAFs are 5-7 NM from the
    /// threshold for ILS approaches at OAK/SFO/SJC).
    /// </summary>
    private const double MaxConfigTriggerNm = 8.0;

    /// <summary>
    /// Configuration speed multiplier on Vref. 1.3·Vref is the unstabilized-approach
    /// gate (FAA AC 120-71 / InFO 11009) — held as a configuration target lets the
    /// aircraft be at flap-extended approach speed by the stabilization window, then
    /// bleed to Vref in the last 2 NM. Same multiplier used by InterceptCoursePhase.
    /// </summary>
    private const double ConfigSpeedMultiplier = 1.3;

    /// <summary>
    /// Time-to-threshold (seconds) inside which the follower stops chasing the
    /// leader and just stabilizes for landing. Committed to the approach at this
    /// point — either land or go around; chasing risks tripping the unstabilized
    /// gate (IAS &gt; 1.3·Vref). At ~75 kt this is ≈1.25 nm ≈ 400 ft AGL on a 3° GS,
    /// firmly inside the industry VMC 500-ft stabilized gate (FAA AC 120-71 /
    /// InFO 11009) and well below the 1000-ft IMC gate.
    /// </summary>
    private const double StabilizationWindowSeconds = 60.0;

    /// <summary>
    /// Maximum FAC-vs-runway-heading difference at which an approach is considered "aligned"
    /// for establishment-check fallback purposes. Below this, the runway-heading branch is
    /// allowed (Issue #101 mag-variation tolerance); above it, only the FAC counts as
    /// established. ~10° leaves headroom for the largest mag-variation discrepancies in CONUS
    /// while still excluding genuine offset approaches like KCCR S19R (~18° offset).
    /// </summary>
    private const double IsAlignedToleranceDeg = 10.0;

    /// <summary>
    /// AGL at which the visual-alignment ramp begins for a small (CIFP/mag-var-rounding)
    /// FAC offset. Below this altitude the steering reference (cross-track course AND
    /// lateral anchor) blends from the published FAC toward the runway centerline,
    /// completing by <see cref="MagVarRampEndAgl"/>. 300 ft AGL gives the aircraft enough
    /// time to converge laterally onto the centerline at gentle bank (≤ ~1° for a typical
    /// 3° offset) before flare. Treated as straight-in per AIM 5-4-20.3 (offsets &lt; 30°
    /// from runway alignment).
    /// </summary>
    private const double MagVarRampStartAgl = 300.0;

    /// <summary>
    /// AGL at which the small-offset alignment ramp completes. Anchored at 50 ft AGL —
    /// above the AGL-primary FinalApproach handoff (now <c>agl &lt; 30 || (distNm &lt;
    /// 0.05 &amp;&amp; agl &lt; 50)</c>) so the lerp finishes before LandingPhase takes
    /// over, and close to the AIM 5-4-20.3 visual-segment guidance (transition to visual
    /// reference around 50 ft AGL on a 3° glideslope).
    /// </summary>
    private const double MagVarRampEndAgl = 50.0;

    /// <summary>
    /// Buffer (ft) added above the published MAP-altitude AGL to derive the genuine-offset
    /// ramp start. For non-precision approaches the MAP altitude is the published MDA — a
    /// reasonable DA proxy. The buffer puts the ramp start ~300 ft above DA so the
    /// alignment maneuver overlaps the visual segment per AIM 5-4-16.7.4 (stabilized BY
    /// SAP). FAACIFP18 doesn't publish actual DA on procedure continuation records, so
    /// MAP-altitude + buffer is the closest we can get without commercial NavData.
    /// </summary>
    private const double OffsetRampStartBufferAgl = 300.0;

    /// <summary>
    /// Floor for the genuine-offset ramp start AGL when MAP altitude is unavailable, very
    /// low (precision approach with MAP at threshold), or otherwise produces a too-narrow
    /// window above SAP. 700 ft AGL guarantees at least 200 ft of ramp window above
    /// <see cref="OffsetRampEndAgl"/>.
    /// </summary>
    private const double MinOffsetRampStartAgl = 700.0;

    /// <summary>
    /// AGL at which the offset-approach alignment ramp completes — the Stabilized
    /// Approach Point (SAP) per AIM 5-4-16.7.4. Aircraft must be aligned with extended
    /// runway centerline AND runway heading by this altitude, otherwise the published
    /// stabilized-approach criteria are violated.
    /// </summary>
    private const double OffsetRampEndAgl = 500.0;

    /// <summary>
    /// Minimum FAC-vs-runway-heading offset (degrees) at which the visual-alignment ramp
    /// applies. Below this threshold the ramp is a no-op — the bearing-to-aim-point already
    /// resolves to the runway centerline because FAC == runway, and lerping further would
    /// drag the bearing toward runway heading at the expense of XTE-correction angles
    /// (breaks pattern intercepts that rely on bearing being the aim-point intercept).
    /// </summary>
    private const double FacRampMinOffsetDeg = 0.5;

    /// <summary>
    /// FAC-vs-runway-heading offset (degrees) above which we treat the approach as a
    /// genuine offset (LDA / RNAV-with-offset-CF / VOR-offset / SOIA) rather than a
    /// small CIFP/mag-var rounding artifact. Genuine offsets anchor the alignment ramp
    /// on the SAP (500 ft AGL); rounding artifacts use the tighter 300 → 100 ft window.
    /// 5° is a defensible split — typical CONUS mag-var rounding is well under 5°, and
    /// virtually every published offset approach exceeds it (KCCR S19R = 18°, KSAN
    /// LOC 27 = 9°, etc.).
    /// </summary>
    private const double OffsetApproachThresholdDeg = 5.0;

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

    /// <summary>
    /// Cached distance from aircraft to threshold, updated each OnTick.
    /// Read by CanAcceptCommand to gate speed-class commands inside 5 nm
    /// (where the aircraft is committed to the final approach speed).
    /// </summary>
    internal double DistanceToThresholdNm { get; set; } = double.MaxValue;

    private const double SpeedCommandFinalGateNm = 5.0;
    private bool _noClearanceWarningIssued;
    private bool _interceptChecked;
    private bool _isPatternTraffic;
    private bool _tooHighGoAroundChecked;
    private bool _fasSet;
    private bool _configSet;
    private bool _gsCaptured;
    private double _mapDistNm;

    /// <summary>
    /// Aircraft is considered "captured" on the glideslope once its altitude reaches the GS
    /// path (within this window from above) — at that point the phase locks onto GS for the
    /// rest of the descent. Below the window and not yet captured, the phase holds the
    /// assigned altitude rather than commanding a climb up to GS.
    /// </summary>
    private const double GsCaptureWindowFt = 50.0;

    /// <summary>
    /// When true, skips the illegal intercept distance check.
    /// Set for aircraft that spawn on final (not vectored by RPO).
    /// </summary>
    public bool SkipInterceptCheck { get; init; }

    public override string Name => "FinalApproach";

    /// <summary>
    /// FinalApproach owns speed: it commands the FAS deceleration and (for followers)
    /// the spacing-adjusted target. Without this, <see cref="FlightPhysics.UpdateSpeed"/>'s
    /// auto-speed-schedule fires whenever <see cref="ControlTargets.TargetSpeed"/> snaps
    /// to null (which happens every time IAS reaches the FAS goal within tolerance) and
    /// reassigns the category-default descent speed (e.g. ~110 kt for a small piston),
    /// causing the aircraft to accelerate from FAS back up through 1.3·Vref before reaching
    /// the threshold and triggering an unprompted unstable-approach go-around.
    /// </summary>
    public override bool ManagesSpeed => true;

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
            ConfigSet = _configSet,
            MapDistNm = _mapDistNm,
            GsCaptured = _gsCaptured,
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
        // Pre-config-gate snapshots (ConfigSet absent / default false) replay correctly:
        // FasSet=true implies the aircraft already cleared the config band, so seed
        // _configSet from FasSet to avoid re-firing the config gate on restored state.
        phase._configSet = dto.ConfigSet || dto.FasSet;
        phase._mapDistNm = dto.MapDistNm;
        phase._gsCaptured = dto.GsCaptured;
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

        // Lateral intercept only (JFAC/JLOC, no CAPP): keep the aircraft's assigned speed —
        // a localizer join does not start the approach deceleration. OnTick holds altitude
        // and skips the FAS/config decel until CAPP authorizes the approach.
        if (clearance?.LateralInterceptOnly == true)
        {
            return;
        }

        double approachSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        double configSpeed = approachSpeed * ConfigSpeedMultiplier;
        double decelRate = AircraftPerformance.DecelRate(ctx.AircraftType, ctx.Category);

        double startDist = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon));

        // Two-stage decel. Inside the FAS trigger: command Vref directly (FAS implies
        // past the configuration band, so seed _configSet too). Otherwise check the
        // configuration gate — heavies arriving at 1.6·Vref bleed to 1.3·Vref before
        // the stabilized window. If already at or below configSpeed (small jets, or
        // aircraft handed off from InterceptCoursePhase post-1.3·FAS), short-circuit
        // _configSet and leave TargetSpeed alone so OnTick can fire FAS later.
        double startFasTrigger = ComputeFasTriggerDistanceNm(ctx.Aircraft.IndicatedAirspeed, approachSpeed, ctx.Aircraft.GroundSpeed, decelRate);
        if (startDist <= startFasTrigger)
        {
            ctx.Targets.TargetSpeed = approachSpeed;
            _fasSet = true;
            _configSet = true;
        }
        else if (ctx.Aircraft.IndicatedAirspeed > configSpeed)
        {
            double startConfigTrigger = ComputeConfigTriggerDistanceNm(
                ctx.Aircraft.IndicatedAirspeed,
                configSpeed,
                ctx.Aircraft.GroundSpeed,
                decelRate
            );
            if (startDist <= startConfigTrigger)
            {
                ctx.Targets.TargetSpeed = configSpeed;
                _configSet = true;
            }
        }
        else
        {
            _configSet = true;
        }

        double startXte = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_anchorLat, _anchorLon), _finalApproachCourse);
        Log.LogDebug(
            "[FinalApproach] {Callsign}: started, fac={Fac:F0} (rwy {Rwy:F0}), dist={Dist:F1}nm, alt={Alt:F0}ft, apchSpd={Spd:F0}kts, configSpd={Cfg:F0}kts, configSet={CfgSet}, fasSet={FasSet}, xte={Xte:F3}nm",
            ctx.Aircraft.Callsign,
            _finalApproachCourse.Degrees,
            _runwayHeading.Degrees,
            startDist,
            ctx.Aircraft.Altitude,
            approachSpeed,
            configSpeed,
            _configSet,
            _fasSet,
            startXte
        );

        // Pilot check-in for aircraft that spawn directly on final. Aircraft that flew the
        // approach sequence already announced upstream (at parking, on a STAR check-in) — the
        // HasMadeInitialContact gate excludes them. Pattern traffic is also excluded:
        // PatternEntryPhase fires its own initial-call (closed-traffic request), and the
        // uncleared short-final reminder below handles the mid-final pilot speech.
        if (
            ctx.SoloTrainingMode
            && !ctx.Aircraft.HasMadeInitialContact
            && !_isPatternTraffic
            && PilotInitialContactEligibility.CanInitiateWithStudent(
                ctx.Aircraft,
                new InitialContactEligibilityContext(
                    ctx.StudentPosition,
                    ctx.StudentPositionType,
                    ctx.ArtccId,
                    ctx.PrimaryAirportId,
                    ctx.InitialContactTransfers
                )
            )
        )
        {
            var rwyId = ctx.Runway?.Designator ?? clearance?.RunwayId ?? "the runway";
            var ifrWithApch = !ctx.Aircraft.FlightPlan.IsVfr && clearance is not null;
            var distMiles = (int)Math.Round(startDist);
            var facilityCallName = PilotResponder.ResolveContextFacilityCallName(ctx.StudentPositionType, ctx.StudentRadioName, "TWR", "tower");
            var line = PilotResponder.BuildOnFinal(ctx.Aircraft, rwyId, ifrWithApch, clearance?.ApproachId, distMiles, facilityCallName);
            PilotResponder.QueueSoloPilotTransmission(ctx.Aircraft, line, PilotTransmissionKind.Proactive, PilotResponder.SourceResponse);
            PilotRequestTracker.RecordRequest(
                ctx.Aircraft,
                PilotPendingRequestKind.Landing,
                ctx.ScenarioElapsedSeconds,
                line.Tts,
                PilotRequestContext.Runway(rwyId, facilityCallName)
            );
            ctx.Aircraft.HasMadeInitialContact = true;
        }

        // Solo-training pilot-decision go-around. Single roll per approach: AI aircraft on
        // final spontaneously break off the approach with a controller-configurable chance.
        // Consumes one value from the deterministic RNG stream — replays regenerate the same
        // outcome because SimulationWorld.Rng state is captured in StateSnapshotDto.
        if (ctx.SoloTrainingMode && ctx.SoloGoAroundProbabilityPercent > 0 && ctx.Rng is not null)
        {
            double roll = ctx.Rng.NextDouble() * 100.0;
            if (roll < ctx.SoloGoAroundProbabilityPercent)
            {
                _goAroundTriggered = true;
                TriggerGoAround(ctx, "pilot decision");
            }
        }
    }

    /// <summary>
    /// Re-aim this active phase at a different runway without resetting glideslope /
    /// FAS / intercept state. Used by EF parallel-runway sidestep (7110.65 §5-9-7):
    /// the aircraft is already established on a parallel final, the controller
    /// switches the assigned runway, and the phase keeps running with shifted
    /// threshold + centerline. The cross-track correction in OnTick converges the
    /// aircraft onto the new centerline.
    /// </summary>
    internal void RetargetRunway(RunwayInfo newRunway, double gsAngleDeg)
    {
        _thresholdLat = newRunway.ThresholdLatitude;
        _thresholdLon = newRunway.ThresholdLongitude;
        _thresholdElevation = newRunway.ElevationFt;
        _runwayHeading = newRunway.TrueHeading;
        _finalApproachCourse = newRunway.TrueHeading;
        _anchorLat = newRunway.ThresholdLatitude;
        _anchorLon = newRunway.ThresholdLongitude;
        _gsAngleDeg = gsAngleDeg;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (_goAroundTriggered)
        {
            return false;
        }

        // Lead-not-found / lead-on-ground / runaway-distance watchdog. See
        // DownwindPhase.OnTick for the full rationale. The on-ground branch
        // is what catches a leader that lands while we're still on final.
        AirborneFollowHelper.CheckLeadLifecycle(ctx);

        double distNm = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon));
        DistanceToThresholdNm = distNm;

        // JFAC/JLOC lateral intercept without CAPP: track the final approach course but hold
        // the assigned altitude and assigned speed. No approach deceleration here.
        bool lateralOnly = ctx.Aircraft.Phases?.ActiveApproach?.LateralInterceptOnly == true;

        // Two-stage decel. FAS gate fires first (Vref by ~2 NM); if the aircraft is
        // outside that trigger but still above configuration speed (1.3·Vref), the
        // configuration gate fires to start the first-stage bleed. Both respect
        // controller-issued speed commands.
        if (!lateralOnly && !_fasSet && !ctx.Targets.HasExplicitSpeedCommand)
        {
            double fas = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            double decelRate = AircraftPerformance.DecelRate(ctx.AircraftType, ctx.Category);
            double fasTrigger = ComputeFasTriggerDistanceNm(ctx.Aircraft.IndicatedAirspeed, fas, ctx.Aircraft.GroundSpeed, decelRate);
            if (distNm <= fasTrigger)
            {
                ctx.Targets.TargetSpeed = fas;
                _fasSet = true;
                _configSet = true;
                Log.LogDebug(
                    "[FinalApproach] {Callsign}: slowing to FAS {Fas:F0}kts at {Dist:F1}nm (trigger={Trigger:F2}nm)",
                    ctx.Aircraft.Callsign,
                    fas,
                    distNm,
                    fasTrigger
                );
            }
            else if (!_configSet)
            {
                double configSpeed = fas * ConfigSpeedMultiplier;
                if (ctx.Aircraft.IndicatedAirspeed > configSpeed)
                {
                    double configTrigger = ComputeConfigTriggerDistanceNm(
                        ctx.Aircraft.IndicatedAirspeed,
                        configSpeed,
                        ctx.Aircraft.GroundSpeed,
                        decelRate
                    );
                    if (distNm <= configTrigger)
                    {
                        ctx.Targets.TargetSpeed = configSpeed;
                        _configSet = true;
                        Log.LogDebug(
                            "[FinalApproach] {Callsign}: slowing to config speed {Cfg:F0}kts at {Dist:F1}nm (trigger={Trigger:F2}nm)",
                            ctx.Aircraft.Callsign,
                            configSpeed,
                            distNm,
                            configTrigger
                        );
                    }
                }
                else
                {
                    _configSet = true;
                }
            }
        }

        // Follow speed adjustment on final has three rules:
        // 1. Feed Vref (phase baseline) as normalSpeed — never the previous tick's
        //    target — otherwise the +MaxSpeedAdjust clamp compounds each tick and
        //    TargetSpeed runs away (how N346G hit 167 KIAS in S2-OAK-3).
        // 2. Use the tighter MaxSpeedAdjustFinalKts ceiling so even a legitimate
        //    chase stays clear of the unstabilized-GA gate (IAS > 1.3·Vref).
        // 3. Inside the stabilization window (≤60s to threshold, or leader on
        //    ground), stop adjusting altogether and anchor on Vref. The follower
        //    is committed — land safely or go around, don't chase.
        if (_fasSet && ctx.Aircraft.Approach.FollowingCallsign is not null)
        {
            double vref = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            double gs = ctx.Aircraft.GroundSpeed;
            bool inStabilizationWindow = (gs > 0) && ((distNm / gs * 3600.0) <= StabilizationWindowSeconds);
            var lead = ctx.AircraftLookup?.Invoke(ctx.Aircraft.Approach.FollowingCallsign);
            bool leaderOnGround = lead?.IsOnGround ?? true;

            if (inStabilizationWindow || leaderOnGround)
            {
                ctx.Targets.TargetSpeed = vref;
            }
            else
            {
                var adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, vref, vref, AirborneFollowHelper.MaxSpeedAdjustFinalKts);
                if (adjusted is not null)
                {
                    ctx.Targets.TargetSpeed = adjusted.Value;
                }
            }
        }

        CheckInterceptDistance(ctx, distNm);

        // Visual-alignment ramp: blend the lateral guidance reference (cross-track course
        // AND anchor) from the published FAC toward the runway centerline as the aircraft
        // descends through the alignment window. The aim-point bearing then naturally
        // rotates from FAC-bearing to runway-heading-bearing, pulling the aircraft onto
        // the centerline in both heading AND lateral position simultaneously — mirroring
        // a pilot's transition at minimums (AIM 5-4-16.7.4/5: stabilize on extended
        // runway centerline by SAP for offset approaches; AIM 5-4-20.3: small offsets
        // are flown straight-in with the visual segment finishing the alignment).
        //
        // Three windows depending on offset magnitude:
        //   * < FacRampMinOffsetDeg          → no-op (FAC ≡ runway centerline)
        //   * < OffsetApproachThresholdDeg   → small CIFP/mag-var rounding: 300 → 50 ft AGL
        //   * ≥ OffsetApproachThresholdDeg   → genuine offset approach (LDA, RNAV w/ offset
        //                                      CF, VOR offset, SOIA): 1000 → 500 ft AGL,
        //                                      ending at the FAA-published Stabilized
        //                                      Approach Point (AIM 5-4-16.7.4).
        //
        // Smoothstep easing (3t²−2t³) gives zero-derivative endpoints — bank rolls in
        // and out smoothly, matching real pilot stick inputs and avoiding the
        // commanded-turn-rate square wave that linear lerping produces.
        double agl = ctx.Aircraft.Altitude - _thresholdElevation;
        double facVsRunwayDeg = _finalApproachCourse.AbsAngleTo(_runwayHeading);

        TrueHeading lateralCourse = _finalApproachCourse;
        LatLon lateralAnchor = new(_anchorLat, _anchorLon);
        double rampT = 0.0;

        if (facVsRunwayDeg >= FacRampMinOffsetDeg)
        {
            bool genuineOffset = facVsRunwayDeg >= OffsetApproachThresholdDeg;
            double rampStart = genuineOffset ? ComputeOffsetRampStartAgl(ctx.Aircraft.Phases?.ActiveApproach?.MapAltitudeFt) : MagVarRampStartAgl;
            double rampEnd = genuineOffset ? OffsetRampEndAgl : MagVarRampEndAgl;

            double linearT;
            if (agl >= rampStart)
            {
                linearT = 0.0;
            }
            else if (agl <= rampEnd)
            {
                linearT = 1.0;
            }
            else
            {
                linearT = (rampStart - agl) / (rampStart - rampEnd);
            }

            rampT = (linearT * linearT) * (3.0 - (2.0 * linearT));
            lateralCourse = TrueHeading.Lerp(_finalApproachCourse, _runwayHeading, rampT);
            lateralAnchor = LatLon.Lerp(new LatLon(_anchorLat, _anchorLon), new LatLon(_thresholdLat, _thresholdLon), rampT);
        }

        // Lateral guidance: steer toward an aim point on the (possibly lerped) reference
        // line. Lead distance based on turn radius — the kinematically natural look-ahead.
        double signedXte = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, lateralAnchor, lateralCourse);
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
        double leadNm = Math.Max((turnRadiusNm * xteRatio) + (absXte * (1.0 - xteRatio)), minLead);

        double alongTrack = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, lateralAnchor, lateralCourse);
        double aimAlongTrack = alongTrack + leadNm;

        // Aim-point along the (lerped) course relative to the (lerped) anchor. When
        // aimAlongTrack ≥ 0, the projection runs forward along the course (e.g. the
        // aircraft is within leadNm of the anchor — projecting forward along the course
        // keeps bearing aligned with the course rather than collapsing to "direction to
        // anchor"). When negative, the projection runs backward along the reciprocal.
        LatLon aimPoint =
            aimAlongTrack >= 0
                ? GeoMath.ProjectPoint(lateralAnchor, lateralCourse, aimAlongTrack)
                : GeoMath.ProjectPoint(lateralAnchor, lateralCourse.ToReciprocal(), -aimAlongTrack);
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, aimPoint);

        ctx.Targets.TargetTrueHeading = new TrueHeading(bearing);

        // JFAC/JLOC lateral intercept without CAPP: hold the assigned altitude on the final
        // approach course — no glideslope descent, no minimums/go-around/landing logic — until
        // CAPP clears LateralInterceptOnly. AIM §5-4-7.a.6 / §5-4-14.b.1: maintain the last
        // assigned altitude until cleared for the approach and established on the glideslope.
        if (lateralOnly)
        {
            ctx.Targets.TargetAltitude = ctx.Targets.AssignedAltitude ?? ctx.Aircraft.Altitude;
            ctx.Targets.DesiredVerticalRate = null;
            return false;
        }

        // Target: glideslope altitude at current distance (true 3°/6° path).
        // Only follow GS once the aircraft is at/above it. Below GS and not yet captured,
        // hold the assigned altitude (clamped by current altitude — never climb up to assigned
        // either) and wait for the GS to descend to meet the aircraft from above. Aircraft must
        // never fly UP to capture a glideslope.
        double gsAltitude = GlideSlopeGeometry.AltitudeAtDistance(distNm, _thresholdElevation, _gsAngleDeg);
        if (!_gsCaptured && (ctx.Aircraft.Altitude >= gsAltitude - GsCaptureWindowFt) && IsLaterallyEstablishedForGs(ctx, absXte))
        {
            _gsCaptured = true;
        }

        if (_gsCaptured)
        {
            ctx.Targets.TargetAltitude = gsAltitude;
        }
        else
        {
            double assigned = ctx.Targets.AssignedAltitude ?? ctx.Aircraft.Altitude;
            ctx.Targets.TargetAltitude = Math.Min(assigned, ctx.Aircraft.Altitude);
        }

        // Descent rate: geometry-based convergence when above GS, gentle recovery when below
        double standardFpm = GlideSlopeGeometry.RequiredDescentRate(ctx.Aircraft.GroundSpeed, _gsAngleDeg);
        double deviation = ctx.Aircraft.Altitude - gsAltitude;
        double maxFpm = distNm > 2.0 ? Math.Max(Math.Min(2500, standardFpm * 2.0), 200) : 1500;

        double fpm;
        if (!_gsCaptured)
        {
            // Below GS waiting for capture: hold level. Without this branch the gentle-recovery
            // formula below would still command a 200 fpm minimum descent (per the Math.Clamp
            // floor) and slowly bleed altitude — opposite of AIM 5-4-14 ("maintain assigned
            // altitude until intercepting the glideslope").
            ctx.Targets.DesiredVerticalRate = 0;
            fpm = 0;
        }
        else if (deviation > 50)
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
            // On or below GS (captured): gentle scaling (0.5–1.0×)
            double scale = Math.Clamp(1.0 + deviation / 1000.0, 0.5, 1.0);
            fpm = standardFpm * scale;
        }

        if (_gsCaptured)
        {
            ctx.Targets.DesiredVerticalRate = -Math.Clamp(fpm, 200, maxFpm);
        }

        // Check landing clearance from PhaseList (set earlier by CTL command)
        bool hasLandingClearance = HasLandingClearance(ctx);

        var activeApproach = ctx.Aircraft.Phases?.ActiveApproach;
        if (activeApproach?.MapAltitudeFt is { } mapAltitudeFt)
        {
            double warningAltitudeFt = mapAltitudeFt + MinimumsNoClearanceWarningBufferFt;
            if (
                (ctx.Aircraft.Altitude <= warningAltitudeFt)
                && (ctx.Aircraft.Altitude > mapAltitudeFt)
                && !hasLandingClearance
                && !_noClearanceWarningIssued
            )
            {
                _noClearanceWarningIssued = true;
                PilotResponder.RouteSoloOrRpoTransmission(
                    ctx.Aircraft,
                    ctx.SoloTrainingMode,
                    ctx.RpoShowPilotSpeech,
                    ctx.StudentPositionType,
                    PilotResponder.BuildApproachingMinimumsNoLandingClearance(ctx.Aircraft),
                    PilotResponder.SoloPositionsTowerApproach
                );
            }

            if ((ctx.Aircraft.Altitude <= mapAltitudeFt) && !hasLandingClearance)
            {
                _goAroundTriggered = true;
                Log.LogDebug(
                    "[FinalApproach] {Callsign}: go-around triggered (no landing clearance at minimums: alt {Alt:F0}ft, MAP alt {MapAlt}ft, {Dist:F2}nm)",
                    ctx.Aircraft.Callsign,
                    ctx.Aircraft.Altitude,
                    mapAltitudeFt,
                    distNm
                );
                ctx.Aircraft.NoLandingClearanceWarningActive = false;
                TriggerGoAround(ctx, "no landing clearance at minimums");
                return false;
            }
        }
        else
        {
            // Warn at 1nm if no landing clearance (only when auto-CTL is off).
            // Solo-training VFR pattern aircraft voice the reminder as delayed pilot speech.
            // every other aircraft (IFR, non-pattern, RPO mode) keeps the controller-facing warning.
            if ((distNm <= NoClearanceWarningDistNm) && !hasLandingClearance && !ctx.AutoClearedToLand && !_noClearanceWarningIssued)
            {
                _noClearanceWarningIssued = true;
                if (ctx.SoloTrainingMode && _isPatternTraffic && ctx.Aircraft.FlightPlan.IsVfr)
                {
                    string runwayId = ctx.Runway?.Designator ?? "the runway";
                    PilotResponder.QueueSoloPilotTransmission(
                        ctx.Aircraft,
                        PilotResponder.BuildShortFinalReminder(ctx.Aircraft, runwayId),
                        PilotTransmissionKind.Proactive,
                        PilotResponder.SourceResponse
                    );
                }
                else
                {
                    string runwayId = ctx.Runway?.Designator ?? "the runway";
                    PilotResponder.RouteSoloOrRpoTransmission(
                        ctx.Aircraft,
                        ctx.SoloTrainingMode,
                        ctx.RpoShowPilotSpeech,
                        ctx.StudentPositionType,
                        PilotResponder.BuildShortFinalReminder(ctx.Aircraft, runwayId),
                        PilotResponder.SoloPositionsTower
                    );
                }
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
                ctx.Aircraft.NoLandingClearanceWarningActive = false;
                TriggerGoAround(ctx, "no landing clearance");
                return false;
            }
        }

        // Datablock state: flash while the warning has fired and clearance is still missing.
        // Idempotent — flips off the moment any qualifying clearance is granted.
        ctx.Aircraft.NoLandingClearanceWarningActive = _noClearanceWarningIssued && !hasLandingClearance;

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
                ctx.Aircraft.NoLandingClearanceWarningActive = false;
                TriggerGoAround(ctx, "too high at missed approach point");
                return false;
            }
        }

        // Phase complete at threshold. AGL-primary so the FAC-to-runway alignment ramp
        // (MagVarRampEndAgl, OffsetRampEndAgl) reliably completes before LandingPhase
        // takes over. The distance term still fires for aircraft that happen to be high
        // on glideslope at the threshold, but only once they've also descended below
        // 50 ft AGL — preventing the pre-fix case where dist&lt;0.05 fired at AGL ~55 ft
        // and forced the small-offset ramp end to 100 ft (vs the AIM-aligned ~50 ft).
        bool complete = (agl < 30) || ((distNm < 0.05) && (agl < 50));
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

    /// <summary>
    /// Computes the latest distance from the threshold at which a deceleration must
    /// begin so the aircraft is settled at <paramref name="targetSpeed"/> by
    /// <paramref name="reachGateNm"/>. Equals <c>reachGateNm + bleedDistance</c>,
    /// capped at <paramref name="maxTriggerNm"/>. Bleed distance uses an average of
    /// pre- and post-decel ground speeds (linear approximation of a constant-decel
    /// kinematic integration).
    /// </summary>
    private static double ComputeKinematicTriggerNm(
        double ias,
        double targetSpeed,
        double groundSpeed,
        double decelRateKtsPerSec,
        double reachGateNm,
        double maxTriggerNm
    )
    {
        double speedDelta = ias - targetSpeed;
        if (speedDelta <= 0)
        {
            return reachGateNm;
        }

        double decelRate = Math.Max(decelRateKtsPerSec, 0.1);
        double bleedSeconds = speedDelta / decelRate;

        // Approximate the post-decel ground speed by scaling current GS in proportion
        // to the IAS reduction. Reasonable below 10k ft where TAS ≈ IAS.
        double iasFloor = Math.Max(ias, 1.0);
        double gsAvg = (groundSpeed + (groundSpeed * targetSpeed / iasFloor)) / 2.0;

        double bleedDistanceNm = bleedSeconds * gsAvg / 3600.0;
        double trigger = reachGateNm + bleedDistanceNm;

        return Math.Min(trigger, maxTriggerNm);
    }

    private static double ComputeFasTriggerDistanceNm(double ias, double fas, double groundSpeed, double decelRateKtsPerSec) =>
        ComputeKinematicTriggerNm(ias, fas, groundSpeed, decelRateKtsPerSec, FasReachGateNm, MaxFasTriggerNm);

    private static double ComputeConfigTriggerDistanceNm(double ias, double configSpeed, double groundSpeed, double decelRateKtsPerSec) =>
        ComputeKinematicTriggerNm(ias, configSpeed, groundSpeed, decelRateKtsPerSec, ConfigReachGateNm, MaxConfigTriggerNm);

    /// <summary>
    /// Genuine-offset (≥ <see cref="OffsetApproachThresholdDeg"/>) ramp-start AGL derived
    /// from the published MAP altitude as a DA proxy. For non-precision approaches the MAP
    /// altitude is the published MDA — close to the controlling minimums. For precision
    /// approaches the MAP is at threshold (essentially 0 AGL), so the floor
    /// <see cref="MinOffsetRampStartAgl"/> dominates. Returns the floor when MAP altitude
    /// isn't extracted (visual approaches, non-CIFP cases).
    /// </summary>
    private double ComputeOffsetRampStartAgl(int? mapAltitudeFtMsl)
    {
        double mapAgl = mapAltitudeFtMsl is { } mslFt ? Math.Max(mslFt - _thresholdElevation, 0.0) : 0.0;
        return Math.Max(mapAgl + OffsetRampStartBufferAgl, MinOffsetRampStartAgl);
    }

    /// <summary>
    /// Heading difference from the published final approach course. Keeps a runway-heading fallback
    /// to absorb magnetic-variation noise when the FAC and runway heading are within
    /// <see cref="IsAlignedToleranceDeg"/> of each other (Issue #101) — but never for genuine offset
    /// approaches (LDA, RNAV with an offset CF leg, VOR offset like KCCR S19R), where tracking the
    /// runway centerline must not count as established on the offset course.
    /// </summary>
    private double ComputeFacHeadingDiff(PhaseContext ctx)
    {
        double facDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(_finalApproachCourse);
        double facVsRwy = _finalApproachCourse.AbsAngleTo(_runwayHeading);
        return facVsRwy < IsAlignedToleranceDeg ? Math.Min(facDiff, ctx.Aircraft.TrueHeading.AbsAngleTo(_runwayHeading)) : facDiff;
    }

    /// <summary>
    /// Whether the aircraft is laterally established on the final approach course closely enough to
    /// begin the glideslope descent: within <see cref="GsEstablishedHeadingDeg"/> of the FAC and
    /// <see cref="GsEstablishedCrossTrackNm"/> of centerline. PTACF forced intercepts (which
    /// intentionally S-turn back onto course) and visual approaches bypass the gate — the former
    /// would otherwise be stranded high, the latter have no electronic glideslope. A relaxed
    /// JFAC/JLOC join is NOT bypassed even from a steep cut: it held its altitude until CAPP and
    /// must settle onto the localizer before descending. AIM 5-4-7 / 7110.65 5-9-4 ("until
    /// established on the localizer, cleared ILS").
    /// </summary>
    private bool IsLaterallyEstablishedForGs(PhaseContext ctx, double absCrossTrackNm)
    {
        // The gate is an instrument-approach concept: only an aircraft vectored to an electronic
        // final must be established on the localizer before starting down. Bypass it for finals
        // without an approach clearance (pattern/visual turning final), pattern traffic, visual
        // approaches (no electronic glideslope), and PTACF forced intercepts (which intentionally
        // S-turn back onto course and would otherwise be stranded high).
        var clearance = ctx.Aircraft.Phases?.ActiveApproach;
        if (clearance is null || _isPatternTraffic)
        {
            return true;
        }

        bool forcedIntercept =
            clearance.ForcedInterceptCapture && clearance.InterceptCaptureAngleDeg is { } angle && (angle > ForcedInterceptCaptureAngleDeg);
        bool visualApproach = clearance.ApproachId.StartsWith("VIS", StringComparison.Ordinal);
        if (forcedIntercept || visualApproach)
        {
            return true;
        }

        return (absCrossTrackNm < GsEstablishedCrossTrackNm) && (ComputeFacHeadingDiff(ctx) < GsEstablishedHeadingDeg);
    }

    private void CheckInterceptDistance(PhaseContext ctx, double distNm)
    {
        if (_interceptChecked || SkipInterceptCheck || ctx.Runway is null)
        {
            return;
        }

        // VFR aircraft are not included in the approach report at all
        if (ctx.Aircraft.FlightPlan.IsVfr)
        {
            _interceptChecked = true;
            return;
        }

        double crossTrack = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_anchorLat, _anchorLon), _finalApproachCourse)
        );

        // Establishment check: aircraft must be aligned with the published final approach course.
        double headingDiff = ComputeFacHeadingDiff(ctx);

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
        // Clearance / GA / runway-exit commands always pass through.
        var alwaysAllowed = cmd switch
        {
            CanonicalCommandType.ClearedToLand => true,
            CanonicalCommandType.LandAndHoldShort => true,
            CanonicalCommandType.ClearedForOption => true,
            CanonicalCommandType.GoAround => true,
            CanonicalCommandType.Follow => true,
            CanonicalCommandType.ExitLeft => true,
            CanonicalCommandType.ExitRight => true,
            CanonicalCommandType.ExitTaxiway => true,
            _ => false,
        };
        if (alwaysAllowed)
        {
            return CommandAcceptance.Allowed;
        }

        // Speed-class commands are additive only when the aircraft is still
        // outside SpeedCommandFinalGateNm. Inside that gate the aircraft is
        // committed to the final approach speed and the controller should
        // either send GA or accept the FAS profile.
        if (IsSpeedFamilyCommand(cmd))
        {
            return DistanceToThresholdNm > SpeedCommandFinalGateNm ? CommandAcceptance.Allowed : CommandAcceptance.ClearsPhase;
        }

        // Heading / nav / pattern / approach changes all take the aircraft off
        // the stabilized final approach — clear the phase.
        return CommandAcceptance.ClearsPhase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        // No per-phase requirements — clearance is tracked at PhaseList level
        return [];
    }
}
