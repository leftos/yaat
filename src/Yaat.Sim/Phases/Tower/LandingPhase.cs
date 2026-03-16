using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Flare, touchdown, and rollout deceleration.
/// Flare begins at category-specific altitude AGL.
/// Touchdown sets IsOnGround=true.
/// When an exit is assigned, the aircraft maintains coast speed until the kinematic braking
/// point, then decelerates to the angle-dependent turn-off speed.
/// Without an exit, decelerates uniformly to 20 kts.
/// </summary>
public sealed class LandingPhase : Phase
{
    private const double DefaultRolloutCompleteSpeed = 20.0;
    private const double CenterlineGainDegPerNm = 150.0;
    private const double MaxCenterlineCorrectionDeg = 10.0;
    private const double MaxDecelRateKtsPerSec = 10.0;

    private double _fieldElevation;
    private double _runwayHeading;
    private double _thresholdLat;
    private double _thresholdLon;
    private bool _touchedDown;
    private bool _canGoAround;
    private double _lahsoHoldShortDistNm;
    private bool _hasLahso;

    // Exit-aware braking state
    private GroundNode? _resolvedExitNode;
    private double _exitTurnOffSpeed;
    private ExitPreference? _lastResolvedPreference;

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
            double tdSpeed = AircraftPerformance.TouchdownSpeed(ctx.AircraftType, ctx.Category);
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

        // Re-resolve exit if preference changed mid-rollout
        var currentPref = ctx.Aircraft.Phases?.RequestedExit;
        if (currentPref != _lastResolvedPreference)
        {
            ResolveExit(ctx);
        }

        double decelRate = CategoryPerformance.RolloutDecelRate(ctx.Category);

        // LAHSO: compute distance to hold-short point and increase deceleration if needed
        if (_hasLahso)
        {
            double distFromThreshold = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _thresholdLat, _thresholdLon);
            double distToHoldShort = _lahsoHoldShortDistNm - distFromThreshold;

            if (distToHoldShort > 0 && ctx.Aircraft.IndicatedAirspeed > 1.0)
            {
                double lahsoDecel = ComputeRequiredDecel(ctx.Aircraft.GroundSpeed, 0, distToHoldShort);
                if (lahsoDecel > decelRate)
                {
                    decelRate = lahsoDecel;
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

        // Exit-aware braking: compute required decel to reach exit at turn-off speed
        if (_resolvedExitNode is not null)
        {
            double distToExit = GeoMath.AlongTrackDistanceNm(
                _resolvedExitNode.Latitude,
                _resolvedExitNode.Longitude,
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _runwayHeading
            );

            if (distToExit > 0 && ctx.Aircraft.IndicatedAirspeed > _exitTurnOffSpeed)
            {
                double exitDecel = ComputeRequiredDecel(ctx.Aircraft.GroundSpeed, _exitTurnOffSpeed, distToExit);

                if (exitDecel > decelRate)
                {
                    // Need to brake harder to make the exit
                    decelRate = Math.Min(exitDecel, MaxDecelRateKtsPerSec);
                }
                else
                {
                    // Not yet at braking point — coast at or above RolloutCoastSpeed
                    double coastSpeed = CategoryPerformance.RolloutCoastSpeed(ctx.Category);
                    if (ctx.Aircraft.IndicatedAirspeed > coastSpeed)
                    {
                        // Allow normal decel down to coast speed, but not below it
                        double coastLimited = ctx.Aircraft.IndicatedAirspeed - decelRate * ctx.DeltaSeconds;
                        if (coastLimited < coastSpeed)
                        {
                            decelRate = 0;
                        }
                    }
                    else
                    {
                        // Already at or below coast speed — hold speed until braking point
                        decelRate = 0;
                    }
                }
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

        // Completion threshold depends on whether an exit is resolved
        double completeSpeed = _resolvedExitNode is not null ? _exitTurnOffSpeed : DefaultRolloutCompleteSpeed;

        if (!_hasLahso && ctx.Aircraft.IndicatedAirspeed <= completeSpeed)
        {
            ctx.Logger.LogDebug("[Landing] {Callsign}: rollout complete, gs={Gs:F1}kts", ctx.Aircraft.Callsign, ctx.Aircraft.GroundSpeed);
            return true;
        }

        return false;
    }

    private void ResolveExit(PhaseContext ctx)
    {
        var preference = ctx.Aircraft.Phases?.RequestedExit;
        _lastResolvedPreference = preference;
        _resolvedExitNode = null;

        if (preference is null || ctx.GroundLayout is null)
        {
            return;
        }

        var result = ctx.GroundLayout.FindExitAheadOnRunway(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _runwayHeading, preference);

        if (result is null)
        {
            return;
        }

        _resolvedExitNode = result.Value.Node;
        double? exitAngle = ctx.GroundLayout.ComputeExitAngle(result.Value.Node, result.Value.Taxiway, _runwayHeading);
        _exitTurnOffSpeed = CategoryPerformance.ExitTurnOffSpeed(ctx.Category, exitAngle);

        ctx.Logger.LogDebug(
            "[Landing] {Callsign}: resolved exit at {Taxiway}, angle={Angle}, turnOffSpeed={Speed:F0}kts",
            ctx.Aircraft.Callsign,
            result.Value.Taxiway,
            exitAngle?.ToString("F0") ?? "?",
            _exitTurnOffSpeed
        );
    }

    /// <summary>
    /// Compute required deceleration (kts/sec) to go from current ground speed to target speed
    /// over the given distance. Uses kinematic equation: v_final² = v_initial² - 2*a*d.
    /// </summary>
    private static double ComputeRequiredDecel(double currentGroundSpeedKts, double targetSpeedKts, double distanceNm)
    {
        double currentFps = currentGroundSpeedKts * 6076.12 / 3600.0;
        double targetFps = targetSpeedKts * 6076.12 / 3600.0;
        double distFt = distanceNm * 6076.12;

        if (distFt <= 0)
        {
            return MaxDecelRateKtsPerSec;
        }

        // a = (v_initial² - v_final²) / (2d)
        double requiredDecelFps2 = (currentFps * currentFps - targetFps * targetFps) / (2.0 * distFt);
        return requiredDecelFps2 * 3600.0 / 6076.12;
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
