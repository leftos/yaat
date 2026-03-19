using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Approach;

/// <summary>
/// Flies the aircraft on its assigned intercept heading until it intercepts the
/// final approach course, then hands off to FinalApproachPhase.
///
/// The phase anticipates the turn: it computes the aircraft's turn radius and
/// begins turning onto the FAC when the cross-track distance is within that
/// lead distance, provided the intercept angle is legal (≤ 30°). This avoids
/// lateral overshoot and lets FinalApproachPhase begin glideslope descent
/// immediately.
///
/// If the aircraft is still turning toward its assigned heading (intercept angle
/// not yet legal), the phase keeps checking each tick until either the angle
/// becomes legal or the aircraft crosses the centerline.
///
/// Bust-through: if the aircraft crosses the centerline with heading > 30° off,
/// the approach is cleared and the RPO is notified.
///
/// Times out after <see cref="MaxElapsedSeconds"/> if the aircraft never
/// reaches the centerline (e.g., flying parallel).
/// </summary>
public sealed partial class InterceptCoursePhase : Phase
{
    private const double AlreadyOnCourseThresholdNm = 0.15;
    private const double SpeedAnticipationThresholdNm = 2.0;
    private const double InterceptSpeedFasMultiplier = 1.3;
    private const double BustThroughAlignmentDeg = 30.0;
    private const double MaxElapsedSeconds = 180.0;

    private double? _previousSignedCrossTrack;
    private TrueHeading? _runwayHeadingCache;
    private bool _approachSpeedSet;

    /// <summary>Final approach course heading (true).</summary>
    public required TrueHeading FinalApproachCourse { get; init; }

    /// <summary>Runway threshold latitude (course target point).</summary>
    public required double ThresholdLat { get; init; }

    /// <summary>Runway threshold longitude (course target point).</summary>
    public required double ThresholdLon { get; init; }

    /// <summary>Approach procedure ID for notification messages.</summary>
    public string? ApproachId { get; init; }

    public override string Name => "InterceptCourse";

    public override void OnStart(PhaseContext ctx)
    {
        // Aircraft continues on its current heading — no target change.
        // Approach speed set by the phase that follows (FinalApproachPhase).
        ctx.Logger.LogDebug(
            "[InterceptCourse] {Callsign}: started, hdg={Hdg:F0}, course={Crs:F0}",
            ctx.Aircraft.Callsign,
            ctx.Aircraft.TrueHeading.Degrees,
            FinalApproachCourse.Degrees
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double signedCrossTrack = GeoMath.SignedCrossTrackDistanceNm(
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            ThresholdLat,
            ThresholdLon,
            FinalApproachCourse
        );

        double crossTrack = Math.Abs(signedCrossTrack);
        TrueHeading aircraftHeading = ctx.Aircraft.TrueHeading;

        // Speed anticipation: decelerate to intercept speed as the aircraft nears the
        // localizer. At 250kts the turn radius is large, causing overshoot. Target 1.3× FAS
        // (not FAS itself — that's too slow this far from the threshold). FAS is set later
        // by FinalApproachPhase when the aircraft is closer in.
        if ((crossTrack < SpeedAnticipationThresholdNm) && !_approachSpeedSet && !ctx.Targets.HasExplicitSpeedCommand)
        {
            double fas = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            double interceptSpeed = fas * InterceptSpeedFasMultiplier;
            ctx.Targets.TargetSpeed = interceptSpeed;
            _approachSpeedSet = true;
            ctx.Logger.LogDebug(
                "[InterceptCourse] {Callsign}: slowing to {Spd:F0}kts (1.3×FAS {Fas:F0}, crossTrack={XT:F1}nm)",
                ctx.Aircraft.Callsign,
                interceptSpeed,
                fas,
                crossTrack
            );
        }

        // Already on the centerline with heading roughly aligned — complete immediately.
        if ((crossTrack < AlreadyOnCourseThresholdNm) && (ComputeEffectiveHeadingDiff(ctx, aircraftHeading) <= BustThroughAlignmentDeg))
        {
            return Capture(ctx, aircraftHeading, crossTrack, "already on course");
        }

        // Anticipation: compute the turn radius and begin turning before crossing the
        // centerline. This prevents overshoot and lets FinalApproachPhase start GS descent
        // immediately. Turn radius = GS / (turnRate × 20π) in nm.
        // Check current heading diff first. If that's > 30° (can happen due to magnetic
        // variation), also check the assigned heading vs runway heading — but only once the
        // aircraft has settled onto its assigned heading (within 5°). This handles cases like
        // 150° mag for rwy 12: true heading ~163° vs FAC 130° = 33° (fails), but assigned
        // 150° vs rwy 120° = 30° (passes, and the aircraft is on the heading the controller gave).
        double turnRate = ctx.Aircraft.Targets.TurnRateOverride ?? AircraftPerformance.TurnRate(ctx.AircraftType, ctx.Category);
        double turnRadiusNm = ctx.Aircraft.GroundSpeed / (turnRate * 62.832);
        double leadDistNm = turnRadiusNm;

        if (crossTrack <= leadDistNm)
        {
            double currentDiff = ComputeCurrentHeadingDiff(aircraftHeading);
            bool legalIntercept = currentDiff <= BustThroughAlignmentDeg;

            // If current true heading diff fails, check assigned magnetic heading —
            // but only when the aircraft has actually reached it (not mid-turn).
            if (!legalIntercept && (ctx.Targets.AssignedMagneticHeading is { } assignedHdg))
            {
                TrueHeading assignedTrue = assignedHdg.ToTrue(ctx.Aircraft.Declination);
                bool onAssignedHeading = aircraftHeading.AbsAngleTo(assignedTrue) < 5.0;
                if (onAssignedHeading)
                {
                    double assignedDiff = Math.Abs(assignedHdg.Degrees - GetRunwayHeading().Degrees);
                    if (assignedDiff > 180)
                    {
                        assignedDiff = 360 - assignedDiff;
                    }

                    legalIntercept = assignedDiff <= BustThroughAlignmentDeg;
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
                double effectiveDiff = ComputeEffectiveHeadingDiff(ctx, aircraftHeading);
                if (effectiveDiff <= BustThroughAlignmentDeg)
                {
                    return Capture(ctx, aircraftHeading, crossTrack, "centerline crossing");
                }

                // Bust-through: heading too far off to capture
                TrueHeading rwyHdg = GetRunwayHeading();
                double headingDiff = aircraftHeading.AbsAngleTo(FinalApproachCourse);
                double runwayHeadingDiff = aircraftHeading.AbsAngleTo(rwyHdg);
                ctx.Logger.LogInformation(
                    "[InterceptCourse] {Callsign}: bust-through detected — hdgDiff={HD:F1}° (fac={FacDiff:F1}°, rwy={RwyDiff:F1}°), crossTrack flipped {Prev:F3}→{Now:F3}",
                    ctx.Aircraft.Callsign,
                    effectiveDiff,
                    headingDiff,
                    runwayHeadingDiff,
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
            ctx.Logger.LogInformation(
                "[InterceptCourse] {Callsign}: timeout after {Elapsed:F0}s — never captured course",
                ctx.Aircraft.Callsign,
                ElapsedSeconds
            );
            HandleBustThrough(ctx);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Heading diff using current aircraft heading only — for anticipation decisions
    /// where the aircraft must actually be on a legal intercept heading.
    /// </summary>
    private double ComputeCurrentHeadingDiff(TrueHeading aircraftHeading)
    {
        double headingDiff = aircraftHeading.AbsAngleTo(FinalApproachCourse);
        double runwayHeadingDiff = aircraftHeading.AbsAngleTo(GetRunwayHeading());
        return Math.Min(headingDiff, runwayHeadingDiff);
    }

    /// <summary>
    /// Computes the effective heading diff for capture/bust-through decisions at crossing.
    /// Takes the minimum of: diff from FAC, diff from runway-number heading,
    /// and the controller's assigned magnetic heading vs runway-number heading.
    /// </summary>
    private double ComputeEffectiveHeadingDiff(PhaseContext ctx, TrueHeading aircraftHeading)
    {
        double headingDiff = aircraftHeading.AbsAngleTo(FinalApproachCourse);
        TrueHeading rwyHdg = GetRunwayHeading();
        double runwayHeadingDiff = aircraftHeading.AbsAngleTo(rwyHdg);
        double effectiveDiff = Math.Min(headingDiff, runwayHeadingDiff);

        // Also check controller's intended intercept angle: assigned magnetic heading
        // vs runway-number heading (both magnetic). Accounts for magnetic variation.
        if (ctx.Targets.AssignedMagneticHeading is { } assignedHdg)
        {
            double assignedDiff = Math.Abs(assignedHdg.Degrees - rwyHdg.Degrees);
            if (assignedDiff > 180)
            {
                assignedDiff = 360 - assignedDiff;
            }

            effectiveDiff = Math.Min(effectiveDiff, assignedDiff);
        }

        return effectiveDiff;
    }

    private bool Capture(PhaseContext ctx, TrueHeading aircraftHeading, double crossTrack, string reason)
    {
        ctx.Targets.TargetTrueHeading = FinalApproachCourse;
        ctx.Targets.AssignedMagneticHeading = null;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        double headingDiff = aircraftHeading.AbsAngleTo(FinalApproachCourse);
        ctx.Logger.LogDebug(
            "[InterceptCourse] {Callsign}: captured ({Reason}) — hdgDiff={HD:F1}°, crossTrack={XT:F3}nm",
            ctx.Aircraft.Callsign,
            reason,
            headingDiff,
            crossTrack
        );
        return true;
    }

    /// <summary>
    /// Derives the runway-number heading from the <see cref="ApproachId"/>.
    /// E.g. "I12" → 120°, "ILS28R" → 280°, "L04L" → 40°.
    /// Falls back to <see cref="FinalApproachCourse"/> if the designator can't be parsed.
    /// </summary>
    private TrueHeading GetRunwayHeading()
    {
        if (_runwayHeadingCache is { } cached)
        {
            return cached;
        }

        TrueHeading result = FinalApproachCourse;

        if (ApproachId is not null)
        {
            var match = RunwayDesignatorRegex().Match(ApproachId);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int rwyNum) && (rwyNum >= 1) && (rwyNum <= 36))
            {
                result = new TrueHeading(rwyNum * 10.0);
            }
        }

        _runwayHeadingCache = result;
        return _runwayHeadingCache.Value;
    }

    [GeneratedRegex(@"(\d{1,2})[LRC]?$")]
    private static partial Regex RunwayDesignatorRegex();

    private void HandleBustThrough(PhaseContext ctx)
    {
        string label = ApproachId ?? "approach";
        ctx.Aircraft.PendingNotifications.Add($"Unable, passing through localizer — {label}");

        // Clear remaining approach phases and approach clearance
        ctx.Aircraft.Phases?.Clear(ctx);
        if (ctx.Aircraft.Phases is not null)
        {
            ctx.Aircraft.Phases.ActiveApproach = null;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            // Approach-related commands pass through
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            // Speed/altitude adjust targets without leaving the approach
            CanonicalCommandType.Speed => CommandAcceptance.Allowed,
            CanonicalCommandType.Mach => CommandAcceptance.Allowed,
            CanonicalCommandType.ClimbMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.DescendMaintain => CommandAcceptance.Allowed,
            // Everything else (heading, direct-to, etc.) takes the aircraft off the approach
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
