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
    /// Maximum deceleration the pilot will use to make a requested exit.
    /// Above this, the pilot says "unable" — it would require emergency braking.
    /// Normal rollout is ~2.5kts/s; firm braking is ~5kts/s; emergency is 10kts/s.
    /// </summary>
    private const double ReasonableBrakingRateKtsPerSec = 5.0;

    /// <summary>
    /// Comfortable braking multiplier for default exit selection (no explicit preference).
    /// The pilot picks an exit achievable at this multiple of the default rollout decel rate.
    /// 1.5× default gives a natural, unhurried deceleration — not the first exit that
    /// requires maximum effort, but the first one that's comfortable.
    /// </summary>
    private const double ComfortableBrakingMultiplier = 1.5;

    /// <summary>
    /// Floor for gentle braking on explicit far exits. Prevents near-zero decel rates
    /// when the exit is extremely far ahead.
    /// </summary>
    private const double MinSoftBrakingRateKtsPerSec = 0.5;

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

    // Inferred default exit side from runway layout (high-speed exits + parking).
    // Used as a soft tiebreaker in ResolveNextCandidate when the preference has a
    // taxiway but no side — tries inferred side first, falls back to no side.
    private ExitSide? _inferredSide;

    // Branch-point node IDs where the aircraft was declared "unable" (too fast).
    // Prevents ResolveNextCandidate from re-finding the same exit after it was
    // already missed — without this, the aircraft re-discovers L at node 327
    // after coasting to a lower speed and takes it with a near-zero turn.
    private readonly HashSet<int> _unableBranchPoints = [];

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

        // Infer a side preference from the runway's high-speed exit layout and parking
        // proximity. Applied when no side is set (default selection or after unable-replan).
        // For taxiway-only commands (EXIT K), the inferred side is stored separately and
        // used as a soft tiebreaker in ResolveNextCandidate — not merged into _activePreference,
        // since the taxiway might only exist on the other side (e.g., C3 is right-only at SFO).
        if (
            (_activePreference?.Side is null)
            && (ctx.GroundLayout is not null)
            && (ctx.Aircraft.Phases?.AssignedRunway?.Designator is { } rwyDesignator)
        )
        {
            _inferredSide = ctx.GroundLayout.InferPreferredExitSide(rwyDesignator, _runwayHeading);
            if ((_inferredSide is not null) && (_activePreference?.Taxiway is null))
            {
                _activePreference = new ExitPreference { Side = _inferredSide.Value };
            }
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

        // Always search for the next exit ahead — even without an explicit
        // preference, the pilot plans deceleration for the first reachable exit.
        if (_candidateExit is null)
        {
            ResolveNextCandidate(ctx);
        }

        if (_candidateExit is not null)
        {
            double distToBranchPoint = GeoMath.AlongTrackDistanceNm(
                _candidateExit.BranchPointNode.Latitude,
                _candidateExit.BranchPointNode.Longitude,
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _runwayHeading
            );

            // Two "unable" conditions:
            // 1. Past branch AND too fast (original condition)
            // 2. Past branch AND standard exit (>45°, turn-off = standard speed).
            //    Standard exits need room for a 70-90° turn arc. If the aircraft
            //    arrives at the branch at exactly the turn-off speed, the virtual
            //    segment has ~0 length and the turn degenerates to a point turn.
            //    High-speed exits (≤45°) tolerate short virtual segments since
            //    the turn angle is small.
            double highSpeedTurnOff = CategoryPerformance.HighSpeedExitSpeed(ctx.Category);
            bool tooFast = ctx.Aircraft.IndicatedAirspeed > _candidateExit.TurnOffSpeed + TurnOffSpeedToleranceKts;
            bool standardExitAtBranch = _candidateExit.TurnOffSpeed < highSpeedTurnOff;

            if ((distToBranchPoint <= 0) && (tooFast || standardExitAtBranch))
            {
                // Missed this exit — broadcast unable, replan
                string missedTaxiway = _candidateExit.TaxiwayName;
                ctx.Logger.LogDebug(
                    "[Landing] {Callsign}: missed exit {Taxiway} (gs={Gs:F1}kts > {TurnOff:F0}kts)",
                    ctx.Aircraft.Callsign,
                    missedTaxiway,
                    ctx.Aircraft.GroundSpeed,
                    _candidateExit.TurnOffSpeed
                );

                if (_originalPreference?.Taxiway is not null)
                {
                    ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} unable to exit at {missedTaxiway}");
                }

                // Remember this branch point so ResolveNextCandidate won't re-find it.
                _unableBranchPoints.Add(_candidateExit.BranchPointNode.Id);

                // Replan: keep the side from EL/ER commands but drop the failed taxiway.
                // EXIT T (no side) → null preference. EL T → Side=Left. ER → Side=Right.
                // ResolveNextCandidate will fire next tick with the relaxed preference and
                // find the next comfortable exit — same as default behavior for that side.
                _candidateExit = null;
                var keepSide = _originalPreference?.Side;
                _activePreference = keepSide is not null ? new ExitPreference { Side = keepSide } : null;
                _originalPreference = _activePreference;
                _exitResolutionEnabled = false;

                if (ctx.Aircraft.Phases is not null)
                {
                    ctx.Aircraft.Phases.RequestedExit = _activePreference;
                }
            }
        }

        // Speed planning: if we have a candidate exit ahead, compute the required
        // decel to reach its turn-off speed by the branch point. If reasonable
        // (within firm-braking limits), brake harder when needed. The aircraft
        // coasts at coast speed and only brakes below it when kinematically
        // required — no premature crawling at 15kts with the exit far ahead.
        double targetSpeed = coastSpeed;
        if (_candidateExit is not null)
        {
            double distToBranch = GeoMath.AlongTrackDistanceNm(
                _candidateExit.BranchPointNode.Latitude,
                _candidateExit.BranchPointNode.Longitude,
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _runwayHeading
            );

            if ((distToBranch > 0) && (ctx.Aircraft.IndicatedAirspeed > _candidateExit.TurnOffSpeed))
            {
                double requiredDecel = ComputeRequiredDecel(ctx.Aircraft.GroundSpeed, _candidateExit.TurnOffSpeed, distToBranch);

                // Braking limit depends on whether the pilot was told to exit here
                // or is choosing on their own. Explicit preference = firm braking
                // (5kts/s). No preference = comfortable braking (1.5× default).
                double defaultDecel = CategoryPerformance.RolloutDecelRate(ctx.Category);
                double brakingLimit = _exitResolutionEnabled ? ReasonableBrakingRateKtsPerSec : defaultDecel * ComfortableBrakingMultiplier;

                if (requiredDecel <= brakingLimit)
                {
                    // This exit is reachable. Only increase decel when the
                    // kinematic requirement exceeds the current rate — the
                    // aircraft coasts at normal speed and only brakes harder
                    // as the branch point approaches.
                    if (requiredDecel > decelRate)
                    {
                        decelRate = requiredDecel;
                    }

                    if (_exitResolutionEnabled)
                    {
                        // Hand off at coast speed. RunwayExitPhase + GroundNavigator
                        // handle braking below coast for the turn.
                        targetSpeed = coastSpeed;

                        // Reserve distance for RunwayExitPhase to brake from coast
                        // to turn-off speed. Aim to reach coast speed that far
                        // before the exit — not at the exit itself.
                        double brakingBufferNm = ComputeBrakingDistance(coastSpeed, _candidateExit.TurnOffSpeed, defaultDecel);
                        double effectiveDist = distToBranch - brakingBufferNm;

                        // Gentle decel when the exit is far enough that normal
                        // braking would reach coast speed too early.
                        if (effectiveDist > 0)
                        {
                            double requiredDecelToCoast = ComputeRequiredDecel(ctx.Aircraft.GroundSpeed, coastSpeed, effectiveDist);
                            if ((requiredDecelToCoast > 0) && (requiredDecelToCoast < decelRate))
                            {
                                decelRate = Math.Max(requiredDecelToCoast, MinSoftBrakingRateKtsPerSec);
                            }
                        }
                    }
                    else
                    {
                        // Default selection — only brake below coast speed when
                        // kinematically needed. Prevents premature crawling when
                        // the exit is still far ahead.
                        targetSpeed = Math.Max(_candidateExit.TurnOffSpeed, coastSpeed);
                        if (requiredDecel > defaultDecel)
                        {
                            targetSpeed = _candidateExit.TurnOffSpeed;
                        }
                    }
                }
            }
        }

        // Clamp deceleration so we don't go below the target speed
        if (ctx.Aircraft.IndicatedAirspeed <= targetSpeed)
        {
            decelRate = 0;
        }
        else
        {
            double projected = ctx.Aircraft.IndicatedAirspeed - decelRate * ctx.DeltaSeconds;
            if (projected < targetSpeed)
            {
                decelRate = (ctx.Aircraft.IndicatedAirspeed - targetSpeed) / ctx.DeltaSeconds;
                if (decelRate < 0)
                {
                    decelRate = 0;
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

        // Hand off to RunwayExitPhase when at or below the target speed.
        // For standard exits (>45°), ensure enough distance to the branch point
        // for a proper turn arc. If the aircraft is too close, skip this exit
        // (the unable check at the top of next tick will catch it).
        bool handoffBlocked = false;
        if ((_candidateExit is not null) && (ctx.Aircraft.IndicatedAirspeed <= targetSpeed))
        {
            double distToBranch = GeoMath.AlongTrackDistanceNm(
                _candidateExit.BranchPointNode.Latitude,
                _candidateExit.BranchPointNode.Longitude,
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _runwayHeading
            );

            double hsExitSpeed = CategoryPerformance.HighSpeedExitSpeed(ctx.Category);
            bool isStandardExit = _candidateExit.TurnOffSpeed < hsExitSpeed;

            // Standard exits need ~0.02nm (~120ft) for a turn arc at 15kts.
            // If less distance remains, the virtual segment will be too short.
            if (isStandardExit && (distToBranch < 0.02))
            {
                handoffBlocked = true;
            }
        }

        if (!_hasLahso && !handoffBlocked && (ctx.Aircraft.IndicatedAirspeed <= targetSpeed))
        {
            ctx.Logger.LogDebug(
                "[Landing] {Callsign}: rollout complete, gs={Gs:F1}kts, target={Target:F0}kts",
                ctx.Aircraft.Callsign,
                ctx.Aircraft.GroundSpeed,
                targetSpeed
            );
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
        // Skip exits whose branch points were already declared "unable" to prevent
        // re-discovering a missed exit after the aircraft has slowed near it.
        //
        // Soft tiebreaker: when the preference has a taxiway but no side (EXIT K),
        // try with the inferred side first. If the taxiway doesn't exist on that
        // side (e.g., C3 is right-only at SFO), fall back to taxiway-only.
        if (rwyDesignator is not null)
        {
            var searchPref = _activePreference;

            // Try inferred side first for taxiway-only preferences
            if ((_activePreference is { Taxiway: not null, Side: null }) && (_inferredSide is not null))
            {
                searchPref = new ExitPreference { Taxiway = _activePreference.Taxiway, Side = _inferredSide.Value };
            }

            var exit = ctx.GroundLayout.FindExitFromCenterline(
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude,
                _runwayHeading,
                rwyDesignator,
                searchPref,
                _unableBranchPoints
            );

            // Fall back to taxiway-only if inferred side found nothing
            if ((exit is null) && (searchPref != _activePreference))
            {
                exit = ctx.GroundLayout.FindExitFromCenterline(
                    ctx.Aircraft.Latitude,
                    ctx.Aircraft.Longitude,
                    _runwayHeading,
                    rwyDesignator,
                    _activePreference,
                    _unableBranchPoints
                );
            }

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

    /// <summary>
    /// Compute distance (nm) required to brake from one speed to another at a given decel rate.
    /// Inverse of ComputeRequiredDecel: d = (v_i² - v_f²) / (2a).
    /// </summary>
    private static double ComputeBrakingDistance(double fromSpeedKts, double toSpeedKts, double decelRateKtsPerSec)
    {
        if (decelRateKtsPerSec <= 0)
        {
            return 0;
        }

        double fromFps = fromSpeedKts * 6076.12 / 3600.0;
        double toFps = toSpeedKts * 6076.12 / 3600.0;
        double decelFps2 = decelRateKtsPerSec * 6076.12 / 3600.0;
        double distFt = (fromFps * fromFps - toFps * toFps) / (2.0 * decelFps2);
        return distFt / 6076.12;
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
