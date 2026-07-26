using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// After landing rollout completes, rolls the aircraft forward along runway
/// centerline edges until a hold-short exit is found, then follows the exit
/// path using <see cref="GroundNavigator"/> with exit-appropriate speed.
///
/// States:
///   RollingOnCenterline — following RWY edges forward, checking for exits
///   FollowingExitPath — navigator follows exit taxiway edges to hold-short
/// </summary>
public sealed class RunwayExitPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("RunwayExitPhase");

    private const double LogIntervalSeconds = 3.0;

    /// <summary>
    /// Lead time (seconds) the aircraft must still have before the branch point for a late
    /// <c>EL</c>/<c>ER</c>/<c>EXIT</c> to be honored. Inside it the pilot is committed to the turn-off.
    ///
    /// <para>
    /// This is purely a <em>reaction</em> budget, aviation-reviewed at 4.0 s: receive/comprehend + readback +
    /// re-plan (AIM 4-4-10.a.4 has the pilot act on acknowledgement, and a refusal is itself a transmission
    /// per 7110.65 §2-1-18.3). It is well under the 5-10 s used for airborne clearances because an exit
    /// instruction during rollout is high-expectancy — AIM 4-3-21.a already has the pilot hunting for a
    /// turnoff. Whether the aircraft can physically <em>make</em> a candidate exit is a separate, explicit
    /// check in <see cref="RunRetargetSearch"/>; do not fold the two back together.
    /// </para>
    /// </summary>
    private const double RetargetLeadSeconds = 4.0;

    /// <summary>
    /// Floor for the retarget lead distance (ft), so a slow-rolling aircraft still needs real room —
    /// roughly one fuselage length, a physical minimum for a nose-wheel steering setup. Binds below ~15 kt,
    /// where it yields well over <see cref="RetargetLeadSeconds"/> of warning anyway.
    /// </summary>
    private const double MinRetargetLeadFt = 100.0;

    /// <summary>
    /// Heading divergence (degrees) from the rollout datum — the heading captured at
    /// <see cref="OnStart"/>, which is what <see cref="TickRolling"/> holds — that counts as the turn-off
    /// having started, even while the navigator is still nominally on the virtual approach segment:
    /// <c>GroundNavigator</c>'s pre-turn blend starts swinging the nose inside the last ~50 ft of a straight.
    /// The datum is held flat with no cross-track correction, and a taxiing aircraft has no crab, so there is
    /// no heading noise for this to trip on.
    /// </summary>
    private const double RetargetMaxHeadingDeviationDeg = 10.0;

    /// <summary>
    /// Length (nm) of the runway-centerline approach leg synthesised as segment 0 when the exit route is rebuilt
    /// for an aircraft that has already passed the branch node. The live route's segment 0 ran from wherever
    /// <c>LandingPhase</c> handed off, down the centerline to the branch; a restore cannot know where that was, so
    /// it reconstructs a leg of this length on the runway heading. Only the leg's <em>bearing</em> shapes the
    /// resumed turn — <c>GroundNavigator</c> reads it as the corner's incoming tangent — but the length has to stay
    /// well clear of the short-connector scale so the runway is not mistaken for one.
    /// </summary>
    private const double RestoredApproachSegmentNm = 0.25;

    /// <summary>
    /// Sentinel node ID for the virtual approach segment. The segment from the
    /// aircraft's current position to the exit branch point uses this as FromNodeId.
    /// It never needs to be looked up in the layout — the navigator only resolves
    /// ToNodeId for target coordinates.
    /// </summary>
    public enum ExitState
    {
        RollingOnCenterline,
        FollowingExitPath,
    }

    private ExitState _state = ExitState.RollingOnCenterline;
    private string? _runwayId;
    private TrueHeading _runwayHeading;
    private ExitPreference? _lastResolvedPreference;
    private ExitSide? _inferredSide;
    private double _coastSpeed;
    private double _timeSinceLastLog;

    // Exit target (set when a hold-short is found)
    private GroundNode? _holdShortNode;
    private string? _exitTaxiway;

    // Full exit path including branch point: [branchNode, wp1, wp2, ..., holdShort]
    private List<GroundNode>? _exitPath;

    // Exit path navigation (FollowingExitPath state)
    private TaxiRoute? _exitRoute;
    private GroundNavigator? _navigator;

    // Latched once the turn-off is physically under way — see TurnStarted.
    private bool _turnStarted;

    // Segment the exit route was on when the snapshot was taken. The route itself is built from the live ground
    // layout and is not serialized, so the first tick after a restore rebuilds it — and without this the rebuild
    // would restart at segment 0, the virtual approach leg to the branch node, which points backward once the
    // aircraft is past the branch. Zero for a phase that was never restored.
    private int _restoreSegmentIndex;

    // The aircraft's RequestedExit as it stood when the route was handed to the navigator. A late exit
    // change is "the controller issued something new since we committed", which is identity against this —
    // not against _lastResolvedPreference, which OnStart may have replaced with an inferred-side variant
    // that never matches what the aircraft is carrying. Re-captured by StartExitNavigation, including the
    // route rebuild after a snapshot restore, so it needs no DTO field.
    private ExitPreference? _committedPreference;

    /// <summary>
    /// The hold-short node ID this aircraft is targeting (or null if still searching).
    /// Used by <see cref="SimulationEngine"/> to mark the exit as occupied so other
    /// aircraft don't select the same exit.
    /// </summary>
    public int? TargetHoldShortNodeId => _holdShortNode?.Id;

    /// <summary>
    /// True while the aircraft is rolling along the runway centerline searching for
    /// an exit. False once it has committed to an exit and is following the taxiway
    /// path. Used by <see cref="GroundConflictDetector"/> to decide whether the
    /// aircraft should be exempt from ground conflict checks (only while on centerline).
    /// </summary>
    public bool IsOnCenterline => _state == ExitState.RollingOnCenterline;

    /// <summary>
    /// True once the turn-off is physically under way and a late exit change can no longer be honored:
    /// the navigator has left the virtual approach segment, or the nose has already swung off the runway
    /// heading. Latched — a momentary re-alignment must not reopen the window.
    /// </summary>
    public bool TurnStarted => _turnStarted;

    /// <summary>
    /// The runway being exited. Captured in <see cref="OnStart"/> from the
    /// aircraft's assigned runway. Used by the client info text to render
    /// "Exiting runway {id} via {taxiway}".
    /// </summary>
    public string? RunwayId => _runwayId;

    public override string Name => "Runway Exit";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        _runwayId = ctx.Aircraft.Phases?.AssignedRunway?.Designator;
        _runwayHeading = ctx.Aircraft.TrueHeading;
        _lastResolvedPreference = ctx.Aircraft.Phases?.RequestedExit;
        _coastSpeed = CategoryPerformance.RolloutCoastSpeed(ctx.Category);

        // Infer a side from runway layout. For default (no preference), merge directly.
        // For taxiway-only (EXIT K), store separately — TryFindExitAhead uses it as
        // a soft tiebreaker so taxiways that only exist on one side still work.
        if ((_lastResolvedPreference?.Side is null) && (ctx.GroundLayout is not null) && (_runwayId is not null))
        {
            _inferredSide = ctx.GroundLayout.InferPreferredExitSide(_runwayId, _runwayHeading);
            Log.LogDebug(
                "[Exit] {Callsign}: inferred exit side for {Rwy} = {Side}",
                ctx.Aircraft.Callsign,
                _runwayId,
                _inferredSide?.ToString() ?? "none"
            );
            if (_inferredSide is not null)
            {
                _lastResolvedPreference = new ExitPreference { Side = _inferredSide.Value, Taxiway = _lastResolvedPreference?.Taxiway };
            }
        }

        if (ctx.GroundLayout is null)
        {
            Log.LogDebug("[Exit] {Callsign}: no ground layout, will stop immediately", ctx.Aircraft.Callsign);
            return;
        }

        // If LandingPhase committed a resolved exit, honor it — but only if
        // the hold-short isn't currently occupied. LandingPhase plans without
        // regard to occupancy so the pilot continues to brake for the commanded
        // exit, only deciding to skip at the last moment. RunwayExitPhase is
        // that last moment: if another aircraft is now claiming the committed
        // hold-short, drop the commit and fall through to a fresh analog search
        // that excludes occupied nodes.
        var committed = ctx.Aircraft.Phases?.ResolvedExit;
        if (committed is not null)
        {
            ctx.Aircraft.Phases!.ResolvedExit = null;

            bool occupied = ctx.OccupiedHoldShortNodes?.Contains(committed.HoldShortNode.Id) ?? false;
            if (!occupied)
            {
                _holdShortNode = committed.HoldShortNode;
                _exitTaxiway = committed.TaxiwayName;
                _exitPath = committed.Path;

                Log.LogDebug(
                    "[Exit] {Callsign}: using committed exit {Twy}, path=[{Path}]",
                    ctx.Aircraft.Callsign,
                    _exitTaxiway,
                    string.Join("→", _exitPath.Select(n => n.Id))
                );
            }
            else
            {
                Log.LogDebug(
                    "[Exit] {Callsign}: committed exit {Twy} is now occupied, falling back to analog search",
                    ctx.Aircraft.Callsign,
                    committed.TaxiwayName
                );
            }
        }

        if (_holdShortNode is null)
        {
            // Search for exits ahead immediately.
            TryFindExitAhead(ctx);
        }

        Log.LogDebug(
            "[Exit] {Callsign}: rwy {Rwy}, hdg={Hdg:F0}, holdShort={HS}",
            ctx.Aircraft.Callsign,
            _runwayId ?? "?",
            _runwayHeading.Degrees,
            _holdShortNode?.Id.ToString() ?? "searching"
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.Ground.IsImmobile)
        {
            ctx.Targets.TargetSpeed = 0;
            ctx.Targets.DesiredDecelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
            return false;
        }

        if (_state == ExitState.FollowingExitPath)
        {
            // A snapshot restore brings back the state, the waypoint nodes and the navigator, but the exit route is
            // built from the live ground layout and is not serialized — so it comes back null. TickFollowingExitPath
            // reads a null route as "exit complete", which would end the phase without CompleteExit's cleanup.
            // Rebuild it here, mirroring the sibling navigator-owning phases that defer their route build to the
            // first tick after restore. The rebuild resumes on the segment the route was on, so an aircraft already
            // past the branch node keeps following the exit instead of turning back onto the runway.
            if (_exitRoute is null)
            {
                int resumeIndex = ResumeSegmentIndexAfterRestore(ctx);
                _restoreSegmentIndex = 0;
                if (!StartExitNavigation(ctx, resumeIndex))
                {
                    // Rebuild failed (layout gone, or an edge on the stored path no longer exists). Fall back to the
                    // centerline search rather than silently declaring the exit complete — same recovery the
                    // build-time failure path takes.
                    _state = ExitState.RollingOnCenterline;
                    _holdShortNode = null;
                    _exitTaxiway = null;
                    _exitPath = null;
                    return TickRolling(ctx);
                }
            }

            // A late EL/ER/EXIT that arrived while the aircraft is committed but still tracking the
            // centerline re-points it at a different exit. On success the new exit is already loaded, so
            // fall through to the commit block below and hand the fresh route to a new navigator.
            if (!TryRetargetCommittedExit(ctx))
            {
                return TickFollowingExitPath(ctx);
            }
        }

        // Re-check preference if changed mid-phase
        var currentPref = ctx.Aircraft.Phases?.RequestedExit;
        if (currentPref != _lastResolvedPreference && _holdShortNode is null)
        {
            _lastResolvedPreference = currentPref;
            TryFindExitAhead(ctx);
        }

        // Exit found — build route with virtual approach segment and hand to navigator
        if (_holdShortNode is not null && _state == ExitState.RollingOnCenterline)
        {
            if (StartExitNavigation(ctx, resumeSegmentIndex: 0))
            {
                return TickFollowingExitPath(ctx);
            }

            // Route construction failed. Clear and keep searching.
            _holdShortNode = null;
            _exitTaxiway = null;
            _exitPath = null;
        }

        return TickRolling(ctx);
    }

    /// <summary>
    /// Analog centerline rolling: steer along the runway heading (no node
    /// walking) and continuously search for exits ahead. Writes ControlTargets
    /// and lets FlightPhysics integrate — no direct pose or IAS writes. Safe
    /// from the StationaryGroundSpeedKts guard because rolling is always at
    /// coast speed (≥ 15 kt helicopter, ≥ 40 kt jet), never approaching the
    /// 0.1 kt floor.
    /// </summary>
    private bool TickRolling(PhaseContext ctx)
    {
        ctx.Targets.TargetTrueHeading = _runwayHeading;
        ctx.Targets.TargetSpeed = _coastSpeed;
        // Use ground rollout decel (category-specific, 5 kt/s jet, 2 kt/s
        // piston) rather than the airborne default from AircraftPerformance.DecelRate.
        ctx.Targets.DesiredDecelRate = CategoryPerformance.TaxiDecelRate(ctx.Category);
        // Use ground turn rate (20 deg/s jet, 35 deg/s piston) rather than the
        // airborne turn rate (~2.5 deg/s). On the ground FlightPhysics uses this
        // override via TurnRateOverride; V1 achieved the same effect by writing
        // Aircraft.TrueHeading directly with GroundTurnRate.
        ctx.Targets.TurnRateOverride = CategoryPerformance.GroundTurnRate(ctx.Category);

        // Continuously search for exits ahead
        if (_holdShortNode is null)
        {
            TryFindExitAhead(ctx);
        }

        // Terminal-end safety stop: if no exit was found and the aircraft is
        // running out of runway, brake to a halt rather than coasting off the
        // end. Without this backstop, a missing or unreachable forward exit
        // (typically a geojson defect or an aircraft that landed past every
        // exit) leaves the phase looping at coast speed indefinitely.
        if ((_holdShortNode is null) && (ctx.Aircraft.Phases?.AssignedRunway is { } rwy))
        {
            double distToEndNm = GeoMath.AlongTrackDistanceNm(new LatLon(rwy.EndLatitude, rwy.EndLongitude), ctx.Aircraft.Position, _runwayHeading);

            // 0.15 nm ≈ 911 ft — enough headroom for the aircraft to slow from
            // coast speed (40 kts jet) to a stop at the firm braking rate
            // before the runway end, without prematurely stopping on a runway
            // where exits are still being searched for.
            const double TerminalStopBufferNm = 0.15;
            if (distToEndNm <= TerminalStopBufferNm)
            {
                ctx.Targets.TargetSpeed = 0;
                // Firm braking (5 kts/s) — same rate LandingPhase uses for explicit
                // exit commands. From 40 kts coast, this stops the aircraft in
                // about 0.044 nm (260 ft) — comfortably inside the 0.15 nm buffer.
                ctx.Targets.DesiredDecelRate = 5.0;
                if (_timeSinceLastLog >= LogIntervalSeconds)
                {
                    _timeSinceLastLog = 0;
                    Log.LogWarning(
                        "[Exit] {Callsign}: no exit found, {DistFt:F0}ft to runway end — braking to stop",
                        ctx.Aircraft.Callsign,
                        distToEndNm * 6076.12
                    );
                }
            }
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            Log.LogTrace(
                "[Exit] {Callsign}: rolling, gs={Gs:F1}kts, hdg={Hdg:F0}",
                ctx.Aircraft.Callsign,
                ctx.Aircraft.GroundSpeed,
                ctx.Aircraft.TrueHeading.Degrees
            );
        }

        return false;
    }

    /// <summary>
    /// Search for the nearest exit ahead of the aircraft using the runway
    /// centerline graph. If the preferred taxiway isn't found ahead, relaxes
    /// the preference (taxiway → side → any) until an exit is found.
    /// Sets _holdShortNode/_exitTaxiway/_exitPath if found.
    ///
    /// Lookahead rule (mirrors <see cref="LandingPhase.ResolveNextCandidate"/>):
    /// when there is a side preference (explicit or inferred) and the BFS at a
    /// given centerline returns an off-side hold-short (because the on-side was
    /// occupied or doesn't exist there), defer that candidate and continue
    /// walking forward for an on-side option. Only commit the deferred off-side
    /// fallback when the walk exhausts.
    /// </summary>
    private void TryFindExitAhead(PhaseContext ctx)
    {
        if (ctx.GroundLayout is null || _runwayId is null)
        {
            return;
        }

        // Occupied hold-short nodes are excluded at the BFS level so the finder
        // returns the next-best unoccupied exit at each centerline node, rather
        // than returning an occupied exit that we'd have to skip post-hoc (which
        // would miss other exits from the same centerline node).
        var occupied = ctx.OccupiedHoldShortNodes;

        // Soft tiebreaker: when the preference has a taxiway but no side, try
        // with the inferred side first. If nothing found, fall through to the
        // normal relaxation loop with the original preference.
        if ((_lastResolvedPreference is { Taxiway: not null, Side: null }) && (_inferredSide is not null))
        {
            var tiebreakerPref = new ExitPreference { Taxiway = _lastResolvedPreference.Taxiway, Side = _inferredSide.Value };
            if (TryRunSearchWithLookahead(ctx, tiebreakerPref, occupied, _inferredSide))
            {
                return;
            }
        }

        // Try with current preference, then relax until we find something.
        var preference = _lastResolvedPreference;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ExitSide? sidePref = preference?.Side ?? _inferredSide;
            if (TryRunSearchWithLookahead(ctx, preference, occupied, sidePref))
            {
                return;
            }

            // Relax preference: taxiway → side → any
            if (preference?.Taxiway is not null)
            {
                preference = new ExitPreference { Side = preference.Side };
            }
            else if (preference?.Side is not null)
            {
                preference = null;
            }
            else
            {
                break; // Already at "any", nothing more to relax
            }
        }
    }

    /// <summary>
    /// Walk centerlines forward looking for an exit that satisfies the side
    /// preference. Defers off-side candidates while searching for an on-side
    /// option, falling back to the deferred off-side if none is found.
    /// Returns true on commit.
    /// </summary>
    private bool TryRunSearchWithLookahead(PhaseContext ctx, ExitPreference? preference, HashSet<int>? occupied, ExitSide? sidePref)
    {
        if (ctx.GroundLayout is null || _runwayId is null)
        {
            return false;
        }

        bool isExplicit = (preference?.Taxiway is not null) || (preference?.Side is not null);

        var found = ctx.GroundLayout.FindOnSidePreferredExit(
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            _runwayHeading,
            _runwayId,
            preference,
            sidePref,
            excludeBranchPoints: null,
            excludeHoldShortNodes: occupied,
            filter: candidate =>
            {
                // Skip backward exits when no explicit preference
                if ((candidate.ExitAngle > 100) && !isExplicit)
                {
                    return AirportGroundLayout.CandidateVerdict.Skip;
                }
                return AirportGroundLayout.CandidateVerdict.Accept;
            }
        );

        if (found is null)
        {
            return false;
        }

        CommitFoundExit(ctx, found.Value.HoldShort, found.Value.Taxiway, found.Value.Path, found.Value.ExitAngle, preference);
        return true;
    }

    private void CommitFoundExit(
        PhaseContext ctx,
        GroundNode holdShort,
        string taxiway,
        List<GroundNode> path,
        double exitAngle,
        ExitPreference? preference
    )
    {
        if ((preference != _lastResolvedPreference) && (_lastResolvedPreference?.Taxiway is not null))
        {
            Log.LogDebug(
                "[Exit] {Callsign}: preferred exit {Twy} not ahead, relaxed to {Actual}",
                ctx.Aircraft.Callsign,
                _lastResolvedPreference.Taxiway,
                taxiway
            );
        }

        _holdShortNode = holdShort;
        _exitTaxiway = taxiway;
        _exitPath = path;

        Log.LogDebug(
            "[Exit] {Callsign}: found exit {Twy}, angle={Angle:F0}°, path=[{Path}]",
            ctx.Aircraft.Callsign,
            _exitTaxiway,
            exitAngle,
            string.Join("→", _exitPath.Select(n => n.Id))
        );
    }

    /// <summary>
    /// Whether a late <c>EL</c>/<c>ER</c>/<c>EXIT</c> can still be honored, and if not, what the pilot says.
    /// Committing a route to the navigator is not the same thing as turning: the route's first segment is a
    /// virtual straight down the runway centerline, so the aircraft can be committed and still have the whole
    /// runway to run. This is the shared verdict — <see cref="Commands.GroundCommandHandler"/> asks it at
    /// command time so the controller gets immediate feedback, and <see cref="TryRetargetCommittedExit"/>
    /// re-asks it at tick time from the aircraft's updated position.
    /// </summary>
    public ExitRetargetVerdict EvaluateRetarget(AircraftState aircraft, ExitPreference newPreference)
    {
        if (_state != ExitState.FollowingExitPath)
        {
            // Still tracking the centerline with no route handed over — OnTick's own preference re-check
            // picks the change up, no gate needed.
            return new ExitRetargetVerdict(true, null);
        }

        if ((_exitTaxiway is null) || (_exitPath is not { Count: > 0 }))
        {
            // FollowingExitPath with nothing resolved behind it — a restore whose stored nodes no longer
            // exist. There is no turn to protect, and refusing here would assert one that isn't happening.
            return new ExitRetargetVerdict(true, null);
        }

        if (_turnStarted || IsInsideTurnLead(aircraft))
        {
            // Names the exit the aircraft will actually take: the controller's next crossing decision
            // depends on knowing where this arrival turns off (7110.65 §3-7-2.a.7.b.2).
            return new ExitRetargetVerdict(false, $"Unable, already turning off at {_exitTaxiway}");
        }

        if (aircraft.Ground.Layout is not { } layout)
        {
            // No layout to reason about. Never refuse because of missing data.
            return new ExitRetargetVerdict(true, null);
        }

        // No occupancy set outside the tick loop; this probe only answers "is that exit still reachable",
        // and the tick-time search re-runs it with live occupancy before anything is torn down.
        if (RunRetargetSearch(layout, aircraft, newPreference, occupied: null) is not null)
        {
            return new ExitRetargetVerdict(true, null);
        }

        // "no X ahead" rather than "X is behind us": the search returns nothing both when the taxiway was
        // passed and when it isn't on this runway at all, and only the first would justify "behind".
        string what =
            newPreference.Taxiway is { } taxiway ? $"no {taxiway} ahead"
            : newPreference.Side == ExitSide.Left ? "no left exit ahead"
            : "no right exit ahead";
        return new ExitRetargetVerdict(false, $"Unable, {what}");
    }

    /// <summary>
    /// True when the aircraft is close enough to the branch point that the turn-off is effectively begun:
    /// less than <see cref="RetargetLeadSeconds"/> of travel away, floored at <see cref="MinRetargetLeadFt"/>,
    /// plus the corner-rounding radius (the navigator starts its arc a tangent length <em>before</em> the
    /// branch vertex).
    /// </summary>
    private bool IsInsideTurnLead(AircraftState aircraft)
    {
        if (_exitPath is not { Count: > 0 })
        {
            return true;
        }

        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        return (GeoMath.AlongTrackDistanceNm(_exitPath[0].Position, aircraft.Position, _runwayHeading) * GeoMath.FeetPerNm)
            <= TurnLeadDistanceFt(aircraft, category);
    }

    private static double TurnLeadDistanceFt(AircraftState aircraft, AircraftCategory category)
    {
        double groundSpeedFtPerSec = aircraft.GroundSpeed * GeoMath.FeetPerNm / 3600.0;
        return Math.Max(RetargetLeadSeconds * groundSpeedFtPerSec, MinRetargetLeadFt) + CategoryPerformance.NoseWheelTurnRadiusFt(category);
    }

    /// <summary>
    /// Resolve the exit a re-target would land on: the preference is honored exactly, with no relaxation —
    /// tearing down a perfectly good committed exit for a taxiway that isn't ahead is worse than refusing —
    /// and only candidates the aircraft could still turn off at are eligible. Returns null when the new
    /// preference cannot be satisfied ahead.
    /// </summary>
    private AirportGroundLayout.CenterlineExitResult? RunRetargetSearch(
        AirportGroundLayout layout,
        AircraftState aircraft,
        ExitPreference preference,
        HashSet<int>? occupied
    )
    {
        if (_runwayId is null)
        {
            return null;
        }

        var searchPref =
            (preference.Side is null) && (_inferredSide is not null)
                ? new ExitPreference { Taxiway = preference.Taxiway, Side = _inferredSide.Value }
                : preference;

        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        double leadFt = TurnLeadDistanceFt(aircraft, category);

        // A re-target is always an explicit instruction, so the pilot will brake firmly for it — or at the
        // max-effort rate when the exit was ordered without delay. Mirrors LandingPhase.BrakingLimit's
        // explicit-exit branch.
        double brakingLimit = aircraft.Ground.IsExpeditingExit
            ? CategoryPerformance.ExpediteExitDecelRate(category)
            : RolloutBraking.FirmBrakingRateKtsPerSec;

        return layout.FindOnSidePreferredExit(
            aircraft.Position.Lat,
            aircraft.Position.Lon,
            _runwayHeading,
            _runwayId,
            searchPref,
            searchPref.Side ?? _inferredSide,
            excludeBranchPoints: null,
            excludeHoldShortNodes: occupied,
            filter: candidate =>
            {
                double distToBranchNm = GeoMath.AlongTrackDistanceNm(candidate.Path[0].Position, aircraft.Position, _runwayHeading);

                // Far enough ahead that the pilot has time to take the instruction...
                if ((distToBranchNm * GeoMath.FeetPerNm) <= leadFt)
                {
                    return AirportGroundLayout.CandidateVerdict.Skip;
                }

                // ...and close enough to the aircraft's energy state to actually make. Without this the lead
                // above would be silently doubling as the braking floor, and re-targeting a jet still at
                // coast speed could send it to an exit it can only arrive at hot.
                double turnOffSpeed = CategoryPerformance.ExitTurnOffSpeed(category, candidate.ExitAngle);
                bool alreadySlowEnough = aircraft.GroundSpeed <= turnOffSpeed + RolloutBraking.TurnOffSpeedToleranceKts;
                if (!alreadySlowEnough && (RolloutBraking.RequiredDecelKtsPerSec(aircraft.GroundSpeed, turnOffSpeed, distToBranchNm) > brakingLimit))
                {
                    return AirportGroundLayout.CandidateVerdict.Skip;
                }

                return AirportGroundLayout.CandidateVerdict.Accept;
            }
        );
    }

    /// <summary>
    /// The occupancy set for a re-target search, minus this aircraft's own claim.
    /// <c>SimulationEngine.BuildOccupiedHoldShortNodes</c> adds every <see cref="TargetHoldShortNodeId"/>,
    /// including ours, so without subtracting it a re-search could never re-select the exit we already hold —
    /// an <c>EL</c> on an aircraft already exiting left would needlessly skip to the next taxiway.
    /// </summary>
    private HashSet<int>? RetargetOccupancyExcludingSelf(PhaseContext ctx)
    {
        if (ctx.OccupiedHoldShortNodes is not { Count: > 0 } occupied)
        {
            return null;
        }

        var filtered = new HashSet<int>(occupied);
        if (_holdShortNode is not null)
        {
            filtered.Remove(_holdShortNode.Id);
        }

        return filtered;
    }

    /// <summary>
    /// Honor an exit change that arrived after the route was handed to the navigator but before the turn-off
    /// began. Returns true when the phase has been re-pointed at a different exit and the caller should
    /// re-commit; false to carry on to the exit already being followed.
    /// </summary>
    private bool TryRetargetCommittedExit(PhaseContext ctx)
    {
        var newPreference = ctx.Aircraft.Phases?.RequestedExit;
        if (ReferenceEquals(newPreference, _committedPreference) || (newPreference is null))
        {
            return false;
        }

        // Considered — whatever the outcome, don't re-evaluate this same instruction every tick.
        _committedPreference = newPreference;

        var verdict = EvaluateRetarget(ctx.Aircraft, newPreference);
        if (!verdict.Allowed)
        {
            // The command handler already refused this, so reaching here means the aircraft crossed into the
            // turn between dispatch and this tick. Keep the committed exit and the resolution behind it.
            Log.LogDebug("[Exit] {Callsign}: late exit change not honored — {Reason}", ctx.Aircraft.Callsign, verdict.UnableReason);
            return false;
        }

        // The instruction stands even when it resolves to the exit already being taken, so a later fallback
        // search uses the controller's current intent rather than the superseded one.
        _lastResolvedPreference = newPreference;

        if (ctx.GroundLayout is not { } layout)
        {
            return false;
        }

        var found = RunRetargetSearch(layout, ctx.Aircraft, newPreference, RetargetOccupancyExcludingSelf(ctx));
        if ((found is null) || (found.Value.HoldShort.Id == _holdShortNode?.Id))
        {
            return false;
        }

        Log.LogDebug(
            "[Exit] {Callsign}: re-targeting from {Old} to {New} before the turn-off",
            ctx.Aircraft.Callsign,
            _exitTaxiway,
            found.Value.Taxiway
        );

        // Drop the navigator and its route, then load the new exit. The aircraft is still on the runway
        // centerline at runway heading, so the commit block re-runs StartExitNavigation from here exactly
        // as it would for a first commit.
        _navigator = null;
        _exitRoute = null;
        _turnStarted = false;
        _state = ExitState.RollingOnCenterline;
        _holdShortNode = found.Value.HoldShort;
        _exitTaxiway = found.Value.Taxiway;
        _exitPath = found.Value.Path;
        return true;
    }

    /// <summary>
    /// Segment the exit route rebuilt after a snapshot restore should resume on: the segment the live route was
    /// on, floored at 1 whenever the aircraft has already rolled past the branch node.
    ///
    /// <para>
    /// The floor covers a snapshot that landed on the tick before <c>GroundNavigator</c> signalled arrival at the
    /// branch. Past the branch the aircraft is on the exit taxiway by definition, so segment 0 — the virtual
    /// approach leg down the runway — is behind it either way, and resuming there would aim the navigator back at
    /// a node it has already crossed.
    /// </para>
    /// </summary>
    private int ResumeSegmentIndexAfterRestore(PhaseContext ctx)
    {
        if (_exitPath is null || _exitPath.Count == 0)
        {
            return _restoreSegmentIndex;
        }

        bool pastBranch = GeoMath.AlongTrackDistanceNm(ctx.Aircraft.Position, _exitPath[0].Position, _runwayHeading) > 0;
        return Math.Max(_restoreSegmentIndex, pastBranch ? 1 : 0);
    }

    /// <summary>
    /// Build a TaxiRoute from the exit path with a virtual approach segment
    /// from the aircraft's current position to the branch node, and start the
    /// GroundNavigator. The virtual segment gives the navigator inbound bearing
    /// context so it can anticipate the turn at the branch node.
    /// </summary>
    /// <param name="ctx">Phase context.</param>
    /// <param name="resumeSegmentIndex">
    /// Route segment to resume on. Zero for a first commit or a re-target, both of which start the aircraft on the
    /// virtual approach leg down the runway. Non-zero only on the tick that rebuilds the route after a snapshot
    /// restore, where it is the segment the live route was on.
    /// </param>
    private bool StartExitNavigation(PhaseContext ctx, int resumeSegmentIndex)
    {
        if (_exitPath is null || _exitPath.Count < 2 || _exitTaxiway is null || ctx.GroundLayout is null)
        {
            Log.LogWarning("[Exit] {Callsign}: cannot build exit route", ctx.Aircraft.Callsign);
            return false;
        }

        var segments = new List<TaxiRouteSegment>();
        var branchNode = _exitPath[0];

        // Virtual approach segment: [aircraft position → branch node].
        // Always added — gives the navigator inbound bearing context for turn
        // anticipation at the branch node, whether the aircraft is far away
        // (analog search) or right at the branch (committed exit from LandingPhase).
        //
        // Resuming past it, the aircraft's position is no longer a valid anchor: the leg would run backward, and
        // GroundNavigator reads its arrival bearing as the corner's incoming tangent, which is what turned a
        // restored aircraft around and taxied it back onto the runway. Anchor it on the centerline behind the
        // branch instead, reproducing the geometry the live route had.
        var approachFrom =
            resumeSegmentIndex > 0
                ? GeoMath.ProjectPoint(branchNode.Position, _runwayHeading.ToReciprocal(), RestoredApproachSegmentNm)
                : ctx.Aircraft.Position;
        var virtualFromNode = VirtualNode.Create(approachFrom.Lat, approachFrom.Lon);
        double distToBranch = GeoMath.DistanceNm(approachFrom, branchNode.Position);
        var approachEdge = new GroundEdge
        {
            Nodes = [virtualFromNode, branchNode],
            TaxiwayName = $"RWY{_runwayId}",
            DistanceNm = Math.Max(distToBranch, 0.001),
        };
        segments.Add(new TaxiRouteSegment { TaxiwayName = _exitTaxiway, Edge = approachEdge.Directed(virtualFromNode, branchNode) });

        for (int i = 0; i < _exitPath.Count - 1; i++)
        {
            var fromNode = _exitPath[i];
            var toNode = _exitPath[i + 1];
            var edge = FindEdgeBetween(fromNode, toNode.Id);
            if (edge is null)
            {
                Log.LogWarning("[Exit] {Callsign}: no edge between nodes {From} and {To}", ctx.Aircraft.Callsign, fromNode.Id, toNode.Id);
                return false;
            }

            segments.Add(new TaxiRouteSegment { TaxiwayName = _exitTaxiway, Edge = edge.Directed(fromNode, toNode) });
        }

        // Append a virtual segment past the hold-short node so the aircraft's tail
        // clears the hold-short line. The virtual node is offset along the graph edge.
        var holdShortNode = _exitPath[^1];
        double lengthFt = FaaAircraftDatabase.Get(ctx.Aircraft.AircraftType)?.LengthFt ?? 60.0;
        double halfLengthNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;

        GroundNode virtualTarget;
        if (_exitPath.Count >= 2)
        {
            virtualTarget = VirtualNode.OffsetPast(ctx.GroundLayout!, holdShortNode, _exitPath[^2], halfLengthNm);
        }
        else
        {
            virtualTarget = VirtualNode.OffsetPast(
                ctx.GroundLayout!,
                holdShortNode,
                ctx.Aircraft.Position.Lat,
                ctx.Aircraft.Position.Lon,
                halfLengthNm
            );
        }

        segments.Add(VirtualNode.CreateSegment(holdShortNode, virtualTarget, _exitTaxiway));

        // Never resume onto the past-the-end index: that reads as "route complete" and would end the phase without
        // CompleteExit's cleanup. An aircraft that really is at the last node completes on the coming tick anyway.
        _exitRoute = new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = [],
            CurrentSegmentIndex = Math.Min(resumeSegmentIndex, segments.Count - 1),
        };

        // Cap the exit maneuver at normal taxiway speed, not the runway-rollout coast speed: once the
        // aircraft turns off onto the exit taxiway it is taxiing, so it should not accelerate past taxi
        // speed toward coast on the straight to the hold-short and then brake hard — the
        // slow-turn-then-surge profile. The turn itself is governed by the junction arc's
        // MaxSafeSpeedKts (a rapid exit is taken at its design speed). Mirror TaxiingPhase's expedite
        // bump so an EXP exit still taxis briskly to the hold-short.
        double taxiCeiling =
            CategoryPerformance.TaxiSpeed(ctx.Category) * (ctx.Aircraft.Ground.IsExpeditingExit ? CategoryPerformance.TaxiExpediteMultiplier : 1.0);
        double maxSpeed = Math.Min(_coastSpeed, taxiCeiling);

        _navigator = new GroundNavigator();
        _navigator.MaxSpeedKts = maxSpeed;
        if (ctx.Aircraft.Ground.IsExpeditingExit)
        {
            // Brake firmly to the hold-short stop after the turn-off. Corner-speed
            // caps still govern the turn itself, so a high-speed exit keeps its speed.
            _navigator.DecelRateKts = CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
        }

        _navigator.SetupSegment(_exitRoute, ctx, _ => true);

        // TickRolling holds the runway heading through the persistent ControlTargets
        // (TargetTrueHeading + TurnRateOverride). From here the navigator owns steering and
        // writes TrueHeading directly — drop the heading hold, or FlightPhysics keeps turning
        // the aircraft back toward the runway heading every substep, fighting the navigator's
        // exit turn to a standstill (a pure-pursuit "orbit" at full ground-turn rate).
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.TurnRateOverride = null;

        _state = ExitState.FollowingExitPath;
        _committedPreference = ctx.Aircraft.Phases?.RequestedExit;
        ctx.Aircraft.Ground.CurrentTaxiway = _exitTaxiway;

        Log.LogDebug(
            "[Exit] {Callsign}: following exit path, {SegCount} segments on {Twy}, maxSpeed={Speed:F0}kts, path=[virtual→{Path}]",
            ctx.Aircraft.Callsign,
            segments.Count,
            _exitTaxiway,
            maxSpeed,
            string.Join("→", _exitPath.Select(n => n.Id))
        );
        return true;
    }

    private bool TickFollowingExitPath(PhaseContext ctx)
    {
        if (_exitRoute is null || _navigator is null)
        {
            return true;
        }

        // Segment 0 is the virtual approach leg down the runway centerline to the branch node; leaving it
        // means the aircraft is at the turn. The heading test catches the navigator's pre-turn blend, which
        // starts rotating the nose inside the last ~50 ft while still nominally on segment 0.
        if ((_exitRoute.CurrentSegmentIndex > 0) || (_runwayHeading.AbsAngleTo(ctx.Aircraft.TrueHeading) > RetargetMaxHeadingDeviationDeg))
        {
            _turnStarted = true;
        }

        bool isLastSegment = _exitRoute.CurrentSegmentIndex + 1 >= _exitRoute.Segments.Count;
        var result = _navigator.Tick(ctx, isLastSegment, _ => true);

        if (result == NavigatorResult.ArrivedAtNode)
        {
            if (_exitRoute.CurrentSegment is { } seg)
            {
                ctx.Aircraft.Ground.CurrentTaxiway = seg.TaxiwayName;
            }

            _exitRoute.CurrentSegmentIndex += 1;
            if (_exitRoute.CurrentSegmentIndex > _exitRoute.Segments.Count)
            {
                _exitRoute.CurrentSegmentIndex = _exitRoute.Segments.Count;
            }

            if (_exitRoute.IsComplete)
            {
                return CompleteExit(ctx);
            }

            _navigator.SetupSegment(_exitRoute, ctx, _ => true);
        }

        return false;
    }

    private bool CompleteExit(PhaseContext ctx)
    {
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;

        // The runway exit is done — drop the expedite flag so it doesn't bleed
        // into a subsequent taxi (which has its own EXP).
        ctx.Aircraft.Ground.IsExpeditingExit = false;

        // No position snap — the GroundNavigator already brakes to 0 at the
        // final node (FinalNodeArrivalThresholdNm ≈ 1.8ft). The aircraft is
        // close enough; teleporting to exact node coords causes overlap when
        // another aircraft is already there.

        // Vacated between two parallels with a clear shot to the parallel runway's hold-short:
        // auto-pull-up there (and hold short pending an explicit CROSS) instead of stopping at
        // the landing runway's exit hold-short.
        if (TryStartParallelCrossing(ctx))
        {
            return true;
        }

        // Mark this hold-short as occupied so same-tick aircraft see it
        if (_holdShortNode is not null)
        {
            ctx.MarkHoldShortNodeOccupied?.Invoke(_holdShortNode.Id);
        }

        // Insert HoldingAfterExitPhase
        ctx.Aircraft.Phases?.InsertAfterCurrent(new HoldingAfterExitPhase(_runwayId, _exitTaxiway, _holdShortNode?.Id));

        Log.LogDebug(
            "[Exit] {Callsign}: exit complete on {Twy}, holding at ({Lat:F6},{Lon:F6}), hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            _exitTaxiway,
            ctx.Aircraft.Position.Lat,
            ctx.Aircraft.Position.Lon,
            ctx.Aircraft.TrueHeading.Degrees
        );

        return true;
    }

    /// <summary>
    /// When the aircraft vacated between two parallel runways and the parallel runway's hold-short
    /// is reachable on the same exit taxiway with no intervening taxiway intersection (issue #175),
    /// build a taxi route that pulls up to and holds short of the parallel — and continues across it
    /// once an explicit CROSS clears the hold-short — then hand off to a <see cref="TaxiingPhase"/>.
    /// Returns true when the auto-pull-up was started; false to fall through to the normal
    /// hold-after-exit behavior. Gated on the <c>AutoPullUpToParallel</c> scenario setting.
    /// </summary>
    private bool TryStartParallelCrossing(PhaseContext ctx)
    {
        if (
            !ctx.AutoPullUpToParallel
            || ctx.GroundLayout is null
            || _holdShortNode is null
            || _exitTaxiway is null
            || _runwayId is null
            || _exitPath is null
            || _exitPath.Count < 2
        )
        {
            return false;
        }

        var comeFromNode = _exitPath[^2];
        var crossing = ctx.GroundLayout.FindParallelRunwayCrossing(_holdShortNode, comeFromNode, _exitTaxiway, _runwayId);
        if (crossing is not { } xing)
        {
            return false;
        }

        // Combined node path: pull-up (landingHS → parallel near HS) then crossing (near → far HS).
        var fullPath = new List<GroundNode>(xing.PullUpPath);
        fullPath.AddRange(xing.CrossingPath.Skip(1));

        var segments = BuildRouteSegments(fullPath);
        if (segments is null)
        {
            Log.LogWarning("[Exit] {Callsign}: could not build parallel-crossing route, holding after exit instead", ctx.Aircraft.Callsign);
            return false;
        }

        var holdShorts = new List<HoldShortPoint>();
        HoldShortAnnotator.AddImplicitRunwayHoldShorts(ctx.GroundLayout, segments, holdShorts);

        var route = new TaxiRoute { Segments = segments, HoldShortPoints = holdShorts };
        double lengthFt =
            FaaAircraftDatabase.Get(ctx.Aircraft.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(ctx.Aircraft.AircraftType);
        HoldShortAnnotator.ComputeHoldShortPositions(ctx.GroundLayout, route, lengthFt);

        ctx.Aircraft.Ground.AssignedTaxiRoute = route;
        ctx.Aircraft.Ground.CurrentTaxiway = _exitTaxiway;

        // Pilot reports clear of the landing runway as it pulls up toward the parallel.
        var clearText = Pilot.PilotResponder.BuildClearOfRunwayText(ctx.Aircraft, _runwayId, _exitTaxiway);
        Pilot.PilotResponder.RouteSoloOrRpoTransmission(
            ctx.Aircraft,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            ctx.StudentPositionType,
            clearText,
            Pilot.PilotResponder.SoloPositionsTower
        );

        ctx.Aircraft.Phases?.InsertAfterCurrent(new TaxiingPhase());

        Log.LogDebug(
            "[Exit] {Callsign}: auto pull-up between parallels — {Land} exit → hold short {Parallel} via {Twy}, route=[{Path}]",
            ctx.Aircraft.Callsign,
            _runwayId,
            xing.ParallelRunwayId,
            _exitTaxiway,
            string.Join("→", fullPath.Select(n => n.Id))
        );

        return true;
    }

    /// <summary>
    /// Build directional taxi-route segments along the exit taxiway for a node path of real,
    /// adjacency-connected graph nodes. Returns null if any consecutive pair lacks a connecting edge.
    /// </summary>
    private List<TaxiRouteSegment>? BuildRouteSegments(List<GroundNode> path)
    {
        var segments = new List<TaxiRouteSegment>(path.Count - 1);
        for (int i = 0; i < path.Count - 1; i++)
        {
            var edge = FindEdgeBetween(path[i], path[i + 1].Id);
            if (edge is null)
            {
                return null;
            }

            segments.Add(new TaxiRouteSegment { TaxiwayName = _exitTaxiway!, Edge = edge.Directed(path[i], path[i + 1]) });
        }

        return segments;
    }

    private static IGroundEdge? FindEdgeBetween(GroundNode fromNode, int toNodeId)
    {
        foreach (var edge in fromNode.Edges)
        {
            if (edge.OtherNodeId(fromNode.Id) == toNodeId)
            {
                return edge;
            }
        }
        return null;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        Log.LogDebug("[Exit] {Callsign}: OnEnd ({Status}), taxiway={Twy}", ctx.Aircraft.Callsign, endStatus, _exitTaxiway ?? "none");
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ExitLeft => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitRight => CommandAcceptance.Allowed,
            CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is rolling out / exiting the runway; only EL/ER/EXIT or a new TAXI apply"),
        };
    }

    public override PhaseDto ToSnapshot() =>
        new RunwayExitPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            ExitNodeId = _holdShortNode?.Id,
            ReachedExitNode = _state == ExitState.FollowingExitPath,
            ExitTaxiway = _exitTaxiway,
            RunwayId = _runwayId,
            LastResolvedPreference = (int?)_lastResolvedPreference?.Side,
            LastResolvedPreferenceTaxiway = _lastResolvedPreference?.Taxiway,
            ExitWaypointNodeIds = _exitPath?.Select(n => n.Id).ToList(),
            // Falls back to the restored index, not 0: a snapshot round-tripped before the first tick after a
            // restore has no route yet, and writing 0 there would lose the resume point all over again.
            ExitWaypointIndex = _exitRoute?.CurrentSegmentIndex ?? _restoreSegmentIndex,
            ExitSpeed = _coastSpeed,
            TimeSinceLastLog = _timeSinceLastLog,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            ExitStateValue = (int)_state,
            TurnStarted = _turnStarted,
            Navigator = _navigator?.ToSnapshot(),
        };

    public static RunwayExitPhase FromSnapshot(RunwayExitPhaseDto dto, AirportGroundLayout? groundLayout)
    {
        var phase = new RunwayExitPhase();
        phase._exitTaxiway = dto.ExitTaxiway;
        phase._runwayId = dto.RunwayId;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._state = (ExitState)dto.ExitStateValue;
        phase._lastResolvedPreference = dto.LastResolvedPreference.HasValue
            ? new ExitPreference { Side = (ExitSide)dto.LastResolvedPreference.Value, Taxiway = dto.LastResolvedPreferenceTaxiway }
            : null;
        phase._coastSpeed = dto.ExitSpeed;
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase._turnStarted = dto.TurnStarted;
        phase._restoreSegmentIndex = Math.Max(dto.ExitWaypointIndex, 0);
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);

        if (groundLayout is not null)
        {
            if (dto.ExitNodeId.HasValue)
            {
                phase._holdShortNode = groundLayout.Nodes.GetValueOrDefault(dto.ExitNodeId.Value);
            }
            if (dto.ExitWaypointNodeIds is not null)
            {
                var path = new List<GroundNode>();
                foreach (int id in dto.ExitWaypointNodeIds)
                {
                    if (groundLayout.Nodes.TryGetValue(id, out var n))
                    {
                        path.Add(n);
                    }
                }
                if (path.Count > 0)
                {
                    phase._exitPath = path;
                }
            }
            if (dto.Navigator is not null)
            {
                phase._navigator = GroundNavigator.FromSnapshot(dto.Navigator);
            }
        }

        return phase;
    }
}
