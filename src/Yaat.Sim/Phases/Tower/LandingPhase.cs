using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Flare, touchdown, and rollout deceleration.
/// Flare begins at category-specific altitude AGL.
/// Touchdown sets IsOnGround=true. Rollout completes at 20 kts.
/// </summary>
public sealed class LandingPhase : Phase
{
    private const double RolloutCompleteSpeed = 20.0;
    private const double CenterlineGainDegPerNm = 150.0;
    private const double MaxCenterlineCorrectionDeg = 10.0;

    private double _fieldElevation;
    private double _runwayHeading;
    private double _thresholdLat;
    private double _thresholdLon;
    private bool _touchedDown;
    private bool _canGoAround;
    private double _lahsoHoldShortDistNm;
    private bool _hasLahso;

    public bool StoppedForLahso { get; private set; }

    public override string Name => "Landing";

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.Heading;
        _thresholdLat = ctx.Runway?.ThresholdLatitude ?? ctx.Aircraft.Latitude;
        _thresholdLon = ctx.Runway?.ThresholdLongitude ?? ctx.Aircraft.Longitude;

        // Capture LAHSO target if set
        if (ctx.Aircraft.Phases?.LahsoHoldShort is { } lahso)
        {
            _hasLahso = true;
            _lahsoHoldShortDistNm = lahso.DistFromThresholdNm;
        }

        // Continue approach descent toward field elevation
        ctx.Targets.TargetAltitude = _fieldElevation;

        ctx.Logger.LogDebug(
            "[Landing] {Callsign}: started, fieldElev={Elev:F0}ft, gs={Gs:F1}kts{Lahso}",
            ctx.Aircraft.Callsign,
            _fieldElevation,
            ctx.Aircraft.GroundSpeed,
            _hasLahso ? $", LAHSO hold-short at {_lahsoHoldShortDistNm:F2}nm" : ""
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;

        if (!_touchedDown)
        {
            return TickAirborne(ctx, agl);
        }

        return TickRollout(ctx);
    }

    private bool TickAirborne(PhaseContext ctx, double agl)
    {
        double flareAlt = CategoryPerformance.FlareAltitude(ctx.Category);

        if (agl <= flareAlt)
        {
            // Flare: reduce descent rate
            double flareRate = CategoryPerformance.FlareDescentRate(ctx.Category);
            ctx.Targets.DesiredVerticalRate = -flareRate;
        }

        // Touchdown
        if (agl <= 0)
        {
            _touchedDown = true;
            ctx.Aircraft.IsOnGround = true;
            ctx.Aircraft.Altitude = _fieldElevation;
            ctx.Aircraft.VerticalSpeed = 0;
            ctx.Targets.TargetAltitude = null;
            ctx.Targets.DesiredVerticalRate = null;

            // Set touchdown speed and begin deceleration
            double tdSpeed = CategoryPerformance.TouchdownSpeed(ctx.Category, ctx.Aircraft.AircraftType);
            if (ctx.Aircraft.IndicatedAirspeed > tdSpeed)
            {
                ctx.Aircraft.IndicatedAirspeed = tdSpeed;
            }

            ctx.Logger.LogDebug("[Landing] {Callsign}: touchdown, gs={Gs:F1}kts", ctx.Aircraft.Callsign, ctx.Aircraft.GroundSpeed);
        }

        return false;
    }

    private bool TickRollout(PhaseContext ctx)
    {
        // Steer toward runway centerline
        double signedXte = GeoMath.SignedCrossTrackDistanceNm(
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            _thresholdLat,
            _thresholdLon,
            _runwayHeading
        );
        double correction = Math.Clamp(signedXte * CenterlineGainDegPerNm, -MaxCenterlineCorrectionDeg, MaxCenterlineCorrectionDeg);
        ctx.Targets.TargetHeading = FlightPhysics.NormalizeHeading(_runwayHeading - correction);

        // LAHSO: compute distance to hold-short point and increase deceleration if needed
        double decelRate = CategoryPerformance.RolloutDecelRate(ctx.Category);
        if (_hasLahso)
        {
            double distFromThreshold = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _thresholdLat, _thresholdLon);
            double distToHoldShort = _lahsoHoldShortDistNm - distFromThreshold;

            if (distToHoldShort > 0 && ctx.Aircraft.IndicatedAirspeed > 1.0)
            {
                // v² = 2 * a * d → a = v² / (2d)
                double speedFps = ctx.Aircraft.GroundSpeed * 6076.12 / 3600.0;
                double distFt = distToHoldShort * 6076.12;
                double requiredDecelFps2 = (speedFps * speedFps) / (2.0 * distFt);
                double requiredDecelKtsPerSec = requiredDecelFps2 * 3600.0 / 6076.12;

                if (requiredDecelKtsPerSec > decelRate)
                {
                    decelRate = requiredDecelKtsPerSec;
                }
            }
            else if (distToHoldShort <= 0)
            {
                // Past the hold-short point — stop immediately
                ctx.Aircraft.IndicatedAirspeed = 0;
                StoppedForLahso = true;
                ctx.Logger.LogDebug("[Landing] {Callsign}: LAHSO stop", ctx.Aircraft.Callsign);
                return true;
            }
        }

        // Decelerate on the ground
        double newSpeed = ctx.Aircraft.IndicatedAirspeed - decelRate * ctx.DeltaSeconds;
        if (newSpeed < 0)
        {
            newSpeed = 0;
        }
        ctx.Aircraft.IndicatedAirspeed = newSpeed;
        ctx.Targets.TargetSpeed = null;

        var cat = AircraftCategorization.Categorize(ctx.Aircraft.AircraftType);
        _canGoAround = ctx.Aircraft.IndicatedAirspeed >= CategoryPerformance.RejectedLandingMinSpeed(cat);

        // LAHSO: complete when stopped (speed ≤ 0)
        if (_hasLahso && ctx.Aircraft.IndicatedAirspeed <= 0)
        {
            StoppedForLahso = true;
            ctx.Logger.LogDebug("[Landing] {Callsign}: LAHSO rollout complete, stopped", ctx.Aircraft.Callsign);
            return true;
        }

        if (!_hasLahso && ctx.Aircraft.IndicatedAirspeed <= RolloutCompleteSpeed)
        {
            ctx.Logger.LogDebug("[Landing] {Callsign}: rollout complete, gs={Gs:F1}kts", ctx.Aircraft.Callsign, ctx.Aircraft.GroundSpeed);
            return true;
        }

        return false;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        if (!_touchedDown)
        {
            // During flare, reject most commands (exit preference is OK)
            return cmd switch
            {
                CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
                CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
                CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
                CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
                CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
                _ => CommandAcceptance.Rejected,
            };
        }

        // During rollout, reject speed/heading changes (exit preference is OK)
        return cmd switch
        {
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.GoAround => _canGoAround ? CommandAcceptance.Allowed : CommandAcceptance.Rejected,
            _ => CommandAcceptance.Rejected,
        };
    }
}
