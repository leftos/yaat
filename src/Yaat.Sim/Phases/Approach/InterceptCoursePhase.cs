using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Approach;

/// <summary>
/// Flies the aircraft on its assigned intercept heading until it intercepts the
/// final approach course, then hands off to FinalApproachPhase.
///
/// The phase anticipates the turn: it computes the aircraft's turn radius and
/// begins turning onto the FAC when the cross-track distance is within that
/// lead distance, provided the intercept angle is legal (≤ 30°, or ≤ 45° for a
/// helicopter — 7110.65 §5-9-2 TBL 5-9-1). This avoids lateral overshoot and lets
/// FinalApproachPhase begin glideslope descent immediately.
///
/// If the aircraft is still turning toward its assigned heading (intercept angle
/// not yet legal), the phase keeps checking each tick until either the angle
/// becomes legal or the aircraft crosses the centerline.
///
/// Legality is judged against the vector the controller assigned
/// (<see cref="AssignedInterceptHeading"/>, captured at install), not against the live
/// assigned heading: the clearance that installs this phase clears that heading.
///
/// The cut is measured against the final approach course and — only on a straight-in whose
/// course lies within <see cref="AlignedFinalToleranceDeg"/> of the runway number — against the
/// runway number as well, magnetic against magnetic, which forgives the magnetic variation baked
/// into the controller's vector. An offset final (LDA/SDF/localizer back-course) or an id with no
/// runway number is judged on the final approach course alone.
///
/// Bust-through: if the aircraft crosses the centerline with heading beyond that
/// category gate, the approach is cleared and the pilot reports passing through the
/// localizer on the frequency (solo) or to the instructor (RPO).
///
/// When <see cref="ForcedIntercept"/> is true (PTACF / CAPPF implied-PTAC), the
/// capture-angle gate is bypassed: the aircraft will capture the FAC at any
/// angle, overshoot laterally, and S-turn back under FinalApproachPhase control.
/// The approach clearance is preserved regardless of intercept geometry.
///
/// <see cref="RelaxedJoin"/> (JFAC / JLOC) also bypasses the capture-angle gate, but it
/// is a relaxed armed join, not a forced/steered intercept: the aircraft keeps flying its
/// assigned vector (e.g. "FH 220, JLOC") and turns to join the localizer when it reaches it,
/// at whatever cut the vector produces — it never busts through. The 7110.65 §5-9-2
/// intercept-angle limits constrain the controller's vector ("judged by the controller"),
/// not this join, so a steeper cut is flown rather than refused. It is kept distinct from
/// <see cref="ForcedIntercept"/> so the glideslope-established gate stays scoped to PTACF.
///
/// Times out after <see cref="MaxElapsedSeconds"/> if the aircraft never reaches the centerline
/// (e.g., flying parallel). The approach is cleared the same way, but the pilot reports being
/// unable to intercept and asks for vectors — it never reached the course, so it cannot report
/// having passed through it.
/// </summary>
public sealed class InterceptCoursePhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("InterceptCoursePhase");

    private const double AlreadyOnCourseThresholdNm = 0.15;
    private const double SpeedAnticipationThresholdNm = 2.0;
    private const double InterceptSpeedFasMultiplier = 1.3;
    private const double MaxElapsedSeconds = 180.0;

    /// <summary>
    /// How far the published final approach course may sit from the runway number before the runway
    /// number stops standing in for it. Within this, the approach is an aligned straight-in and a
    /// vector judged against the runway number is judged against the course; beyond it the final is
    /// offset (LDA/SDF/back-course) and only the course counts.
    /// </summary>
    private const double AlignedFinalToleranceDeg = 10.0;

    private double? _previousSignedCrossTrack;
    private bool _approachSpeedSet;

    /// <summary>Final approach course heading (true).</summary>
    public required TrueHeading FinalApproachCourse { get; init; }

    /// <summary>Runway threshold latitude (course target point).</summary>
    public required double ThresholdLat { get; init; }

    /// <summary>Runway threshold longitude (course target point).</summary>
    public required double ThresholdLon { get; init; }

    /// <summary>Approach procedure ID for notification messages.</summary>
    public string? ApproachId { get; init; }

    /// <summary>
    /// The controller's assigned magnetic heading at the moment the intercept was installed, or null
    /// when the aircraft was not on a vector. The install site captures it because an approach clearance
    /// nulls the aircraft's assigned heading when the approach takes over steering — the vector is gone
    /// from <c>Targets</c> by the first tick, and the runway-number leniency needs the angle the
    /// controller actually assigned.
    /// </summary>
    public required MagneticHeading? AssignedInterceptHeading { get; init; }

    /// <summary>
    /// When true, bypass the capture-angle gate: the aircraft captures the FAC at
    /// any intercept angle instead of busting through the localizer. Set by PTACF and
    /// by the implied-PTAC branch of CAPPF.
    /// </summary>
    public bool ForcedIntercept { get; init; }

    /// <summary>
    /// When true (JFAC / JLOC), bypass the capture-angle gate the same way
    /// <see cref="ForcedIntercept"/> does — a relaxed armed join: the aircraft flies its
    /// assigned vector and joins the localizer/FAC when it intercepts, at any cut, never
    /// reporting passing through it. Kept separate from <see cref="ForcedIntercept"/>
    /// because this join does NOT authorize the glideslope-established bypass: it holds the
    /// assigned altitude until CAPP and must be laterally established before descending.
    /// </summary>
    public bool RelaxedJoin { get; init; }

    public override string Name => "InterceptCourse";

    public override void OnStart(PhaseContext ctx)
    {
        // Aircraft continues on its current heading — no target change.
        // Approach speed set by the phase that follows (FinalApproachPhase).
        Log.LogDebug(
            "[InterceptCourse] {Callsign}: started, hdg={Hdg:F0}, course={Crs:F0}",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.TrueHeading.Degrees,
            FinalApproachCourse.Degrees
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Lead lifecycle watchdog — see ApproachNavigationPhase.OnTick. Normally a new
        // approach clearance clears any follow, but restored or hand-authored state can
        // carry one onto an intercept; the same lost-lead event must behave the same
        // here as on any other approach segment. Bails out when a cancel replaced or
        // cleared the phase list mid-tick.
        if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
        {
            return false;
        }

        // When ForcedIntercept (PTACF) or RelaxedJoin (JFAC/JLOC) is set, the capture-angle
        // gate is effectively unbounded (180° is the theoretical maximum between two headings).
        // The bust-through branch at the centerline-crossing check becomes unreachable, so the
        // aircraft always joins the localizer regardless of how steep the cut is.
        double maxAlignmentDeg = (ForcedIntercept || RelaxedJoin) ? 180.0 : InterceptAngleLimits.BeyondGateAngleForCategory(ctx.Category);

        // For parallel-offset approaches the FAC line does not pass through the runway
        // threshold, so we measure cross-track against the published anchor (e.g. the LDA's
        // displaced MAP fix) when the active clearance carries one. For ordinary approaches
        // the anchor is null and we fall back to the threshold, matching pre-anchor behaviour.
        var clearance = ctx.Aircraft.Phases?.ActiveApproach;
        double anchorLat = clearance?.FinalApproachAnchorLat ?? ThresholdLat;
        double anchorLon = clearance?.FinalApproachAnchorLon ?? ThresholdLon;

        double signedCrossTrack = GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(anchorLat, anchorLon), FinalApproachCourse);

        double crossTrack = Math.Abs(signedCrossTrack);
        TrueHeading aircraftHeading = ctx.Aircraft.TrueHeading;

        // Speed anticipation: decelerate to intercept speed as the aircraft nears the
        // localizer. At 250kts the turn radius is large, causing overshoot. Target 1.3× FAS
        // (not FAS itself — that's too slow this far from the threshold). FAS is set later
        // by FinalApproachPhase when the aircraft is closer in.
        // A lateral-only clearance (JFAC/JLOC) authorizes joining the course and nothing else: the aircraft keeps its
        // assigned speed and any STAR crossing-speed ceiling until CAPP upgrades it. Anticipating the approach speed
        // here would slow it ~70 kt below what it was told to fly, and FinalApproachPhase's lateral-only branch never
        // re-targets speed, so it would then hold that speed indefinitely awaiting the clearance.
        if (
            (crossTrack < SpeedAnticipationThresholdNm)
            && !_approachSpeedSet
            && !ctx.Targets.HasExplicitSpeedCommand
            && clearance is not { LateralInterceptOnly: true }
        )
        {
            double fas = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            double interceptSpeed = fas * InterceptSpeedFasMultiplier;
            ctx.Targets.TargetSpeed = interceptSpeed;
            _approachSpeedSet = true;
            Log.LogDebug(
                "[InterceptCourse] {Callsign}: slowing to {Spd:F0}kts (1.3×FAS {Fas:F0}, crossTrack={XT:F1}nm)",
                ctx.Aircraft.Callsign,
                interceptSpeed,
                fas,
                crossTrack
            );
        }

        // Already on the centerline with heading roughly aligned — complete immediately.
        if ((crossTrack < AlreadyOnCourseThresholdNm) && (ComputeEffectiveHeadingDiff(ctx) <= maxAlignmentDeg))
        {
            return Capture(ctx, aircraftHeading, crossTrack, "already on course");
        }

        // Anticipation: compute the turn radius and begin turning before crossing the
        // centerline. This prevents overshoot and lets FinalApproachPhase start GS descent
        // immediately. Turn radius = GS / (turnRate × 20π) in nm.
        // Check current heading diff first. If that's > 30° (can happen due to magnetic
        // variation), also check the assigned heading vs the runway number — but only once the
        // aircraft has settled onto its assigned heading (within 5°), and only on an aligned final.
        // This handles cases like 150° mag for rwy 12: true heading ~163° vs FAC 130° = 33° (fails),
        // but assigned 150° vs rwy 120° = 30° (passes, and the aircraft is on the heading the
        // controller gave).
        double turnRate = ctx.Aircraft.Targets.TurnRateOverride ?? AircraftPerformance.TurnRate(ctx.AircraftType, ctx.Category);
        double turnRadiusNm = ctx.Aircraft.GroundSpeed / (turnRate * 62.832);
        double leadDistNm = turnRadiusNm;

        if (crossTrack <= leadDistNm)
        {
            double currentDiff = ComputeCurrentHeadingDiff(ctx);
            bool legalIntercept = currentDiff <= maxAlignmentDeg;

            // If current true heading diff fails, check assigned magnetic heading against the
            // runway number — but only when the aircraft has actually reached it (not mid-turn).
            if (!legalIntercept && (AssignedInterceptHeading is { } assignedHdg) && (AlignedRunwayNumberHeading(ctx) is { } rwyMag))
            {
                TrueHeading assignedTrue = assignedHdg.ToTrue(ctx.Aircraft.Declination);
                bool onAssignedHeading = aircraftHeading.AbsAngleTo(assignedTrue) < 5.0;
                if (onAssignedHeading)
                {
                    legalIntercept = assignedHdg.AbsAngleTo(rwyMag) <= maxAlignmentDeg;
                }
            }

            if (legalIntercept)
            {
                return Capture(ctx, aircraftHeading, crossTrack, $"anticipated (lead={leadDistNm:F2}nm)");
            }
        }

        // Check for actual centerline crossing (sign flip).
        if (_previousSignedCrossTrack is { } prev)
        {
            bool signFlipped = ((prev > 0) && (signedCrossTrack <= 0)) || ((prev < 0) && (signedCrossTrack >= 0));
            if (signFlipped)
            {
                double effectiveDiff = ComputeEffectiveHeadingDiff(ctx);
                if (effectiveDiff <= maxAlignmentDeg)
                {
                    return Capture(ctx, aircraftHeading, crossTrack, "centerline crossing");
                }

                // Bust-through: heading too far off to capture
                double headingDiff = aircraftHeading.AbsAngleTo(FinalApproachCourse);
                string runwayDiffText = AlignedRunwayNumberHeading(ctx) is { } rwyMag
                    ? $"{ctx.Aircraft.MagneticHeading.AbsAngleTo(rwyMag):F1}°"
                    : "n/a";
                Log.LogInformation(
                    "[InterceptCourse] {Callsign}: bust-through detected — hdgDiff={HD:F1}° (fac={FacDiff:F1}°, rwy={RwyDiff}), crossTrack flipped {Prev:F3}→{Now:F3}",
                    ctx.Aircraft.Callsign,
                    effectiveDiff,
                    headingDiff,
                    runwayDiffText,
                    prev,
                    signedCrossTrack
                );
                HandleBustThrough(ctx);
                return true;
            }
        }

        _previousSignedCrossTrack = signedCrossTrack;

        // Safety timeout
        if (ElapsedSeconds >= MaxElapsedSeconds)
        {
            Log.LogInformation(
                "[InterceptCourse] {Callsign}: timeout after {Elapsed:F0}s — never captured course",
                ctx.Aircraft.Callsign,
                ElapsedSeconds
            );
            HandleInterceptTimeout(ctx);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Heading diff using current aircraft heading only — for anticipation decisions
    /// where the aircraft must actually be on a legal intercept heading. The true heading
    /// is measured against the final approach course; the magnetic heading against the
    /// runway number, when that is an aligned straight-in.
    /// </summary>
    private double ComputeCurrentHeadingDiff(PhaseContext ctx)
    {
        double headingDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(FinalApproachCourse);
        if (AlignedRunwayNumberHeading(ctx) is not { } rwyMag)
        {
            return headingDiff;
        }

        return Math.Min(headingDiff, ctx.Aircraft.MagneticHeading.AbsAngleTo(rwyMag));
    }

    /// <summary>
    /// Computes the effective heading diff for capture/bust-through decisions at crossing.
    /// Takes the minimum of: true heading vs the final approach course, magnetic heading vs the
    /// runway number, and the controller's assigned magnetic heading vs the runway number. The two
    /// runway-number terms apply only on an aligned straight-in (see
    /// <see cref="AlignedRunwayNumberHeading"/>); on an offset final the course is the only measure.
    /// </summary>
    private double ComputeEffectiveHeadingDiff(PhaseContext ctx)
    {
        double effectiveDiff = ctx.Aircraft.TrueHeading.AbsAngleTo(FinalApproachCourse);
        if (AlignedRunwayNumberHeading(ctx) is not { } rwyMag)
        {
            return effectiveDiff;
        }

        effectiveDiff = Math.Min(effectiveDiff, ctx.Aircraft.MagneticHeading.AbsAngleTo(rwyMag));

        // Also check controller's intended intercept angle: assigned magnetic heading
        // vs runway-number heading (both magnetic). Accounts for magnetic variation.
        if (AssignedInterceptHeading is { } assignedHdg)
        {
            effectiveDiff = Math.Min(effectiveDiff, assignedHdg.AbsAngleTo(rwyMag));
        }

        return effectiveDiff;
    }

    private bool Capture(PhaseContext ctx, TrueHeading aircraftHeading, double crossTrack, string reason)
    {
        ctx.Targets.TargetTrueHeading = FinalApproachCourse;
        ctx.Targets.AssignedMagneticHeading = null;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        // Record the capture distance for approach scoring and check intercept legality
        // at the actual capture point (not at FinalApproachPhase's stricter establishment).
        if (ctx.Aircraft.Phases?.ActiveApproach is { } clearance)
        {
            double captureDistNm = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(ThresholdLat, ThresholdLon));
            clearance.InterceptCaptureDistanceNm = captureDistNm;
            clearance.InterceptCaptureAngleDeg = aircraftHeading.AbsAngleTo(FinalApproachCourse);
            // Only a PTACF forced intercept bypasses the glideslope-established gate. A relaxed
            // JFAC/JLOC join captures at any angle too, but must hold altitude until established.
            clearance.ForcedInterceptCapture = ForcedIntercept;

            CheckInterceptLegality(ctx, clearance, captureDistNm, aircraftHeading);
        }

        double headingDiff = aircraftHeading.AbsAngleTo(FinalApproachCourse);
        double gateDeg = InterceptAngleLimits.BeyondGateAngleForCategory(ctx.Category);
        if ((ForcedIntercept || RelaxedJoin) && headingDiff > gateDeg)
        {
            Log.LogInformation(
                "[InterceptCourse] {Callsign}: forced capture ({Reason}) — hdgDiff={HD:F1}° > {Gate:F0}°, expect lateral overshoot and S-turn recovery",
                ctx.Aircraft.Callsign,
                reason,
                headingDiff,
                gateDeg
            );
        }
        else
        {
            Log.LogDebug(
                "[InterceptCourse] {Callsign}: captured ({Reason}) — hdgDiff={HD:F1}°, crossTrack={XT:F3}nm",
                ctx.Aircraft.Callsign,
                reason,
                headingDiff,
                crossTrack
            );
        }
        return true;
    }

    private void CheckInterceptLegality(PhaseContext ctx, ApproachClearance clearance, double captureDistNm, TrueHeading aircraftHeading)
    {
        // VFR and visual approaches are not subject to 7110.65 §5-9-1
        if (ctx.Aircraft.FlightPlan.IsVfr)
        {
            return;
        }

        bool isVisualApproach = clearance.ApproachId.StartsWith("VIS", StringComparison.Ordinal);
        if (isVisualApproach)
        {
            return;
        }

        // Pattern traffic is not vectored — skip intercept legality
        bool isPatternTraffic = ctx.Aircraft.Phases?.TrafficDirection is not null;
        if (isPatternTraffic)
        {
            return;
        }

        var runway = ctx.Aircraft.Phases?.AssignedRunway;
        if (runway is null)
        {
            return;
        }

        double minIntercept = ApproachGateDatabase.GetMinInterceptDistanceNm(
            runway.AirportId,
            runway.Designator,
            LandingThreshold.DisplacementFt(runway, ctx.GroundLayout) / GeoMath.FeetPerNm
        );

        if (captureDistNm < minIntercept)
        {
            ctx.Aircraft.PendingWarnings.Add(
                $"Illegal intercept: turned on final {captureDistNm:F1}nm " + $"from threshold (min {minIntercept:F1}nm) " + "[7110.65 §5-9-1]"
            );
        }
    }

    /// <summary>
    /// Derives the runway-number heading from the <see cref="ApproachId"/>. Runway numbers are
    /// magnetic by definition: "I12" → 120° magnetic, "ILS28R" → 280°, "L04L" → 40°, "I29RY" → 290°.
    /// Null when the id carries no parseable runway number (a circling approach such as "VDM-A", or
    /// an unprefixed id).
    /// </summary>
    private MagneticHeading? RunwayNumberHeading()
    {
        if (ApproachId is null || RunwayIdentifier.FromApproachId(ApproachId) is not { } designator)
        {
            return null;
        }

        string digits = designator.TrimEnd('L', 'R', 'C');
        if (int.TryParse(digits, out int rwyNum) && (rwyNum >= 1) && (rwyNum <= 36))
        {
            return new MagneticHeading(rwyNum * 10.0);
        }

        return null;
    }

    /// <summary>
    /// The runway-number heading when it stands in for the final approach course — i.e. the course is
    /// within <see cref="AlignedFinalToleranceDeg"/> of it, so the approach is an aligned straight-in.
    /// Null on an offset final (LDA/SDF/localizer back-course), where the runway number says nothing
    /// about the course the aircraft must intercept, and null when the id carries no runway number.
    /// </summary>
    private MagneticHeading? AlignedRunwayNumberHeading(PhaseContext ctx)
    {
        if (RunwayNumberHeading() is not { } rwyMag)
        {
            return null;
        }

        MagneticHeading facMag = FinalApproachCourse.ToMagnetic(ctx.Aircraft.Declination);
        return facMag.AbsAngleTo(rwyMag) <= AlignedFinalToleranceDeg ? rwyMag : null;
    }

    private void HandleBustThrough(PhaseContext ctx)
    {
        string label = ApproachId ?? "approach";
        var text = Pilot.PilotResponder.BuildUnable(ctx.Aircraft, "passing through the localizer") with
        {
            RpoTerminal = $"unable, passing through the localizer — {label}.",
        };
        RefuseIntercept(ctx, text);
    }

    /// <summary>
    /// The assigned vector never brought the aircraft to the course (it flew parallel or diverging),
    /// so the pilot asks for a new one instead of reporting a position it never reached.
    /// </summary>
    private void HandleInterceptTimeout(PhaseContext ctx)
    {
        string label = ApproachId ?? "approach";
        var text = Pilot.PilotResponder.BuildUnableToInterceptRequestVectors(ctx.Aircraft) with
        {
            RpoTerminal = $"unable to intercept the localizer, request vectors — {label}.",
        };
        RefuseIntercept(ctx, text);
    }

    /// <summary>
    /// Transmits the pilot's refusal of the intercept and ends the approach: the remaining approach
    /// phases and the clearance are dropped, so the aircraft holds its last heading and altitude
    /// until the controller vectors it again.
    /// </summary>
    private static void RefuseIntercept(PhaseContext ctx, Pilot.PilotSpeechText text)
    {
        // A refusal to fly the intercept is a pilot transmission like any other: the student hears it
        // in solo mode, and in RPO the instructor sees the green pilot-speech line (callsign, no
        // procedure id) when RpoShowPilotSpeech is on, or the amber line when it is off — that one
        // carries the procedure id as an RPO-only diagnostic (a real crew would not read the procedure
        // id back on an unable call).
        Pilot.PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            text,
            Pilot.PilotResponder.SoloPositionsTowerApproach
        );

        // Clear remaining approach phases and approach clearance
        ctx.Aircraft.Phases?.Clear(ctx);
        if (ctx.Aircraft.Phases is not null)
        {
            ctx.Aircraft.Phases.ActiveApproach = null;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Speed/altitude adjust targets without leaving the approach.
        if (IsAdditiveAirborneAdjustment(cmd))
        {
            return CommandAcceptance.Allowed;
        }

        return cmd switch
        {
            // Approach-related commands pass through
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.ForceLanding => CommandAcceptance.Allowed,
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            // Everything else (heading, direct-to, etc.) takes the aircraft off the approach
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new InterceptCoursePhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            FinalApproachCourseDeg = FinalApproachCourse.Degrees,
            ThresholdLat = ThresholdLat,
            ThresholdLon = ThresholdLon,
            ApproachId = ApproachId,
            AssignedInterceptHeadingDeg = AssignedInterceptHeading?.Degrees,
            PreviousSignedCrossTrack = _previousSignedCrossTrack,
            ApproachSpeedSet = _approachSpeedSet,
            ForcedIntercept = ForcedIntercept,
            RelaxedJoin = RelaxedJoin,
        };

    public static InterceptCoursePhase FromSnapshot(InterceptCoursePhaseDto dto)
    {
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = new TrueHeading(dto.FinalApproachCourseDeg),
            ThresholdLat = dto.ThresholdLat,
            ThresholdLon = dto.ThresholdLon,
            ApproachId = dto.ApproachId,
            AssignedInterceptHeading = dto.AssignedInterceptHeadingDeg is { } hdg ? new MagneticHeading(hdg) : null,
            ForcedIntercept = dto.ForcedIntercept,
            RelaxedJoin = dto.RelaxedJoin,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase._previousSignedCrossTrack = dto.PreviousSignedCrossTrack;
        phase._approachSpeedSet = dto.ApproachSpeedSet;
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
