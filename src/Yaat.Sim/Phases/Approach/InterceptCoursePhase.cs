using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Approach;

/// <summary>
/// Flies the aircraft on its current heading until it intercepts the
/// final approach course. Completes when the aircraft is aligned with
/// the course and within cross-track tolerance.
///
/// Detects bust-through: if the aircraft crosses the course but its heading
/// is too far off to capture, the phase clears the approach and notifies the RPO.
/// Also times out after <see cref="MaxElapsedSeconds"/> as a safety net.
/// </summary>
public sealed partial class InterceptCoursePhase : Phase
{
    private const double CrossTrackThresholdNm = 0.15;
    private const double HeadingAlignmentDeg = 15.0;
    private const double BustThroughAlignmentDeg = 30.0;
    private const double MaxElapsedSeconds = 180.0;

    private double? _previousSignedCrossTrack;
    private double? _runwayHeadingCache;

    /// <summary>Final approach course heading (true).</summary>
    public required double FinalApproachCourse { get; init; }

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
            ctx.Aircraft.Heading,
            FinalApproachCourse
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
        double headingDiff = Math.Abs(FlightPhysics.NormalizeAngle(ctx.Aircraft.Heading - FinalApproachCourse));

        // Normal intercept: within cross-track and heading tolerances
        if ((crossTrack < CrossTrackThresholdNm) && (headingDiff < HeadingAlignmentDeg))
        {
            ctx.Targets.TargetHeading = FinalApproachCourse;
            ctx.Targets.PreferredTurnDirection = null;
            ctx.Targets.NavigationRoute.Clear();
            ctx.Logger.LogDebug(
                "[InterceptCourse] {Callsign}: established, crossTrack={XT:F3}nm, hdgDiff={HD:F1}°",
                ctx.Aircraft.Callsign,
                crossTrack,
                headingDiff
            );
            return true;
        }

        // Bust-through: cross-track sign flipped but heading too far off to capture.
        // Use the smaller of the diff from FAC vs runway-number heading, since controllers
        // assign intercept headings based on the runway number (e.g. 120° for rwy 12)
        // which can differ from the actual FAC (e.g. 130°) by up to ~10°.
        double rwyHdg = GetRunwayHeading();
        double runwayHeadingDiff = Math.Abs(FlightPhysics.NormalizeAngle(ctx.Aircraft.Heading - rwyHdg));
        double effectiveHeadingDiff = Math.Min(headingDiff, runwayHeadingDiff);

        if (_previousSignedCrossTrack is { } prev)
        {
            bool signFlipped = (prev > 0 && signedCrossTrack < 0) || (prev < 0 && signedCrossTrack > 0);
            if (signFlipped && (effectiveHeadingDiff >= BustThroughAlignmentDeg))
            {
                ctx.Logger.LogInformation(
                    "[InterceptCourse] {Callsign}: bust-through detected — hdgDiff={HD:F1}° (fac={FacDiff:F1}°, rwy={RwyDiff:F1}°), crossTrack flipped {Prev:F3}→{Now:F3}",
                    ctx.Aircraft.Callsign,
                    effectiveHeadingDiff,
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
    /// Derives the runway-number heading from the <see cref="ApproachId"/>.
    /// E.g. "I12" → 120°, "ILS28R" → 280°, "L04L" → 40°.
    /// Falls back to <see cref="FinalApproachCourse"/> if the designator can't be parsed.
    /// </summary>
    private double GetRunwayHeading()
    {
        if (_runwayHeadingCache is { } cached)
        {
            return cached;
        }

        double result = FinalApproachCourse;

        if (ApproachId is not null)
        {
            var match = RunwayDesignatorRegex().Match(ApproachId);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int rwyNum) && (rwyNum >= 1) && (rwyNum <= 36))
            {
                result = rwyNum * 10.0;
            }
        }

        _runwayHeadingCache = result;
        return result;
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
