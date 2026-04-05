using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

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

    /// <summary>
    /// Tolerance for turn-off speed commit check. Discrete-tick deceleration can overshoot
    /// the target by up to decelRate * deltaSeconds (~2.5 kts). Without tolerance, an aircraft
    /// at 30.2 kts misses a 30 kt turn-off by a hair and falls back to the next exit.
    /// </summary>
    private const double TurnOffSpeedToleranceKts = 3.0;

    private double _fieldElevation;
    private TrueHeading _runwayHeading;
    private double _thresholdLat;
    private double _thresholdLon;
    private bool _touchedDown;
    private bool _canGoAround;
    private double _lahsoHoldShortDistNm;
    private bool _hasLahso;

    // Exit-aware braking state (continuous evaluation)
    private ResolvedExitInfo? _candidateExit;
    private ExitPreference? _activePreference;
    private ExitPreference? _originalPreference;

    // True only when a controller has explicitly assigned an exit preference.
    // Without an explicit preference, LandingPhase coasts and hands off to RunwayExitPhase.
    private bool _exitResolutionEnabled;

    public bool StoppedForLahso { get; private set; }

    public override string Name => "Landing";

    public override PhaseDto ToSnapshot() =>
        new LandingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            FieldElevation = _fieldElevation,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            TouchedDown = _touchedDown,
            CanGoAround = _canGoAround,
            LahsoHoldShortDistNm = _lahsoHoldShortDistNm,
            HasLahso = _hasLahso,
            CandidateExitHoldShortId = _candidateExit?.HoldShortNode.Id,
            CandidateExitBranchPointId = _candidateExit?.BranchPointNode.Id,
            CandidateExitTaxiway = _candidateExit?.TaxiwayName,
            CandidateExitTurnOffSpeed = _candidateExit?.TurnOffSpeed ?? 0,
            CandidateExitPathNodeIds = _candidateExit?.Path.Select(n => n.Id).ToList(),
            ActivePreferenceSide = (int?)_activePreference?.Side,
            ActivePreferenceTaxiway = _activePreference?.Taxiway,
            OriginalPreferenceSide = (int?)_originalPreference?.Side,
            OriginalPreferenceTaxiway = _originalPreference?.Taxiway,
            ExitResolutionEnabled = _exitResolutionEnabled,
            StoppedForLahso = StoppedForLahso,
        };

    public static LandingPhase FromSnapshot(LandingPhaseDto dto, AirportGroundLayout? groundLayout)
    {
        var phase = new LandingPhase();
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._fieldElevation = dto.FieldElevation;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
        phase._touchedDown = dto.TouchedDown;
        phase._canGoAround = dto.CanGoAround;
        phase._lahsoHoldShortDistNm = dto.LahsoHoldShortDistNm;
        phase._hasLahso = dto.HasLahso;
        phase._exitResolutionEnabled = dto.ExitResolutionEnabled;
        phase.StoppedForLahso = dto.StoppedForLahso;
        if (dto.ActivePreferenceSide.HasValue || dto.ActivePreferenceTaxiway is not null)
        {
            phase._activePreference = new ExitPreference
            {
                Side = dto.ActivePreferenceSide.HasValue ? (ExitSide)dto.ActivePreferenceSide.Value : null,
                Taxiway = dto.ActivePreferenceTaxiway,
            };
        }
        if (dto.OriginalPreferenceSide.HasValue || dto.OriginalPreferenceTaxiway is not null)
        {
            phase._originalPreference = new ExitPreference
            {
                Side = dto.OriginalPreferenceSide.HasValue ? (ExitSide)dto.OriginalPreferenceSide.Value : null,
                Taxiway = dto.OriginalPreferenceTaxiway,
            };
        }
        if (
            groundLayout is not null
            && dto.CandidateExitHoldShortId.HasValue
            && dto.CandidateExitBranchPointId.HasValue
            && dto.CandidateExitTaxiway is not null
            && groundLayout.Nodes.TryGetValue(dto.CandidateExitHoldShortId.Value, out var holdShortNode)
            && groundLayout.Nodes.TryGetValue(dto.CandidateExitBranchPointId.Value, out var branchPointNode)
        )
        {
            List<GroundNode> path = [];
            if (dto.CandidateExitPathNodeIds is not null)
            {
                foreach (int nodeId in dto.CandidateExitPathNodeIds)
                {
                    if (groundLayout.Nodes.TryGetValue(nodeId, out var pathNode))
                    {
                        path.Add(pathNode);
                    }
                }
            }
            phase._candidateExit = new ResolvedExitInfo
            {
                HoldShortNode = holdShortNode,
                BranchPointNode = branchPointNode,
                TaxiwayName = dto.CandidateExitTaxiway,
                TurnOffSpeed = dto.CandidateExitTurnOffSpeed,
                Path = path,
            };
        }
        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.TrueHeading;
        _thresholdLat = ctx.Runway?.ThresholdLatitude ?? ctx.Aircraft.Latitude;
        _thresholdLon = ctx.Runway?.ThresholdLongitude ?? ctx.Aircraft.Longitude;

        // Capture LAHSO target if set
        if (ctx.Aircraft.Phases?.LahsoHoldShort is { } lahso)
        {
            _hasLahso = true;
            _lahsoHoldShortDistNm = lahso.DistFromThresholdNm;
        }

        _originalPreference = ctx.Aircraft.Phases?.RequestedExit;
        _activePreference = _originalPreference;
        _exitResolutionEnabled = _originalPreference is not null;

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
        ctx.Targets.TargetTrueHeading = new TrueHeading(_runwayHeading.Degrees - correction);

        // Re-resolve candidate from scratch if the controller changed the preference mid-rollout
        var currentPref = ctx.Aircraft.Phases?.RequestedExit;
        if (currentPref != _originalPreference)
        {
            _originalPreference = currentPref;
            _activePreference = currentPref;
            _candidateExit = null;
            _exitResolutionEnabled = currentPref is not null;
        }

        double decelRate = CategoryPerformance.RolloutDecelRate(ctx.Category);

        // LAHSO: compute distance to hold-short point and increase deceleration if needed
        if (_hasLahso)
        {
            double distFromThreshold = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _thresholdLat, _thresholdLon);
            double distToHoldShort = _lahsoHoldShortDistNm - distFromThreshold;

            if ((distToHoldShort > 0) && (ctx.Aircraft.IndicatedAirspeed > 1.0))
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

        // Coast speed: decelerate to this speed and hold it while searching for exits
        double coastSpeed = CategoryPerformance.RolloutCoastSpeed(ctx.Category);

        // Continuous exit evaluation: resolve a candidate if we don't have one
        // Only when exit resolution was enabled by an explicit controller preference.
        if (_exitResolutionEnabled && (_candidateExit is null))
        {
            ResolveNextCandidate(ctx);
        }

        if (_candidateExit is not null)
        {
            // Commit as soon as the aircraft is slow enough, regardless of whether it
            // has reached the branch point. This covers the case where the aircraft
            // decelerates below turn-off speed before reaching the branch point.
            if (ctx.Aircraft.IndicatedAirspeed <= _candidateExit.TurnOffSpeed)
            {
                ctx.Aircraft.Phases!.ResolvedExit = _candidateExit;
                ctx.Logger.LogDebug(
                    "[Landing] {Callsign}: committing to exit {Taxiway}, gs={Gs:F1}kts",
                    ctx.Aircraft.Callsign,
                    _candidateExit.TaxiwayName,
                    ctx.Aircraft.GroundSpeed
                );
                return true;
            }

            double distToBranchPoint = GeoMath.AlongTrackDistanceNm(
                _candidateExit.BranchPointNode.Latitude,
                _candidateExit.BranchPointNode.Longitude,
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _runwayHeading
            );

            if (distToBranchPoint <= 0)
            {
                // At or past the branch point — commit if within tolerance of turn-off speed.
                // Discrete-tick deceleration can overshoot by a few kts; don't abandon the
                // preferred exit over a marginal speed difference.
                if (ctx.Aircraft.IndicatedAirspeed <= _candidateExit.TurnOffSpeed + TurnOffSpeedToleranceKts)
                {
                    ctx.Aircraft.Phases!.ResolvedExit = _candidateExit;
                    ctx.Logger.LogDebug(
                        "[Landing] {Callsign}: committing to exit {Taxiway} at branch point, gs={Gs:F1}kts (tolerance)",
                        ctx.Aircraft.Callsign,
                        _candidateExit.TaxiwayName,
                        ctx.Aircraft.GroundSpeed
                    );
                    return true;
                }

                // Too fast even with tolerance — abandon and try next candidate
                string missedTaxiway = _candidateExit.TaxiwayName;
                ctx.Logger.LogDebug(
                    "[Landing] {Callsign}: missed exit {Taxiway} (gs={Gs:F1}kts > {TurnOff:F0}kts), relaxing preference",
                    ctx.Aircraft.Callsign,
                    missedTaxiway,
                    ctx.Aircraft.GroundSpeed,
                    _candidateExit.TurnOffSpeed
                );
                RelaxPreference();
                _candidateExit = null;
                ResolveNextCandidate(ctx);

                // Pilot broadcast: tell ATC we couldn't make the preferred exit
                if (_originalPreference?.Taxiway is not null)
                {
                    string fallbackInfo = _candidateExit is not null ? $", will exit at {_candidateExit.TaxiwayName}" : "";
                    ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} unable to exit at {missedTaxiway}{fallbackInfo}");
                }
            }

            // Before the branch point: brake toward turn-off speed
            if (_candidateExit is not null)
            {
                double distToUse = GeoMath.AlongTrackDistanceNm(
                    _candidateExit.BranchPointNode.Latitude,
                    _candidateExit.BranchPointNode.Longitude,
                    ctx.Aircraft.Latitude,
                    ctx.Aircraft.Longitude,
                    _runwayHeading
                );

                if ((distToUse > 0) && (ctx.Aircraft.IndicatedAirspeed > _candidateExit.TurnOffSpeed))
                {
                    double exitDecel = ComputeRequiredDecel(ctx.Aircraft.GroundSpeed, _candidateExit.TurnOffSpeed, distToUse);

                    if (exitDecel > decelRate)
                    {
                        decelRate = Math.Min(exitDecel, MaxDecelRateKtsPerSec);
                    }
                    else if (ctx.Aircraft.IndicatedAirspeed <= coastSpeed)
                    {
                        // At or below coast speed — hold until braking point
                        decelRate = 0;
                    }
                    else
                    {
                        // Above coast speed — decel to coast speed but not below
                        double coastLimited = ctx.Aircraft.IndicatedAirspeed - decelRate * ctx.DeltaSeconds;
                        if (coastLimited < coastSpeed)
                        {
                            decelRate = 0;
                        }
                    }
                }
            }
        }
        else
        {
            // No candidate — decelerate to coast speed and hold
            if (ctx.Aircraft.IndicatedAirspeed <= coastSpeed)
            {
                decelRate = 0;
            }
            else
            {
                double coastLimited = ctx.Aircraft.IndicatedAirspeed - decelRate * ctx.DeltaSeconds;
                if (coastLimited < coastSpeed)
                {
                    decelRate = (ctx.Aircraft.IndicatedAirspeed - coastSpeed) / ctx.DeltaSeconds;
                    if (decelRate < 0)
                    {
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
        if (_hasLahso && (ctx.Aircraft.IndicatedAirspeed <= 0))
        {
            StoppedForLahso = true;
            ctx.Logger.LogDebug("[Landing] {Callsign}: LAHSO rollout complete, stopped", ctx.Aircraft.Callsign);
            return true;
        }

        // No candidate and at coast speed: hand off to RunwayExitPhase with no exit info
        if (!_hasLahso && (_candidateExit is null) && (ctx.Aircraft.IndicatedAirspeed <= coastSpeed))
        {
            ctx.Logger.LogDebug("[Landing] {Callsign}: rollout complete (no exit), gs={Gs:F1}kts", ctx.Aircraft.Callsign, ctx.Aircraft.GroundSpeed);
            return true;
        }

        return false;
    }

    private void ResolveNextCandidate(PhaseContext ctx)
    {
        if (ctx.GroundLayout is null)
        {
            return;
        }

        string? rwyDesignator = ctx.Aircraft.Phases?.AssignedRunway?.Designator;

        // Primary: walk centerline nodes ahead, search outward at each for a matching exit.
        // This searches runway → taxiway → hold-short (the correct direction).
        if (rwyDesignator is not null)
        {
            var exit = ctx.GroundLayout.FindExitFromCenterline(
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _runwayHeading,
                rwyDesignator,
                _activePreference
            );

            if (exit is not null)
            {
                double turnOffSpeed = CategoryPerformance.ExitTurnOffSpeed(ctx.Category, exit.Value.ExitAngle);
                _candidateExit = new ResolvedExitInfo
                {
                    HoldShortNode = exit.Value.HoldShort,
                    TaxiwayName = exit.Value.Taxiway,
                    TurnOffSpeed = turnOffSpeed,
                    Path = exit.Value.Path,
                    BranchPointNode = exit.Value.Path[0],
                };

                ctx.Logger.LogDebug(
                    "[Landing] {Callsign}: candidate exit {Taxiway}, angle={Angle:F0}, turnOffSpeed={Speed:F0}kts, path=[{Path}]",
                    ctx.Aircraft.Callsign,
                    exit.Value.Taxiway,
                    exit.Value.ExitAngle,
                    turnOffSpeed,
                    string.Join("→", exit.Value.Path.Select(n => n.Id))
                );
                return;
            }
        }

        // Fallback: straight-line search (airports without hold-short data)
        var result = ctx.GroundLayout.FindExitAheadOnRunway(
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            _runwayHeading,
            _activePreference,
            rwyDesignator
        );

        if (result is null)
        {
            return;
        }

        double? fallbackAngle = ctx.GroundLayout.ComputeExitAngle(result.Value.Node, result.Value.Taxiway, _runwayHeading);
        double fallbackTurnOffSpeed = CategoryPerformance.ExitTurnOffSpeed(ctx.Category, fallbackAngle);

        // For the fallback path, branch point = the exit node itself
        _candidateExit = new ResolvedExitInfo
        {
            HoldShortNode = result.Value.Node,
            TaxiwayName = result.Value.Taxiway,
            TurnOffSpeed = fallbackTurnOffSpeed,
            Path = [result.Value.Node],
            BranchPointNode = result.Value.Node,
        };

        ctx.Logger.LogDebug(
            "[Landing] {Callsign}: candidate exit (fallback) {Taxiway}, turnOffSpeed={Speed:F0}kts",
            ctx.Aircraft.Callsign,
            result.Value.Taxiway,
            fallbackTurnOffSpeed
        );
    }

    /// <summary>
    /// Steps the active preference down the fallback chain:
    /// specific taxiway + side → side only → any exit → coast to end.
    /// Disables exit resolution once we reach the coast-to-end state.
    /// </summary>
    private void RelaxPreference()
    {
        if (_activePreference?.Taxiway is not null)
        {
            // Drop taxiway, keep side preference
            _activePreference = new ExitPreference { Side = _activePreference.Side };
        }
        else if (_activePreference?.Side is not null)
        {
            // Drop side — accept any exit
            _activePreference = null;
        }
        else
        {
            // Already at "any exit" and still missed — coast to end
            _exitResolutionEnabled = false;
        }
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
