using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Taxis the aircraft from the hold-short point onto the runway centerline
/// and aligns with the runway heading. Completes when aligned and on centerline.
/// Analog navigation — no ground graph nodes, just cross-track and heading.
///
///   Stage 1 — turn perpendicular to the runway centerline (face toward it).
///   Stage 2 — drive straight across onto the centerline (no heading change).
///   Stage 3 — turn 90° to align with the runway heading.
///
/// Inserted before LinedUpAndWaitingPhase when LUAW/CTO is issued.
/// </summary>
public sealed class LineUpPhase : Phase
{
    private const double CenterlineThresholdNm = 0.005;
    private const double HeadingToleranceDeg = 2.0;
    private const double LogIntervalSeconds = 3.0;

    private TrueHeading _runwayHeading;
    private bool _initialized;
    private double _timeSinceLastLog;

    private TrueHeading _perpHeading;
    private bool _perpAligned;
    private bool _onCenterline;

    public override string Name => "LiningUp";

    public override PhaseDto ToSnapshot() =>
        new LineUpPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            Initialized = _initialized,
            TimeSinceLastLog = _timeSinceLastLog,
            PerpHeadingDeg = _perpHeading.Degrees,
            PerpAligned = _perpAligned,
            OnCenterline = _onCenterline,
        };

    public static LineUpPhase FromSnapshot(LineUpPhaseDto dto)
    {
        var phase = new LineUpPhase();
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._initialized = dto.Initialized;
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase._perpHeading = new TrueHeading(dto.PerpHeadingDeg);
        phase._perpAligned = dto.PerpAligned;
        phase._onCenterline = dto.OnCenterline;
        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;

        if (ctx.Runway is null)
        {
            ctx.Logger.LogWarning("[LineUp] {Callsign}: no runway context, skipping", ctx.Aircraft.Callsign);
            return;
        }

        _runwayHeading = ctx.Runway.TrueHeading;

        // Compute the heading perpendicular to the runway, facing toward the centerline.
        // Signed cross-track: positive = left of track, negative = right of track.
        double signedCross = GeoMath.SignedCrossTrackDistanceNm(
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            ctx.Runway.ThresholdLatitude,
            ctx.Runway.ThresholdLongitude,
            _runwayHeading
        );
        double perpOffset = signedCross >= 0 ? -90.0 : 90.0;
        _perpHeading = new TrueHeading(_runwayHeading.Degrees + perpOffset);

        _initialized = true;

        ctx.Logger.LogDebug(
            "[LineUp] {Callsign}: perpHdg={PerpHdg:F0}, crossTrack={Cross:F4}nm, rwy hdg {Hdg:F0}",
            ctx.Aircraft.Callsign,
            _perpHeading.Degrees,
            Math.Abs(signedCross),
            _runwayHeading
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_initialized)
        {
            return true;
        }

        // Stage 1: turn perpendicular to the runway centerline before crossing.
        if (!_perpAligned)
        {
            double perpDiff = _perpHeading.AbsAngleTo(ctx.Aircraft.TrueHeading);
            if (perpDiff > HeadingToleranceDeg)
            {
                double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
                ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, _perpHeading.Degrees, maxTurn);
                AdjustSpeed(ctx, CategoryPerformance.TaxiSpeed(ctx.Category) * 0.3);
                LogPeriodic(ctx);
                return false;
            }

            ctx.Aircraft.TrueHeading = _perpHeading;
            _perpAligned = true;
            ctx.Logger.LogDebug(
                "[LineUp] {Callsign}: perpendicular to centerline (hdg {Hdg:F0}), crossing",
                ctx.Aircraft.Callsign,
                _perpHeading.Degrees
            );
        }

        // Stage 2: drive straight across the runway (no heading change) until on centerline.
        if (!_onCenterline)
        {
            double crossTrack = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    ctx.Aircraft.Latitude,
                    ctx.Aircraft.Longitude,
                    ctx.Runway!.ThresholdLatitude,
                    ctx.Runway.ThresholdLongitude,
                    _runwayHeading
                )
            );

            if (crossTrack > CenterlineThresholdNm)
            {
                AdjustSpeed(ctx, CategoryPerformance.TaxiSpeed(ctx.Category) * 0.8);
                LogPeriodic(ctx);
                return false;
            }

            _onCenterline = true;
            ctx.Logger.LogDebug("[LineUp] {Callsign}: on centerline (crossTrack={Cross:F4}nm), turning to align", ctx.Aircraft.Callsign, crossTrack);
        }

        // Stage 3: turn to align with runway heading.
        double alignDiff = _runwayHeading.AbsAngleTo(ctx.Aircraft.TrueHeading);

        if (alignDiff > HeadingToleranceDeg)
        {
            double maxTurn = CategoryPerformance.GroundTurnRate(ctx.Category) * ctx.DeltaSeconds;
            ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, _runwayHeading.Degrees, maxTurn);
            AdjustSpeed(ctx, CategoryPerformance.TaxiSpeed(ctx.Category) * 0.5);
            LogPeriodic(ctx);
            return false;
        }

        // Aligned — snap heading and stop
        ctx.Aircraft.TrueHeading = _runwayHeading;
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;

        ctx.Logger.LogDebug("[LineUp] {Callsign}: aligned on runway, heading {Hdg:F0}", ctx.Aircraft.Callsign, _runwayHeading.Degrees);
        return true;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClimbMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.DescendMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.CancelTakeoffClearance => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
        };
    }

    private void LogPeriodic(PhaseContext ctx)
    {
        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            double crossTrack = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    ctx.Aircraft.Latitude,
                    ctx.Aircraft.Longitude,
                    ctx.Runway!.ThresholdLatitude,
                    ctx.Runway.ThresholdLongitude,
                    _runwayHeading
                )
            );
            double hdgDiff = _runwayHeading.AbsAngleTo(ctx.Aircraft.TrueHeading);
            ctx.Logger.LogTrace(
                "[LineUp] {Callsign}: crossTrack={Cross:F4}nm, hdgDiff={Diff:F1}, gs={Gs:F1}kts",
                ctx.Aircraft.Callsign,
                crossTrack,
                hdgDiff,
                ctx.Aircraft.GroundSpeed
            );
        }
    }

    private static void AdjustSpeed(PhaseContext ctx, double targetSpeed)
    {
        double current = ctx.Aircraft.IndicatedAirspeed;
        if (current < targetSpeed)
        {
            double rate = CategoryPerformance.TaxiAccelRate(ctx.Category);
            ctx.Aircraft.IndicatedAirspeed = Math.Min(targetSpeed, current + rate * ctx.DeltaSeconds);
        }
        else if (current > targetSpeed)
        {
            double rate = CategoryPerformance.TaxiDecelRate(ctx.Category);
            ctx.Aircraft.IndicatedAirspeed = Math.Max(targetSpeed, current - rate * ctx.DeltaSeconds);
        }
    }
}
