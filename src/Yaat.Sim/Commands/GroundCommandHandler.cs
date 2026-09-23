using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands.Arguments;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

/// <summary>
/// Handles the ground commands — taxi, pushback and tug moves, hold short, crossings. Public only so the client's
/// ground-view push-route preview can resolve its target tokens through <see cref="ResolveTugGoal"/>, the same body
/// the commands themselves use; every other member stays internal to the simulation.
/// </summary>
public static class GroundCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("GroundCommandHandler");

    // A node-reference path pins one segment per drawn node. Allow generous slack for the start
    // bridge, the parking/runway extension, and hold-short splits, then treat anything beyond as a
    // failed resolution rather than a clearance. The reported 48-node route resolved to 544 segments.
    private const int NodeRefSegmentSlack = 20;
    private const int NodeRefSegmentFactor = 2;

    // A gate's first cleared taxiway may be a neighbouring ramp lane the ground graph does not connect to
    // the gate (SFO B20S → M4: M4 only joins M1, but its nearest node is 404 ft away across the M3 lane —
    // exactly where a tug would push the aircraft; 7110.65 §3-7-2 NOTE 2 puts that ramp movement on the
    // pilot/operator, not ATC). TryTaxi drops such a lead-out lane and warns instead of rejecting the whole
    // clearance, but only within this range and only when no runway centerline lies between the gate and the
    // lane: a taxiway across the field (SFO 41-15 → A, 3404 ft, across active runways) stays a hard
    // rejection. Ramp-alley scale: B4 → M3 is 206 ft; the next real taxiway (B20S → M1) is 563 ft, so the
    // radius is kept well short of it.
    private const double GateAdjacentTaxiwayMaxFt = 450.0;

    /// <summary>Smallest turn between two consecutive segments of a held-short leg that counts as a reversal.</summary>
    private const double HeldShortLegReversalDeg = 150.0;

    /// <summary>
    /// A controller-typed or scenario-preset <c>TAXI</c>. A bare runway destination with no taxiways named
    /// (<c>TAXI 1L</c>) is honoured only when the aircraft is already at that runway's hold-short — see
    /// <see cref="TaxiPathfinder.FindAdjacentRunwayRoute"/>; <see cref="TryTaxiAuto"/> is the explicit auto-route.
    /// </summary>
    internal static CommandResult TryTaxi(
        AircraftState aircraft,
        TaxiCommand taxi,
        AirportGroundLayout? groundLayout,
        bool autoCrossRunway = false
    ) => TryTaxi(aircraft, taxi, groundLayout, autoCrossRunway, listAircraft: null);

    /// <summary>
    /// A <c>TAXI</c> dispatched with the world's aircraft in view: a spot line-up from the ramp
    /// (<see cref="RampLaneReposition.TryPlanSpotLineUp"/>) is planned clear of every other aircraft on the ground.
    /// </summary>
    internal static CommandResult TryTaxi(
        AircraftState aircraft,
        TaxiCommand taxi,
        AirportGroundLayout? groundLayout,
        bool autoCrossRunway,
        Func<IReadOnlyList<AircraftState>>? listAircraft
    ) => TryTaxiCore(aircraft, taxi, groundLayout, new TaxiCoreOptions(autoCrossRunway, AllowRemoteRunwayAutoRoute: false, listAircraft));

    /// <summary>The dispatch-level switches a TAXI or TAXIAUTO resolves under.</summary>
    /// <param name="AutoCrossRunway">The scenario pre-clears runway crossings.</param>
    /// <param name="AllowRemoteRunwayAutoRoute">A bare runway destination may be auto-routed from afar (TAXIAUTO).</param>
    /// <param name="ListAircraft">Every aircraft in the world, or null when the caller has no world.</param>
    private sealed record TaxiCoreOptions(bool AutoCrossRunway, bool AllowRemoteRunwayAutoRoute, Func<IReadOnlyList<AircraftState>>? ListAircraft);

    /// <summary>What <see cref="ApplySpotLineUp"/> needs beyond the aircraft, the layout and the resolved route.</summary>
    /// <param name="Category">The aircraft's performance category.</param>
    /// <param name="AircraftLengthFt">Its fuselage length, feet.</param>
    /// <param name="ClearedPath">The taxiways the clearance named, as the controller worded it.</param>
    /// <param name="ListAircraft">Every aircraft in the world, or null when the caller has no world.</param>
    private sealed record SpotLineUpInputs(
        AircraftCategory Category,
        double AircraftLengthFt,
        IReadOnlyList<string> ClearedPath,
        Func<IReadOnlyList<AircraftState>>? ListAircraft
    );

    /// <summary>The command a TAXI clearance resolved from, its route, and the failure when no route resolved.</summary>
    private sealed record StartResolution(TaxiCommand Command, TaxiRoute? Route, PathfindingFailure? Failure);

    /// <summary>Resolves one candidate command to a route, or to null with the failure.</summary>
    private delegate TaxiRoute? TaxiRouteResolver(TaxiCommand command, out PathfindingFailure? failure);

    private static CommandResult TryTaxiCore(AircraftState aircraft, TaxiCommand taxi, AirportGroundLayout? groundLayout, TaxiCoreOptions options)
    {
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "Taxi requires aircraft on the ground");
        }

        if (groundLayout is null)
        {
            Log.LogWarning(
                "[TryTaxi] {Callsign}: no ground layout (departure={Dep}, destination={Dest})",
                aircraft.Callsign,
                aircraft.FlightPlan.Departure,
                aircraft.FlightPlan.Destination
            );
            return new CommandResult(false, "No airport ground layout available");
        }

        // The clearance as the controller worded it. When a cleared taxiway has to be dropped below, the
        // pilot's readback is built from this minus the dropped lane — not from the internally folded /
        // prepended working copy — so the solo student hears "unable M4, taxi via M1 …" and nothing else.
        TaxiCommand asCleared = taxi;
        TaxiCommand? effectiveCommand = null;

        // A $spot inside the path is a via — the route has to pass through that spot's node on its way to the
        // destination. The resolver already understands a #nodeId token, so the via is rewritten into one here,
        // the one place the clearance first meets the layout. The readback still names the lanes, because it is
        // built from the resolved route's segments and from asCleared above.
        (TaxiCommand withSpotVias, string? unknownSpot) = ResolveSpotVias(groundLayout, taxi);
        if (unknownSpot is not null)
        {
            return new CommandResult(false, $"Unknown spot {unknownSpot}");
        }

        taxi = withSpotVias;

        // A taxiway named only as a hold-short target ("... HS E") can also be a directional hint.
        // With a destination the clearance is resolved as cleared first and the hint is folded into
        // the path only when that route cannot honor it (see ResolveRoute below — issue #395: SFO
        // "TAXI T7A A A1 1R HS H" crosses H on A and must not detour back to H after A1). Without a
        // destination the hint is the only direction cue, so it is folded up front.
        List<HoldShortTarget> foldTargets = HoldShortTaxiwaysToFold(groundLayout, taxi);
        if ((foldTargets.Count > 0) && !HasDestination(taxi))
        {
            taxi = AugmentPathWithHoldShortTaxiways(taxi, foldTargets);
            foldTargets = [];
        }

        // Find starting node. Prefer the heading-aligned endpoint of the
        // nearest taxi edge (handles post-pushback poses where the aircraft
        // rests between graph nodes — see issue #161); fall back to the
        // absolute nearest node when the aircraft is genuinely off-graph.
        GroundNode? startNode =
            groundLayout.FindNearestNodeForTaxi(aircraft.Position, aircraft.TrueHeading) ?? groundLayout.FindNearestNode(aircraft.Position);

        // Anchor the start node to the first cleared taxiway when the aircraft is sitting on a node
        // of it. The heading-biased FindNearestNodeForTaxi can land on an adjacent parallel taxiway
        // after a directional pushback (the aircraft's heading aligns with the neighbour's edge, not
        // its own taxiway's), which then makes the named first taxiway "unreachable" — WJA1521 pushed
        // onto M4 but the start node resolved onto the parallel M5, so "TAXI M4 M2 ..." was rejected.
        // Only overrides when the on-taxiway node is at least as close as the heuristic's pick.
        if (
            startNode is not null
            && taxi.Path.Count > 0
            && !taxi.Path[0].StartsWith('#')
            && !startNode.Edges.Any(e => e.MatchesTaxiway(taxi.Path[0]))
            && groundLayout.FindNearestNodeOnTaxiway(aircraft.Position, taxi.Path[0], maxDistFt: 100.0) is { } onFirstCleared
            && GeoMath.DistanceNm(aircraft.Position, onFirstCleared.Position) <= GeoMath.DistanceNm(aircraft.Position, startNode.Position)
        )
        {
            Log.LogDebug(
                "[TryTaxi] {Callsign}: start node {Old} is not on cleared {Twy}; anchoring to nearer on-taxiway node {New}",
                aircraft.Callsign,
                startNode.Id,
                taxi.Path[0],
                onFirstCleared.Id
            );
            startNode = onFirstCleared;
        }

        if (startNode is null)
        {
            Log.LogWarning(
                "[TryTaxi] {Callsign}: no nearest node at ({Lat:F6}, {Lon:F6})",
                aircraft.Callsign,
                aircraft.Position.Lat,
                aircraft.Position.Lon
            );
            return new CommandResult(false, "Cannot find position on taxiway graph");
        }

        // A drawn route's leading nodes go stale while the controller is drawing — see the helper.
        taxi = TrimPassedNodeRefPrefix(aircraft, startNode, taxi);
        if ((taxi.Path.Count == 0) && (taxi.DestinationRunway is null) && (taxi.DestinationParking is null) && (taxi.DestinationSpot is null))
        {
            return new CommandResult(false, $"{aircraft.Callsign} has already taxied past the whole route");
        }

        // Where the cleared path starts: the candidates ResolveFromBestStart tries (see the resolution below
        // ResolveRoute). The taxiway held short of is no candidate when the aircraft already stands on the first
        // cleared taxiway: holding short of K on B, "TAXI B T" continues along B across K.
        bool standsOnFirstCleared = (taxi.Path.Count > 0) && startNode.Edges.Any(e => e.MatchesTaxiway(taxi.Path[0]));
        TaxiCommand? heldShortPrepend =
            (!standsOnFirstCleared && (HeldShortTaxiway(aircraft) is { } heldShortTwy))
                ? PrependTaxiwayJoiningPath(groundLayout, taxi, heldShortTwy)
                : null;
        string? occupiedTaxiway = OccupiedTaxiway(aircraft, startNode, groundLayout);
        TaxiCommand? currentTaxiwayPrepend = CurrentTaxiwayPrepend(groundLayout, occupiedTaxiway, taxi);

        Log.LogDebug(
            "[TryTaxi] {Callsign}: nearest node {NodeId} ({NodeType}) at ({NLat:F6}, {NLon:F6}), dist={Dist:F4}nm, path=[{Path}], "
                + "destRwy={Rwy}, destParking={Pkg}, destSpot={Spot}",
            aircraft.Callsign,
            startNode.Id,
            startNode.Type,
            startNode.Position.Lat,
            startNode.Position.Lon,
            GeoMath.DistanceNm(aircraft.Position, startNode.Position),
            string.Join(" ", taxi.Path),
            taxi.DestinationRunway ?? "(none)",
            taxi.DestinationParking ?? "(none)",
            taxi.DestinationSpot ?? "(none)"
        );

        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);

        double startHeadingTrueDeg = aircraft.TrueHeading.Degrees;
        TaxiRoute? ResolveDirect(TaxiCommand command, out PathfindingFailure? routeFailure)
        {
            if (command.DestinationParking is not null || command.DestinationSpot is not null)
            {
                return ResolveParkingRoute(groundLayout, startNode, command, out routeFailure, category, startHeadingTrueDeg, occupiedTaxiway);
            }

            if ((command.Path.Count == 0) && (command.DestinationRunway is not null))
            {
                TaxiRoute? adjacent = ResolveAdjacentRunwayRoute(
                    groundLayout,
                    startNode,
                    aircraft,
                    command.DestinationRunway,
                    out string? adjacentReason
                );
                // TAXIAUTO at the bar has nothing to route either — the full-length auto-route would
                // otherwise return an empty fallback with no destination hold-short to hold at.
                if (!options.AllowRemoteRunwayAutoRoute || (adjacent is { Segments.Count: 0 }))
                {
                    routeFailure = adjacent is null ? DestinationFailure(adjacentReason ?? $"No route to runway {command.DestinationRunway}") : null;
                    return adjacent;
                }
            }

            return ResolveStandardRoute(groundLayout, startNode, command, out routeFailure, category, startHeadingTrueDeg, occupiedTaxiway);
        }

        // As-cleared first: the route the named taxiways produce on their own wins when it honors every
        // hold-short hint en route (see AsClearedRejectionReason). Only then is the hint's taxiway folded
        // into the path — the OAK "TAXI D C HS E RWY 28R" shape, where E is the way to the runway.
        TaxiRoute? ResolveRoute(TaxiCommand command, out PathfindingFailure? routeFailure)
        {
            if (foldTargets.Count == 0)
            {
                return ResolveDirect(command, out routeFailure);
            }

            TaxiRoute? asClearedRoute = ResolveDirect(command, out routeFailure);
            string? rejection = asClearedRoute is null
                ? routeFailure?.HumanMessage ?? "no route"
                : AsClearedRejectionReason(asClearedRoute, command, foldTargets);
            if (rejection is null)
            {
                return asClearedRoute;
            }

            Log.LogDebug(
                "[TryTaxi] {Callsign}: as-cleared route ({Segs} segments) does not honor the hold-short hint — {Reason}; folding [{Twys}] into the path",
                aircraft.Callsign,
                asClearedRoute?.Segments.Count ?? 0,
                rejection,
                string.Join(" ", foldTargets.Select(t => t.OnTaxiway ?? t.Target))
            );
            return ResolveDirect(AugmentPathWithHoldShortTaxiways(command, foldTargets), out routeFailure);
        }

        StartResolution start = ResolveFromBestStart(aircraft.Callsign, taxi, heldShortPrepend, currentTaxiwayPrepend, ResolveRoute);
        taxi = start.Command;
        TaxiRoute? route = start.Route;
        PathfindingFailure? failure = start.Failure;
        string? failReason = failure?.HumanMessage;

        // A parallel ramp lane the map does not connect (SFO M3 → M4): the pilot cuts across the apron onto it
        // and taxis the clearance as issued — from a gate or mid-lane. Only for sibling numbered lanes over
        // open apron; see RampLaneReposition.
        GroundNode? destinationNode = FindTaxiDestinationNode(groundLayout, taxi);
        double aircraftLengthFt =
            FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(aircraft.AircraftType);
        if (route is null && failure is not null && !AirportGroundLayout.HasRunwayCenterlineEdge(startNode))
        {
            RampLaneRepositionPlan? plan = RampLaneReposition.TryPlan(
                groundLayout,
                new RampLaneRepositionRequest
                {
                    Position = aircraft.Position,
                    Heading = aircraft.TrueHeading,
                    CurrentTaxiway = aircraft.Ground.CurrentTaxiway,
                    Path = taxi.Path,
                    Options = new ExplicitPathOptions
                    {
                        OccupiedTaxiway = occupiedTaxiway,
                        ExplicitHoldShorts = taxi.HoldShorts,
                        DestinationRunway = taxi.DestinationRunway,
                        DestinationHintNode = destinationNode,
                        PathTurnHints = taxi.PathTurnHints,
                    },
                    Category = category,
                },
                failure
            );
            if (plan is not null)
            {
                route = plan.Route;
                failure = null;
                failReason = null;
            }
        }

        // The mirror image at the far end (OAK "TAXI V T TE @22"): the clearance resolves along its lanes but the
        // last lane's ramp end does not join the stand's lane, so the pilot taxis it to the point nearest the stand
        // and cuts across the apron onto the stand's lane. Only for sibling ramp lanes over open apron.
        if (
            route is null
            && failure is { Kind: FailureKind.DestinationUnreachable, InfeasibleTaxiway: null }
            && destinationNode is { } cutDestination
        )
        {
            RampLaneDestinationCutPlan? cut = RampLaneReposition.TryPlanDestinationCut(
                groundLayout,
                new RampLaneDestinationCutRequest
                {
                    StartNodeId = startNode.Id,
                    Path = taxi.Path,
                    Destination = cutDestination,
                    Options = new ExplicitPathOptions
                    {
                        OccupiedTaxiway = occupiedTaxiway,
                        ExplicitHoldShorts = taxi.HoldShorts,
                        DestinationRunway = taxi.DestinationRunway,
                        PathTurnHints = taxi.PathTurnHints,
                        StartHeadingTrue = startHeadingTrueDeg,
                    },
                    Category = category,
                    AircraftLengthFt = aircraftLengthFt,
                }
            );
            if (cut is not null)
            {
                route = cut.Route;
                failure = null;
                failReason = null;
            }
        }

        // A route that resolved but only reaches the stand the long way round (SFO "TAXI $5A" from gate D2: 998 ft
        // down T5, out to Alpha and back up T5A for a 529 ft move) is flown as the apron cut instead. The two
        // blocks above only fire when the graph fails; this one improves a success, and only when the crossing is
        // drivable and materially shorter.
        if ((route is not null) && (destinationNode is { } resolvedDestination))
        {
            RampLaneDestinationCutPlan? improved = RampLaneReposition.TryPlanResolvedRouteCut(
                groundLayout,
                route,
                resolvedDestination,
                aircraftLengthFt
            );
            if (improved is not null)
            {
                route = improved.Route;
            }
        }

        // Two recoveries for a clearance that names pavement the aircraft cannot use as issued. Each drops
        // exactly one cleared taxiway, re-resolves, and records the as-applied command for the readback.
        if (route is null && startNode.Type == GroundNodeType.Parking)
        {
            DroppedTaxiwayRoute? leadOut = TryDropGateLeadOut(aircraft, groundLayout, taxi, failure, cmd => ResolveRoute(cmd, out _));
            if (leadOut is not null)
            {
                effectiveCommand = WithoutPathToken(asCleared, leadOut.DroppedName);
                taxi = leadOut.Command;
                route = leadOut.Route;
                failure = null;
                failReason = null;
            }
        }

        if (route is null)
        {
            DroppedTaxiwayRoute? via = TryDropContradictoryVia(aircraft, taxi, cmd => ResolveRoute(cmd, out _));
            if (via is not null)
            {
                effectiveCommand = WithoutPathToken(asCleared, via.DroppedName);
                taxi = via.Command;
                route = via.Route;
                failReason = null;
            }
        }

        // Last, a clearance whose taxiways do not join up: hold short of the taxiway the route needs, or refuse naming it.
        var startLink = new StartLinkInputs(aircraft.Callsign, groundLayout, startNode, taxi, occupiedTaxiway, category);
        MissingTaxiwayFallback fallback = ApplyMissingTaxiwayFallbacks(startLink, route, cmd => ResolveRoute(cmd, out _));
        route = fallback.Held ?? route;
        if (route is null)
        {
            Log.LogWarning("[TryTaxi] {Callsign}: route resolution failed — {Reason}", aircraft.Callsign, failReason ?? "no matching taxiways");
            return new CommandResult(false, UnresolvedTaxiMessage(fallback.Refusal, failReason, taxi));
        }

        // A route held short of a missing taxiway never reaches the spot, so there is nothing to line up on.
        route = ApplySpotLineUp(
            aircraft,
            groundLayout,
            route,
            fallback.Held is null ? destinationNode : null,
            new SpotLineUpInputs(category, aircraftLengthFt, asCleared.Path, options.ListAircraft)
        );

        // The resolver starts from the nearest graph node, which after a pushback onto open apron can be a
        // hundred feet from the aircraft. Drive it there rather than letting the navigator snap onto segment 0.
        route = TaxiApproachLeg.Prepend(groundLayout, aircraft.Position, aircraft.TrueHeading, route);

        // Compute dynamic hold-short positions based on aircraft fuselage length
        HoldShortAnnotator.ComputeHoldShortPositions(groundLayout, route, aircraftLengthFt);

        string hsDetails = string.Join(", ", route.HoldShortPoints.Select(h => $"{h.TargetName}@{h.NodeId}({h.Reason})"));
        Log.LogInformation(
            "[TryTaxi] {Callsign}: route resolved — {SegCount} segments, {HsCount} hold-shorts [{HsDetails}], summary: {Summary}",
            aircraft.Callsign,
            route.Segments.Count,
            route.HoldShortPoints.Count,
            hsDetails,
            // Same overload as the response below, so the log names an along-runway leg by the
            // commanded end ("on 33") instead of the travel-direction fallback ("on 15").
            route.ToSummary(BuildTurnHintMap(taxi), taxi.Path)
        );

        if (!IsPlausibleNodeRefResolution(groundLayout, taxi, route))
        {
            Log.LogWarning(
                "[TryTaxi] {Callsign}: rejecting node-reference route — {SegCount} segments for {NodeCount} drawn nodes: {Summary}",
                aircraft.Callsign,
                route.Segments.Count,
                taxi.Path.Count,
                route.ToSummary()
            );
            return new CommandResult(false, "Cannot resolve the drawn taxi route from the aircraft's current position");
        }

        // Implicit first-crossing clearance: when the aircraft is already holding short of a
        // runway — or is in the middle of landing on / exiting one (it must taxi past that
        // runway's hold-short bars to finish clearing the runway it just landed on, not hold
        // short of the runway it is leaving) — and the new route's first runway crossing is for
        // that same runway, the TAXI command itself authorizes it. No separate CTO needed.
        // Subsequent crossings still require explicit clearance. TryTaxi already rejected the
        // command unless the aircraft is on the ground, so a LandingPhase here is the rollout on
        // the runway just landed on — its exit bar is a crossing of that runway, not a hold-short.
        string? priorRwy = aircraft.Phases?.CurrentPhase switch
        {
            HoldingShortPhase priorHold when priorHold.HoldShort.TargetName is { Length: > 0 } heldRwy => heldRwy,
            RunwayExitPhase exitPhase when exitPhase.RunwayId is { Length: > 0 } exitRwy => exitRwy,
            HoldingAfterExitPhase afterExit when afterExit.RunwayId is { Length: > 0 } afterExitRwy => afterExitRwy,
            LandingPhase => aircraft.Phases?.ClearedRunwayId ?? aircraft.Phases?.AssignedRunway?.Designator,
            _ => null,
        };
        string? implicitCrossLabel = null;
        if (priorRwy is not null)
        {
            HoldShortPoint? firstCrossing = route.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.RunwayCrossing);
            if (firstCrossing is not null && firstCrossing.TargetName is { Length: > 0 } crossingRwy)
            {
                if (RunwayIdentifier.Parse(crossingRwy).Overlaps(RunwayIdentifier.Parse(priorRwy)))
                {
                    firstCrossing.IsCleared = true;
                    implicitCrossLabel = crossingRwy;
                    Log.LogInformation(
                        "[TryTaxi] {Callsign}: implicit cross of {Rwy} at node {NodeId} (already at/exiting {PriorRwy})",
                        aircraft.Callsign,
                        crossingRwy,
                        firstCrossing.NodeId,
                        priorRwy
                    );
                }
            }
        }

        TaxiRouteAutoCross.Apply(route, options.AutoCrossRunway);

        // Pre-clear specific runway crossings from CROSS keywords in the TAXI command
        if (taxi.CrossRunways is { Count: > 0 })
        {
            var matchedCrossRunways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HoldShortPoint hs in route.HoldShortPoints)
            {
                if (hs.Reason == HoldShortReason.RunwayCrossing && hs.TargetName is not null)
                {
                    var hsRwyId = RunwayIdentifier.Parse(hs.TargetName);
                    foreach (string crossRwy in taxi.CrossRunways)
                    {
                        if (hsRwyId.Contains(crossRwy))
                        {
                            hs.IsCleared = true;
                            // Explicit user CROSS keyword owns the clearance — clear any
                            // AutoCross attribution so a future AutoCross-OFF toggle does
                            // not revert this user-issued crossing authorization.
                            hs.ClearedByAutoCross = false;
                            matchedCrossRunways.Add(crossRwy);
                            break;
                        }
                    }
                }
            }

            // A CROSS runway that pre-cleared nothing was silently inert; tell the controller.
            foreach (string crossRwy in taxi.CrossRunways)
            {
                if (!matchedCrossRunways.Contains(crossRwy))
                {
                    route.Warnings.Add($"CROSS {crossRwy} not applied — the route crosses no runway {crossRwy}");
                }
            }
        }

        // Auto-detect runway from final node when no explicit destination runway
        RunwayInfo? detectedRunway = null;
        if (taxi.DestinationRunway is null && route.Segments.Count > 0)
        {
            detectedRunway = DetectRunwayFromRoute(route, groundLayout, aircraft);
        }
        else if (taxi.DestinationRunway is not null)
        {
            detectedRunway = CommandDispatcher.ResolveRunway(aircraft, taxi.DestinationRunway);
        }

        // Captured before the fresh PhaseList below drops the old clearance: both the arrival gate and the
        // runway-change warning read state this reset erases.
        DepartureRunwayAssignment priorAssignment = CaptureDepartureRunwayAssignment(aircraft);

        // Clear current phases
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases?.Clear(ctx);

        // Set up the taxi route and phase
        aircraft.Ground.AssignedTaxiRoute = route;
        aircraft.Ground.AwaitingTaxiInCall = false;
        aircraft.Ground.Hold = null;
        // A fresh taxi clearance resets the commanded taxi speed to the category default.
        aircraft.Ground.CommandedTaxiSpeedKts = null;
        // The aircraft is leaving whatever stand it was on. A taxi to parking re-sets this when it
        // arrives (the zero-segment fallback below, or TaxiingPhase on route completion).
        aircraft.Ground.ParkingSpot = null;

        if (taxi.NoDelete)
        {
            aircraft.Ground.AutoDeleteExempt = true;
            aircraft.Ground.NoDeleteRequested = true;
        }

        aircraft.Phases = new PhaseList();
        if (detectedRunway is not null)
        {
            ApplyDepartureRunway(aircraft, detectedRunway, priorAssignment);
        }

        // Zero-segment route to parking: A* snapped the aircraft to the destination node.
        // Only skip TaxiingPhase when the aircraft is genuinely at the spot; if it's
        // physically distant (e.g. after pushback), re-route from the nearest neighbor
        // so TaxiingPhase drives the aircraft back to the parking position.
        string? parkingName = route.DestinationParking ?? route.DestinationSpot;
        if (route.Segments.Count == 0 && parkingName is not null)
        {
            // A gate or helipad is a stand the aircraft parks on; a taxi spot is an intermediate waypoint
            // it waits on for further instructions. Same rule — and the same discriminator, the route's
            // parking destination — as TaxiingPhase.CompleteRoute, the moving-route counterpart to this
            // standing-on-the-destination shortcut. The sigil is the token that names the destination in a
            // command, so it has to follow the same split.
            bool atStand = route.DestinationParking is not null;
            string destinationSigil = atStand ? "@" : "$";

            GroundNode? destNode = taxi.DestinationSpot is not null
                ? groundLayout.FindSpotNodeByName(taxi.DestinationSpot)
                : (groundLayout.FindHelipadByName(taxi.DestinationParking!) ?? groundLayout.FindParkingByName(taxi.DestinationParking!));

            const double atParkingThresholdNm = 50.0 / GeoMath.FeetPerNm;
            double distToDest = destNode is not null ? GeoMath.DistanceNm(aircraft.Position, destNode.Position) : 0;

            bool rerouted = false;
            if (destNode is not null && distToDest > atParkingThresholdNm)
            {
                // Aircraft is far from parking but snapped to the same node.
                // Re-route from the neighbor of destNode closest to the aircraft.
                GroundNode? bestNeighbor = null;
                double bestNeighborDist = double.MaxValue;
                foreach (IGroundEdge edge in destNode.Edges)
                {
                    GroundNode neighbor = edge.OtherNode(destNode);
                    double d = GeoMath.DistanceNm(aircraft.Position, neighbor.Position);
                    if (d < bestNeighborDist)
                    {
                        bestNeighborDist = d;
                        bestNeighbor = neighbor;
                    }
                }

                if (bestNeighbor is not null)
                {
                    TaxiRoute? reroute = TaxiPathfinder.FindRoute(
                        groundLayout,
                        bestNeighbor.Id,
                        destNode.Id,
                        AircraftCategorization.Categorize(aircraft.AircraftType)
                    );
                    if (reroute is not null && reroute.Segments.Count > 0)
                    {
                        route = TaxiApproachLeg.Prepend(groundLayout, aircraft.Position, aircraft.TrueHeading, SetDestination(reroute, taxi));
                        aircraft.Ground.AssignedTaxiRoute = route;
                        aircraft.Ground.AwaitingTaxiInCall = false;
                        HoldShortAnnotator.ComputeHoldShortPositions(groundLayout, route, aircraftLengthFt);
                        rerouted = true;

                        Log.LogInformation(
                            "[TryTaxi] {Callsign}: zero-segment re-route via neighbor {NeighborId} to {Sigil}{Destination} ({SegCount} segments)",
                            aircraft.Callsign,
                            bestNeighbor.Id,
                            destinationSigil,
                            parkingName,
                            route.Segments.Count
                        );
                    }
                }
            }

            if (!rerouted)
            {
                if (atStand)
                {
                    aircraft.Ground.ParkingSpot = route.DestinationParking;
                }

                aircraft.Phases.Add(atStand ? new AtParkingPhase() : new HoldingInPositionPhase());
                ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
                aircraft.Phases.Start(ctx);
                return CommandDispatcher.Ok($"Taxi via {destinationSigil}{parkingName}") with { EffectiveCommand = effectiveCommand };
            }
        }

        aircraft.Phases.Add(new TaxiingPhase());
        ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases.Start(ctx);

        string msg;
        if ((route.Segments.Count == 0) && (taxi.DestinationRunway is not null))
        {
            string runwayDisplay = RunwayIdentifier.ToDisplayDesignator(taxi.DestinationRunway);
            msg = $"Already at runway {runwayDisplay} — holding short (runway {runwayDisplay} assigned)";
        }
        else
        {
            msg = BuildTaxiReadback(route, groundLayout, taxi, occupiedTaxiway, fallback.Held is not null);
        }

        if (route.Warnings.Count > 0)
        {
            msg += " [" + string.Join("; ", route.Warnings) + "]";
        }

        if (implicitCrossLabel is not null)
        {
            msg += $" (cross {implicitCrossLabel})";
        }

        return CommandDispatcher.Ok(msg) with
        {
            EffectiveCommand = effectiveCommand,
        };
    }

    /// <summary>
    /// The departure-runway state a taxi or air-taxi clearance has to read <em>before</em> it installs a fresh
    /// <see cref="PhaseList"/>: the runway the aircraft was previously assigned, whether this is an arrival being
    /// routed across the field rather than a departure, and what has been issued against the old runway — a SID
    /// initial altitude (the departure clearance was read to the crew) and/or a takeoff clearance stored during
    /// the taxi. The new list carries none of it, so it is captured up front and applied by
    /// <see cref="ApplyDepartureRunway"/>; the two clearance flags also decide which warning the change draws.
    /// </summary>
    private readonly record struct DepartureRunwayAssignment(
        string? PriorDepartureRunway,
        bool IsArrival,
        bool HasSidInitialAltitude,
        bool HasStoredTakeoffClearance
    );

    /// <summary>
    /// Snapshots <see cref="DepartureRunwayAssignment"/> from the aircraft as it stands. Arrival context is the
    /// arrival half of <see cref="TryAssignRunway"/>'s test — on a STAR to a filed destination with no departure
    /// clearance pending — without its airborne clause: a taxi clearance already requires the wheels on the
    /// ground, and a helicopter hovering over the field on an air taxi is manoeuvring on the airport, not
    /// arriving. The two clearance flags are what makes a runway change worth warning about: a SID initial
    /// altitude means the departure clearance has been read to the crew, and
    /// <see cref="PhaseList.DepartureClearance"/> is a takeoff clearance stored while the aircraft taxis. A VFR
    /// or repositioning aircraft has neither.
    /// </summary>
    private static DepartureRunwayAssignment CaptureDepartureRunwayAssignment(AircraftState aircraft)
    {
        bool isArrival =
            (aircraft.Procedure.ActiveStarId is not null)
            && (!string.IsNullOrEmpty(aircraft.FlightPlan.Destination))
            && (aircraft.Phases?.DepartureClearance is null);
        return new DepartureRunwayAssignment(
            aircraft.Procedure.DepartureRunway,
            isArrival,
            aircraft.Procedure.SidInitialAltitudeFt is not null,
            aircraft.Phases?.DepartureClearance is not null
        );
    }

    /// <summary>
    /// Assigns the runway a taxi/air-taxi clearance resolved. <see cref="PhaseList.AssignedRunway"/> and
    /// <see cref="AircraftProcedure.DepartureRunway"/> are written together — the SID runway transition is
    /// re-derived from the first (<see cref="DepartureClearanceHandler.TryResolveSidFromCifp"/>) and the CVIA
    /// rejoin from the second — except for an arrival, whose taxi across the field must not claim a departure
    /// runway. That gate is only the arrival <em>test</em> <see cref="TryAssignRunway"/> uses; a taxi clearance
    /// does not take its arrival branch's actions (the STAR-runway sync and the pending-approach clear), which
    /// belong to an explicit runway assignment. The runway in a taxi clearance is normally a confirmation
    /// (7110.65 3-7-2), so a clearance that names a different one is honoured and warned about, never refused;
    /// see <see cref="WarnIfDepartureRunwayChanged"/>.
    /// </summary>
    private static void ApplyDepartureRunway(AircraftState aircraft, RunwayInfo runway, DepartureRunwayAssignment prior)
    {
        aircraft.Phases ??= new PhaseList();
        aircraft.Phases.AssignedRunway = runway;
        if (prior.IsArrival)
        {
            return;
        }

        WarnIfDepartureRunwayChanged(aircraft, runway, prior);
        aircraft.Procedure.DepartureRunway = runway.Designator;
    }

    /// <summary>
    /// Warns the controller when the runway named in a taxi clearance is a different one from the departure
    /// runway the aircraft is already flying to. 7110.65 3-7-2 frames the runway in a taxi clearance as a
    /// confirmation, so silently moving the aircraft to another one hides what the change costs — and what it
    /// costs depends on what had been issued for the old runway, so the warning says only what applies:
    ///
    /// <list type="bullet">
    /// <item>a departure clearance had been read to the crew (a SID initial altitude is held): it no longer
    /// applies, and 4-3-2.c.1 NOTE 1 wants the amended one issued before the aircraft enters the runway;</item>
    /// <item>only a takeoff clearance was stored during the taxi: a takeoff clearance is runway-specific
    /// (3-9-10.a) and is cancelled explicitly (3-9-11), never by re-taxiing the aircraft.</item>
    /// </list>
    ///
    /// Only a genuine change is warned: the first assignment and a re-statement of the same runway are silent,
    /// and so is an aircraft with nothing issued against the old runway.
    /// </summary>
    private static void WarnIfDepartureRunwayChanged(AircraftState aircraft, RunwayInfo runway, DepartureRunwayAssignment prior)
    {
        if ((prior.PriorDepartureRunway is not { Length: > 0 } previous) || (!prior.HasSidInitialAltitude && !prior.HasStoredTakeoffClearance))
        {
            return;
        }

        if (
            RunwayIdentifier
                .NormalizeDesignator(previous)
                .Equals(RunwayIdentifier.NormalizeDesignator(runway.Designator), StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        string assigned = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
        string briefed = RunwayIdentifier.ToDisplayDesignator(previous);
        string head =
            $"taxi clearance names runway {assigned} but the departure runway assigned is {briefed} — this is a runway change, "
            + "not a confirmation (7110.65 3-7-2); ";
        string warning = prior.HasSidInitialAltitude
            ? head
                + $"the departure clearance issued for {briefed} no longer applies and the SID runway transition will be "
                + $"resolved for {assigned} when the amended clearance is issued, so issue it before the aircraft enters the "
                + "runway (4-3-2.c.1 NOTE 1)"
            : head
                + $"the takeoff clearance issued for {briefed} is void — a takeoff clearance is runway-specific (3-9-10.a) "
                + "and must be cancelled explicitly (3-9-11)";
        aircraft.PendingWarnings.Add(warning);
        Log.LogDebug("[TryTaxi] {Callsign}: {Warning}", aircraft.Callsign, warning);
    }

    /// <summary>
    /// The clearance with its first occurrence of <paramref name="token"/> removed from the path (and the
    /// matching turn hint dropped so hints stay index-aligned) — the command as applied after a cleared
    /// taxiway was dropped. Returns the command unchanged when the token is not in the path.
    /// </summary>
    private static TaxiCommand WithoutPathToken(TaxiCommand command, string token)
    {
        int index = command.Path.FindIndex(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return command;
        }

        var path = new List<string>(command.Path);
        path.RemoveAt(index);
        List<TurnDirection?>? hints = null;
        if (command.PathTurnHints is not null)
        {
            hints = [.. command.PathTurnHints];
            if (index < hints.Count)
            {
                hints.RemoveAt(index);
            }
        }

        return command with
        {
            Path = path,
            PathTurnHints = hints,
        };
    }

    private static bool HasDestination(TaxiCommand taxi) =>
        (taxi.DestinationRunway is not null) || (taxi.DestinationParking is not null) || (taxi.DestinationSpot is not null);

    /// <summary>
    /// The <c>HS</c> targets whose taxiway is a real taxiway on the layout that the path does not already
    /// name — the candidates <see cref="AugmentPathWithHoldShortTaxiways"/> would append. A located target
    /// (<c>HS C@J</c>) contributes its ON-taxiway (<c>J</c>): the aircraft travels J and holds short OF C,
    /// so C itself must never join the cleared path. Runway targets (<c>HS 28L</c>) have no taxiway nodes
    /// and are left to the runway-crossing machinery; a spot target (<c>HS $17</c>) is a point the route
    /// passes through, not a taxiway to steer onto; a second target on an already-collected taxiway
    /// is dropped so the taxiway is folded at most once.
    /// </summary>
    private static List<HoldShortTarget> HoldShortTaxiwaysToFold(AirportGroundLayout groundLayout, TaxiCommand taxi)
    {
        var candidates = new List<HoldShortTarget>();
        var named = new HashSet<string>(taxi.Path, StringComparer.OrdinalIgnoreCase);
        foreach (HoldShortTarget target in taxi.HoldShorts)
        {
            if (target.IsSpot)
            {
                continue;
            }

            string foldTaxiway = target.OnTaxiway ?? target.Target;
            if (named.Contains(foldTaxiway) || (groundLayout.GetNodesOnTaxiway(foldTaxiway).Count == 0))
            {
                continue;
            }

            named.Add(foldTaxiway);
            candidates.Add(target);
        }

        return candidates;
    }

    /// <summary>
    /// Appends the taxiway of each fold target (from <see cref="HoldShortTaxiwaysToFold"/>) to the taxi
    /// command's <see cref="TaxiCommand.Path"/> so the hold-short target steers the route as a directional
    /// hint rather than only annotating an already-chosen route. The target stays in
    /// <see cref="TaxiCommand.HoldShorts"/> so the hold-short is still placed at that intersection — this
    /// turns <c>TAXI D C HS E RWY 28R</c> into the already-supported <c>TAXI D C E HS E RWY 28R</c> shape.
    /// It is the fallback reading of a clearance with a destination (see <c>ResolveRoute</c> in
    /// <see cref="TryTaxi"/>) and the only reading of one without.
    /// </summary>
    private static TaxiCommand AugmentPathWithHoldShortTaxiways(TaxiCommand taxi, IReadOnlyList<HoldShortTarget> foldTargets)
    {
        if (foldTargets.Count == 0)
        {
            return taxi;
        }

        List<string> path = [.. taxi.Path];
        List<TurnDirection?>? hints = taxi.PathTurnHints is null ? null : [.. taxi.PathTurnHints];
        foreach (HoldShortTarget target in foldTargets)
        {
            path.Add(target.OnTaxiway ?? target.Target);
            hints?.Add(null);
        }

        return taxi with
        {
            Path = path,
            PathTurnHints = hints,
        };
    }

    /// <summary>
    /// Why the as-cleared resolution of a command whose HS taxiways were NOT folded into the path does not
    /// honor the clearance — null when it does. Honoring means the destination was actually reached (a
    /// runway destination has its <see cref="HoldShortReason.DestinationRunway"/> stop; a parking/spot route
    /// is only ever non-null when it reaches the spot) and every fold candidate is bound on the route AND
    /// the route continues on a cleared taxiway past that bar. The continuation test is what separates
    /// "hold short of H, which A crosses on the way to A1" (SFO, issue #395 — no fold) from "hold short of
    /// E, then onto E to the runway" (OAK, E beyond the last cleared taxiway — fold): merely touching the
    /// target's taxiway at a junction and then reaching the runway over free numbered pavement is not the
    /// cleared route, however cheap the pathfinder found it.
    /// </summary>
    private static string? AsClearedRejectionReason(TaxiRoute route, TaxiCommand command, IReadOnlyList<HoldShortTarget> foldTargets)
    {
        if ((command.DestinationRunway is not null) && !route.HoldShortPoints.Exists(h => h.Reason == HoldShortReason.DestinationRunway))
        {
            return $"route does not reach runway {command.DestinationRunway}";
        }

        foreach (HoldShortTarget target in foldTargets)
        {
            HoldShortPoint? bound = RouteMaterialiser.FindBoundHoldShort(route.HoldShortPoints, target);
            if (bound is null)
            {
                return $"HS {target.ToCanonical()} is not on the route";
            }

            if (!ContinuesOnClearedTaxiwayPast(route, bound.NodeId, command.Path))
            {
                return $"route leaves the cleared taxiways at HS {target.ToCanonical()}";
            }
        }

        return null;
    }

    /// <summary>
    /// True when some segment after the hold-short bar at <paramref name="holdShortNodeId"/> runs along a
    /// taxiway the controller named (exact segment name — a junction arc like <c>"C - E"</c> is the turn
    /// OFF the cleared taxiway, not travel along it). A bar at the route's start node counts as index −1,
    /// mirroring the materialiser's start-node hold-short handling.
    /// </summary>
    private static bool ContinuesOnClearedTaxiwayPast(TaxiRoute route, int holdShortNodeId, IReadOnlyList<string> path)
    {
        int holdIndex = -1;
        if ((route.Segments.Count == 0) || (route.Segments[0].FromNodeId != holdShortNodeId))
        {
            holdIndex = route.Segments.FindIndex(s => s.ToNodeId == holdShortNodeId);
            if (holdIndex < 0)
            {
                return false;
            }
        }

        for (int i = holdIndex + 1; i < route.Segments.Count; i++)
        {
            string name = route.Segments[i].TaxiwayName;
            if (path.Any(token => !NodeRefToken.IsNodeReference(token) && string.Equals(token, name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A spot cleared from the ramp is a line-up to leave it (#456): the aircraft comes up the spot's lane from the
    /// ramp side and stops facing the movement-area taxiway the lane joins, rather than arriving from that taxiway.
    /// Only a spot destination and an aircraft that starts off the movement area qualify — at its stand (the one
    /// phase the geometry cannot see) or by where it stands (<see cref="RampLaneReposition.StartsOffMovementArea"/>);
    /// the plan itself decides the rest and hands the resolved route back when it declines.
    /// </summary>
    /// <param name="aircraft">The aircraft cleared.</param>
    /// <param name="layout">The airport it is on.</param>
    /// <param name="route">The route the clearance resolved to.</param>
    /// <param name="destination">The clearance's destination node, if any.</param>
    /// <param name="inputs">Its category, length, the clearance as worded, and the world's aircraft.</param>
    /// <returns>The line-up route, or <paramref name="route"/>.</returns>
    private static TaxiRoute ApplySpotLineUp(
        AircraftState aircraft,
        AirportGroundLayout layout,
        TaxiRoute route,
        GroundNode? destination,
        SpotLineUpInputs inputs
    )
    {
        if (
            (destination is not { Type: GroundNodeType.Spot })
            || !RampLaneReposition.StartsOffMovementArea(
                layout,
                aircraft.Position,
                aircraft.Phases?.CurrentPhase is AtParkingPhase,
                aircraft.AircraftType
            )
        )
        {
            return route;
        }

        IEnumerable<AircraftState> others = inputs.ListAircraft?.Invoke() ?? [];
        var request = new SpotLineUpRequest
        {
            Callsign = aircraft.Callsign,
            AircraftType = aircraft.AircraftType,
            Position = aircraft.Position,
            Route = route,
            Spot = destination,
            Category = inputs.Category,
            AircraftLengthFt = inputs.AircraftLengthFt,
            ClearedTaxiways = inputs.ClearedPath,
            OtherGroundAircraft = [.. others.Where(a => a.IsOnGround).Select(TugNeighbourCandidate.From)],
        };
        return RampLaneReposition.TryPlanSpotLineUp(layout, request) ?? route;
    }

    /// <summary>
    /// TAXIAUTO &lt;RWY&gt; or TAXIAUTO @&lt;PARKING&gt; — delegates to <see cref="TryTaxi"/>
    /// with an empty taxiway path so the standard pipeline's existing A* route resolvers
    /// (<see cref="ResolveRunwayRouteByAStar"/> / <see cref="ResolveParkingRoute"/>) discover
    /// the taxiway sequence. Hold-short annotation, auto-cross handling, and phase handoff
    /// work identically to a user-typed TAXI.
    /// </summary>
    internal static CommandResult TryTaxiAuto(
        AircraftState aircraft,
        TaxiAutoCommand autoTaxi,
        AirportGroundLayout? groundLayout,
        bool autoCrossRunway = false
    ) => TryTaxiAuto(aircraft, autoTaxi, groundLayout, autoCrossRunway, listAircraft: null);

    /// <summary>
    /// A <c>TAXIAUTO</c> dispatched with the world's aircraft in view, as the matching
    /// <see cref="TryTaxi(AircraftState, TaxiCommand, AirportGroundLayout?, bool, Func{IReadOnlyList{AircraftState}}?)"/>.
    /// </summary>
    internal static CommandResult TryTaxiAuto(
        AircraftState aircraft,
        TaxiAutoCommand autoTaxi,
        AirportGroundLayout? groundLayout,
        bool autoCrossRunway,
        Func<IReadOnlyList<AircraftState>>? listAircraft
    )
    {
        if (autoTaxi.DestinationRunway is null && autoTaxi.DestinationParking is null && autoTaxi.DestinationSpot is null)
        {
            return new CommandResult(false, "TAXIAUTO requires a runway, @parking, or $spot destination");
        }

        Log.LogDebug(
            "[TryTaxiAuto] {Callsign}: destRwy={Rwy} destParking={Parking} destSpot={Spot}",
            aircraft.Callsign,
            autoTaxi.DestinationRunway ?? "(none)",
            autoTaxi.DestinationParking ?? "(none)",
            autoTaxi.DestinationSpot ?? "(none)"
        );

        var taxi = new TaxiCommand(
            Path: [],
            HoldShorts: [],
            DestinationRunway: autoTaxi.DestinationRunway,
            DestinationParking: autoTaxi.DestinationParking,
            DestinationSpot: autoTaxi.DestinationSpot
        );

        return TryTaxiCore(aircraft, taxi, groundLayout, new TaxiCoreOptions(autoCrossRunway, AllowRemoteRunwayAutoRoute: true, listAircraft));
    }

    /// <summary>
    /// Resolves the cleared path from the first start that resolves. A candidate that fails leaves the as-cleared
    /// command and its failure in place, so the readback and every recovery after it see the clearance as issued.
    /// </summary>
    private static StartResolution ResolveFromBestStart(
        string callsign,
        TaxiCommand asCleared,
        TaxiCommand? heldShortPrepend,
        TaxiCommand? currentTaxiwayPrepend,
        TaxiRouteResolver resolve
    )
    {
        // The controller clears a continuation ("TAXI E") without re-naming the taxiway the aircraft is on, so the
        // path is resolved from the first of these that resolves:
        //   1. the taxiway the aircraft holds short of, prepended — holding short of K on B, "TAXI A" means
        //      "K A", never back along B to some other B/A junction (#455). Only when its leg turns onto that
        //      taxiway and follows it to the first junction with the first cleared one, holding short of no runway,
        //      crossing none and never reversing (HeldShortLegRejection);
        //   2. the path as cleared, bridging from the start node onto its first taxiway;
        //   3. only when that fails, the taxiway the aircraft occupies prepended. Prepending it unconditionally
        //      forced the entry onto the first cleared taxiway through a junction the two share, whichever way
        //      that entry then had to go: THY9WC on B, "TAXI A F 28L" became "B A F", entered A southbound and
        //      U-turned across 01L/19R where the as-cleared route took B1 (#457).
        if (heldShortPrepend is not null)
        {
            TaxiRoute? heldShortRoute = resolve(heldShortPrepend, out PathfindingFailure? heldShortFailure);
            string? rejection = heldShortRoute is null
                ? (heldShortFailure?.HumanMessage ?? "no route")
                : HeldShortLegRejection(heldShortRoute, heldShortPrepend.Path[1]);
            Log.LogDebug(
                "[TryTaxi] {Callsign}: holding short of {Twy}; path [{Path}] {Outcome}",
                callsign,
                heldShortPrepend.Path[0],
                string.Join(" ", heldShortPrepend.Path),
                rejection is null ? "resolved" : $"not taken — {rejection}"
            );
            if (rejection is null)
            {
                return new StartResolution(heldShortPrepend, heldShortRoute, heldShortFailure);
            }
        }

        TaxiRoute? route = resolve(asCleared, out PathfindingFailure? failure);
        if ((route is not null) || (currentTaxiwayPrepend is null))
        {
            return new StartResolution(asCleared, route, failure);
        }

        TaxiRoute? prepended = resolve(currentTaxiwayPrepend, out PathfindingFailure? prependFailure);
        Log.LogDebug(
            "[TryTaxi] {Callsign}: as-cleared path failed ({Reason}); current-taxiway path [{Path}] {Outcome}",
            callsign,
            failure?.HumanMessage ?? "no route",
            string.Join(" ", currentTaxiwayPrepend.Path),
            prepended is null ? "did not resolve either" : "resolved"
        );
        return prepended is null
            ? new StartResolution(asCleared, null, failure)
            : new StartResolution(currentTaxiwayPrepend, prepended, prependFailure);
    }

    /// <summary>
    /// Why the route of a held-short prepend is not the plain turn onto the taxiway held short of, or null when it is.
    /// Its leg up to <paramref name="firstCleared"/> must join it at the first junction of the two the leg reaches, hold
    /// short of no runway, cross none, and never reverse — including the turn onto <paramref name="firstCleared"/>.
    /// </summary>
    private static string? HeldShortLegRejection(TaxiRoute route, string firstCleared)
    {
        if (HeldShortLegEnd(route, firstCleared) is not { } joinIndex)
        {
            return $"never joins {firstCleared}";
        }

        List<TaxiRouteSegment> leg = route.Segments.GetRange(0, joinIndex);
        int lastTurnIndex = Math.Min(joinIndex, route.Segments.Count - 1);
        return ReversalUpTo(route.Segments, lastTurnIndex) ?? RunwayOnLeg(route, leg) ?? PassedJunctionOnLeg(leg, firstCleared);
    }

    /// <summary>
    /// The index of the first segment on <paramref name="firstCleared"/>; the segment count when the route ends at a
    /// junction with it without entering it (a clearance with no destination ends there); null when it does neither.
    /// </summary>
    private static int? HeldShortLegEnd(TaxiRoute route, string firstCleared)
    {
        int joinIndex = route.Segments.FindIndex(s => s.Edge.Edge.MatchesTaxiway(firstCleared));
        if (joinIndex >= 0)
        {
            return joinIndex;
        }

        bool endsAtJunction = (route.Segments.Count > 0) && route.Segments[^1].Edge.ToNode.Edges.Any(e => e.MatchesTaxiway(firstCleared));
        return endsAtJunction ? route.Segments.Count : null;
    }

    private static string? ReversalUpTo(List<TaxiRouteSegment> segments, int lastIndex)
    {
        for (int i = 1; i <= lastIndex; i++)
        {
            double turn = GeoMath.AbsBearingDifference(segments[i - 1].Edge.ArrivalBearing, segments[i].Edge.DepartureBearing);
            if (turn >= HeldShortLegReversalDeg)
            {
                return $"reverses ({turn:F0}°) at node {segments[i].FromNodeId}";
            }
        }

        return null;
    }

    private static string? RunwayOnLeg(TaxiRoute route, List<TaxiRouteSegment> leg)
    {
        var legNodeIds = new HashSet<int>();
        foreach (TaxiRouteSegment segment in leg)
        {
            legNodeIds.Add(segment.FromNodeId);
            legNodeIds.Add(segment.ToNodeId);
            if (AirportGroundLayout.HasRunwayCenterlineEdge(segment.Edge.ToNode))
            {
                return $"crosses a runway at node {segment.ToNodeId}";
            }
        }

        HoldShortPoint? runwayBar = route.HoldShortPoints.FirstOrDefault(h =>
            (h.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.DestinationRunway) && legNodeIds.Contains(h.NodeId)
        );
        return runwayBar is null ? null : $"holds short of runway {runwayBar.TargetName} at node {runwayBar.NodeId}";
    }

    /// <summary>
    /// A junction with <paramref name="firstCleared"/> the leg runs through before the one it turns at. Only straight
    /// edges mark a junction: a fillet's tangent point bears the arc onto <paramref name="firstCleared"/> short of the
    /// junction the leg may still turn at square.
    /// </summary>
    private static string? PassedJunctionOnLeg(List<TaxiRouteSegment> leg, string firstCleared)
    {
        for (int i = 0; i < leg.Count - 1; i++)
        {
            GroundNode node = leg[i].Edge.ToNode;
            if (node.Edges.Any(e => (e is not GroundArc) && e.MatchesTaxiway(firstCleared)))
            {
                return $"passes the {firstCleared} junction at node {node.Id}";
            }
        }

        return null;
    }

    /// <summary>
    /// The taxiway the aircraft holds short of where it stands: the target of the explicit hold-short its
    /// <see cref="HoldingShortPhase"/> is holding at. Null when it is not holding short, or holds short of a
    /// runway or a spot. A runway is recognised by its shape (<c>28L</c>, <c>10L/28R</c>), not by looking it up: the
    /// layout's runway lookup matches one end at a time, so it misses a target naming the whole runway.
    /// </summary>
    public static string? HeldShortTaxiway(AircraftState aircraft)
    {
        if (
            aircraft.Phases?.CurrentPhase
            is not HoldingShortPhase { HoldShort: { Reason: HoldShortReason.ExplicitHoldShort, TargetName: { Length: > 0 } target } }
        )
        {
            return null;
        }

        bool isRunway = (RunwayArgument.TryParse(target) is not null) || target.Contains('/');
        return (HoldShortTarget.IsSpotTargetName(target) || isRunway) ? null : target;
    }

    /// <summary>
    /// The taxiway the aircraft occupies, prepended to the cleared path — the fallback start when the path as
    /// cleared does not resolve. Null unless the start node lies on <see cref="AircraftGroundOps.CurrentTaxiway"/>
    /// (a stale value never injects a phantom leg) and that taxiway joins the first cleared one.
    /// </summary>
    private static TaxiCommand? CurrentTaxiwayPrepend(AirportGroundLayout groundLayout, string? occupiedTaxiway, TaxiCommand taxi) =>
        occupiedTaxiway is null ? null : PrependTaxiwayJoiningPath(groundLayout, taxi, occupiedTaxiway);

    /// <summary>
    /// The TAXI readback's taxiway filter: the clearance as issued, so a taxiway is named only when the clearance
    /// named it or the aircraft occupies it. A path of node references alone (a drawn route) names nothing, so
    /// every taxiway it drives is shown.
    /// </summary>
    private static Func<string, bool> ReadbackTaxiwayFilter(IReadOnlyList<string> path, string? occupiedTaxiway)
    {
        HashSet<string> named = ClearanceTaxiwayNames(path, occupiedTaxiway);
        return path.All(t => t.StartsWith('#')) ? static _ => true : named.Contains;
    }

    private static HashSet<string> ClearanceTaxiwayNames(IReadOnlyList<string> path, string? occupiedTaxiway)
    {
        var named = new HashSet<string>(path.Where(t => !t.StartsWith('#')), StringComparer.OrdinalIgnoreCase);
        if (occupiedTaxiway is not null)
        {
            named.Add(occupiedTaxiway);
        }

        return named;
    }

    /// <summary>
    /// Warn about each movement-area taxiway the route drives that the clearance did not name (and the readback
    /// therefore leaves out), in the materialiser's words (<c>taxiing via X — not in the route issued</c>). Ramp
    /// taxilanes and RAMP are nonmovement area and stay silent; junction arcs, runway centerlines and free-space
    /// legs are not a taxiway driven; the taxiway the aircraft occupies counts as named. A taxiway another warning
    /// already names (the resolver's connector notices, or this warning itself) is not warned again, and neither is
    /// any of <paramref name="implied"/>, the taxiways the clearance implies (the runway-entry connector, the gate's or
    /// spot's short lead-in). A drawn route of node references alone names no taxiway, so it is not checked.
    /// </summary>
    private static void WarnUnclearedMovementAreaTaxiways(
        TaxiRoute route,
        AirportGroundLayout groundLayout,
        IReadOnlyList<string> path,
        string? occupiedTaxiway,
        IEnumerable<string?> implied
    )
    {
        if (path.All(t => t.StartsWith('#')))
        {
            return;
        }

        var classification = MovementAreaClassification.For(groundLayout);
        HashSet<string> named = ClearanceTaxiwayNames(path, occupiedTaxiway);
        named.UnionWith(implied.OfType<string>());

        IEnumerable<string> driven = route
            .Segments.Select(s => s.Edge.Edge)
            .Where(IsTaxiwayDriven)
            .SelectMany(SegmentExpander.EdgeNames)
            .Where(name => SegmentExpander.IsUnclearedMovementAreaName(name, named, classification));
        foreach (string name in driven)
        {
            if (!route.Warnings.Any(w => NamesTaxiway(w, name)))
            {
                route.Warnings.Add(RouteMaterialiser.NotInRouteIssuedWarning(name));
            }
        }
    }

    /// <summary>An edge that drives a taxiway: not a junction arc between two, a runway centreline, or a free-space leg.</summary>
    private static bool IsTaxiwayDriven(IGroundEdge edge) =>
        (edge is not GroundArc { TaxiwayNames.Length: >= 2 }) && !edge.IsRunwayCenterline && !VirtualNode.IsVirtualEdge(edge);

    /// <summary>
    /// The TAXI readback: the clearance as issued. Lanes and taxiways the driven path adds are left out, and a
    /// movement-area taxiway among them is warned instead (<see cref="WarnUnclearedMovementAreaTaxiways"/>) — except the
    /// implied ones, the runway-entry connector and the gate's or spot's short lead-in, which are driven silently, so the
    /// resolver's own warning about the lead-in is withdrawn too. A route that ends short of its destination has no lead-in.
    /// </summary>
    private static string BuildTaxiReadback(TaxiRoute route, AirportGroundLayout layout, TaxiCommand taxi, string? occupiedTaxiway, bool endsShort)
    {
        string? impliedLeadIn = endsShort ? null : ImpliedDestinationLeadIn(route, layout, taxi, occupiedTaxiway);
        if (impliedLeadIn is not null)
        {
            route.Warnings.RemoveAll(w => w == RouteMaterialiser.NotInRouteIssuedWarning(impliedLeadIn));
        }

        WarnUnclearedMovementAreaTaxiways(route, layout, taxi.Path, occupiedTaxiway, [ImpliedRunwayEntryConnector(route, taxi), impliedLeadIn]);
        return $"Taxi via {route.ToSummary(BuildTurnHintMap(taxi), taxi.Path, ReadbackTaxiwayFilter(taxi.Path, occupiedTaxiway))}";
    }

    /// <summary>
    /// Why an unresolved TAXI is refused: the missing link the start needs (with the resolver's own reason alongside
    /// when it has one), else the resolver's reason, else the path that did not resolve.
    /// </summary>
    private static string UnresolvedTaxiMessage(string? missingLinkRefusal, string? failReason, TaxiCommand taxi) =>
        (missingLinkRefusal, failReason) switch
        {
            ({ } refusal, { } reason) => $"{refusal} ({reason})",
            ({ } refusal, null) => refusal,
            (null, { } reason) => reason,
            _ => $"Cannot resolve taxi route: {string.Join(" ", taxi.Path)}",
        };

    /// <summary>
    /// The runway-entry connector a runway clearance implies: the numbered variant of the clearance's last taxiway
    /// that the resolver extends onto to reach the cleared runway's bar (OAK <c>TAXI U W RWY 30</c> ends on W1). It
    /// is recognised by the resolver's own choice (<see cref="SegmentExpander.IsNumberedVariant"/> of the last
    /// cleared taxiway) on the route's final straight taxiway segment, when the route ends holding short of its
    /// destination runway. Null for any other route.
    /// </summary>
    private static string? ImpliedRunwayEntryConnector(TaxiRoute route, TaxiCommand taxi)
    {
        if ((taxi.DestinationRunway is null) || !route.HoldShortPoints.Any(h => h.Reason == HoldShortReason.DestinationRunway))
        {
            return null;
        }

        string? lastCleared = taxi.Path.LastOrDefault(t => !t.StartsWith('#'));
        TaxiRouteSegment? lastStraight = route.Segments.LastOrDefault(s =>
            (s.Edge.Edge is not GroundArc) && !s.Edge.Edge.IsRunwayCenterline && !VirtualNode.IsVirtualEdge(s.Edge.Edge)
        );
        return (lastCleared is not null) && (lastStraight is not null) && SegmentExpander.IsNumberedVariant(lastStraight.TaxiwayName, lastCleared)
            ? lastStraight.TaxiwayName
            : null;
    }

    /// <summary>
    /// The gate's or spot's lead-in taxiway the clearance implies (<see cref="SegmentExpander.ImpliedDestinationLeadIn"/>:
    /// OAK <c>TAXI G @SIG1</c> ends on 635 ft of D), judged on the route as the resolver built it. Null for a runway or
    /// destination-less clearance. The caller skips it for a route held short of a missing taxiway.
    /// </summary>
    private static string? ImpliedDestinationLeadIn(TaxiRoute route, AirportGroundLayout groundLayout, TaxiCommand taxi, string? occupiedTaxiway)
    {
        var classification = MovementAreaClassification.For(groundLayout);
        HashSet<string> named = ClearanceTaxiwayNames(taxi.Path, occupiedTaxiway);
        return SegmentExpander.ImpliedDestinationLeadIn(
            [.. route.Segments.Select(s => s.Edge.Edge)],
            taxi.DestinationParking,
            taxi.DestinationSpot,
            name => SegmentExpander.IsUnclearedMovementAreaName(name, named, classification)
        );
    }

    /// <summary><paramref name="text"/> mentions <paramref name="taxiway"/> as a whole token (<c>A1</c> is not in <c>A10</c>).</summary>
    private static bool NamesTaxiway(string text, string taxiway) =>
        Regex.IsMatch(text, $@"(?<![A-Za-z0-9]){Regex.Escape(taxiway)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase);

    /// <summary>
    /// The taxiway the aircraft occupies: <see cref="AircraftGroundOps.CurrentTaxiway"/> when the start node lies on it
    /// (a stale value — the aircraft has since left that taxiway — never counts as the taxiway it stands on), else the
    /// taxiway whose line the aircraft's position projects onto, within <see cref="RampLaneReposition.CurrentLaneMaxFt"/>:
    /// an aircraft standing on pavement whose current taxiway was never set still occupies it (issue #454). An aircraft on
    /// a stand (its start node is the parking node, or it is still recorded as parked) occupies no taxiway, however
    /// close the stand sits to one.
    ///
    /// <para>The projection is the ground code's own nearest-edge lookup, so fillet arcs, runway centerlines and RAMP
    /// edges are not candidates; an edge whose name is blank is no taxiway, and a ramp taxilane is no taxiway the
    /// controller cleared — an aircraft standing on one (a spot or stand whose nearest edge is the lane hanging off it)
    /// occupies nothing, and the clearance's own route is left to resolve as it always did. A virtual start node — one
    /// not in the layout graph — lies on a layout edge, and the projection names that edge.</para>
    /// </summary>
    private static string? OccupiedTaxiway(AircraftState aircraft, GroundNode startNode, AirportGroundLayout groundLayout)
    {
        if ((aircraft.Ground.CurrentTaxiway is { Length: > 0 } currentTwy) && startNode.Edges.Any(e => e.MatchesTaxiway(currentTwy)))
        {
            return currentTwy;
        }

        bool onStand = (startNode.Type is GroundNodeType.Parking) || (aircraft.Ground.ParkingSpot is not null);
        if (onStand || (groundLayout.FindNearestTaxiEdge(aircraft.Position) is not { } nearest))
        {
            return null;
        }

        string name = nearest.Edge.TaxiwayName;
        return
            ((nearest.DistNm * GeoMath.FeetPerNm) <= RampLaneReposition.CurrentLaneMaxFt)
            && (name.Length > 0)
            && MovementAreaClassification.For(groundLayout).IsMovementArea(name)
            ? name
            : null;
    }

    /// <summary>
    /// <paramref name="taxi"/> with <paramref name="taxiway"/> put in front of its path, or null when the path is
    /// empty, already starts with it, or the two taxiways share no direct junction node. When they meet only across
    /// a runway (SFO M↔H across 01L/19R, zero shared nodes) a prepend would re-route the crossing through a
    /// named-junction search instead of the runway-crossing bridge, so that case is left to the bridge.
    /// </summary>
    private static TaxiCommand? PrependTaxiwayJoiningPath(AirportGroundLayout groundLayout, TaxiCommand taxi, string taxiway)
    {
        if (
            (taxi.Path.Count == 0)
            || taxi.Path[0].Equals(taxiway, StringComparison.OrdinalIgnoreCase)
            || !SharesDirectJunction(groundLayout, taxiway, taxi.Path[0])
        )
        {
            return null;
        }

        // No hint was given for the prepended taxiway: a null hint keeps PathTurnHints index-aligned with Path, and the
        // controller's hint on the first cleared taxiway becomes the turn from the prepended taxiway onto it.
        return taxi with
        {
            Path = [taxiway, .. taxi.Path],
            PathTurnHints = taxi.PathTurnHints is null ? null : [null, .. taxi.PathTurnHints],
        };
    }

    /// <summary>
    /// True when a single graph node carries edges on both <paramref name="fromTaxiway"/> and
    /// <paramref name="toTaxiway"/> — i.e. the two taxiways meet at a direct junction the explicit
    /// pathfinder can turn through (mirrors <c>SegmentExpander.FindJunctionCandidates</c>). False when
    /// they meet only across a runway (separate "X - RWY" / "Y - RWY" crossing arcs, no shared node),
    /// where prepending a taxiway would mis-route the crossing.
    /// </summary>
    private static bool SharesDirectJunction(AirportGroundLayout groundLayout, string fromTaxiway, string toTaxiway)
    {
        foreach (GroundNode node in groundLayout.GetNodesOnTaxiway(fromTaxiway))
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.MatchesTaxiway(toTaxiway))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Drop the leading node references the aircraft has already taxied past.
    ///
    /// <para>The ground draw tool anchors its node list at the aircraft's position when the controller
    /// <em>starts</em> drawing, so by the time the command is dispatched the aircraft has moved on and
    /// the first few nodes are behind it. Those nodes are unreachable — a taxiing aircraft cannot
    /// reverse, and <see cref="Data.Airport.Pathfinding.GeometricAdmissibility"/> hard-rejects the
    /// ~180° turn — so routing to them makes the search loop a whole block to turn around, once per
    /// stale node (OAK N16390: a 48-node drawn route resolved to 544 segments across ten laps).</para>
    ///
    /// <para>Two exact tests, no geometry: the aircraft is standing on <paramref name="startNode"/>, so
    /// every drawn node up to and including it is behind; and any drawn node the aircraft has already
    /// traversed on its <em>current</em> taxi route is behind whether or not the start node is on the
    /// drawn line. A free-space "is this node behind my nose" test cannot be used — a stopped aircraft's
    /// heading says nothing about where its next clearance goes, which is exactly why
    /// <c>GeometricAdmissibility</c> exempts the first edge.</para>
    ///
    /// <para>Only the <em>leading contiguous run</em> of <c>#NNNN</c> tokens is scanned, and the scan
    /// stops at the first node that is still ahead: a named-taxiway clearance is untouched, and a drawn
    /// route that legitimately loops back past the aircraft later on keeps its tail.</para>
    /// </summary>
    private static TaxiCommand TrimPassedNodeRefPrefix(AircraftState aircraft, GroundNode startNode, TaxiCommand taxi)
    {
        int leadingRun = 0;
        while ((leadingRun < taxi.Path.Count) && NodeRefToken.IsNodeReference(taxi.Path[leadingRun]))
        {
            leadingRun++;
        }

        // The aircraft is standing on the start node, so every drawn node up to and including it is
        // behind. First occurrence only — a drawn route that deliberately loops back through the same
        // node later must keep that leg.
        int drop = 0;
        for (int i = 0; i < leadingRun; i++)
        {
            if (NodeRefToken.ParseNodeId(taxi.Path[i]) == startNode.Id)
            {
                drop = i + 1;
                break;
            }
        }

        // Then consume any further leading nodes the aircraft has already driven through on its
        // current route — this is what catches a start node that snapped off the drawn line.
        HashSet<int> passed = PassedRouteNodeIds(aircraft);
        while ((drop < leadingRun) && passed.Contains(NodeRefToken.ParseNodeId(taxi.Path[drop])))
        {
            drop++;
        }

        if (drop == 0)
        {
            return taxi;
        }

        Log.LogInformation(
            "[TryTaxi] {Callsign}: dropped {Count} node reference(s) already taxied past ({Dropped}); path now starts at {Head}",
            aircraft.Callsign,
            drop,
            string.Join(" ", taxi.Path.Take(drop)),
            drop < taxi.Path.Count ? taxi.Path[drop] : "(empty)"
        );

        return taxi with
        {
            Path = [.. taxi.Path.Skip(drop)],
            PathTurnHints = taxi.PathTurnHints is null ? null : [.. taxi.PathTurnHints.Skip(drop)],
        };
    }

    /// <summary>
    /// Sanity-check a route resolved from a <em>dense</em> node-reference path — every token a node and
    /// every consecutive pair one edge apart, which is what the ground draw tool emits. Such a path
    /// spells out its own geometry, so it must resolve to about one segment per node; a resolution
    /// several times longer did not follow the drawn line but found some other way there, and issuing
    /// it sends the aircraft on a tour of the airport (544 segments for 48 drawn nodes, in the case
    /// this guard was written for). Sparse or hand-typed node paths carry no such size expectation —
    /// two far-apart nodes legitimately resolve to a long route — so they always pass. Segments that start
    /// at a virtual node are not part of the drawn line and are not counted: the free-space approach leg
    /// (<see cref="Data.Airport.TaxiApproachLeg"/>) and the ramp-lane cut legs alike.
    /// </summary>
    internal static bool IsPlausibleNodeRefResolution(AirportGroundLayout groundLayout, TaxiCommand taxi, TaxiRoute route)
    {
        if ((taxi.Path.Count < 2) || !taxi.Path.All(NodeRefToken.IsNodeReference))
        {
            return true;
        }

        for (int i = 0; i < taxi.Path.Count - 1; i++)
        {
            if (!AreNodesAdjacent(groundLayout, NodeRefToken.ParseNodeId(taxi.Path[i]), NodeRefToken.ParseNodeId(taxi.Path[i + 1])))
            {
                return true;
            }
        }

        return route.Segments.Count(s => s.FromNodeId >= 0) <= (taxi.Path.Count * NodeRefSegmentFactor) + NodeRefSegmentSlack;
    }

    /// <summary>True when a single graph edge joins the two nodes.</summary>
    private static bool AreNodesAdjacent(AirportGroundLayout groundLayout, int fromNodeId, int toNodeId)
    {
        if (!groundLayout.Nodes.TryGetValue(fromNodeId, out GroundNode? fromNode))
        {
            return false;
        }

        foreach (IGroundEdge edge in fromNode.Edges)
        {
            if (edge.OtherNode(fromNode).Id == toNodeId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Nodes the aircraft has already driven through on its current taxi route: the route's own start
    /// node plus the endpoint of every segment before the one being traversed. Empty when the aircraft
    /// has no route (a fresh clearance can then only be trimmed by the start-node test).
    /// </summary>
    private static HashSet<int> PassedRouteNodeIds(AircraftState aircraft)
    {
        var passed = new HashSet<int>();
        if (aircraft.Ground.AssignedTaxiRoute is not { Segments.Count: > 0 } route)
        {
            return passed;
        }

        passed.Add(route.Segments[0].FromNodeId);
        for (int i = 0; (i < route.CurrentSegmentIndex) && (i < route.Segments.Count); i++)
        {
            passed.Add(route.Segments[i].ToNodeId);
        }

        return passed;
    }

    /// <summary>
    /// Map each cleared taxiway the controller prefixed with a <c>&gt;</c>/<c>&lt;</c> turn glyph to its
    /// <see cref="TurnDirection"/>, keyed by taxiway name, for echoing the turn in the response summary
    /// (e.g. "right on J"). Null when the command carries no turn hint.
    /// </summary>
    private static IReadOnlyDictionary<string, TurnDirection>? BuildTurnHintMap(TaxiCommand taxi)
    {
        if (taxi.PathTurnHints is not { } hints)
        {
            return null;
        }

        Dictionary<string, TurnDirection>? map = null;
        for (int i = 0; (i < taxi.Path.Count) && (i < hints.Count); i++)
        {
            if (hints[i] is { } dir)
            {
                map ??= new Dictionary<string, TurnDirection>(StringComparer.OrdinalIgnoreCase);
                map[taxi.Path[i]] = dir;
            }
        }

        return map;
    }

    private static TaxiRoute? ResolveStandardRoute(
        AirportGroundLayout groundLayout,
        GroundNode startNode,
        TaxiCommand taxi,
        out PathfindingFailure? failure,
        AircraftCategory category,
        double startHeadingTrueDeg,
        string? occupiedTaxiway
    )
    {
        // Empty path + destination runway → A* to nearest hold-short node
        if (taxi.Path.Count == 0 && taxi.DestinationRunway is not null)
        {
            TaxiRoute? runwayRoute = ResolveRunwayRouteByAStar(groundLayout, startNode, taxi.DestinationRunway, out string? runwayReason, category);
            failure = runwayRoute is null ? DestinationFailure(runwayReason ?? $"No route to runway {taxi.DestinationRunway}") : null;
            return runwayRoute;
        }

        // Crossed-runway directional anchor (issue #172 W6): when CROSS <rwy> is the only directional
        // cue (no destination runway / parking / spot), route the named taxiway(s) toward and across
        // the crossed runway and stop just past it. The far-side hold-short becomes the route terminus,
        // which disambiguates the start direction the way a named destination would — without it,
        // "TAXI G CROSS 28R" from a taxiway that crosses two runways can head the wrong way.
        GroundNode? crossAnchor = ResolveCrossedRunwayAnchor(groundLayout, startNode, taxi);

        return TaxiPathfinder.ResolveExplicitPathDetailed(
            groundLayout,
            startNode.Id,
            taxi.Path,
            out failure,
            new ExplicitPathOptions
            {
                OccupiedTaxiway = occupiedTaxiway,
                ExplicitHoldShorts = taxi.HoldShorts,
                DestinationRunway = taxi.DestinationRunway,

                DestinationHintNode = crossAnchor,
                PathTurnHints = taxi.PathTurnHints,
                StartHeadingTrue = startHeadingTrueDeg,
            },
            category
        );
    }

    /// <summary>
    /// Resolve the routing anchor for a <c>TAXI &lt;twy...&gt; CROSS &lt;rwy&gt;</c> with no other destination
    /// (issue #172 W6). The anchor is the crossed runway's hold-short on the <b>far side</b> of the start
    /// — the one reached only after crossing the runway — so the explicit pathfinder routes toward and
    /// across the runway and terminates just past it. Returns null (no anchor; legacy behaviour) when there
    /// is a real destination, no <c>CROSS</c> keyword, no named taxiways, or the runway lacks a near/far
    /// hold-short pair on the <b>last</b> named taxiway. The farthest-along crossed runway wins, so a route
    /// that crosses several runways stops just past the last one.
    ///
    /// <para>The anchor is matched against the <em>last</em> named taxiway only. The anchor sets the route
    /// terminus just past the crossed runway, which is correct only when that runway is crossed by the
    /// final leg. When an earlier taxiway crosses the runway and a later taxiway continues past it (e.g.
    /// <c>TAXI C B CROSS 33</c>, where 33 is crossed on C and B continues beyond), terminating at the
    /// runway would truncate the route before the last taxiway is reached. There the route walks through to
    /// the last taxiway's natural terminus and direction is disambiguated by the look-ahead toward the next
    /// named taxiway; the <c>CROSS</c> keyword still clears the runway crossing post-resolution.</para>
    /// </summary>
    private static GroundNode? ResolveCrossedRunwayAnchor(AirportGroundLayout layout, GroundNode startNode, TaxiCommand taxi)
    {
        if (
            taxi.Path.Count == 0
            || taxi.DestinationRunway is not null
            || taxi.DestinationParking is not null
            || taxi.DestinationSpot is not null
            || taxi.CrossRunways is not { Count: > 0 } crossings
        )
        {
            return null;
        }

        string lastTaxiway = taxi.Path[^1];
        for (int i = crossings.Count - 1; i >= 0; i--)
        {
            if (ResolveCrossedRunwayFarSideHoldShort(layout, startNode, lastTaxiway, crossings[i]) is { } anchor)
            {
                Log.LogDebug("[TryTaxi] CROSS {Rwy} anchors route toward far-side hold-short node {Anchor}", crossings[i], anchor.Id);
                return anchor;
            }
        }

        return null;
    }

    /// <summary>
    /// Find the crossed runway's hold-short on the far side of <paramref name="startNode"/> — the one
    /// reachable only by crossing the runway. Both of the runway's hold-shorts on <paramref name="lastTaxiway"/>
    /// lie on opposite sides of the runway, so the far one is the farther from the start. Returns null when
    /// the runway has fewer than two hold-shorts on the last taxiway (no near/far pair to anchor with —
    /// the runway is not crossed by the route's final leg, so it should not terminate the route).
    /// </summary>
    private static GroundNode? ResolveCrossedRunwayFarSideHoldShort(
        AirportGroundLayout layout,
        GroundNode startNode,
        string lastTaxiway,
        string crossedRunwayId
    )
    {
        var onLast = new List<GroundNode>();
        foreach (GroundNode node in layout.GetRunwayHoldShortNodes(crossedRunwayId))
        {
            if (node.Edges.Any(e => e.MatchesTaxiway(lastTaxiway)))
            {
                onLast.Add(node);
            }
        }

        if (onLast.Count < 2)
        {
            return null;
        }

        return onLast.MaxBy(n => GeoMath.DistanceNm(startNode.Position, n.Position));
    }

    /// <summary>
    /// Bare <c>TAXI &lt;rwy&gt;</c> with no taxiways named: the aircraft must already be at that runway. Anything
    /// else is rejected with a pointer at the two commands that do carry a route, so a mistyped or under-specified
    /// clearance never taxis an aircraft across the airport on a guessed route (issue #393).
    /// </summary>
    private static TaxiRoute? ResolveAdjacentRunwayRoute(
        AirportGroundLayout groundLayout,
        GroundNode startNode,
        AircraftState aircraft,
        string runwayId,
        out string? failReason
    )
    {
        failReason = null;
        string display = RunwayIdentifier.ToDisplayDesignator(runwayId);
        if (groundLayout.GetRunwayHoldShortNodes(runwayId).Count == 0)
        {
            failReason = $"No hold-short nodes for runway {display}";
            return null;
        }

        TaxiRoute? route = TaxiPathfinder.FindAdjacentRunwayRoute(
            groundLayout,
            startNode,
            (aircraft.Position, aircraft.TrueHeading),
            runwayId,
            AircraftCategorization.Categorize(aircraft.AircraftType)
        );
        if (route is null)
        {
            string where = aircraft.Ground.CurrentTaxiway is { Length: > 0 } currentTaxiway ? $" (on taxiway {currentTaxiway})" : "";
            failReason =
                $"{aircraft.Callsign} is not at runway {display}{where} — give a taxi route (TAXI <route> {display}) "
                + $"or use TAXIAUTO {display} to auto-route";
            return null;
        }

        Log.LogDebug(
            "[TryTaxi] {Callsign}: bare TAXI {Rwy} resolved to the adjacent hold-short ({SegCount} segments)",
            aircraft.Callsign,
            display,
            route.Segments.Count
        );
        return route;
    }

    private static TaxiRoute? ResolveRunwayRouteByAStar(
        AirportGroundLayout groundLayout,
        GroundNode startNode,
        string runwayId,
        out string? failReason,
        AircraftCategory category
    )
    {
        failReason = null;
        List<GroundNode> holdShortNodes = groundLayout.GetRunwayHoldShortNodes(runwayId);
        if (holdShortNodes.Count == 0)
        {
            failReason = $"No hold-short nodes for runway {RunwayIdentifier.ToDisplayDesignator(runwayId)}";
            return null;
        }

        TaxiRoute? route = TaxiPathfinder.FindRunwayRoute(groundLayout, startNode, runwayId, category);
        if (route is null)
        {
            failReason = $"No route to runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} hold-short";
            return null;
        }

        return route;
    }

    /// <summary>
    /// A clearance re-resolved with one cleared taxiway dropped: which one, the command the route was built
    /// from, and the route. <see cref="TryTaxi"/> derives the pilot-readback command from
    /// <see cref="DroppedName"/> so the readback names what could not be taken.
    /// </summary>
    private sealed record DroppedTaxiwayRoute(string DroppedName, TaxiCommand Command, TaxiRoute Route);

    /// <summary>
    /// The first cleared taxiway can be a ramp lane next to the gate that the graph simply does not connect to
    /// it — SFO B20S "TAXI M4 M1 …": M4 only joins M1, so the resolver reports it unreachable although it lies
    /// 404 ft away across the M3 lane, where a tug would push the aircraft. YAAT has no pushback model for a
    /// gate TAXI (the aircraft pivots out instead), so drop that lane, route the rest, and tell the controller.
    /// Guarded to the first taxiway only, the resolver's own "not connected" verdict for that very taxiway,
    /// gate-adjacent range (<see cref="GateAdjacentTaxiwayMaxFt"/>), and no runway between: a taxiway across the
    /// field stays a hard rejection — clearing via pavement the aircraft cannot reach is worse than refusing.
    /// The caller has already established a parking start. Null when the guard fails or the rest does not resolve.
    /// </summary>
    private static DroppedTaxiwayRoute? TryDropGateLeadOut(
        AircraftState aircraft,
        AirportGroundLayout groundLayout,
        TaxiCommand taxi,
        PathfindingFailure? failure,
        Func<TaxiCommand, TaxiRoute?> resolve
    )
    {
        if (
            taxi.Path.Count < 2
            || taxi.Path[0].StartsWith('#')
            || groundLayout.TryGetRunwayCenterlineName(taxi.Path[0], out _)
            || failure is not { Kind: FailureKind.TaxiwayNotConnected } leadOutFailure
            || !string.Equals(leadOutFailure.InfeasibleTaxiway, taxi.Path[0], StringComparison.OrdinalIgnoreCase)
            || groundLayout.FindNearestNodeOnTaxiway(aircraft.Position, taxi.Path[0], GateAdjacentTaxiwayMaxFt) is not { } leadOutNode
            || groundLayout.RunwayCenterlineBetween(aircraft.Position, leadOutNode.Position)
        )
        {
            Log.LogDebug(
                "[TryTaxi] {Callsign}: gate lead-out drop not applicable (path=[{Path}], failure={Kind}/{Twy})",
                aircraft.Callsign,
                string.Join(" ", taxi.Path),
                failure?.Kind,
                failure?.InfeasibleTaxiway
            );
            return null;
        }

        string dropped = taxi.Path[0];
        TaxiCommand withoutLeadOut = taxi with
        {
            Path = [.. taxi.Path.Skip(1)],
            PathTurnHints = taxi.PathTurnHints is null ? null : [.. taxi.PathTurnHints.Skip(1)],
        };
        TaxiRoute? route = resolve(withoutLeadOut);
        if (route is null)
        {
            return null;
        }

        string laneUsed = FirstNonRampTaxiway(route) ?? withoutLeadOut.Path[0];
        Log.LogInformation(
            "[TryTaxi] {Callsign}: dropped unreachable gate lead-out taxiway {Twy} (nearest node {NodeId}, {Dist:F0} ft away); "
                + "taxiing via {Lane}",
            aircraft.Callsign,
            dropped,
            leadOutNode.Id,
            GeoMath.DistanceNm(aircraft.Position, leadOutNode.Position) * GeoMath.FeetPerNm,
            laneUsed
        );
        route.Warnings.Add($"unable via {dropped} — no ramp connection from the gate; taxiing via {laneUsed}");
        return new DroppedTaxiwayRoute(dropped, withoutLeadOut, route);
    }

    /// <summary>
    /// A parking/spot destination whose named path cannot legally reach it (OAK "TAXI C D @GA1": GA1 is east
    /// along C, D leads away northwest, and routing over a runway to satisfy both is forbidden): drop one named
    /// taxiway at a time and keep the shortest resolution that reaches the destination, warning which element
    /// was dropped. The controller's destination wins over a contradictory via — silently failing the whole
    /// clearance helps nobody, silently honoring it via a runway back-taxi is worse. The returned command is the
    /// clearance as applied (without the dropped taxiway). Null when there is no
    /// parking/spot destination, fewer than two path elements, or no single drop resolves.
    /// </summary>
    private static DroppedTaxiwayRoute? TryDropContradictoryVia(AircraftState aircraft, TaxiCommand taxi, Func<TaxiCommand, TaxiRoute?> resolve)
    {
        if ((taxi.DestinationParking is null && taxi.DestinationSpot is null) || taxi.Path.Count < 2)
        {
            return null;
        }

        string destLabel = taxi.DestinationParking is not null ? $"@{taxi.DestinationParking}" : $"${taxi.DestinationSpot}";
        TaxiRoute? bestDropRoute = null;
        TaxiCommand? bestCommand = null;
        string? droppedName = null;
        for (int drop = 0; drop < taxi.Path.Count; drop++)
        {
            if (taxi.Path[drop].StartsWith('#'))
            {
                continue;
            }

            var reducedPath = taxi.Path.Where((_, idx) => idx != drop).ToList();
            var reducedHints = taxi.PathTurnHints?.Where((_, idx) => idx != drop).ToList();
            TaxiCommand reduced = taxi with { Path = reducedPath, PathTurnHints = reducedHints };
            TaxiRoute? candidate = resolve(reduced);
            if (candidate is not null && (bestDropRoute is null || candidate.TotalDistanceNm < bestDropRoute.TotalDistanceNm))
            {
                bestDropRoute = candidate;
                bestCommand = reduced;
                droppedName = taxi.Path[drop];
            }
        }

        if (bestDropRoute is null || bestCommand is null || droppedName is null)
        {
            return null;
        }

        Log.LogInformation(
            "[TryTaxi] {Callsign}: dropped contradictory path element {Dropped} to reach " + "{Dest}",
            aircraft.Callsign,
            droppedName,
            destLabel
        );
        bestDropRoute.Warnings.Add($"unable via {droppedName} — no route via {droppedName} reaches {destLabel}; {droppedName} omitted");
        return new DroppedTaxiwayRoute(droppedName, bestCommand, bestDropRoute);
    }

    /// <summary>
    /// A runway clearance whose resolved route never reaches a holding position for that runway: the route stops
    /// wherever the named taxiways run out (OAK <c>TAXI F C HS 33 RWY 28R</c> ends past the 33 crossing on C).
    /// </summary>
    private static bool IsRunwayRouteShortOfItsRunway(TaxiRoute route, TaxiCommand taxi) =>
        (taxi.DestinationRunway is not null)
        && (route.Segments.Count > 0)
        && !route.HoldShortPoints.Any(h => h.Reason is HoldShortReason.DestinationRunway or HoldShortReason.RouteIncomplete);

    /// <summary>
    /// What the clearance's missing taxiways leave the TAXI with: <paramref name="Held"/>, a route held short of the
    /// taxiway the route needs, or <paramref name="Refusal"/>, the reason naming that taxiway when no route is left.
    /// </summary>
    private sealed record MissingTaxiwayFallback(TaxiRoute? Held, string? Refusal);

    /// <summary>
    /// The recoveries for a clearance whose taxiways do not join up, run after every other one. First the start: when it
    /// does not reach the clearance's first taxiway through the taxiways cleared (N9225L <c>TAXI D @NEW1</c> from the E
    /// exit needs C), the aircraft holds short of the missing link on the taxiway it occupies, or, with no route at all,
    /// the refusal names that link. Then the end (issue #461, OAK <c>RWY 28R TAXI F C HS 33</c> from OLD1): the named
    /// route does not reach its destination — a runway route that resolved without reaching its runway counts — but one
    /// more movement-area taxiway would, so the route is taxied as issued and held short of it. A start that never
    /// reaches the first taxiway is refused rather than held at the end: that hold would drive the missing link silently.
    /// </summary>
    private static MissingTaxiwayFallback ApplyMissingTaxiwayFallbacks(
        StartLinkInputs inputs,
        TaxiRoute? route,
        Func<TaxiCommand, TaxiRoute?> resolve
    )
    {
        if (CheckStartReachesFirstCleared(inputs, route, resolve) is { } link)
        {
            LogStartLinkOutcome(inputs, link, route);
            if ((link.Held is not null) || (route is null))
            {
                return new MissingTaxiwayFallback(link.Held, link.Held is null ? link.Refusal : null);
            }
        }

        bool shortOfDestination = (route is null) || IsRunwayRouteShortOfItsRunway(route, inputs.Taxi);
        TaxiRoute? held = shortOfDestination ? TryHoldShortOfMissingTaxiway(inputs.Callsign, inputs.Layout, inputs.Taxi, resolve) : null;
        return new MissingTaxiwayFallback(held, null);
    }

    /// <summary>How the TAXI answers a start that does not reach its first cleared taxiway: held, refused, or the resolved route accepted.</summary>
    private static void LogStartLinkOutcome(StartLinkInputs inputs, MissingLinkOutcome link, TaxiRoute? route)
    {
        string outcome =
            link.Held is { } held ? $"holding short on {inputs.OccupiedTaxiway} at node {held.Segments[^1].ToNodeId}"
            : route is null ? $"refusing: {link.Refusal}"
            : "accepting the route resolved";
        Log.LogInformation(
            "[TryTaxi] {Callsign}: start does not reach {First} as cleared; missing link {Missing} — {Outcome}",
            inputs.Callsign,
            link.First,
            link.Missing,
            outcome
        );
    }

    /// <summary>
    /// The most candidates <see cref="TryHoldShortOfMissingTaxiway"/> resolves, nearest the destination first: each is a
    /// full route resolution, and the TAXI runs on the shared tick thread.
    /// </summary>
    private const int MaxMissingTaxiwayCandidates = 6;

    /// <summary>What every candidate of <see cref="TryHoldShortOfMissingTaxiway"/> is judged against.</summary>
    private sealed record MissingTaxiwayTarget(TaxiCommand Taxi, string LastCleared, GroundNode? DestinationNode, LatLon? Threshold);

    /// <summary>
    /// One candidate X: <paramref name="Complete"/>, the clearance with X appended resolved to its destination;
    /// <paramref name="Held"/>, that route held short of X; <paramref name="ThresholdNm"/>, how far its runway entry is from
    /// the threshold.
    /// </summary>
    private sealed record MissingTaxiwayCandidate(string Taxiway, TaxiRoute Complete, TaxiRoute Held, double ThresholdNm);

    /// <summary>
    /// Issue #461: the clearance's taxiways do not reach its destination, but one more movement-area taxiway X off the
    /// last cleared taxiway would. The route is the clearance with X appended, resolved as usual — so the junction onto X
    /// is the one the resolver's own destination-reach probe picks, with X counted as cleared — cut at the first node
    /// after the last cleared taxiway is reached that lies on X. The aircraft holds short of X there
    /// (<see cref="HoldShortReason.RouteIncomplete"/>) and the controller is told what the route needs. Among several X,
    /// a runway destination prefers the one whose runway entry is nearest the threshold (the full-length entry, the
    /// resolver's rule for a numbered-variant choice), then the better route; a gate or spot prefers the better route.
    /// A numbered variant of the last taxiway is never X for a runway: the clearance implies it. At most
    /// <see cref="MaxMissingTaxiwayCandidates"/> X are tried, those meeting the last taxiway nearest the destination
    /// first. Null when the path ends on no plain taxiway or no single extra taxiway reaches the destination.
    /// </summary>
    private static TaxiRoute? TryHoldShortOfMissingTaxiway(
        string callsign,
        AirportGroundLayout layout,
        TaxiCommand taxi,
        Func<TaxiCommand, TaxiRoute?> resolve
    )
    {
        string? lastCleared = taxi.Path.Count > 0 ? taxi.Path[^1] : null;
        if (!HasDestination(taxi) || (lastCleared is null) || lastCleared.StartsWith('#') || (layout.GetNodesOnTaxiway(lastCleared).Count == 0))
        {
            return null;
        }

        GroundNode? destinationNode = FindTaxiDestinationNode(layout, taxi);
        LatLon? threshold = taxi.DestinationRunway is { } rwy ? RouteMaterialiser.ResolveRunwayThreshold(layout.AirportId, rwy) : null;
        var target = new MissingTaxiwayTarget(taxi, lastCleared, destinationNode, threshold);
        MissingTaxiwayCandidate? best = null;
        foreach (string missing in MissingTaxiwayCandidates(layout, taxi, lastCleared, destinationNode?.Position ?? threshold))
        {
            MissingTaxiwayCandidate? candidate = EvaluateMissingTaxiway(layout, target, missing, resolve);
            if ((candidate is not null) && ((best is null) || IsBetterMissingTaxiway(candidate, best)))
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return null;
        }

        Log.LogInformation(
            "[TryTaxi] {Callsign}: route does not reach its destination as cleared; holding short of {Missing} at node {Node} ({Summary})",
            callsign,
            best.Taxiway,
            best.Held.Segments[^1].ToNodeId,
            best.Held.ToSummary()
        );
        return best.Held;
    }

    /// <summary>
    /// The clearance with <paramref name="missing"/> appended, when that reaches the destination and can be held short
    /// of <paramref name="missing"/> once the last cleared taxiway is reached; else null.
    /// </summary>
    private static MissingTaxiwayCandidate? EvaluateMissingTaxiway(
        AirportGroundLayout layout,
        MissingTaxiwayTarget target,
        string missing,
        Func<TaxiCommand, TaxiRoute?> resolve
    )
    {
        TaxiCommand taxi = target.Taxi;
        TaxiCommand augmented = taxi with { Path = [.. taxi.Path, missing], PathTurnHints = taxi.PathTurnHints is { } h ? [.. h, null] : null };
        TaxiRoute? complete = resolve(augmented);
        if ((complete is null) || !ReachesDestination(complete, taxi, target.DestinationNode))
        {
            return null;
        }

        int reached = complete.Segments.FindIndex(s => s.Edge.Edge.MatchesTaxiway(target.LastCleared));
        TaxiRoute? held = reached < 0 ? null : HoldShortBeforeTaxiway(complete, layout, reached, missing, MissingTaxiwayWarning(taxi, missing));
        return held is null
            ? null
            : new MissingTaxiwayCandidate(missing, complete, held, DestinationEntryToThresholdNm(layout, complete, target.Threshold));
    }

    /// <summary>
    /// The movement-area taxiways meeting <paramref name="lastCleared"/> that the clearance does not name — never a ramp
    /// taxilane, RAMP, a runway, or (for a runway destination) a numbered variant of the last taxiway — ordered by how
    /// near their junction with it lies to <paramref name="toward"/> (the destination; name order without one), at most
    /// <see cref="MaxMissingTaxiwayCandidates"/>.
    /// </summary>
    private static List<string> MissingTaxiwayCandidates(AirportGroundLayout layout, TaxiCommand taxi, string lastCleared, LatLon? toward)
    {
        var classification = MovementAreaClassification.For(layout);
        var cleared = new HashSet<string>(taxi.Path, StringComparer.OrdinalIgnoreCase);
        var nearestNm = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (GroundNode node in layout.GetNodesOnTaxiway(lastCleared))
        {
            double nm = toward is { } at ? GeoMath.DistanceNm(node.Position, at) : 0.0;
            foreach (string name in node.Edges.SelectMany(SegmentExpander.EdgeNames))
            {
                bool implied = (taxi.DestinationRunway is not null) && SegmentExpander.IsNumberedVariant(name, lastCleared);
                bool candidate = !implied && SegmentExpander.IsUnclearedMovementAreaName(name, cleared, classification);
                if (candidate && (!nearestNm.TryGetValue(name, out double bestNm) || (nm < bestNm)))
                {
                    nearestNm[name] = nm;
                }
            }
        }

        return
        [
            .. nearestNm
                .OrderBy(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(MaxMissingTaxiwayCandidates)
                .Select(kv => kv.Key),
        ];
    }

    /// <summary>The route reaches what the clearance named: the runway's holding position, or the gate / spot node.</summary>
    private static bool ReachesDestination(TaxiRoute route, TaxiCommand taxi, GroundNode? destinationNode)
    {
        if (taxi.DestinationRunway is not null)
        {
            return route.HoldShortPoints.Any(h => h.Reason == HoldShortReason.DestinationRunway);
        }

        return (destinationNode is not null) && (route.Segments.Count > 0) && (route.Segments[^1].ToNodeId == destinationNode.Id);
    }

    /// <summary>
    /// How far the runway entry <paramref name="route"/> holds short at lies from <paramref name="threshold"/>, in nm;
    /// zero when there is no threshold (a gate or spot, or no navdata), so the comparison falls to the route itself.
    /// </summary>
    private static double DestinationEntryToThresholdNm(AirportGroundLayout layout, TaxiRoute route, LatLon? threshold)
    {
        HoldShortPoint? entry = route.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.DestinationRunway);
        if ((threshold is not { } at) || (entry is null) || !layout.Nodes.TryGetValue(entry.NodeId, out GroundNode? node))
        {
            return 0.0;
        }

        return GeoMath.DistanceNm(node.Position, at);
    }

    /// <summary>A runway entry nearer the threshold wins (beyond 100 ft); otherwise the better route does.</summary>
    private static bool IsBetterMissingTaxiway(MissingTaxiwayCandidate candidate, MissingTaxiwayCandidate best)
    {
        const double SameEntryNm = 100.0 / GeoMath.FeetPerNm;
        if (Math.Abs(candidate.ThresholdNm - best.ThresholdNm) > SameEntryNm)
        {
            return candidate.ThresholdNm < best.ThresholdNm;
        }

        return SegmentExpander.IsBetterRoute(candidate.Complete, best.Complete);
    }

    /// <summary>
    /// <paramref name="complete"/> cut at the first node, from segment <paramref name="fromIndex"/> on, that lies on
    /// <paramref name="missing"/> — or, when that node is runway pavement, at the runway's near-side bar
    /// (<see cref="KeepOffRunwayPavement"/>): no edge of it is driven, the hold-shorts ahead of the cut are kept, and the
    /// route ends held short (<see cref="HoldShortsAtCut"/>). Warnings that only concern pavement past the cut are dropped
    /// and <paramref name="warning"/> is added. Null when the cut leaves nothing to taxi.
    /// </summary>
    private static TaxiRoute? HoldShortBeforeTaxiway(TaxiRoute complete, AirportGroundLayout layout, int fromIndex, string missing, string warning)
    {
        int keep = KeepOffRunwayPavement(complete, layout, CutBeforeTaxiway(complete, layout, fromIndex, missing));
        if (keep <= 0)
        {
            return null;
        }

        List<TaxiRouteSegment> kept = [.. complete.Segments.Take(keep)];
        var droppedNames = complete
            .Segments.Skip(keep)
            .SelectMany(s => SegmentExpander.EdgeNames(s.Edge.Edge))
            .Where(n => !kept.Any(k => k.Edge.Edge.MatchesTaxiway(n)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new TaxiRoute
        {
            Segments = kept,
            HoldShortPoints = HoldShortsAtCut(complete, kept, missing),
            Warnings = [.. complete.Warnings.Where(w => !droppedNames.Any(n => NamesTaxiway(w, n))), warning],
            MandatoryConnectorCount = complete.MandatoryConnectorCount,
        };
    }

    /// <summary>
    /// How many segments of <paramref name="complete"/>, from <paramref name="fromIndex"/> on, reach the first node on
    /// <paramref name="missing"/> without driving it; -1 when the route never meets it.
    /// </summary>
    private static int CutBeforeTaxiway(TaxiRoute complete, AirportGroundLayout layout, int fromIndex, string missing)
    {
        for (int i = fromIndex; i < complete.Segments.Count; i++)
        {
            if (complete.Segments[i].Edge.Edge.MatchesTaxiway(missing))
            {
                return i;
            }

            if (layout.Nodes.TryGetValue(complete.Segments[i].ToNodeId, out GroundNode? node) && node.Edges.Any(e => e.MatchesTaxiway(missing)))
            {
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>
    /// <paramref name="keep"/> segments of <paramref name="complete"/>, or fewer, so the route never ends on a runway: a
    /// cut past a runway's near-side bar — the node the route holds at to cross it or to reach it — and short of the
    /// far-side bar (SFO <c>TAXI A2 @B1</c> cut where A2 becomes M1 on the 01L threshold) moves back to that bar, which
    /// stays the route's runway hold. A taxiway may change its name on runway pavement, so the first node on the missing
    /// taxiway can be a runway centreline or end node. -1 when the cut is on a runway centreline and no bar comes before it.
    /// </summary>
    private static int KeepOffRunwayPavement(TaxiRoute complete, AirportGroundLayout layout, int keep)
    {
        if (keep <= 0)
        {
            return keep;
        }

        var runwayBars = complete.HoldShortPoints.Where(IsRunwayHold).Select(h => h.NodeId).ToHashSet();
        int barKeep = -1;
        bool onPavementSinceBar = false;
        for (int i = 0; i < keep; i++)
        {
            int nodeId = complete.Segments[i].ToNodeId;
            GroundNode? node = layout.Nodes.GetValueOrDefault(nodeId);
            if (runwayBars.Contains(nodeId))
            {
                (barKeep, onPavementSinceBar) = (i + 1, false);
                continue;
            }

            bool centreline = IsOnRunwayCentreline(node);
            barKeep = (barKeep > 0) && onPavementSinceBar && !centreline && (node?.Type == GroundNodeType.RunwayHoldShort) ? -1 : barKeep;
            onPavementSinceBar |= centreline;
        }

        if (barKeep > 0)
        {
            return barKeep;
        }

        return IsOnRunwayCentreline(layout.Nodes.GetValueOrDefault(complete.Segments[keep - 1].ToNodeId)) ? -1 : keep;
    }

    private static bool IsOnRunwayCentreline(GroundNode? node) => (node is not null) && AirportGroundLayout.HasRunwayCenterlineEdge(node);

    private static bool IsRunwayHold(HoldShortPoint hold) => hold.Reason is HoldShortReason.RunwayCrossing or HoldShortReason.DestinationRunway;

    /// <summary>
    /// The hold-shorts of <paramref name="complete"/> on the <paramref name="kept"/> part, ending the route held short:
    /// a runway bar at the cut stays the hold there, uncleared (the route goes no further, so the aircraft holds short of
    /// the runway); any other hold already there stands; with none, a <see cref="HoldShortReason.RouteIncomplete"/> hold
    /// of <paramref name="missing"/> is added.
    /// </summary>
    private static List<HoldShortPoint> HoldShortsAtCut(TaxiRoute complete, List<TaxiRouteSegment> kept, string missing)
    {
        int holdNodeId = kept[^1].ToNodeId;
        var keptNodes = new HashSet<int>(kept.Select(s => s.ToNodeId)) { kept[0].FromNodeId };
        List<HoldShortPoint> holdShorts =
        [
            .. complete
                .HoldShortPoints.Where(h => keptNodes.Contains(h.NodeId))
                .Select(h => (h.NodeId == holdNodeId) && h.IsCleared && IsRunwayHold(h) ? Uncleared(h) : h),
        ];
        if (!holdShorts.Any(h => h.NodeId == holdNodeId))
        {
            holdShorts.Add(
                new HoldShortPoint
                {
                    NodeId = holdNodeId,
                    Reason = HoldShortReason.RouteIncomplete,
                    TargetName = missing,
                }
            );
        }

        return holdShorts;
    }

    private static HoldShortPoint Uncleared(HoldShortPoint hold) =>
        new()
        {
            NodeId = hold.NodeId,
            Reason = hold.Reason,
            TargetName = hold.TargetName,
            Latitude = hold.Latitude,
            Longitude = hold.Longitude,
        };

    /// <summary>
    /// The controller's note for a route held short of <paramref name="missing"/>: <c>Holding short of B: route to RWY
    /// 28R needs B, not in clearance</c>. The destination reads as the command writes it (<c>RWY 28R</c>, <c>@D1</c>,
    /// <c>$9</c>); a clearance with no destination reads as its last taxiway.
    /// </summary>
    private static string MissingTaxiwayWarning(TaxiCommand taxi, string missing)
    {
        string destination =
            taxi.DestinationRunway is { } rwy ? $"RWY {RunwayIdentifier.ToDisplayDesignator(rwy)}"
            : taxi.DestinationParking is { } parking ? $"@{parking}"
            : taxi.DestinationSpot is { } spot ? $"${spot}"
            : taxi.Path.LastOrDefault(t => !t.StartsWith('#')) ?? "the destination";
        return $"Holding short of {missing}: route to {destination} needs {missing}, not in clearance";
    }

    /// <summary>What <see cref="CheckStartReachesFirstCleared"/> needs to know about the TAXI being resolved.</summary>
    private sealed record StartLinkInputs(
        string Callsign,
        AirportGroundLayout Layout,
        GroundNode StartNode,
        TaxiCommand Taxi,
        string? OccupiedTaxiway,
        AircraftCategory Category
    );

    /// <summary>
    /// A start that does not reach the clearance's first taxiway <paramref name="First"/> as cleared: the route held
    /// short of the missing link <paramref name="Missing"/>, when one can be placed, and the refusal naming that link.
    /// </summary>
    private sealed record MissingLinkOutcome(TaxiRoute? Held, string Refusal, string First, string Missing);

    /// <summary>
    /// N9225L <c>TAXI D @NEW1</c> from the E exit: the start does not reach the clearance's first taxiway through the
    /// cleared and occupied taxiways and the apron, so the way there drives a missing link X — the first uncleared
    /// movement-area taxiway on the resolved route when there is one, else on the unconstrained route to the nearest
    /// node of the first taxiway. When the aircraft occupies a taxiway and the clearance with X added ahead of its first
    /// taxiway resolves as cleared, the aircraft holds short of X on the taxiway it occupies
    /// (<c>Holding short of C: route to @NEW1 needs C, not in clearance</c>); the refusal is always returned
    /// (<c>Unable, route to D from E needs C, not in clearance</c>). Null when the start reaches the first taxiway, the
    /// clearance is a drawn route, or no X is found.
    /// </summary>
    private static MissingLinkOutcome? CheckStartReachesFirstCleared(StartLinkInputs inputs, TaxiRoute? route, Func<TaxiCommand, TaxiRoute?> resolve)
    {
        int firstIndex = FirstClearedTaxiwayAhead(inputs.Layout, inputs.StartNode, inputs.Taxi, inputs.OccupiedTaxiway);
        if (firstIndex < 0)
        {
            return null;
        }

        string first = inputs.Taxi.Path[firstIndex];
        var classification = MovementAreaClassification.For(inputs.Layout);
        HashSet<string> named = ClearanceTaxiwayNames(inputs.Taxi.Path, inputs.OccupiedTaxiway);
        bool IsBlocked(string name) => SegmentExpander.IsUnclearedMovementAreaName(name, named, classification);
        if (ReachesTaxiwayWithout(inputs.StartNode, first, IsBlocked))
        {
            return null;
        }

        IReadOnlyList<TaxiRouteSegment>? way =
            route?.Segments ?? WayToNearestNodeOf(inputs.Layout, inputs.StartNode, first, inputs.Category)?.Segments;
        if ((way is null) || (FirstBlockedBefore(way, first, IsBlocked) is not { } missing))
        {
            return null;
        }

        string refusal = inputs.OccupiedTaxiway is { } occupied
            ? $"Unable, route to {first} from {occupied} needs {missing}, not in clearance"
            : $"Unable, route to {first} needs {missing}, not in clearance";
        TaxiRoute? held = inputs.OccupiedTaxiway is null ? null : HoldShortOfMissingLink(inputs, firstIndex, missing, resolve);
        return new MissingLinkOutcome(held, refusal, first, missing);
    }

    /// <summary>
    /// The clearance with <paramref name="missing"/> put ahead of its first taxiway, held short of it on the occupied
    /// taxiway — when that route reaches what the clearance clears it to; else null.
    /// </summary>
    private static TaxiRoute? HoldShortOfMissingLink(StartLinkInputs inputs, int firstIndex, string missing, Func<TaxiCommand, TaxiRoute?> resolve)
    {
        TaxiCommand taxi = inputs.Taxi;
        TaxiCommand augmented = taxi with
        {
            Path = [.. taxi.Path.Take(firstIndex), missing, .. taxi.Path.Skip(firstIndex)],
            PathTurnHints = taxi.PathTurnHints is { } hints ? [.. hints.Take(firstIndex), null, .. hints.Skip(firstIndex)] : null,
        };
        TaxiRoute? complete = resolve(augmented);
        return (complete is not null) && ReachesClearance(complete, taxi, inputs.Layout)
            ? HoldShortBeforeTaxiway(complete, inputs.Layout, 0, missing, MissingTaxiwayWarning(taxi, missing))
            : null;
    }

    /// <summary>
    /// The index in the path of the clearance's first taxiway the aircraft is not already on: tokens naming the occupied
    /// taxiway or a taxiway at the start node are passed over. -1 for a drawn route (a <c>#node</c> token comes first) or
    /// when that token names no taxiway in the layout (a runway).
    /// </summary>
    private static int FirstClearedTaxiwayAhead(AirportGroundLayout layout, GroundNode start, TaxiCommand taxi, string? occupied)
    {
        for (int i = 0; i < taxi.Path.Count; i++)
        {
            string token = taxi.Path[i];
            if (token.StartsWith('#'))
            {
                return -1;
            }

            if (token.Equals(occupied, StringComparison.OrdinalIgnoreCase) || start.Edges.Any(e => e.MatchesTaxiway(token)))
            {
                continue;
            }

            return layout.GetNodesOnTaxiway(token).Count > 0 ? i : -1;
        }

        return -1;
    }

    /// <summary>
    /// Whether <paramref name="start"/> reaches a straight edge of <paramref name="taxiway"/> over edges carrying no
    /// <paramref name="isBlocked"/> name — never along a runway centreline.
    /// </summary>
    private static bool ReachesTaxiwayWithout(GroundNode start, string taxiway, Func<string, bool> isBlocked)
    {
        var seen = new HashSet<int> { start.Id };
        var frontier = new Queue<GroundNode>([start]);
        while (frontier.TryDequeue(out GroundNode? node))
        {
            if (node.Edges.Any(e => (e is not GroundArc) && e.MatchesTaxiway(taxiway)))
            {
                return true;
            }

            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.IsRunwayCenterline || SegmentExpander.EdgeNames(edge).Any(isBlocked))
                {
                    continue;
                }

                GroundNode next = edge.OtherNode(node);
                if (seen.Add(next.Id))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The unconstrained route from <paramref name="start"/> to the node with a straight edge of <paramref name="taxiway"/>
    /// nearest it.
    /// </summary>
    private static TaxiRoute? WayToNearestNodeOf(AirportGroundLayout layout, GroundNode start, string taxiway, AircraftCategory category)
    {
        GroundNode? nearest = layout
            .GetNodesOnTaxiway(taxiway)
            .Where(n => n.Edges.Any(e => (e is not GroundArc) && e.MatchesTaxiway(taxiway)))
            .MinBy(n => GeoMath.DistanceNm(n.Position, start.Position));
        return nearest is null ? null : TaxiPathfinder.FindRoute(layout, start.Id, nearest.Id, category);
    }

    /// <summary>
    /// The first <paramref name="isBlocked"/> name <paramref name="way"/> drives before it reaches <paramref name="taxiway"/>;
    /// free-space legs and runway centrelines are passed over. Null when the way reaches the taxiway first, or never.
    /// </summary>
    private static string? FirstBlockedBefore(IReadOnlyList<TaxiRouteSegment> way, string taxiway, Func<string, bool> isBlocked)
    {
        foreach (TaxiRouteSegment segment in way)
        {
            IGroundEdge edge = segment.Edge.Edge;
            if (VirtualNode.IsVirtualEdge(edge) || edge.IsRunwayCenterline)
            {
                continue;
            }

            string? blocked = SegmentExpander.EdgeNames(edge).FirstOrDefault(isBlocked);
            if (blocked is not null)
            {
                return blocked;
            }

            if (edge.MatchesTaxiway(taxiway))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The route reaches what <paramref name="taxi"/> clears it to: its destination (<see cref="ReachesDestination"/>),
    /// or, with none, its last taxiway — driven, or joined at the route's end (a path-only route stops where it meets it).
    /// </summary>
    private static bool ReachesClearance(TaxiRoute route, TaxiCommand taxi, AirportGroundLayout layout)
    {
        if (HasDestination(taxi))
        {
            return ReachesDestination(route, taxi, FindTaxiDestinationNode(layout, taxi));
        }

        string? last = taxi.Path.LastOrDefault(t => !t.StartsWith('#'));
        if ((last is null) || (route.Segments.Count == 0))
        {
            return false;
        }

        bool endsOnLast = layout.Nodes.TryGetValue(route.Segments[^1].ToNodeId, out GroundNode? end) && end.Edges.Any(e => e.MatchesTaxiway(last));
        return endsOnLast || route.Segments.Any(s => s.Edge.Edge.MatchesTaxiway(last));
    }

    /// <summary>
    /// The first taxiway the route actually travels once it leaves the ramp — what a controller needs to
    /// hear when a cleared lane was omitted ("taxiing via M3"). Membership arcs ("M3 - RAMP") contribute
    /// their non-RAMP name; null when the route never leaves RAMP pavement.
    /// </summary>
    private static string? FirstNonRampTaxiway(TaxiRoute route)
    {
        foreach (TaxiRouteSegment segment in route.Segments)
        {
            foreach (string name in segment.TaxiwayName.Split(" - ", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Rewrites every <c>$spot</c> via in the path to the <c>#nodeId</c> token the resolver routes through, so a
    /// clearance that names a spot on the way ("A $7B @E2") is made to pass through that spot's node. The trailing
    /// spot destination is not in the path (the parser split it out), so only vias are touched. Returns the command
    /// unchanged with the offending name when the field has no spot by that name.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="taxi">The parsed clearance.</param>
    /// <returns>The rewritten command, and the unknown spot name when one could not be resolved.</returns>
    private static (TaxiCommand Command, string? UnknownSpot) ResolveSpotVias(AirportGroundLayout layout, TaxiCommand taxi)
    {
        if (!taxi.Path.Exists(t => t.StartsWith('$')))
        {
            return (taxi, null);
        }

        var path = new List<string>(taxi.Path.Count);
        foreach (string token in taxi.Path)
        {
            if (!token.StartsWith('$'))
            {
                path.Add(token);
                continue;
            }

            string name = token[1..];
            if (layout.FindSpotNodeByName(name) is not { } spot)
            {
                return (taxi, name);
            }

            path.Add($"#{spot.Id}");
        }

        return (taxi with { Path = path }, null);
    }

    /// <summary>
    /// The name of the last spot the path is routed through, or null when it names none — the via a destination
    /// the graph cannot reach from it is refused by name ("Cannot reach parking 'E2' via 7B"), so the controller
    /// sees which part of his own clearance is the problem rather than a bare "from end of taxi route".
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="path">The clearance path, spot vias already rewritten to node references.</param>
    /// <returns>The spot's name, or null.</returns>
    private static string? LastSpotViaName(AirportGroundLayout layout, IReadOnlyList<string> path)
    {
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if (!NodeRefToken.IsNodeReference(path[i]))
            {
                continue;
            }

            if (
                layout.Nodes.TryGetValue(NodeRefToken.ParseNodeId(path[i]), out GroundNode? node)
                && node is { Type: GroundNodeType.Spot, Name: { Length: > 0 } spotName }
            )
            {
                return spotName;
            }
        }

        return null;
    }

    /// <summary>
    /// The node a parking / spot clearance ends at: <c>@</c> = helipad or parking only, <c>$</c> = spot only; null
    /// when absent or unknown.
    /// </summary>
    private static GroundNode? FindTaxiDestinationNode(AirportGroundLayout layout, TaxiCommand taxi)
    {
        if (taxi.DestinationSpot is not null)
        {
            return layout.FindSpotNodeByName(taxi.DestinationSpot);
        }

        return taxi.DestinationParking is not null
            ? layout.FindHelipadByName(taxi.DestinationParking) ?? layout.FindParkingByName(taxi.DestinationParking)
            : null;
    }

    /// <summary>A destination that cannot be resolved or reached — not tied to any one cleared taxiway.</summary>
    private static PathfindingFailure DestinationFailure(string message) => new(FailureKind.DestinationUnreachable, message, null, null, null);

    private static TaxiRoute? ResolveParkingRoute(
        AirportGroundLayout groundLayout,
        GroundNode startNode,
        TaxiCommand taxi,
        out PathfindingFailure? failure,
        AircraftCategory category,
        double startHeadingTrueDeg,
        string? occupiedTaxiway
    )
    {
        failure = null;

        string destLabel = taxi.DestinationSpot ?? taxi.DestinationParking!;
        GroundNode? destNode = FindTaxiDestinationNode(groundLayout, taxi);
        if (destNode is null)
        {
            failure = DestinationFailure($"Cannot find {(taxi.DestinationSpot is not null ? "spot" : "parking")} '{destLabel}'");
            return null;
        }

        if (taxi.Path.Count == 0)
        {
            // No explicit path — A* direct to destination
            TaxiRoute? route = TaxiPathfinder.FindRoute(groundLayout, startNode.Id, destNode.Id, category);
            if (route is null)
            {
                failure = DestinationFailure($"No route to {(taxi.DestinationSpot is not null ? "spot" : "parking")} '{destLabel}'");
                return null;
            }

            return SetDestination(route, taxi);
        }

        // Explicit path given — resolve it, then extend to destination via A*
        TaxiRoute? explicitRoute = TaxiPathfinder.ResolveExplicitPathDetailed(
            groundLayout,
            startNode.Id,
            taxi.Path,
            out failure,
            new ExplicitPathOptions
            {
                OccupiedTaxiway = occupiedTaxiway,
                ExplicitHoldShorts = taxi.HoldShorts,
                DestinationRunway = taxi.DestinationRunway,

                DestinationHintNode = destNode,
                PathTurnHints = taxi.PathTurnHints,
                StartHeadingTrue = startHeadingTrueDeg,
            },
            category
        );

        if (explicitRoute is null)
        {
            // The clearance named a spot the destination cannot be reached from (SFO "A $7B @E2": T7B's ramp end
            // has no edge to E2). Name the via rather than the graph node, so the refusal points at the clearance.
            if ((failure is { Kind: FailureKind.DestinationUnreachable }) && (LastSpotViaName(groundLayout, taxi.Path) is { } blockedVia))
            {
                string blockedKind = taxi.DestinationSpot is not null ? "spot" : "parking";
                failure = DestinationFailure($"Cannot reach {blockedKind} '{destLabel}' via {blockedVia}");
            }

            return null;
        }

        // Find where the explicit path ends. ResolveExplicitPath may have appended a
        // Shortest-A* extension to the parking destination (when SelectBestStopNode
        // cached one), so endNodeId may already be destNode.
        int endNodeId = explicitRoute.Segments.Count > 0 ? explicitRoute.Segments[^1].ToNodeId : startNode.Id;

        // The materialised explicit route already carries the controller-facing warnings ("HS … not
        // applied", "taxiing via X — not in the route issued", connector notices) and its connector
        // count; the rebuild below must keep them or a parking clearance echoes none of them.
        List<TaxiRouteSegment> combined;
        List<HoldShortPoint> holdShorts;
        List<string> warnings = [.. explicitRoute.Warnings];
        if (endNodeId == destNode.Id)
        {
            combined = [.. explicitRoute.Segments];
            holdShorts = [.. explicitRoute.HoldShortPoints];
        }
        else
        {
            // Extend from end of explicit path to destination node via A*
            TaxiRoute? extension = TaxiPathfinder.FindRoute(groundLayout, endNodeId, destNode.Id, category);
            if (extension is null)
            {
                Log.LogDebug("[TryTaxi] Cannot extend from node {EndNode} to {DestLabel}", endNodeId, destLabel);
                string destKind = taxi.DestinationSpot is not null ? "spot" : "parking";
                string reachedBy = LastSpotViaName(groundLayout, taxi.Path) is { } via ? $"via {via}" : "from end of taxi route";
                failure = DestinationFailure($"Cannot reach {destKind} '{destLabel}' {reachedBy}");
                return null;
            }

            combined = [.. explicitRoute.Segments, .. extension.Segments];

            holdShorts = [.. explicitRoute.HoldShortPoints];
            HoldShortAnnotator.AddImplicitRunwayHoldShorts(groundLayout, extension.Segments, holdShorts);
            warnings.AddRange(extension.Warnings);
        }

        // Safety net: the route resolver should eliminate reversals (a→b immediately
        // followed by b→a), but warn if one slips through so we notice regressions rather
        // than quietly producing U-turns.
        for (int i = 0; i + 1 < combined.Count; i++)
        {
            TaxiRouteSegment a = combined[i];
            TaxiRouteSegment b = combined[i + 1];
            if (a.FromNodeId == b.ToNodeId && a.ToNodeId == b.FromNodeId)
            {
                Log.LogWarning(
                    "[TryTaxi] Resolved taxi route to {DestLabel} has a reversal at index {Index}: ({FromA}→{ToA}) then ({FromB}→{ToB})",
                    destLabel,
                    i,
                    a.FromNodeId,
                    a.ToNodeId,
                    b.FromNodeId,
                    b.ToNodeId
                );
                break;
            }
        }

        return SetDestination(
            new TaxiRoute
            {
                Segments = combined,
                HoldShortPoints = holdShorts,
                Warnings = warnings,
                MandatoryConnectorCount = explicitRoute.MandatoryConnectorCount,
            },
            taxi
        );
    }

    private static TaxiRoute SetDestination(TaxiRoute route, TaxiCommand taxi)
    {
        // A clearance has exactly one destination: a spot named before a gate is a via in the path, not a second
        // destination (see ParseTaxiTokens). Both set would make the terminal phase — park or hold — ambiguous.
        if ((taxi.DestinationParking is not null) && (taxi.DestinationSpot is not null))
        {
            throw new InvalidOperationException(
                $"taxi clearance names both parking '{taxi.DestinationParking}' and spot '{taxi.DestinationSpot}' as its destination"
            );
        }

        // TaxiRoute uses init-only props, so return a new instance with destination set.
        return new TaxiRoute
        {
            Segments = route.Segments,
            HoldShortPoints = route.HoldShortPoints,
            Warnings = route.Warnings,
            MandatoryConnectorCount = route.MandatoryConnectorCount,
            DestinationParking = taxi.DestinationParking,
            DestinationSpot = taxi.DestinationSpot,
        };
    }

    /// <summary>
    /// <c>PUSH</c> in every form. During a pushback only a heading-only <c>PUSH FACE</c> / <c>PUSH TAIL</c> is taken,
    /// as an amendment (<see cref="TryAmendPushback"/>). Otherwise the form resolves to one tug goal — a bare push
    /// clears the stand, a facing turns onto it, a taxiway is pushed straight back to or lined up on, a gate or spot
    /// is reached — and <see cref="TugMovePlanner"/> plans the move from where the aircraft is. A refused plan
    /// leaves the aircraft untouched.
    /// </summary>
    /// <param name="aircraft">The aircraft to push.</param>
    /// <param name="push">The parsed command.</param>
    /// <param name="groundLayout">The airport's ground layout, or null when it has none.</param>
    /// <param name="listAircraft">Every aircraft in the world, for <see cref="OverlapRefusal"/>; null when the caller has no world.</param>
    /// <returns>The push's acceptance, or why it was refused.</returns>
    internal static CommandResult TryPushback(
        AircraftState aircraft,
        PushbackCommand push,
        AirportGroundLayout? groundLayout,
        Func<IReadOnlyList<AircraftState>>? listAircraft
    )
    {
        if (aircraft.Phases?.CurrentPhase is PushbackPhase)
        {
            return TryAmendPushback(aircraft, push, groundLayout, listAircraft);
        }

        if (aircraft.Phases?.CurrentPhase is not (AtParkingPhase or HoldingAfterPushbackPhase))
        {
            return new CommandResult(false, "Pushback requires aircraft to be at parking");
        }

        PushResolution resolved = ResolvePushTarget(aircraft, push, groundLayout);
        if (resolved.Target is not { } target)
        {
            return new CommandResult(false, resolved.Refusal);
        }

        TugPose start = PoseOf(aircraft);
        bool atStand = aircraft.Phases.CurrentPhase is AtParkingPhase;
        var request = new TugRequest
        {
            Start = start,
            StartsAtStand = atStand,
            AircraftType = aircraft.AircraftType,
            Goals = [target.Goal],
            ParkedNeighbours = ParkedNeighboursNear(aircraft, listAircraft),
            FinalFacingTrueDeg = target.FinalFacingTrueDeg,
            PreviousKind = null,
        };
        TugPlan? plan = TugMovePlanner.Plan(groundLayout, request, out string refusal);
        if (plan is null)
        {
            return new CommandResult(false, refusal);
        }

        if (OverlapRefusal(aircraft, plan, listAircraft) is { } refused)
        {
            return refused;
        }

        InstallTugMove(aircraft, groundLayout, plan, target.Terminus, atStand ? TugAmendment.For(target.Goal, start) : null);
        return CommandDispatcher.Ok(WithPushNotes(target.Message, plan));
    }

    /// <summary>
    /// The push readback with the plan's notes for the RPO appended as parentheticals: each <see cref="TugPlan.Warnings"/>
    /// entry, then, when the facing taxiway's junction is far (<see cref="TugPlan.FacingTaxiwayIsFar"/>), that the facing
    /// only chose the direction. The readback is the RPO terminal's text; the pilot's spoken readback is verbalized from
    /// the command and never carries these.
    /// </summary>
    /// <param name="readback">The readback without notes.</param>
    /// <param name="plan">The accepted plan.</param>
    /// <returns>The readback, followed by its notes when there are any.</returns>
    private static string WithPushNotes(string readback, TugPlan plan)
    {
        List<string> notes = [.. plan.Warnings.Select(PushNote)];
        if (plan.FacingTaxiwayIsFar && (plan.FacingTaxiwayName is { } facingTaxiway) && (plan.FacingJunctionNoteFt is { } junctionFt))
        {
            notes.Add($"({facingTaxiway} is {junctionFt:F0} ft away; facing only)");
        }

        return notes.Count == 0 ? readback : readback + " " + string.Join(" ", notes);
    }

    /// <summary>The RPO note for one <see cref="TugPlanWarning"/>.</summary>
    private static string PushNote(TugPlanWarning warning) =>
        warning switch
        {
            TugFoulsTaxiwayWarning foul => $"({FootprintPartName(foul.Part)} will foul taxiway {foul.Taxiway}, coordinate with ground)",
            TugLongPushWarning longPush => $"(taxiway {longPush.Taxiway} is {longPush.DistanceFt:F0} ft away; long push)",
            _ => throw new InvalidOperationException($"No push readback note for a {warning.Kind} warning"),
        };

    private static string FootprintPartName(TugFootprintPart part) =>
        part switch
        {
            TugFootprintPart.Nose => "nose",
            TugFootprintPart.Tail => "tail",
            TugFootprintPart.LeftWing => "left wing",
            TugFootprintPart.RightWing => "right wing",
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Unknown aircraft footprint part"),
        };

    /// <summary>What a <c>PUSH</c> form asks for: the goal, an explicit final facing, where it ends, and the readback.</summary>
    private sealed record PushTarget(TugGoal Goal, double? FinalFacingTrueDeg, TugTerminus Terminus, string Message);

    /// <summary>A resolved <c>PUSH</c> form, or why it cannot be resolved.</summary>
    private readonly record struct PushResolution(PushTarget? Target, string Refusal)
    {
        internal static PushResolution Refused(string refusal) => new(null, refusal);

        internal static PushResolution Of(PushTarget target) => new(target, string.Empty);
    }

    /// <summary>What a tug move leaves the aircraft doing, and on which stand when it parks.</summary>
    private enum TugTerminusKind
    {
        /// <summary>Parked on a stand: <see cref="AtParkingPhase"/>, with the stand as its parking spot.</summary>
        Stand,

        /// <summary>Holding on a ramp spot: <see cref="HoldingAfterPushbackPhase"/>, with no parking spot.</summary>
        Spot,

        /// <summary>Holding anywhere else: <see cref="HoldingAfterPushbackPhase"/>, parking spot untouched.</summary>
        Hold,
    }

    /// <summary>Where a tug move ends: the phase it leaves the aircraft in, and the stand it parks on.</summary>
    /// <param name="Kind">What the move leaves the aircraft doing.</param>
    /// <param name="StandName">The stand's name, upper-cased, for a move that parks.</param>
    private readonly record struct TugTerminus(TugTerminusKind Kind, string? StandName)
    {
        internal static TugTerminus AtStand(string name) => new(TugTerminusKind.Stand, name.ToUpperInvariant());

        internal static readonly TugTerminus OnSpot = new(TugTerminusKind.Spot, null);

        internal static readonly TugTerminus Holding = new(TugTerminusKind.Hold, null);
    }

    private static PushResolution ResolvePushTarget(AircraftState aircraft, PushbackCommand push, AirportGroundLayout? groundLayout)
    {
        if (push.Destination?.NodeId is { } nodeId)
        {
            return ResolvePushToNode(aircraft, push, groundLayout, nodeId);
        }

        if (push.Destination is { } destination)
        {
            return ResolvePushToStandOrSpot(aircraft, push, destination, groundLayout);
        }

        if (push.Taxiway is not null)
        {
            return ResolvePushToTaxiway(aircraft, push, groundLayout);
        }

        if (push.MagneticHeading is not { } heading)
        {
            return PushResolution.Of(new PushTarget(TugGoal.Clear(), null, TugTerminus.Holding, PushMessage(push, null)));
        }

        // The controller names a magnetic facing; the planner works in degrees true.
        double facingTrueDeg = MagneticDeclination.MagneticToTrue(heading.Degrees, aircraft.Position);
        return PushResolution.Of(new PushTarget(TugGoal.Facing(facingTrueDeg), null, TugTerminus.Holding, PushMessage(push, heading.ToDisplayInt())));
    }

    /// <summary>
    /// <c>PUSH &lt;taxiway&gt;</c> pushes straight back until the aircraft reaches the taxiway; with a facing taxiway or
    /// a <c>FACE</c> it lines up on the taxiway's centreline through the taxiway's exit node instead.
    /// </summary>
    private static PushResolution ResolvePushToTaxiway(AircraftState aircraft, PushbackCommand push, AirportGroundLayout? groundLayout)
    {
        if (groundLayout is null)
        {
            return PushResolution.Refused("No airport ground layout available");
        }

        string taxiway = push.Taxiway!;
        GroundNode? exitNode = groundLayout.FindExitByTaxiway(aircraft.Position, taxiway);
        if (exitNode is null)
        {
            return PushResolution.Refused($"Cannot find taxiway '{taxiway}' near aircraft");
        }

        Log.LogDebug(
            "[Pushback] {Callsign}: target taxiway {Twy} at node {NodeId} ({Lat:F6}, {Lon:F6})",
            aircraft.Callsign,
            taxiway,
            exitNode.Id,
            exitNode.Position.Lat,
            exitNode.Position.Lon
        );

        if ((push.FacingTaxiway is null) && (push.MagneticHeading is null))
        {
            return PushResolution.Of(new PushTarget(TugGoal.StraightBackTo(exitNode, taxiway), null, TugTerminus.Holding, PushMessage(push, null)));
        }

        if (ResolveTaxiwayFacingTrueDeg(aircraft, push, groundLayout, exitNode) is not { } facingTrueDeg)
        {
            return PushResolution.Refused($"Cannot find facing taxiway '{push.FacingTaxiway}' near {taxiway}");
        }

        // The readback echoes the facing the aircraft will end on, which is the taxiway's own direction (true).
        int? echoedHeading = push.MagneticHeading is null ? null : FlightPhysics.BearingToDisplayInt(facingTrueDeg);
        TugGoal goal = TugGoal.TaxiwayLine(exitNode, taxiway, facingTrueDeg) with { FacingTaxiwayName = push.FacingTaxiway };
        return PushResolution.Of(new PushTarget(goal, null, TugTerminus.Holding, PushMessage(push, echoedHeading)));
    }

    /// <summary>
    /// The facing a <c>PUSH &lt;taxiway&gt;</c> lines up on, degrees true: the taxiway's edge direction at the exit node
    /// nearest a <c>FACE</c> hint (converted to true first) or nearest the bearing toward the facing taxiway. With no
    /// such edge, the hint or the bearing itself. Null only when the facing taxiway is not near the exit node.
    /// </summary>
    private static double? ResolveTaxiwayFacingTrueDeg(
        AircraftState aircraft,
        PushbackCommand push,
        AirportGroundLayout groundLayout,
        GroundNode exitNode
    )
    {
        string taxiway = push.Taxiway!;
        if (push.MagneticHeading is { } hint)
        {
            double hintTrueDeg = MagneticDeclination.MagneticToTrue(hint.Degrees, aircraft.Position);
            double? edgeDeg = groundLayout.GetEdgeBearingForTaxiway(exitNode, taxiway, hintTrueDeg);
            Log.LogDebug(
                "[Pushback] {Callsign}: face hint {Hint:000} magnetic ({HintTrue:F1} true) → {Facing} along {PTwy}",
                aircraft.Callsign,
                hint.ToDisplayInt(),
                hintTrueDeg,
                edgeDeg?.ToString("F1") ?? "no edge, the hint itself",
                taxiway
            );
            return edgeDeg ?? hintTrueDeg;
        }

        GroundNode? facingNode = groundLayout.FindExitByTaxiway(exitNode.Position, push.FacingTaxiway!);
        if (facingNode is null)
        {
            Log.LogDebug("[Pushback] {Callsign}: cannot find facing taxiway '{FTwy}' near exit node", aircraft.Callsign, push.FacingTaxiway);
            return null;
        }

        if ((GeoMath.DistanceNm(exitNode.Position, facingNode.Position) * GeoMath.FeetPerNm) <= CoincidentFacingNodeFt)
        {
            return FacingAtJunctionTrueDeg(aircraft, push, groundLayout, exitNode, facingNode);
        }

        double bearingToFacing = GeoMath.BearingTo(exitNode.Position, facingNode.Position);
        double? edgeBearing = groundLayout.GetEdgeBearingForTaxiway(exitNode, taxiway, bearingToFacing);
        Log.LogDebug(
            "[Pushback] {Callsign}: facing {FTwy} → {Facing} along {PTwy} (bearingToFacing={Brg:F0})",
            aircraft.Callsign,
            push.FacingTaxiway,
            edgeBearing?.ToString("F1") ?? "no edge, the bearing itself",
            taxiway,
            bearingToFacing
        );
        return edgeBearing ?? bearingToFacing;
    }

    /// <summary>
    /// How close the facing taxiway's nearest node may lie to the exit node, feet, and still count as the facing
    /// taxiway meeting the taxiway right there: the bearing between two such nodes says nothing. A judgement call.
    /// </summary>
    private const double CoincidentFacingNodeFt = 10.0;

    /// <summary>
    /// How nearly equal, degrees, the two directions along the taxiway may be in their angle to the facing taxiway
    /// before the facing taxiway is taken as leaving square to it and naming neither.
    /// </summary>
    private const double FacingTieDeg = 5.0;

    /// <summary>
    /// The facing along the push-onto taxiway when the facing taxiway meets it at the exit node: the taxiway's direction
    /// nearest the facing taxiway's straight edge leading away from the junction. When that edge leaves square to the
    /// taxiway (both directions within <see cref="FacingTieDeg"/> of each other), or the facing taxiway leaves the
    /// junction by more than one straight edge, neither direction is named, and the push takes the direction nearest the
    /// aircraft's current nose — the least swing — logged as a guess.
    /// </summary>
    private static double FacingAtJunctionTrueDeg(
        AircraftState aircraft,
        PushbackCommand push,
        AirportGroundLayout groundLayout,
        GroundNode exitNode,
        GroundNode facingNode
    )
    {
        string taxiway = push.Taxiway!;
        List<double> leads =
        [
            .. facingNode
                .Edges.OfType<GroundEdge>()
                .Where(e => !e.IsRunwayCenterline && e.MatchesTaxiway(push.FacingTaxiway!))
                .Select(e => GeoMath.BearingTo(facingNode.Position, e.OtherNode(facingNode).Position)),
        ];
        if ((leads.Count == 1) && (groundLayout.GetEdgeBearingForTaxiway(exitNode, taxiway, leads[0]) is { } toward))
        {
            double reciprocal = new TrueHeading(toward).ToReciprocal().Degrees;
            double away = groundLayout.GetEdgeBearingForTaxiway(exitNode, taxiway, reciprocal) ?? reciprocal;
            double towardOffDeg = GeoMath.AbsBearingDifference(toward, leads[0]);
            double awayOffDeg = GeoMath.AbsBearingDifference(away, leads[0]);
            if (Math.Abs(towardOffDeg - awayOffDeg) > FacingTieDeg)
            {
                Log.LogDebug(
                    "[Pushback] {Callsign}: {FTwy} leaves {PTwy} at node {NodeId} on {Lead:F1} → facing {Facing:F1} along {PTwy}",
                    aircraft.Callsign,
                    push.FacingTaxiway,
                    taxiway,
                    exitNode.Id,
                    leads[0],
                    toward,
                    taxiway
                );
                return toward;
            }
        }

        double nose = aircraft.TrueHeading.Degrees;
        double leastSwing = groundLayout.GetEdgeBearingForTaxiway(exitNode, taxiway, nose) ?? nose;
        Log.LogDebug(
            "[Pushback] {Callsign}: {FTwy} leaves {PTwy} at node {NodeId} naming neither direction ({Leads}); guessing the least swing from nose {Nose:F1} → {Facing:F1}",
            aircraft.Callsign,
            push.FacingTaxiway,
            taxiway,
            exitNode.Id,
            string.Join("/", leads.Select(l => l.ToString("F1"))),
            nose,
            leastSwing
        );
        return leastSwing;
    }

    /// <summary>
    /// <c>PUSH @gate</c> (a helipad or parking stand, parked on at the end on the stand's own heading) and
    /// <c>PUSH $spot</c> (a ramp spot, held on at the end with no parking spot — the stand it left is behind it).
    /// A stand destination takes no facing: the parser refuses one, and a hand-built command carrying one is refused
    /// with the parser's words.
    /// </summary>
    private static PushResolution ResolvePushToStandOrSpot(
        AircraftState aircraft,
        PushbackCommand push,
        PushDestination destination,
        AirportGroundLayout? groundLayout
    )
    {
        if ((destination.Parking is { } stand) && HasFacing(push))
        {
            return PushResolution.Refused(GroundCommandParser.StandFacingRefusal($"PUSH @{stand}"));
        }

        if (groundLayout is null)
        {
            return PushResolution.Refused("No airport ground layout available");
        }

        return destination.Spot is { } spot
            ? ResolvePushToSpot(aircraft, push, groundLayout, spot)
            : ResolvePushToStand(groundLayout, destination.Parking!);
    }

    /// <summary>Whether the push names a facing: a heading or a taxiway to face toward.</summary>
    private static bool HasFacing(PushbackCommand push) => (push.MagneticHeading is not null) || (push.FacingTaxiway is not null);

    /// <summary>The readback for a push to a spot or node: <c>Pushing back to X[ facing Y | , heading NNN]</c>.</summary>
    private static string PushToMessage(string name, PushbackCommand push)
    {
        if (push.FacingTaxiway is not null)
        {
            return $"Pushing back to {name} facing {push.FacingTaxiway}";
        }

        return push.MagneticHeading is { } heading ? $"Pushing back to {name}, heading {heading.ToDisplayInt():000}" : $"Pushing back to {name}";
    }

    private static PushResolution ResolvePushToStand(AirportGroundLayout groundLayout, string label)
    {
        if ((groundLayout.FindHelipadByName(label) ?? groundLayout.FindParkingByName(label)) is not { } node)
        {
            return PushResolution.Refused($"Cannot find parking '{label}'");
        }

        return PushResolution.Of(new PushTarget(TugGoal.Stand(node), null, TugTerminus.AtStand(label), $"Pushing back to {label}"));
    }

    private static PushResolution ResolvePushToSpot(AircraftState aircraft, PushbackCommand push, AirportGroundLayout groundLayout, string label)
    {
        if (groundLayout.FindSpotNodeByName(label) is not { } node)
        {
            return PushResolution.Refused($"Cannot find spot '{label}'");
        }

        double? facingTrueDeg = ExplicitSpotFacingTrueDeg(aircraft, push, node, groundLayout);
        Log.LogDebug(
            "[Pushback] {Callsign}: to spot {Label}, explicit facing {Facing}",
            aircraft.Callsign,
            label,
            facingTrueDeg?.ToString("F1") ?? "none"
        );

        return PushResolution.Of(new PushTarget(TugGoal.Spot(node), facingTrueDeg, TugTerminus.OnSpot, PushToMessage(label, push)));
    }

    /// <summary>
    /// <c>PUSH #node</c>: a one-goal tug move to the node, resolved through <see cref="ResolveTugGoal"/> exactly as a
    /// <c>PUSHM</c> target is — a parking or helipad node is a stand (parked on, on its own heading, and taking no
    /// facing), a spot node a spot, and any other node a bare node the aircraft holds on. The optional facing is the
    /// one a <c>PUSH $spot</c> takes.
    /// </summary>
    private static PushResolution ResolvePushToNode(AircraftState aircraft, PushbackCommand push, AirportGroundLayout? groundLayout, int nodeId)
    {
        if (groundLayout is null)
        {
            return PushResolution.Refused("No airport ground layout available");
        }

        string token = $"#{nodeId}";
        if (ResolveTugGoal(groundLayout, token) is not { } goal)
        {
            return PushResolution.Refused($"Cannot find {DescribeTugTarget(token)}");
        }

        string name = TugGoalName(goal);
        if ((goal.Kind == TugGoalKind.Stand) && HasFacing(push))
        {
            return PushResolution.Refused(GroundCommandParser.StandFacingRefusal($"PUSH {token}"));
        }

        if (goal.Kind == TugGoalKind.Stand)
        {
            return PushResolution.Of(new PushTarget(goal, null, TugTerminus.AtStand(name), $"Pushing back to {name}"));
        }

        double? facingTrueDeg = ExplicitSpotFacingTrueDeg(aircraft, push, goal.Node!, groundLayout);
        Log.LogDebug(
            "[Pushback] {Callsign}: to node #{NodeId} ({Kind}), explicit facing {Facing}",
            aircraft.Callsign,
            nodeId,
            goal.Kind,
            facingTrueDeg?.ToString("F1") ?? "none"
        );

        TugTerminus terminus = goal.Kind == TugGoalKind.Spot ? TugTerminus.OnSpot : TugTerminus.Holding;
        return PushResolution.Of(new PushTarget(goal, facingTrueDeg, terminus, PushToMessage(name, push)));
    }

    /// <summary>
    /// The facing a <c>PUSH $spot</c> names, degrees true: a <c>FACE</c> heading converted from magnetic, or the
    /// facing taxiway's edge direction at the spot (the bearing toward it when there is no such edge). Null when the
    /// command names none, or its facing taxiway is not nearby — the spot's own nose-out facing then applies.
    /// </summary>
    private static double? ExplicitSpotFacingTrueDeg(
        AircraftState aircraft,
        PushbackCommand push,
        GroundNode destNode,
        AirportGroundLayout groundLayout
    )
    {
        if (push.MagneticHeading is { } heading)
        {
            return MagneticDeclination.MagneticToTrue(heading.Degrees, aircraft.Position);
        }

        if ((push.FacingTaxiway is not null) && (groundLayout.FindExitByTaxiway(destNode.Position, push.FacingTaxiway) is { } facingNode))
        {
            double bearingToFacing = GeoMath.BearingTo(destNode.Position, facingNode.Position);
            return groundLayout.GetEdgeBearingForTaxiway(destNode, push.FacingTaxiway, bearingToFacing) ?? bearingToFacing;
        }

        return null;
    }

    /// <summary>The <c>PUSH</c> readback: <c>Pushing back[ onto X][ facing Y | , face heading NNN]</c>.</summary>
    private static string PushMessage(PushbackCommand push, int? faceHeading)
    {
        string message = "Pushing back";
        if (push.Taxiway is not null)
        {
            message += $" onto {push.Taxiway}";
        }

        if (push.FacingTaxiway is not null)
        {
            message += $" facing {push.FacingTaxiway}";
        }
        else if (faceHeading is not null)
        {
            message += $", face heading {faceHeading:000}";
        }

        return message;
    }

    /// <summary>
    /// A heading-only <c>PUSH FACE</c> / <c>PUSH TAIL</c> during a pushback (issue #167). Accepted only while the
    /// stand push-off of a single-goal <c>PUSH</c> is still running — before any turn has begun — and then the
    /// same goal is re-planned on the new facing from the stand the push started on. Its first move is the same
    /// push-off, which keeps running; every move queued behind it is replaced. A tug move that ends on a stand is
    /// never amended: the aircraft parks on the stand's own heading.
    /// </summary>
    private static CommandResult TryAmendPushback(
        AircraftState aircraft,
        PushbackCommand push,
        AirportGroundLayout? groundLayout,
        Func<IReadOnlyList<AircraftState>>? listAircraft
    )
    {
        bool headingOnly = (push.Taxiway is null) && (push.FacingTaxiway is null) && (push.Destination is null) && (push.MagneticHeading is not null);
        if (!headingOnly)
        {
            return new CommandResult(false, "Unable, only face/tail amendment accepted during pushback");
        }

        MagneticHeading heading = push.MagneticHeading!.Value;
        var turnInProgress = new CommandResult(false, "Unable, pushback turn in progress");
        string amendedMessage = $"Pushback amended, face heading {heading.ToDisplayInt():000}";
        Phase? current = aircraft.Phases!.CurrentPhase;
        if ((current is PushbackPhase) && (aircraft.Phases.Phases[^1] is AtParkingPhase))
        {
            return new CommandResult(false, "Unable, a pushback to a stand keeps the stand's heading");
        }

        if ((current is not PushbackPhase pushOff) || !pushOff.CanAmend(aircraft))
        {
            return turnInProgress;
        }

        TugAmendment amendment = pushOff.Amendment!;
        double facingTrueDeg = MagneticDeclination.MagneticToTrue(heading.Degrees, aircraft.Position);
        if (AmendedGoal(amendment, facingTrueDeg, groundLayout) is not { } amended)
        {
            return turnInProgress;
        }

        // The re-plan starts with the same push-off that is running, so it carries on without a reversal.
        var request = new TugRequest
        {
            Start = amendment.StandStart,
            StartsAtStand = true,
            AircraftType = aircraft.AircraftType,
            Goals = [amended.Goal],
            ParkedNeighbours = ParkedNeighboursNear(aircraft, listAircraft),
            FinalFacingTrueDeg = amended.FinalFacingTrueDeg,
            PreviousKind = LastTugMotionKind(pushOff),
        };
        TugPlan? plan = TugMovePlanner.Plan(groundLayout, request, out string refusal);
        if (plan is null)
        {
            return new CommandResult(false, refusal);
        }

        aircraft.Phases.ReplaceUpcoming(TugMovePhases(plan, true, false, null, 1));
        Log.LogDebug(
            "[Pushback] {Callsign}: face heading amended to {Heading:000} ({FacingTrue:F1} true), re-planned as {Moves}",
            aircraft.Callsign,
            heading.ToDisplayInt(),
            facingTrueDeg,
            DescribeMoves(plan)
        );
        return CommandDispatcher.Ok(WithPushNotes(amendedMessage, plan));
    }

    /// <summary>
    /// The amended push's goal on the new facing: a facing goal turns onto it; a spot ends on it; a taxiway line
    /// re-snaps to the taxiway's edge direction nearest it (the facing itself when the node has no such edge). Null
    /// when the goal's node is gone, the airport has no layout for it, or the goal kind is never amended.
    /// </summary>
    private static (TugGoal Goal, double? FinalFacingTrueDeg)? AmendedGoal(
        TugAmendment amendment,
        double facingTrueDeg,
        AirportGroundLayout? groundLayout
    )
    {
        if (amendment.GoalKind == TugGoalKind.Facing)
        {
            return (TugGoal.Facing(facingTrueDeg), null);
        }

        if ((groundLayout is null) || (amendment.NodeId is not { } nodeId) || !groundLayout.Nodes.TryGetValue(nodeId, out GroundNode? node))
        {
            return null;
        }

        switch (amendment.GoalKind)
        {
            case TugGoalKind.Spot:
                return (TugGoal.Spot(node), facingTrueDeg);
            case TugGoalKind.TaxiwayLine:
                string taxiway = amendment.TaxiwayName!;
                double lineFacingDeg = groundLayout.GetEdgeBearingForTaxiway(node, taxiway, facingTrueDeg) ?? facingTrueDeg;
                return (TugGoal.TaxiwayLine(node, taxiway, lineFacingDeg), null);
            default:
                return null;
        }
    }

    /// <summary>
    /// A multi-point tug move (<c>PUSHM</c>): resolves each target to a goal, plans the move, and installs one
    /// <see cref="PushbackPhase"/> per planned move followed by the phase the terminus leaves the aircraft in.
    /// Nothing is touched until the plan succeeds, so a refused move leaves the aircraft exactly as it was.
    /// </summary>
    /// <param name="aircraft">The aircraft to move.</param>
    /// <param name="move">The parsed command: the targets in order and an optional final facing.</param>
    /// <param name="groundLayout">The airport's ground layout.</param>
    /// <param name="listAircraft">Every aircraft in the world, for <see cref="OverlapRefusal"/>; null when the caller has no world.</param>
    /// <returns>The move's acceptance, or the planner's refusal verbatim.</returns>
    internal static CommandResult TryPushbackMulti(
        AircraftState aircraft,
        PushbackMultiCommand move,
        AirportGroundLayout? groundLayout,
        Func<IReadOnlyList<AircraftState>>? listAircraft
    )
    {
        if (groundLayout is null)
        {
            return new CommandResult(false, "No airport ground layout available");
        }

        // A move already under way is a legal starting point: the tug is attached, and an RPO redirecting it is
        // ordinary. The plan is made from where the aircraft is now, and InstallTugMove drops the running move
        // along with every move still queued behind it, so the new move replaces the old one whole.
        if (aircraft.Phases?.CurrentPhase is not (AtParkingPhase or HoldingAfterPushbackPhase or PushbackPhase))
        {
            return new CommandResult(false, "A tug move requires the aircraft to be at parking, holding after a pushback, or already under tow");
        }

        var goals = new List<TugGoal>(move.Targets.Count);
        foreach (string token in move.Targets)
        {
            if (ResolveTugGoal(groundLayout, token) is not { } goal)
            {
                return new CommandResult(false, $"Cannot find {DescribeTugTarget(token)}");
            }

            goals.Add(goal);
        }

        if (goals.Count < 2)
        {
            return new CommandResult(false, "Unable, a tug move needs at least two points — use PUSH to reach a single one");
        }

        if ((goals[^1].Kind == TugGoalKind.Stand) && (move.FinalFacing is not null))
        {
            return new CommandResult(false, GroundCommandParser.StandFacingRefusal($"PUSHM to {TugGoalName(goals[^1])}"));
        }

        // The planner works in degrees true; the controller names a magnetic facing.
        double? finalFacingTrueDeg = move.FinalFacing is { } facing ? MagneticDeclination.MagneticToTrue(facing.Degrees, aircraft.Position) : null;
        bool atStand = aircraft.Phases.CurrentPhase is AtParkingPhase;

        // A redirect whose first move reverses the aircraft's last motion stops and dwells before it moves the other
        // way; a running reversal still in its dwell has not moved yet, so the last motion is the move before it.
        var request = new TugRequest
        {
            Start = PoseOf(aircraft),
            StartsAtStand = atStand,
            AircraftType = aircraft.AircraftType,
            Goals = goals,
            ParkedNeighbours = ParkedNeighboursNear(aircraft, listAircraft),
            FinalFacingTrueDeg = finalFacingTrueDeg,
            PreviousKind = LastTugMotionKind(aircraft.Phases.CurrentPhase as PushbackPhase),
        };
        TugPlan? plan = TugMovePlanner.Plan(groundLayout, request, out string refusal);
        if (plan is null)
        {
            return new CommandResult(false, refusal);
        }

        TugGoal last = goals[^1];
        string destination = TugGoalName(last);
        TugTerminus terminus = last.Kind switch
        {
            TugGoalKind.Stand => TugTerminus.AtStand(destination),
            TugGoalKind.Spot => TugTerminus.OnSpot,
            _ => TugTerminus.Holding,
        };
        if (OverlapRefusal(aircraft, plan, listAircraft) is { } refused)
        {
            return refused;
        }

        InstallTugMove(aircraft, groundLayout, plan, terminus, null);
        return CommandDispatcher.Ok(WithPushNotes($"Tug move to {destination}, {goals.Count} legs", plan));
    }

    /// <summary>
    /// The parked or held aircraft near the aircraft, as <see cref="TugMovePlanner"/> sees them — built by
    /// <see cref="TugParkedNeighbours.Build"/>, the body the client's push-route preview plans against too. Empty when
    /// the caller has no view of the other aircraft.
    /// </summary>
    /// <param name="aircraft">The aircraft the move is planned for.</param>
    /// <param name="listAircraft">Every aircraft in the world, or null when the caller has none.</param>
    /// <returns>The neighbours to plan around.</returns>
    private static IReadOnlyList<TugParkedNeighbour> ParkedNeighboursNear(AircraftState aircraft, Func<IReadOnlyList<AircraftState>>? listAircraft) =>
        listAircraft is null
            ? []
            : TugParkedNeighbours.Build(TugNeighbourCandidate.From(aircraft), listAircraft().Select(TugNeighbourCandidate.From));

    /// <summary>
    /// Why a planned tug move may not be installed: the aircraft's <see cref="GroundOutline"/> already touches or
    /// overlaps a parked or held neighbour's where it stands — their clearance is under
    /// <see cref="GroundOutlineSweep.OutlineClearanceSlackFt"/>, the same half-foot of sampling and rounding noise
    /// the detector refuses to read as room (a pair placed exactly wingtip to wingtip measures a few ten-billionths of
    /// a foot, not a clean zero). A modelled collision at the start is a scenario or placement error
    /// the tow must not paper over — the tug would be driving one aircraft through another from its first foot, and
    /// <see cref="GroundConflictDetector"/> can only hold the move at rest against the neighbour it is already
    /// touching, which reads as a move that never starts. Refusing it names both aircraft so the RPO can reposition
    /// one; the detector's clearance floor stays the runtime backstop for a pair that reaches contact some other way.
    ///
    /// <para>Null when the move may go ahead: the caller has no view of the other aircraft
    /// (<paramref name="listAircraft"/> null), or every parked or held neighbour is clear of it. An amendment to a
    /// move already under way is not checked — that aircraft is moving, and the detector owns it from there.</para>
    /// </summary>
    /// <param name="aircraft">The aircraft the move was planned for, where it stands now.</param>
    /// <param name="plan">The planned move; its first leg says whether a tug leads the nose.</param>
    /// <param name="listAircraft">Every aircraft in the world, or null when the caller has none.</param>
    /// <returns>The refusal, or null when nothing overlaps.</returns>
    private static CommandResult? OverlapRefusal(AircraftState aircraft, TugPlan plan, Func<IReadOnlyList<AircraftState>>? listAircraft)
    {
        if (listAircraft is null)
        {
            return null;
        }

        IEnumerable<TugNeighbourCandidate> others = listAircraft().Select(TugNeighbourCandidate.From);
        if (TugParkedNeighbours.FindStartOverlap(TugNeighbourCandidate.From(aircraft), plan, others) is not { } overlap)
        {
            return null;
        }

        Log.LogDebug(
            "[TugMove] {Callsign}: refused, outline overlaps {Other} at the start (clearance {ClearanceFt:F1} ft, towed nose-first={Towed})",
            aircraft.Callsign,
            overlap.NeighbourCallsign,
            overlap.ClearanceFt,
            overlap.TowedNoseFirst
        );
        return new CommandResult(false, overlap.Refusal);
    }

    /// <summary>
    /// Clears whatever the aircraft was doing and installs the planned moves, each as its own
    /// <see cref="PushbackPhase"/>, with the terminus's resting phase behind them. The first move is the stand
    /// push-off when the aircraft was parked, and only that move carries <paramref name="amendment"/>. A move
    /// ending on a stand parks the aircraft there; one ending on a ramp spot holds with no parking spot — the stand
    /// it left is behind it; one ending anywhere else holds and leaves the parking spot as it was.
    /// </summary>
    /// <param name="aircraft">The aircraft to move.</param>
    /// <param name="groundLayout">The airport's ground layout, or null.</param>
    /// <param name="plan">The planned move.</param>
    /// <param name="terminus">What the move leaves the aircraft doing.</param>
    /// <param name="amendment">The stand push-off's facing amendment, or null.</param>
    private static void InstallTugMove(
        AircraftState aircraft,
        AirportGroundLayout? groundLayout,
        TugPlan plan,
        TugTerminus terminus,
        TugAmendment? amendment
    )
    {
        bool atStand = aircraft.Phases?.CurrentPhase is AtParkingPhase;
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases!.Clear(ctx);
        aircraft.Phases = new PhaseList();
        foreach (Phase phase in TugMovePhases(plan, atStand, terminus.Kind == TugTerminusKind.Stand, amendment, 0))
        {
            aircraft.Phases.Add(phase);
        }

        aircraft.Phases.Start(ctx);
        switch (terminus.Kind)
        {
            case TugTerminusKind.Stand:
                aircraft.Ground.ParkingSpot = terminus.StandName;
                break;
            case TugTerminusKind.Spot:
                aircraft.Ground.ParkingSpot = null;
                break;
        }

        Log.LogDebug(
            "[TugMove] {Callsign}: {MoveCount} moves ({Moves}), fromStand={FromStand}, terminus={Terminus}",
            aircraft.Callsign,
            plan.Moves.Count,
            DescribeMoves(plan),
            atStand,
            terminus.Kind
        );
    }

    /// <summary>
    /// One <see cref="PushbackPhase"/> per planned move from <paramref name="firstMove"/> on, then the resting phase.
    /// Move 0 of a plan that started parked is the stand push-off, the only one that carries the amendment. A move
    /// the plan follows with another that does not dwell is flown through at speed
    /// (<see cref="PushbackPhase.ContinuesIntoNextMove"/>); the last move, and one the plan reverses after, stop.
    ///
    /// <para>Off a stand, every move up to the plan's first reversal is leg 1 of the push off it
    /// (<see cref="PushbackPhase.ContinuesStandPushOff"/>), which is what carries the push's ramp priority past the
    /// push-off; the reversal itself and everything behind it is a repositioning tow with no priority. A re-plan that
    /// keeps the running push-off (<paramref name="firstMove"/> of 1) still passes <paramref name="fromStand"/>, so
    /// its moves are judged the same way.</para>
    /// </summary>
    private static IEnumerable<Phase> TugMovePhases(TugPlan plan, bool fromStand, bool parksAtEnd, TugAmendment? amendment, int firstMove)
    {
        bool inFirstLeg = fromStand;
        for (int i = firstMove; i < plan.Moves.Count; i++)
        {
            TugMoveTrace trace = plan.Moves[i];
            bool pushOff = fromStand && (i == 0);
            bool continues = (i + 1 < plan.Moves.Count) && !plan.Moves[i + 1].Move.DwellBefore;
            if (trace.Move.DwellBefore)
            {
                inFirstLeg = false;
            }

            yield return new PushbackPhase
            {
                Move = trace.Move,
                PlannedEnd = trace.End.Position,
                StartsAtStand = pushOff,
                ContinuesIntoNextMove = continues,
                ContinuesStandPushOff = inFirstLeg && (i > 0),
                Amendment = pushOff ? amendment : null,
            };
        }

        yield return parksAtEnd ? new AtParkingPhase() : new HoldingAfterPushbackPhase();
    }

    /// <summary>
    /// The kind of the tug's last motion while <paramref name="running"/> is under way, for a re-plan to judge its
    /// first move a reversal by: the running move's own kind once it has moved, and the other kind while it is a
    /// reversal still waiting out its dwell — the aircraft last moved the previous move's way. Null with no tug move.
    /// </summary>
    private static PushbackLegKind? LastTugMotionKind(PushbackPhase? running)
    {
        if (running is null)
        {
            return null;
        }

        if (running.Move.DwellBefore && !running.HasMoved)
        {
            return running.Kind == PushbackLegKind.Push ? PushbackLegKind.Pull : PushbackLegKind.Push;
        }

        return running.Kind;
    }

    private static string DescribeMoves(TugPlan plan) =>
        string.Join(
            ", ",
            plan.Moves.Select(m => $"{m.Move.Kind} {m.Move.Shape}{(m.Move.DwellBefore ? " after a dwell" : "")} {m.PathLengthFt:F0} ft")
        );

    private static TugPose PoseOf(AircraftState aircraft) => new(aircraft.Position, aircraft.TrueHeading.Degrees);

    /// <summary>
    /// Resolves one tug-move target token to a goal by its sigil: <c>$</c> is a ramp spot, <c>@</c> a helipad or
    /// parking stand, <c>#</c> a graph node id — a stand when the node is a parking or helipad node, a spot when it
    /// is a spot node, otherwise a bare node. A token never falls back across sigils — a spot and a gate can carry
    /// the same name, and resolving <c>$7</c> to gate 7 moves the aircraft to the wrong side of the ramp.
    ///
    /// <para>Public because the ground view's push-route preview resolves the tokens of the <c>PUSHM</c> it is about
    /// to send through this same body, so the drawn path and the executed move cannot disagree about what a token
    /// means.</para>
    /// </summary>
    /// <param name="groundLayout">The airport's ground layout.</param>
    /// <param name="token">The target token, sigil included.</param>
    /// <returns>The goal, or null when the layout carries no such point.</returns>
    public static TugGoal? ResolveTugGoal(AirportGroundLayout groundLayout, string token)
    {
        string name = token[1..];
        switch (token[0])
        {
            case '$':
                return groundLayout.FindSpotNodeByName(name) is { } spot ? TugGoal.Spot(spot) : null;
            case '@':
                return (groundLayout.FindHelipadByName(name) ?? groundLayout.FindParkingByName(name)) is { } stand ? TugGoal.Stand(stand) : null;
            case '#':
                GroundNode? node = NodeRefToken.IsNodeReference(token) ? groundLayout.Nodes.GetValueOrDefault(NodeRefToken.ParseNodeId(token)) : null;
                return node?.Type switch
                {
                    null => null,
                    GroundNodeType.Parking or GroundNodeType.Helipad => TugGoal.Stand(node),
                    GroundNodeType.Spot => TugGoal.Spot(node),
                    _ => TugGoal.AtNode(node, null),
                };
            default:
                return null;
        }
    }

    /// <summary>How an unresolvable target token is named back to the controller.</summary>
    private static string DescribeTugTarget(string token) =>
        token[0] switch
        {
            '$' => $"spot '{token[1..]}'",
            '@' => $"parking '{token[1..]}'",
            _ => $"node '{token}'",
        };

    /// <summary>How a resolved target is named in a readback: its own name, else its node id.</summary>
    private static string TugGoalName(TugGoal goal) => goal.Node!.Name ?? $"#{goal.Node.Id}";

    internal static CommandResult TryAssignRunway(AircraftState aircraft, string runwayId)
    {
        RunwayInfo? runway = CommandDispatcher.ResolveRunway(aircraft, runwayId);
        if (runway is null)
        {
            return new CommandResult(false, $"Unknown runway {RunwayIdentifier.ToDisplayDesignator(runwayId)}");
        }

        aircraft.Phases ??= new PhaseList();
        aircraft.Phases.AssignedRunway = runway;

        bool arrivalContext =
            !aircraft.IsOnGround
            || (
                aircraft.Procedure.ActiveStarId is not null
                && !string.IsNullOrEmpty(aircraft.FlightPlan.Destination)
                && aircraft.Phases.DepartureClearance is null
            );

        if (arrivalContext)
        {
            NavigationCommandHandler.SyncDestinationRunwayWithActiveStar(aircraft, runway.Designator);
            if (
                aircraft.Approach.PendingClearance is { } pending
                && pending.Clearance.RunwayId is not null
                && !pending.Clearance.RunwayId.Equals(runway.Designator, StringComparison.OrdinalIgnoreCase)
            )
            {
                ApproachCommandHandler.ClearPendingApproach(aircraft);
            }
        }
        else
        {
            aircraft.Procedure.DepartureRunway = runway.Designator;
        }

        return CommandDispatcher.Ok($"Runway {RunwayIdentifier.ToDisplayDesignator(runway.Designator)}");
    }

    internal static CommandResult TryHoldPosition(AircraftState aircraft)
    {
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "Hold position requires aircraft on the ground");
        }

        Phase? phase = aircraft.Phases?.CurrentPhase;

        // On the takeoff roll the aircraft is committed; hold position does not apply.
        // Cancelling the takeoff clearance (CTOC) is the way to stop it (7110.65 3-9-11).
        if (phase is TakeoffPhase or HelicopterTakeoffPhase)
        {
            return new CommandResult(false, $"{aircraft.Callsign} is on the takeoff roll — CTOC to cancel takeoff clearance");
        }

        // Lining up onto the runway: hold position where we are rather than continuing
        // onto the centerline. This reuses LineUpPhase's hold-in-position freeze (cleared
        // by a fresh CTO or a re-issued LUAW, not RES) instead of Ground.Hold.
        if (phase is LineUpPhase lineup)
        {
            lineup.HoldPosition = true;
            aircraft.Ground.IsExpeditingTaxi = false;
            return CommandDispatcher.Ok(BuildHoldMessage(aircraft));
        }

        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        aircraft.Ground.IsExpeditingTaxi = false;
        return CommandDispatcher.Ok(BuildHoldMessage(aircraft));
    }

    private static string BuildHoldMessage(AircraftState aircraft)
    {
        Phase? phase = aircraft.Phases?.CurrentPhase;
        string? where = phase switch
        {
            TaxiingPhase => aircraft.Ground.CurrentTaxiway is { } twy ? $"on taxiway {twy}" : "while taxiing",
            CrossingRunwayPhase => "during runway crossing",
            PushbackPhase => "during pushback",
            LineUpPhase or LinedUpAndWaitingPhase => aircraft.Phases?.AssignedRunway?.Designator is { } rwy
                ? $"on runway {RunwayIdentifier.ToDisplayDesignator(rwy)}"
                : "lined up",
            RunwayExitPhase => "during runway exit",
            FollowingPhase => "while following",
            HoldingShortPhase hs => hs.HoldShort.TargetName is { } target
                ? $"already short of {HoldShortTarget.Describe(target)}"
                : "already holding short",
            HoldingInPositionPhase or HoldingAfterPushbackPhase or HoldingAfterExitPhase => "already in position",
            _ => null,
        };

        return where is null ? "Hold position" : $"Hold position ({where})";
    }

    /// <summary>
    /// RES. Releases a HOLD/GIVEWAY, and — for an aircraft that is not held at all but has been stopped or
    /// pinned to a crawl by the ground conflict detector this tick (<see cref="AircraftGroundOps.SpeedLimit"/>
    /// is at or below <see cref="GroundConflictDetector.SlowTaxiSpeedKts"/>, which the detector clears and
    /// re-derives every pass) — does what the controller plainly means by "resume taxi" to a stalled aircraft:
    /// it breaks the conflict, exactly as BREAK does. Refusing with "not held" left the controller no way to
    /// read the difference between a hold and a detector stall except to know that BREAK is the other word.
    ///
    /// <para>Only a stall or a crawl. A higher cap is the detector holding trail speed behind traffic that is
    /// itself moving — the aircraft is taxiing, just slower — and breaking detection there would switch off
    /// collision protection for <see cref="BreakDurationSeconds"/> seconds on an aircraft nobody asked about.</para>
    /// </summary>
    internal static CommandResult TryResumeTaxi(AircraftState aircraft)
    {
        if (!aircraft.Ground.IsImmobile)
        {
            if (aircraft.Ground.SpeedLimit is not { } limit || limit > GroundConflictDetector.SlowTaxiSpeedKts)
            {
                return new CommandResult(false, "Aircraft is not held");
            }

            CommandResult broken = TryBreakConflict(aircraft);
            return broken.Success ? CommandDispatcher.Ok("Resume taxi — breaking ground conflict") : broken;
        }

        aircraft.Ground.Hold = null;
        aircraft.Ground.IsExpeditingTaxi = false;
        return CommandDispatcher.Ok("Resume taxi");
    }

    /// <summary>
    /// Pre-clears upcoming RunwayCrossing hold-shorts in the aircraft's taxi
    /// route for each runway in <paramref name="runways"/>. Strict: returns
    /// failure if any runway has no matching upcoming crossing, or matches a
    /// DestinationRunway hold-short (use CTO/LUAW for those instead). Empty
    /// list is a no-op success.
    /// </summary>
    internal static CommandResult TryPreClearRouteCrossings(AircraftState aircraft, IReadOnlyList<string> runways)
    {
        if (runways.Count == 0)
        {
            return CommandDispatcher.Ok("");
        }

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        foreach (string rwy in runways)
        {
            bool matchedAny = false;
            foreach (HoldShortPoint hs in route.HoldShortPoints)
            {
                if (hs.TargetName is null)
                {
                    continue;
                }
                if (!RunwayIdentifier.Parse(hs.TargetName).Contains(rwy))
                {
                    continue;
                }

                // Multi-target pre-clear (e.g. CROSS A B) only clears intermediate crossings on an
                // in-progress route; crossing your own destination runway is handled by single-target
                // CROSS (TryExtendRouteAcrossDestinationRunway), so reject it here.
                if (hs.Reason == HoldShortReason.DestinationRunway)
                {
                    return new CommandResult(
                        false,
                        $"Cannot cross destination runway {RunwayIdentifier.ToDisplayDesignator(hs.TargetName ?? "")}; use LUAW or CTO"
                    );
                }

                matchedAny = true;
            }

            if (!matchedAny)
            {
                return new CommandResult(false, $"No hold-short for {rwy} in taxi route");
            }
        }

        // All runways validated — now actually mark each matching crossing as cleared.
        foreach (string rwy in runways)
        {
            foreach (HoldShortPoint hs in route.HoldShortPoints)
            {
                if (
                    hs.TargetName is not null
                    && hs.Reason != HoldShortReason.DestinationRunway
                    && RunwayIdentifier.Parse(hs.TargetName).Contains(rwy)
                )
                {
                    hs.IsCleared = true;
                }
            }
        }

        return CommandDispatcher.Ok("");
    }

    /// <summary>
    /// Adds or re-arms explicit hold-short points on the aircraft's taxi route for each target in
    /// <paramref name="targets"/>. A runway target re-arms the route's entry-side hold-short for that
    /// runway, revoking whatever cleared it (AutoCross, an earlier CROSS, the implicit first-crossing
    /// clearance) — a controller-issued hold-short is the most recent instruction and wins. A taxiway
    /// target adds a new ExplicitHoldShort at the first matching intersection ahead.
    ///
    /// Strict and atomic: every target is validated before any is applied, so a compound
    /// <c>RES HS 28R 09L</c> whose second target is unreachable leaves the route untouched. An empty
    /// list is a no-op success. Without a layout only targets already on the route's HoldShortPoints
    /// can be matched.
    /// </summary>
    internal static CommandResult TryAddExplicitHoldShorts(
        AircraftState aircraft,
        AirportGroundLayout? layout,
        IReadOnlyList<HoldShortTarget> targets
    )
    {
        if (targets.Count == 0)
        {
            return CommandDispatcher.Ok("");
        }

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        var plans = new List<ExplicitHoldShortPlan>(targets.Count);
        foreach (HoldShortTarget target in targets)
        {
            ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(layout, route, target);
            if (DescribeHoldShortFailure(plan.Outcome, target) is { } failure)
            {
                return failure;
            }

            plans.Add(plan);
        }

        for (int i = 0; i < targets.Count; i++)
        {
            HoldShortAnnotator.ApplyExplicitHoldShort(route, plans[i], targets[i]);
        }

        RecomputeHoldShortPositions(aircraft, layout, route);
        NotifyTaxiHoldShortsChanged(aircraft);
        return CommandDispatcher.Ok("");
    }

    /// <summary>
    /// Tells a taxi already under way that its hold-shorts changed, so the segment in progress is re-aimed
    /// at the new bar on the next tick instead of driving on to the junction node it was set up for.
    /// </summary>
    private static void NotifyTaxiHoldShortsChanged(AircraftState aircraft) =>
        (aircraft.Phases?.CurrentPhase as TaxiingPhase)?.NotifyHoldShortsChanged();

    /// <summary>
    /// Flags the bar just armed as one the aircraft cannot make — it is closer than the distance needed to
    /// brake to a stop — and answers whether it did. The taxi phase moves the stop forward to the point it
    /// can reach; the answer is decided here, at dispatch, because the controller and the crew are told in
    /// the same breath as the clearance. What cannot be complied with is refused rather than read back:
    /// P/CG "UNABLE" is "inability to comply with a specific instruction, request, or clearance", and the
    /// instruction here is AIM 2-3-5.b.3's — "the pilot MUST STOP so that no part of the aircraft extends
    /// beyond the holding position marking".
    /// </summary>
    private static bool MarkUnmakeableHoldShort(AircraftState aircraft, AirportGroundLayout layout, TaxiRoute route, ExplicitHoldShortPlan plan)
    {
        int nodeId = plan.Outcome switch
        {
            ExplicitHoldShortOutcome.ReArm when plan.Existing is { } existing => existing.NodeId,
            ExplicitHoldShortOutcome.Add => plan.NodeId,
            _ => -1,
        };

        if (route.GetHoldShortAt(nodeId) is not { IsCleared: false } bar)
        {
            return false;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        if (!TaxiingPhase.IsHoldShortUnmakeable(layout, route, aircraft, category, bar))
        {
            return false;
        }

        bar.Unable = true;
        return true;
    }

    /// <summary>
    /// The rejection for a hold-short outcome the aircraft can't honour, or <c>null</c> when the plan
    /// is applicable (including the destination-runway no-op, which is a success).
    /// </summary>
    private static CommandResult? DescribeHoldShortFailure(ExplicitHoldShortOutcome outcome, HoldShortTarget target) =>
        outcome switch
        {
            ExplicitHoldShortOutcome.AlreadyEntered => new CommandResult(
                false,
                $"Already entered RWY {RunwayIdentifier.ToDisplayDesignator(target.Target)}; use HOLD or issue a new TAXI"
            ),
            ExplicitHoldShortOutcome.NotOnRoute => new CommandResult(false, $"No match for HS {target.ToCanonical()} in taxi route"),
            _ => null,
        };

    /// <summary>
    /// Recomputes every hold-short's stop position after the set of hold-shorts changed. A newly added
    /// taxiway hold-short has no offset until this runs, and a re-armed one needs its offset re-applied.
    /// </summary>
    private static void RecomputeHoldShortPositions(AircraftState aircraft, AirportGroundLayout? layout, TaxiRoute route)
    {
        if (layout is null)
        {
            return;
        }

        double aircraftLengthFt =
            FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(aircraft.AircraftType);
        HoldShortAnnotator.ComputeHoldShortPositions(layout, route, aircraftLengthFt);
    }

    /// <summary>
    /// Applies a combined set of runway-crossing clearances and hold-short targets to the
    /// aircraft's taxi route — the shared core behind both RES and CROSS. Crossings are
    /// validated and pre-cleared first (atomic via <see cref="TryPreClearRouteCrossings"/>),
    /// then hold-shorts are added/re-armed (atomic via <see cref="TryAddExplicitHoldShorts"/>).
    /// Returns the first failure without applying later steps; empty lists are a no-op success.
    /// </summary>
    internal static CommandResult TryApplyRouteCrossingsAndHoldShorts(
        AircraftState aircraft,
        AirportGroundLayout? layout,
        IReadOnlyList<string> crossRunways,
        IReadOnlyList<HoldShortTarget> holdShorts
    )
    {
        CommandResult preClear = TryPreClearRouteCrossings(aircraft, crossRunways);
        if (!preClear.Success)
        {
            return preClear;
        }

        return TryAddExplicitHoldShorts(aircraft, layout, holdShorts);
    }

    internal static CommandResult TryCrossRunway(AircraftState aircraft, CrossRunwayCommand cross, AirportGroundLayout? layout)
    {
        // Bare CROSS: clear the next uncleared hold-short on the route.
        if (cross.RunwayIds.Count == 0 && cross.HoldShorts.Count == 0)
        {
            return TryCrossNextHoldShort(aircraft);
        }

        // A single runway with no hold-short modifier keeps its dedicated path, which can also
        // cross a *destination* runway to the far side (TryExtendRouteAcrossDestinationRunway)
        // and drives the CROSS; HOLD chaining. Multi-runway / HS forms use the shared engine.
        if (cross.RunwayIds.Count == 1 && cross.HoldShorts.Count == 0)
        {
            return TryCrossSingleRunway(aircraft, cross.RunwayIds[0]);
        }

        return TryCrossMultiple(aircraft, cross, layout);
    }

    /// <summary>
    /// Multi-runway (and/or HS-modified) CROSS. If the aircraft is holding short of one of the
    /// listed runways, that clearance is satisfied immediately (it starts crossing); the rest are
    /// pre-cleared on the upcoming route and any hold-shorts are armed, reusing the same atomic
    /// engine as RES (<see cref="TryApplyRouteCrossingsAndHoldShorts"/>).
    /// </summary>
    private static CommandResult TryCrossMultiple(AircraftState aircraft, CrossRunwayCommand cross, AirportGroundLayout? layout)
    {
        var holdPhase = aircraft.Phases?.CurrentPhase as HoldingShortPhase;
        string? currentHoldMatch = null;
        if (holdPhase is not null)
        {
            foreach (string rwy in cross.RunwayIds)
            {
                if (HoldShortAnnotator.TargetMatches(holdPhase.HoldShort.TargetName, rwy))
                {
                    currentHoldMatch = rwy;
                    break;
                }
            }
        }

        // Can't cross a destination runway you're holding short of mid-route (use LUAW/CTO).
        if (currentHoldMatch is not null && holdPhase!.HoldShort.Reason == HoldShortReason.DestinationRunway && !HasArrivedAtHoldShort(aircraft))
        {
            return new CommandResult(
                false,
                $"Cannot cross destination runway {RunwayIdentifier.ToDisplayDesignator(holdPhase.HoldShort.TargetName ?? "")}; use LUAW or CTO"
            );
        }

        // The current-hold runway is satisfied via the phase, so only the remaining listed
        // runways are pre-cleared as upcoming crossings.
        IReadOnlyList<string> upcoming = currentHoldMatch is null
            ? cross.RunwayIds
            : [.. cross.RunwayIds.Where(r => !string.Equals(r, currentHoldMatch, StringComparison.OrdinalIgnoreCase))];

        CommandResult applied = TryApplyRouteCrossingsAndHoldShorts(aircraft, layout, upcoming, cross.HoldShorts);
        if (!applied.Success)
        {
            return applied;
        }

        if (currentHoldMatch is not null)
        {
            CommandResult continuation = TryPrepareCompletedRouteCrossing(aircraft, holdPhase!);
            if (!continuation.Success)
            {
                return continuation;
            }

            holdPhase!.SatisfyClearance(ClearanceType.RunwayCrossing);
        }

        return CommandDispatcher.Ok(CommandDescriber.DescribeNatural(cross));
    }

    private static CommandResult TryCrossSingleRunway(AircraftState aircraft, string target)
    {
        // Currently holding short AT the requested target: satisfy the clearance now.
        // Target match works for both runway designators (e.g. CROSS 28R against
        // "28R/10L") and taxiway/intersection names (e.g. CROSS B against "B").
        if (aircraft.Phases?.CurrentPhase is HoldingShortPhase holdPhase && HoldShortAnnotator.TargetMatches(holdPhase.HoldShort.TargetName, target))
        {
            // A DestinationRunway hold-short is a departure hold (use LUAW/CTO) only while the
            // taxi is still in progress. Once the route has completed at it, CROSS undesignates
            // the runway and taxis the aircraft across to the far-side hold-short instead.
            if (holdPhase.HoldShort.Reason == HoldShortReason.DestinationRunway && !HasArrivedAtHoldShort(aircraft))
            {
                return new CommandResult(
                    false,
                    $"Cannot cross destination runway {RunwayIdentifier.ToDisplayDesignator(holdPhase.HoldShort.TargetName ?? "")}; use LUAW or CTO"
                );
            }

            CommandResult continuation = TryPrepareCompletedRouteCrossing(aircraft, holdPhase);
            if (!continuation.Success)
            {
                return continuation;
            }

            holdPhase.SatisfyClearance(ClearanceType.RunwayCrossing);
            return CommandDispatcher.Ok($"Cross {target}");
        }

        // Either not holding short, or holding short of a different target:
        // pre-clear matching upcoming hold-short(s) on the taxi route. Accepts
        // both runway designators and taxiway/intersection names.
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        bool matchedAny = false;
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            if (!HoldShortAnnotator.TargetMatches(hs.TargetName, target))
            {
                continue;
            }

            if (hs.Reason == HoldShortReason.DestinationRunway)
            {
                return TryExtendRouteAcrossDestinationRunway(aircraft, hs);
            }

            matchedAny = true;
        }

        if (!matchedAny)
        {
            return new CommandResult(false, $"No hold-short for {target} in taxi route");
        }

        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            if (HoldShortAnnotator.TargetMatches(hs.TargetName, target) && hs.Reason != HoldShortReason.DestinationRunway)
            {
                hs.IsCleared = true;
            }
        }

        return CommandDispatcher.Ok($"Cross {target}");
    }

    /// <summary>
    /// True when the aircraft is standing AT its hold short rather than still taxiing to it: the route completed
    /// there, or there is no route at all because the aircraft was put at the bar by something other than a taxi
    /// clearance (an <c>ATXI</c> to a runway). A destination-runway hold is a departure hold — CROSS is refused
    /// for LUAW/CTO — only while the taxi is still running; once the aircraft is at the bar, CROSS undesignates
    /// the runway and taxis it across.
    /// </summary>
    internal static bool HasArrivedAtHoldShort(AircraftState aircraft) => aircraft.Ground.AssignedTaxiRoute is null or { IsComplete: true };

    private static CommandResult TryPrepareCompletedRouteCrossing(AircraftState aircraft, HoldingShortPhase holdPhase)
    {
        if (aircraft.Ground.AssignedTaxiRoute is { IsComplete: false })
        {
            return CommandDispatcher.Ok("");
        }

        if (aircraft.Phases is not { } phases || phases.Phases.Skip(phases.CurrentIndex + 1).Any(static p => p is CrossingRunwayPhase))
        {
            return CommandDispatcher.Ok("");
        }

        if (!IsRunwayHoldShort(aircraft, holdPhase.HoldShort, out RunwayIdentifier runwayId))
        {
            return CommandDispatcher.Ok("");
        }

        AirportGroundLayout? layout = aircraft.Ground.Layout;
        if (layout is null)
        {
            return new CommandResult(false, "No airport ground layout available");
        }

        CompletedRouteCrossing? crossing = FindCompletedRouteCrossing(aircraft, layout, holdPhase.HoldShort.NodeId, runwayId);
        if (crossing is null)
        {
            return new CommandResult(false, $"No crossing route found for {holdPhase.HoldShort.TargetName ?? "runway"}");
        }

        // Mark the synthetic crossing route complete up front: CrossingRunwayPhase.HandRouteBack re-asserts
        // this same index when the crossing finishes, and this early write is what the overlay and the
        // IsComplete readers see for the whole crossing.
        crossing.Route.CurrentSegmentIndex = crossing.Route.Segments.Count;
        aircraft.Ground.AssignedTaxiRoute = crossing.Route;
        phases.ReplaceUpcoming([
            new CrossingRunwayPhase(holdPhase.HoldShort.NodeId, crossing.ExitNodeId, runwayId.ToString()),
            new HoldingInPositionPhase(),
        ]);
        return CommandDispatcher.Ok("");
    }

    private static bool IsRunwayHoldShort(AircraftState aircraft, HoldShortPoint holdShort, out RunwayIdentifier runwayId)
    {
        runwayId = default;
        AirportGroundLayout? layout = aircraft.Ground.Layout;
        if (
            layout is null
            || !layout.Nodes.TryGetValue(holdShort.NodeId, out GroundNode? node)
            || node.Type != GroundNodeType.RunwayHoldShort
            || node.RunwayId is not { } nodeRunwayId
        )
        {
            return false;
        }

        runwayId = nodeRunwayId;
        return true;
    }

    private static CompletedRouteCrossing? FindCompletedRouteCrossing(
        AircraftState aircraft,
        AirportGroundLayout layout,
        int holdShortNodeId,
        RunwayIdentifier runwayId
    )
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        CompletedRouteCrossing? best = null;
        double bestDistance = double.MaxValue;

        foreach (GroundNode candidate in layout.Nodes.Values)
        {
            if (
                candidate.Id == holdShortNodeId
                || candidate.Type != GroundNodeType.RunwayHoldShort
                || candidate.RunwayId is not { } candidateRunway
                || !candidateRunway.Equals(runwayId)
            )
            {
                continue;
            }

            TaxiRoute? route = TaxiPathfinder.FindRoute(layout, holdShortNodeId, candidate.Id, category);
            if (route is null || !RouteTraversesRunway(layout, route, runwayId))
            {
                continue;
            }

            if (route.TotalDistanceNm >= bestDistance)
            {
                continue;
            }

            if (route.GetHoldShortAt(candidate.Id) is { } exitHoldShort)
            {
                exitHoldShort.IsCleared = true;
            }

            best = new CompletedRouteCrossing(route, candidate.Id);
            bestDistance = route.TotalDistanceNm;
        }

        return best;
    }

    private static bool RouteTraversesRunway(AirportGroundLayout layout, TaxiRoute route, RunwayIdentifier runwayId)
    {
        foreach (TaxiRouteSegment segment in route.Segments)
        {
            if (segment.Edge.Edge.MatchesRunway(runwayId.End1) || segment.Edge.Edge.MatchesRunway(runwayId.End2))
            {
                return true;
            }

            // A crossing that runs straight through the runway centerline intersection (e.g. a
            // through-taxiway like B at OAK) uses plain taxiway edges, not runway-tagged ones — so
            // detect it by the on-centerline node the route passes through.
            if (NodeIsOnRunwayCenterline(layout, segment.ToNodeId, runwayId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NodeIsOnRunwayCenterline(AirportGroundLayout layout, int nodeId, RunwayIdentifier runwayId)
    {
        if (!layout.Nodes.TryGetValue(nodeId, out GroundNode? node))
        {
            return false;
        }

        foreach (IGroundEdge edge in node.Edges)
        {
            if (edge.IsRunwayCenterline && (edge.MatchesRunway(runwayId.End1) || edge.MatchesRunway(runwayId.End2)))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record CompletedRouteCrossing(TaxiRoute Route, int ExitNodeId);

    /// <summary>
    /// CROSS issued for the runway the aircraft is taxiing TO — its route's
    /// <see cref="HoldShortReason.DestinationRunway"/> hold-short — before it has arrived.
    /// Extends the still-in-progress route across the runway to the far-side hold-short and
    /// converts the destination hold-short into a pre-cleared <see cref="HoldShortReason.RunwayCrossing"/>,
    /// so <see cref="TaxiingPhase"/> taxis across on arrival instead of stopping for departure.
    /// </summary>
    private static CommandResult TryExtendRouteAcrossDestinationRunway(AircraftState aircraft, HoldShortPoint terminalHoldShort)
    {
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        if (!IsRunwayHoldShort(aircraft, terminalHoldShort, out RunwayIdentifier runwayId))
        {
            return new CommandResult(
                false,
                $"Cannot cross destination runway {RunwayIdentifier.ToDisplayDesignator(terminalHoldShort.TargetName ?? "")}; use LUAW or CTO"
            );
        }

        AirportGroundLayout? layout = aircraft.Ground.Layout;
        if (layout is null)
        {
            return new CommandResult(false, "No airport ground layout available");
        }

        CompletedRouteCrossing? crossing = FindCompletedRouteCrossing(aircraft, layout, terminalHoldShort.NodeId, runwayId);
        if (crossing is null)
        {
            return new CommandResult(false, $"No crossing route found for {terminalHoldShort.TargetName ?? "runway"}");
        }

        // Append the crossing (terminal hold-short → far-side hold-short) onto the live route
        // so TaxiingPhase finds a forward same-runway exit when it reaches the hold-short.
        route.Segments.AddRange(crossing.Route.Segments);
        foreach (HoldShortPoint hs in crossing.Route.HoldShortPoints)
        {
            if (hs.NodeId != terminalHoldShort.NodeId && route.GetHoldShortAt(hs.NodeId) is null)
            {
                route.HoldShortPoints.Add(hs);
            }
        }

        // Undesignate: the destination runway becomes a pre-cleared crossing.
        terminalHoldShort.Reason = HoldShortReason.RunwayCrossing;
        terminalHoldShort.IsCleared = true;

        return CommandDispatcher.Ok($"Cross {terminalHoldShort.TargetName}");
    }

    /// <summary>
    /// Bare <c>CROSS</c> (no runway argument). Clears exactly one — the next
    /// uncleared hold-short — either the current <see cref="HoldingShortPhase"/>
    /// or the first uncleared point on the taxi route. Rejects when that
    /// hold-short is the destination runway (use CTO/LUAW) and when there is no
    /// remaining uncleared hold-short.
    /// </summary>
    private static CommandResult TryCrossNextHoldShort(AircraftState aircraft)
    {
        if (aircraft.Phases?.CurrentPhase is HoldingShortPhase holdPhase)
        {
            if (holdPhase.HoldShort.Reason == HoldShortReason.DestinationRunway && !HasArrivedAtHoldShort(aircraft))
            {
                return new CommandResult(
                    false,
                    $"Cannot cross destination runway {RunwayIdentifier.ToDisplayDesignator(holdPhase.HoldShort.TargetName ?? "")}; use LUAW or CTO"
                );
            }

            CommandResult continuation = TryPrepareCompletedRouteCrossing(aircraft, holdPhase);
            if (!continuation.Success)
            {
                return continuation;
            }

            holdPhase.SatisfyClearance(ClearanceType.RunwayCrossing);
            return CommandDispatcher.Ok(DescribeHoldShortRelease(holdPhase.HoldShort.TargetName));
        }

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        HoldShortPoint? next = null;
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            if (!hs.IsCleared)
            {
                next = hs;
                break;
            }
        }

        if (next is null)
        {
            return new CommandResult(false, "No upcoming hold-short to cross");
        }

        if (next.Reason == HoldShortReason.DestinationRunway)
        {
            return TryExtendRouteAcrossDestinationRunway(aircraft, next);
        }

        next.IsCleared = true;
        return CommandDispatcher.Ok(DescribeHoldShortRelease(next.TargetName));
    }

    /// <summary>
    /// Echo for a bare <c>CROSS</c> release. "Cross" is runway vocabulary (7110.65 §3-7-2.a.3); releasing a
    /// spot hold-short is the §3-7-2.a.1 "continue taxiing", so the echo names the spot that way instead.
    /// </summary>
    private static string DescribeHoldShortRelease(string? targetName) =>
        targetName switch
        {
            null => "Cross next hold-short",
            var spot when HoldShortTarget.IsSpotTargetName(spot) => $"Continue taxi past {HoldShortTarget.Describe(spot)}",
            var target => $"Cross {target}",
        };

    internal static CommandResult TryHoldShort(AircraftState aircraft, HoldShortCommand hs, AirportGroundLayout? groundLayout)
    {
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "Hold short requires aircraft on the ground");
        }

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        if (groundLayout is null)
        {
            return new CommandResult(false, "No ground layout available");
        }

        ExplicitHoldShortPlan plan = HoldShortAnnotator.PlanExplicitHoldShort(groundLayout, route, hs.Target);
        if (DescribeHoldShortFailure(plan.Outcome, hs.Target) is { } failure)
        {
            return failure;
        }

        HoldShortAnnotator.ApplyExplicitHoldShort(route, plan, hs.Target);
        RecomputeHoldShortPositions(aircraft, groundLayout, route);

        aircraft.Ground.IsExpeditingTaxi = false;

        bool unable = MarkUnmakeableHoldShort(aircraft, groundLayout, route, plan);
        NotifyTaxiHoldShortsChanged(aircraft);

        Log.LogDebug(
            "[HS] {Callsign}: hold short of {Target} ({Outcome}), unable={Unable}",
            aircraft.Callsign,
            hs.Target.ToCanonical(),
            plan.Outcome,
            unable
        );

        return CommandDispatcher.Ok(
            unable ? $"Unable to hold short of {hs.Target.ToNatural()} — stopping" : $"Hold short of {hs.Target.ToNatural()}"
        );
    }

    internal static CommandResult TryFollow(
        AircraftState aircraft,
        FollowGroundCommand follow,
        AirportGroundLayout? groundLayout,
        Func<string, AircraftState?>? findAircraft
    )
    {
        Phase? currentPhase = aircraft.Phases?.CurrentPhase;
        if (currentPhase is null)
        {
            return new CommandResult(false, "Aircraft has no active phase");
        }

        // Must be on the ground in a phase that accepts Follow
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "Follow requires the aircraft to be on the ground");
        }

        if (string.Equals(follow.TargetCallsign, aircraft.Callsign, StringComparison.OrdinalIgnoreCase))
        {
            return new CommandResult(false, "Aircraft cannot follow itself");
        }

        // Validate the leader when a lookup is available (minimal harnesses pass null and skip,
        // same graceful contract as DispatchContext.FindAircraft's other consumers).
        if (findAircraft is not null)
        {
            AircraftState? leader = findAircraft(follow.TargetCallsign);
            if (leader is null)
            {
                return new CommandResult(false, $"No aircraft {follow.TargetCallsign}");
            }

            if (!leader.IsOnGround)
            {
                return new CommandResult(false, $"{follow.TargetCallsign} is not on the ground");
            }
        }

        CommandAcceptance acceptance = currentPhase.CanAcceptCommand(CanonicalCommandType.FollowGround);
        if (acceptance.IsRejected)
        {
            string reason = acceptance.Reason ?? $"Cannot follow during {currentPhase.Name}";
            return new CommandResult(false, reason);
        }

        // Replace phases with FollowingPhase. Clear() marks the active phase as Skipped
        // and advances CurrentIndex past the end, but does not remove the phase entries —
        // truncate the list before adding so Start() lands on the new FollowingPhase at index 0.
        PhaseList phases = aircraft.Phases!;
        // Clear any active ground hold (HOLD/GIVEWAY) so FollowingPhase is not frozen by IsImmobile —
        // FOLLOWG is a fresh movement clearance, same as TryTaxi/TryAirTaxi which also reset Hold.
        aircraft.Ground.Hold = null;
        phases.Clear(CommandDispatcher.BuildMinimalContext(aircraft, groundLayout));
        phases.Phases.Clear();
        phases.Phases.Add(new FollowingPhase(follow.TargetCallsign));
        phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, groundLayout));

        return CommandDispatcher.Ok($"Follow {follow.TargetCallsign}");
    }

    internal static CommandResult TryGiveWay(AircraftState aircraft, string targetCallsign)
    {
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "Give way requires aircraft on the ground");
        }

        if (aircraft.Ground.AssignedTaxiRoute is null)
        {
            return new CommandResult(false, "Aircraft must have a taxi route assigned");
        }

        aircraft.Ground.Hold = HoldDirective.GiveWay(targetCallsign);
        aircraft.Ground.IsExpeditingTaxi = false;
        return CommandDispatcher.Ok($"Give way to {targetCallsign}");
    }

    internal static CommandResult TryAirTaxi(AircraftState aircraft, string? destination, AirportGroundLayout? groundLayout)
    {
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        if (cat != AircraftCategory.Helicopter)
        {
            return new CommandResult(false, "Air taxi is only available for helicopters");
        }

        if (destination is null)
        {
            return new CommandResult(false, "ATXI requires a destination (helipad, parking, taxiway spot, or runway)");
        }

        if (groundLayout is null)
        {
            return new CommandResult(false, "No airport ground layout available");
        }

        if (!TryResolveAirTaxiDestination(groundLayout, destination, out AirTaxiDestination? resolved))
        {
            return new CommandResult(
                false,
                $"Cannot find destination '{destination}' in airport layout (expected helipad, parking, taxiway spot, or runway)"
            );
        }

        // The layout knows the pavement, but the hold and the runway assignment need the navdata record. Resolve
        // it before anything is torn down: installing a DestinationRunway hold with no assigned runway would
        // leave an aircraft that refuses RES and has nothing to clear it with.
        RunwayInfo? terminusRunway = null;
        if (resolved.Kind == AirTaxiDestinationKind.Runway)
        {
            terminusRunway = CommandDispatcher.ResolveRunway(aircraft, resolved.RunwayId!);
            if (terminusRunway is null)
            {
                string unknown = RunwayIdentifier.ToDisplayDesignator(resolved.RunwayId!);
                Log.LogWarning(
                    "[TryAirTaxi] {Callsign}: runway {Rwy} is on the {Airport} layout but has no navdata record",
                    aircraft.Callsign,
                    unknown,
                    groundLayout.AirportId
                );
                return new CommandResult(false, $"Unable, runway {unknown} has no navdata record");
            }
        }

        // A runway terminus stops the heli with its nose AT the marking, so the point it flies to is the bar set
        // back by half the fuselage — the nose-at-the-line setback every taxi hold-short gets from
        // HoldShortAnnotator.ComputeHoldShortPositions, which never runs on this path.
        resolved = WithHoldShortSetback(groundLayout, aircraft, resolved);

        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        if (!IsOnFieldForAirTaxi(aircraft, groundLayout, ctx.FieldElevation))
        {
            // Air taxi is a ground movement on the airport (AIM §4-3-17.b; 7110.65 §3-11-1.c NOTE, §3-11-3
            // NOTE). From miles out, or well above the field, the pilot advises that the clearance does not
            // fit (AIM §4-3-17.a.3) and asks for the landing clearance §3-11-6.a prefers instead. The
            // message is spoken by the pilot as the "unable" readback: short, about the aircraft, no
            // punctuation the verbalizer cannot voice.
            double distNm = GeoMath.DistanceNm(aircraft.Position, resolved.Target);
            // Over the field but too high for an air taxi reads "0 miles out" as a distance; say where it is.
            string where = distNm < OverheadMaxNm ? "we're overhead" : $"we're {distNm:F0} miles out";
            return new CommandResult(false, $"Unable, {where}, request landing {resolved.SpokenDestination}");
        }

        // Captured before the fresh PhaseList drops the old clearance — a runway destination assigns the
        // departure runway exactly as a taxi clearance does, warning included.
        DepartureRunwayAssignment priorAssignment = CaptureDepartureRunwayAssignment(aircraft);

        // Clear current phases and chain air-taxi → land → the terminus the destination class implies, so the
        // heli lifts off, cruises to the destination, descends, and settles there.
        aircraft.Phases?.Clear(ctx);

        aircraft.Ground.Hold = null;
        // An air taxi supersedes the taxi clearance — the heli flies to the destination, it does not follow the
        // route. Leaving the route behind would also corrupt a restore: AircraftState.FromSnapshot re-binds a
        // restored HoldingShortPhase to Ground.AssignedTaxiRoute.GetHoldShortAt(NodeId), so a stale point at the
        // same node (a cleared crossing, say) would silently replace the terminus hold this command created.
        aircraft.Ground.AssignedTaxiRoute = null;
        aircraft.Phases = new PhaseList();

        // Only a helipad/parking destination is a parking position; a spot or a runway holding position is not,
        // and a stale name there would have the aircraft reported as parked on the taxiway or at the bar.
        aircraft.Ground.ParkingSpot = resolved.Kind == AirTaxiDestinationKind.Parking ? resolved.Name : null;

        aircraft.Phases.Add(new AirTaxiPhase(resolved.Target.Lat, resolved.Target.Lon, resolved.Name));
        aircraft.Phases.Add(new HelicopterLandingPhase());
        AddAirTaxiTerminus(aircraft, resolved, terminusRunway, priorAssignment);
        ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases.Start(ctx);

        return CommandDispatcher.Ok(BuildAirTaxiMessage(resolved)) with
        {
            // The readback names the destination as the layout resolved it, not as typed: a bare "9" that
            // resolved to a runway reads back "air taxi to runway nine" instead of falling silent on the
            // unpadded token. Parking and spot destinations read back exactly as issued.
            EffectiveCommand = resolved.Kind == AirTaxiDestinationKind.Runway ? new AirTaxiCommand(resolved.CanonicalToken) : null,
        };
    }

    /// <summary>
    /// The controller-facing result line. A runway terminus says where it ends, because an air taxi to a runway
    /// stops at the holding position rather than on the pavement.
    /// </summary>
    private static string BuildAirTaxiMessage(AirTaxiDestination resolved) =>
        resolved.Kind == AirTaxiDestinationKind.Runway ? $"Air taxi to {resolved.Name}, holding short" : $"Air taxi to {resolved.Name}";

    /// <summary>
    /// The destination point moved back off a runway holding-position node by half the aircraft length, along the
    /// bar's taxiway away from the runway, so the aircraft centre (its position) settles with the nose at the
    /// marking — <see cref="HoldShortAnnotator.ComputeHoldShortPositions"/>'s setback for a
    /// <see cref="HoldShortReason.DestinationRunway"/> hold, which only runs on a taxi route. Non-runway
    /// destinations, and a bar with no taxiway edge leading away from the runway, are returned unchanged.
    /// </summary>
    private static AirTaxiDestination WithHoldShortSetback(AirportGroundLayout layout, AircraftState aircraft, AirTaxiDestination resolved)
    {
        if ((resolved.Kind != AirTaxiDestinationKind.Runway) || (resolved.HoldShortNodeId is not { } nodeId))
        {
            return resolved;
        }

        if (!layout.Nodes.TryGetValue(nodeId, out GroundNode? bar) || (layout.FindRunway(resolved.RunwayId!) is not { } runway))
        {
            return resolved;
        }

        GroundNode? away = bar
            .Edges.Where(e => !e.IsRunwayCenterline)
            .Select(e => e.OtherNode(bar))
            .MaxBy(n => DistanceToCenterlineFt(runway, n.Position));
        if ((away is null) || (DistanceToCenterlineFt(runway, away.Position) <= DistanceToCenterlineFt(runway, bar.Position)))
        {
            return resolved;
        }

        double lengthFt = FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(aircraft.AircraftType);
        double setbackNm = Math.Min((lengthFt / 2.0) / GeoMath.FeetPerNm, GeoMath.DistanceNm(bar.Position, away.Position));
        var heading = new TrueHeading(GeoMath.BearingTo(bar.Position, away.Position));
        return resolved with { Target = GeoMath.ProjectPoint(bar.Position, heading, setbackNm) };
    }

    /// <summary>
    /// Perpendicular distance in feet from a point to the runway's pavement centerline — measured to the
    /// <em>segments</em> between its vertices, not to the vertices themselves. A vertex metric is dominated by
    /// along-track distance on a runway drawn with a handful of far-apart points (KOAK 28L-10R has segments up
    /// to 2,500 ft), which would let a node sitting on the pavement read as "farther from the runway" than the
    /// bar and send the setback the wrong way.
    /// </summary>
    private static double DistanceToCenterlineFt(GroundRunway runway, LatLon position) =>
        runway.Coordinates.Count < 2
            ? double.MaxValue
            : Enumerable
                .Range(0, runway.Coordinates.Count - 1)
                .Min(i =>
                    GeoMath.DistanceToSegmentFt(
                        position,
                        new LatLon(runway.Coordinates[i].Lat, runway.Coordinates[i].Lon),
                        new LatLon(runway.Coordinates[i + 1].Lat, runway.Coordinates[i + 1].Lon)
                    )
                );

    /// <summary>
    /// Adds the phase an air taxi ends in.
    ///
    /// <para>A helipad or parking position is a shutdown spot (<see cref="AtParkingPhase"/>). A taxiway spot is
    /// not: the heli sets down and holds where it is (<see cref="HoldingInPositionPhase"/>). A runway is neither
    /// — an air taxi is a ground movement (AIM 4-3-17.b), and a ground movement to a runway ends at its holding
    /// position (AIM 4-3-18.a.5/6; 7110.65 3-11-1.c "HOLD FOR"), never on the pavement, so the heli holds short
    /// with <see cref="HoldShortReason.DestinationRunway"/> — which refuses RES and takes CTO/LUAW, like any
    /// departure at the bar — and the runway is assigned the same way a taxi clearance assigns it.</para>
    /// </summary>
    private static void AddAirTaxiTerminus(
        AircraftState aircraft,
        AirTaxiDestination resolved,
        RunwayInfo? terminusRunway,
        DepartureRunwayAssignment priorAssignment
    )
    {
        if (resolved.Kind == AirTaxiDestinationKind.Parking)
        {
            aircraft.Phases!.Add(new AtParkingPhase());
            return;
        }

        if (resolved.Kind == AirTaxiDestinationKind.Spot)
        {
            aircraft.Phases!.Add(new HoldingInPositionPhase());
            return;
        }

        string runwayId = resolved.RunwayId!;
        aircraft.Phases!.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = resolved.HoldShortNodeId!.Value,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = runwayId,
                    Latitude = resolved.Target.Lat,
                    Longitude = resolved.Target.Lon,
                }
            )
        );

        // Non-null for a runway destination: TryAirTaxi refuses the command when the record cannot be resolved.
        ApplyDepartureRunway(aircraft, terminusRunway!, priorAssignment);
    }

    /// <summary>
    /// Distance (nm) to the destination inside which the refusal says "overhead" rather than a rounded mileage
    /// that would read "0 miles out".
    /// </summary>
    private const double OverheadMaxNm = 0.5;

    /// <summary>
    /// Distance (nm) from the nearest ground-layout node within which a helicopter counts as over the airport
    /// for the air-taxi gate — the sim's stand-in for "within the boundary of the airport".
    /// </summary>
    private const double OnFieldMarginNm = 0.3;

    /// <summary>
    /// Height (ft AGL) above which a helicopter over the field is treated as inbound rather than manoeuvring
    /// on it. A simulation threshold: the rotorcraft pattern altitude (AIM §4-3-3.a.3) is the figure it
    /// borrows, but no publication ties air taxi to an altitude other than its own ~100 ft AGL (§3-11-1.c).
    /// </summary>
    private const double OnFieldMaxAgl = 500.0;

    /// <summary>
    /// True when a helicopter may fly the air-taxi profile to a spot: on the ground, or airborne over the
    /// airport (within <see cref="OnFieldMarginNm"/> of a layout node) no higher than <see cref="OnFieldMaxAgl"/>.
    /// Anywhere else a LAND is an approach (<see cref="HelicopterApproachPhase"/>) and an ATXI is refused —
    /// air taxi is a ground movement on the airport (AIM §4-3-17.b; 7110.65 §3-11-1.c NOTE, §3-11-3 NOTE).
    /// </summary>
    internal static bool IsOnFieldForAirTaxi(AircraftState aircraft, AirportGroundLayout layout, double fieldElevation)
    {
        if (aircraft.IsOnGround)
        {
            return true;
        }

        GroundNode? nearest = layout.FindNearestNode(aircraft.Position);
        if (nearest is null)
        {
            return false;
        }

        double agl = aircraft.Altitude - fieldElevation;
        return (GeoMath.DistanceNm(aircraft.Position, nearest.Position) <= OnFieldMarginNm) && (agl <= OnFieldMaxAgl);
    }

    /// <summary>What an <c>ATXI</c> destination names — which decides where the air taxi ends.</summary>
    internal enum AirTaxiDestinationKind
    {
        /// <summary>A helipad or a parking position: the heli sets down and parks there.</summary>
        Parking,

        /// <summary>A named taxiway spot: the heli sets down and holds where it is — a spot is not a parking position.</summary>
        Spot,

        /// <summary>A runway: the heli ends at that runway's holding position, clear of the pavement.</summary>
        Runway,
    }

    /// <summary>
    /// A resolved <c>ATXI</c> destination: its class, the point to fly to, the name controller-facing text uses,
    /// and — for the runway form only — the runway held short of and the graph node of that bar.
    /// </summary>
    internal sealed record AirTaxiDestination(AirTaxiDestinationKind Kind, LatLon Target, string Name, string? RunwayId, int? HoldShortNodeId)
    {
        /// <summary>The command token this destination resolved from, normalized: <c>09</c>, <c>28L@J</c>, <c>FDX1</c>.</summary>
        public string CanonicalToken =>
            Kind == AirTaxiDestinationKind.Runway ? new HoldShortTarget(RunwayId!, LocationTaxiway, false).ToCanonical() : Name;

        /// <summary>The destination as a pilot says it: "runway 28L", "at FDX1" — the clause after "request landing".</summary>
        public string SpokenDestination => Kind == AirTaxiDestinationKind.Runway ? Name : $"at {Name}";

        /// <summary>The taxiway a located runway destination names, or null for the bare and non-runway forms.</summary>
        public string? LocationTaxiway { get; init; }
    }

    /// <summary>
    /// Resolve an ATXI destination by trying, in order: helipad/parking, taxiway spot, then runway. The token is
    /// parsed by <see cref="HoldShortTarget"/> — the same <c>TARGET@TAXIWAY</c> grammar the located hold short
    /// uses — and a runway resolves to its <em>holding position</em>, never the threshold, because an air taxi is
    /// a ground movement (AIM 4-3-17.b) that ends clear of the pavement. Returns false when nothing matches, or
    /// when a located runway form names a taxiway with no bar on that runway.
    /// </summary>
    internal static bool TryResolveAirTaxiDestination(
        AirportGroundLayout layout,
        string destination,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AirTaxiDestination? resolved
    )
    {
        resolved = null;
        if (!HoldShortTarget.TryParse(destination, out HoldShortTarget target, out _))
        {
            return false;
        }

        resolved = ResolveAirTaxiSpot(layout, target) ?? ResolveAirTaxiRunway(layout, target);
        return resolved is not null;
    }

    /// <summary>
    /// The helipad, gate, or taxiway spot <paramref name="target"/> names, or null when it names neither. A
    /// located target (<c>28L@J</c>) is never a spot: the locative only qualifies a runway, since a gate or a
    /// spot is a single node.
    /// </summary>
    private static AirTaxiDestination? ResolveAirTaxiSpot(AirportGroundLayout layout, HoldShortTarget target)
    {
        if (target.OnTaxiway is not null)
        {
            return null;
        }

        if ((layout.FindHelipadByName(target.Target) ?? layout.FindParkingByName(target.Target)) is { } parking)
        {
            return new AirTaxiDestination(AirTaxiDestinationKind.Parking, parking.Position, target.Target, null, null);
        }

        return layout.FindSpotNodeByName(target.Target) is { } spot
            ? new AirTaxiDestination(AirTaxiDestinationKind.Spot, spot.Position, target.Target, null, null)
            : null;
    }

    /// <summary>
    /// The runway holding position <paramref name="target"/> names, or null when the layout has no such runway or
    /// no bar on the named taxiway.
    /// </summary>
    private static AirTaxiDestination? ResolveAirTaxiRunway(AirportGroundLayout layout, HoldShortTarget target)
    {
        if (layout.FindRunway(target.Target) is null || ResolveRunwayHoldShortNode(layout, target) is not { } bar)
        {
            return null;
        }

        string runwayId = RunwayIdentifier.NormalizeDesignator(target.Target);
        string display = RunwayIdentifier.ToDisplayDesignator(runwayId);
        string name = target.OnTaxiway is { } taxiway ? $"runway {display} at {taxiway}" : $"runway {display}";
        return new AirTaxiDestination(AirTaxiDestinationKind.Runway, bar.Position, name, runwayId, bar.Id) { LocationTaxiway = target.OnTaxiway };
    }

    /// <summary>
    /// The holding-position node an air taxi to a runway ends at: the bar nearest the named end's threshold — the
    /// full-length entrance — narrowed for the located <c>28L@J</c> form to the bars incident to that taxiway, the
    /// same node-incidence test a located hold short (<c>HS 28R@J</c>) binds with. Null when the runway has no
    /// hold-short node at all, or none on the named taxiway.
    /// </summary>
    private static GroundNode? ResolveRunwayHoldShortNode(AirportGroundLayout layout, HoldShortTarget target)
    {
        List<GroundNode> candidates = layout.GetRunwayHoldShortNodes(target.Target);
        if (target.OnTaxiway is { } taxiway)
        {
            candidates = [.. candidates.Where(node => HoldShortAnnotator.NodeOnLocationTaxiway(layout, node.Id, taxiway))];
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // The same threshold reference TAXIAUTO's runway routing measures from, so both pick the same bar even on
        // a displaced threshold. Node id breaks a tie only when the airport has no resolvable threshold.
        LatLon? threshold = RouteMaterialiser.ResolveRunwayThreshold(layout.AirportId, target.Target);
        return threshold is { } reference
            ? candidates.MinBy(node => GeoMath.DistanceNm(reference, node.Position))
            : candidates.OrderBy(node => node.Id).First();
    }

    internal static CommandResult TryLand(AircraftState aircraft, LandCommand land, AirportGroundLayout? groundLayout)
    {
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        if (cat != AircraftCategory.Helicopter)
        {
            return new CommandResult(false, "LAND is only available for helicopters (use CLAND for fixed-wing)");
        }

        if (groundLayout is null)
        {
            return new CommandResult(false, "No airport ground layout available");
        }

        double destLat;
        double destLon;
        string resolvedName;

        if (land.IsTaxiway)
        {
            // Resolve nearest node on the named taxiway
            GroundNode? node = groundLayout.FindExitByTaxiway(aircraft.Position, land.SpotName);
            if (node is null)
            {
                return new CommandResult(false, $"Cannot find taxiway '{land.SpotName}' near aircraft");
            }

            destLat = node.Position.Lat;
            destLon = node.Position.Lon;
            resolvedName = land.SpotName.ToUpperInvariant();
        }
        else
        {
            GroundNode? spot = groundLayout.FindSpotByName(land.SpotName);
            if (spot is null)
            {
                return new CommandResult(false, $"Cannot find spot '{land.SpotName}' in airport layout");
            }

            destLat = spot.Position.Lat;
            destLon = spot.Position.Lon;
            resolvedName = land.SpotName.ToUpperInvariant();
        }

        if (land.NoDelete)
        {
            aircraft.Ground.AutoDeleteExempt = true;
            aircraft.Ground.NoDeleteRequested = true;
        }

        // Clear current phases and set up the arrival → land sequence. On the field (or hovering over it
        // at pattern altitude or below) that is an air taxi to the spot; from anywhere else it is a landing
        // clearance flown as an approach (7110.65 §3-11-6), because air taxi is a ground movement on the
        // airport (AIM §4-3-17.b; §3-11-1.c NOTE) and must not carry a helicopter miles across the bay at 100 ft.
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        bool onField = IsOnFieldForAirTaxi(aircraft, groundLayout, ctx.FieldElevation);
        aircraft.Phases?.Clear(ctx);

        aircraft.Ground.Hold = null;
        aircraft.Phases = new PhaseList();
        if (onField)
        {
            aircraft.Phases.Add(new AirTaxiPhase(destLat, destLon, resolvedName));
        }
        else
        {
            aircraft.Phases.Add(new HelicopterApproachPhase(destLat, destLon, resolvedName));
        }

        aircraft.Phases.Add(new HelicopterLandingPhase());
        aircraft.Phases.Add(new AtParkingPhase());
        ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases.Start(ctx);

        aircraft.Ground.ParkingSpot = resolvedName;

        return CommandDispatcher.Ok($"Land at {resolvedName}");
    }

    private static RunwayInfo? DetectRunwayFromRoute(TaxiRoute route, AirportGroundLayout layout, AircraftState aircraft)
    {
        int finalNodeId = route.Segments[^1].ToNodeId;
        if (!layout.Nodes.TryGetValue(finalNodeId, out GroundNode? node))
        {
            return null;
        }

        RunwayIdentifier? rwyId = null;

        // Case 1: RunwayHoldShort node
        if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is not null)
        {
            rwyId = node.RunwayId;
        }
        else
        {
            // Case 2: Runway surface node (edge named "RWY...")
            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.IsRunwayCenterline)
                {
                    string rawDesignator = edge.TaxiwayName[3..];
                    rwyId = RunwayIdentifier.Parse(rawDesignator);
                    break;
                }
            }
        }

        if (rwyId is null)
        {
            return null;
        }

        RunwayInfo? runway = ResolveClosestRunwayEnd(rwyId.Value, node.Position.Lat, node.Position.Lon, aircraft);
        if (runway is not null)
        {
            Log.LogDebug(
                "[TryTaxi] {Callsign}: auto-detected runway {Rwy} from final node {NodeId}",
                aircraft.Callsign,
                runway.Designator,
                finalNodeId
            );
        }

        return runway;
    }

    private static RunwayInfo? ResolveClosestRunwayEnd(RunwayIdentifier rwyId, double nodeLat, double nodeLon, AircraftState aircraft)
    {
        string? airportId = aircraft.FlightPlan.Departure;
        if (airportId is null)
        {
            return null;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        RunwayInfo? info = navDb.GetRunway(airportId, rwyId.End1) ?? navDb.GetRunway(airportId, rwyId.End2);
        if (info is null)
        {
            return null;
        }

        double dist1 = GeoMath.DistanceNm(nodeLat, nodeLon, info.Lat1, info.Lon1);
        double dist2 = GeoMath.DistanceNm(nodeLat, nodeLon, info.Lat2, info.Lon2);
        string closerDesignator = dist1 <= dist2 ? info.Id.End1 : info.Id.End2;
        return info.ForApproach(closerDesignator);
    }

    internal const double BreakDurationSeconds = 15.0;

    internal static CommandResult TryBreakConflict(AircraftState aircraft)
    {
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "Break requires aircraft on the ground");
        }

        aircraft.Ground.ConflictBreakRemainingSeconds = BreakDurationSeconds;
        aircraft.Ground.SpeedLimit = null;
        Log.LogInformation("[Break] {Callsign}: ignoring ground conflicts for {Duration}s", aircraft.Callsign, BreakDurationSeconds);
        return CommandDispatcher.Ok("Break conflict");
    }

    /// <summary>
    /// CLRWY — pull an aircraft holding short of a taxiway with its tail over a runway (issue #172 W2 state)
    /// forward just until it is clear of the runway, then hold. The taxiway hold-short is superseded and the
    /// hold released, so the aircraft resumes its route to the terminus just past the runway (where the hold
    /// positions it nose-at-the-junction, body back across the taxiway line, tail just clear of the runway
    /// bars) and holds in position. Clearing the tail-over state releases the occupied runway hold-short node
    /// (the "runway not clear" warning). Valid only from the tail-over-runway hold.
    /// </summary>
    internal static CommandResult TryClearRunway(AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "CLRWY requires aircraft on the ground");
        }

        if (aircraft.Phases?.CurrentPhase is not HoldingShortPhase holdPhase || holdPhase.HoldShort.TailOverRunwayNodeId is not { } runwayNodeId)
        {
            return new CommandResult(false, "CLRWY only applies when holding short of a taxiway with the tail over a runway");
        }

        // The approach node is the route node on the runway side of the crossed hold-short, so the
        // ½-length tail-clearance offset projects forward (away from the runway).
        int approachNodeId = aircraft.Ground.AssignedTaxiRoute?.Segments.FirstOrDefault(s => s.ToNodeId == runwayNodeId)?.FromNodeId ?? -1;
        if (
            groundLayout is null
            || approachNodeId < 0
            || !groundLayout.Nodes.ContainsKey(approachNodeId)
            || !groundLayout.Nodes.ContainsKey(runwayNodeId)
        )
        {
            // "Unable, <reason>" — CLRWY is a Ground verb, so PilotResponder.BuildUnable strips the leading
            // "unable" and speaks the rest. "Unable to pull forward, …" would come out as "unable, to pull
            // forward, …".
            return new CommandResult(false, "Unable, runway-clearance geometry unavailable");
        }

        // Supersede the binding taxiway hold-short and release the hold (resolving the tail-over-runway
        // state, which releases the occupied runway node), then drive forward ½ aircraft length past the
        // runway bars and hold there — clear of the runway behind it (issue #172 W5).
        holdPhase.HoldShort.IsCleared = true;
        holdPhase.HoldShort.TailOverRunwayNodeId = null;

        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new ClearRunwayPhase(runwayNodeId, approachNodeId));
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(ctx);

        Log.LogInformation(
            "[ClearRunway] {Callsign}: pulling forward clear of RWY node {Rwy} from hold-short of {Target}",
            aircraft.Callsign,
            runwayNodeId,
            holdPhase.HoldShort.TargetName ?? "taxiway"
        );
        return CommandDispatcher.Ok("clearing the runway, holding");
    }

    internal static CommandResult TryGo(AircraftState aircraft)
    {
        if (aircraft.Phases?.CurrentPhase is not StopAndGoPhase stopAndGo)
        {
            return new CommandResult(false, "GO requires aircraft in a stop-and-go");
        }

        stopAndGo.TriggerGo();
        Log.LogInformation("[Go] {Callsign}: manual takeoff roll triggered", aircraft.Callsign);
        return CommandDispatcher.Ok("Begin takeoff roll");
    }

    internal static CommandResult TryExitCommand(AircraftState aircraft, ExitPreference preference, bool noDelete, bool expedite)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        // Require a landing or runway-exit context. EXIT during cruise/enroute
        // would silently store the preference for a landing that may never
        // happen. Real ATC issues EL/ER on short final or during rollout.
        if (!HasLandingOrExitPhase(aircraft, PhaseStatus.Pending))
        {
            return new CommandResult(false, "Exit requires a pending landing or active runway exit");
        }

        // A taxiway-only exit (EXIT D) issued after an explicit side (EL/ER) keeps the
        // standing side, so "ER ; EXIT D" exits right AT D instead of dropping Right and
        // falling back to the inferred side (issue #276). An explicit side on the new
        // command still wins. If D only exists on the other side, the resolver's
        // on-/off-side fallback still takes it — the taxiway name is a hard constraint.
        if ((preference.Side is null) && (preference.Taxiway is not null) && (aircraft.Phases.RequestedExit?.Side is { } standingSide))
        {
            preference = new ExitPreference { Side = standingSide, Taxiway = preference.Taxiway };
        }

        // Handing a route to the navigator is not the same as turning off: the route's first segment runs
        // straight down the runway centerline to the branch node, so a committed aircraft can still have the
        // whole runway to run. The phase owns that distinction — it honors a change made before the turn-off
        // begins and refuses one made after. Evaluated with the merged preference so "ER ; EXIT D" is probed
        // as "right at D", not as a bare D.
        if (aircraft.Phases.CurrentPhase is Phases.Ground.RunwayExitPhase exitPhase)
        {
            ExitRetargetVerdict verdict = exitPhase.EvaluateRetarget(aircraft, preference);
            if (!verdict.Allowed)
            {
                return new CommandResult(false, verdict.UnableReason!);
            }
        }

        aircraft.Phases.RequestedExit = preference;
        if (noDelete)
        {
            aircraft.Ground.AutoDeleteExempt = true;
            aircraft.Ground.NoDeleteRequested = true;
        }

        aircraft.Ground.IsExpeditingExit = expedite;

        // 7110.65 §3-7-2.b.10: the phrase is "without delay" (the word "expedite"
        // is reserved by §2-1-5 for imminent situations). The EXP token is just a
        // terse keyboard mnemonic.
        string expediteText = expedite ? ", without delay" : "";
        if (preference.Taxiway is not null)
        {
            string sideText = preference.Side switch
            {
                ExitSide.Left => "left ",
                ExitSide.Right => "right ",
                _ => "",
            };
            return CommandDispatcher.Ok($"Exit {sideText}at {preference.Taxiway}{expediteText}");
        }

        return CommandDispatcher.Ok((preference.Side == ExitSide.Left ? "Exit left" : "Exit right") + expediteText);
    }

    /// <summary>
    /// True when the aircraft has a Landing/HelicopterLanding/RunwayExit phase at
    /// or beyond <paramref name="minStatus"/>. Pass <see cref="PhaseStatus.Pending"/>
    /// to include short-final landings (EL/ER), or <see cref="PhaseStatus.Active"/>
    /// to require the aircraft to actually be flaring/rolling-out/exiting (standalone EXP).
    /// </summary>
    internal static bool HasLandingOrExitPhase(AircraftState aircraft, PhaseStatus minStatus)
    {
        if (aircraft.Phases is null)
        {
            return false;
        }

        foreach (Phase phase in aircraft.Phases.Phases)
        {
            bool statusMatch =
                minStatus == PhaseStatus.Active ? phase.Status is PhaseStatus.Active : phase.Status is PhaseStatus.Pending or PhaseStatus.Active;
            if (statusMatch && phase is LandingPhase or HelicopterLandingPhase or RunwayExitPhase)
            {
                return true;
            }
        }

        return false;
    }
}
