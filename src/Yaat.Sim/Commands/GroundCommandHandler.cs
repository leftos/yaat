using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

internal static class GroundCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("GroundCommandHandler");

    internal static CommandResult TryTaxi(AircraftState aircraft, TaxiCommand taxi, AirportGroundLayout? groundLayout, bool autoCrossRunway = false)
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

        // Find starting node. Prefer the heading-aligned endpoint of the
        // nearest taxi edge (handles post-pushback poses where the aircraft
        // rests between graph nodes — see issue #161); fall back to the
        // absolute nearest node when the aircraft is genuinely off-graph.
        var startNode =
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

        // Infer the taxiway the aircraft is already on. The controller clears a continuation
        // ("TAXI E") without re-naming the taxiway the aircraft currently occupies; prepending it
        // makes the explicit pathfinder start there instead of bridging onto the first cleared
        // taxiway (which both flags the occupied taxiway "not in authorized path" and hard-fails
        // when the bridge's hop cap can't reach it). Guards:
        //   * start node actually lies on that taxiway — a stale CurrentTaxiway never injects a phantom leg;
        //   * path doesn't already begin with it;
        //   * the two taxiways share a direct junction node — i.e. the aircraft can turn straight from
        //     its current taxiway onto the first cleared one. When they meet only across a runway (e.g.
        //     SFO M↔H across 01L/19R, zero shared nodes) prepending would re-route the crossing through a
        //     named-junction search instead of the runway-crossing bridge, so it is left to that path.
        // Remember the as-cleared path so the prepend can be undone below if it strands the route.
        var pathAsCleared = taxi.Path;
        var pathTurnHintsAsCleared = taxi.PathTurnHints;
        bool prependedCurrentTaxiway = false;
        if (
            taxi.Path.Count > 0
            && aircraft.Ground.CurrentTaxiway is { Length: > 0 } currentTwy
            && !taxi.Path[0].Equals(currentTwy, StringComparison.OrdinalIgnoreCase)
            && startNode.Edges.Any(e => e.MatchesTaxiway(currentTwy))
            && SharesDirectJunction(groundLayout, currentTwy, taxi.Path[0])
        )
        {
            // The aircraft is already on currentTwy, so it makes no turn onto it: prepend a null hint
            // to keep PathTurnHints index-aligned with Path. The controller's hint on the first cleared
            // taxiway thereby becomes the (mid-route) turn from currentTwy onto it.
            taxi = taxi with
            {
                Path = [currentTwy, .. taxi.Path],
                PathTurnHints = taxi.PathTurnHints is null ? null : [null, .. taxi.PathTurnHints],
            };
            prependedCurrentTaxiway = true;
            Log.LogDebug("[TryTaxi] {Callsign}: prepended current taxiway {Twy} to cleared path", aircraft.Callsign, currentTwy);
        }

        Log.LogDebug(
            "[TryTaxi] {Callsign}: nearest node {NodeId} at ({NLat:F6}, {NLon:F6}), dist={Dist:F4}nm, path=[{Path}], destRwy={Rwy}, destParking={Pkg}, destSpot={Spot}",
            aircraft.Callsign,
            startNode.Id,
            startNode.Position.Lat,
            startNode.Position.Lon,
            GeoMath.DistanceNm(aircraft.Position, startNode.Position),
            string.Join(" ", taxi.Path),
            taxi.DestinationRunway ?? "(none)",
            taxi.DestinationParking ?? "(none)",
            taxi.DestinationSpot ?? "(none)"
        );

        var category = AircraftCategorization.Categorize(aircraft.AircraftType);

        double startHeadingTrueDeg = aircraft.TrueHeading.Degrees;
        TaxiRoute? ResolveRoute(TaxiCommand command, out string? reason)
        {
            return (command.DestinationParking is not null || command.DestinationSpot is not null)
                ? ResolveParkingRoute(groundLayout, startNode, command, out reason, category, startHeadingTrueDeg)
                : ResolveStandardRoute(groundLayout, startNode, command, out reason, category, startHeadingTrueDeg);
        }

        var route = ResolveRoute(taxi, out string? failReason);

        // The current-taxiway prepend above is an optimization: start on the taxiway the aircraft
        // occupies rather than bridging onto the first cleared one. When the aircraft sits at the far
        // end of a stub connector, prepending forces the route back to that connector's only junction
        // with the first cleared taxiway, which can strand the onward transition — SIA31 holding at the
        // NE end of the B5 spot got "B5 B B1 …", routing B5 back to the B5/B junction (node 117) and
        // making B→B1 infeasible. When the prepended path yields no route, drop the prepend and bridge
        // directly onto the first cleared taxiway (the original, un-prepended behaviour).
        if (route is null && prependedCurrentTaxiway)
        {
            Log.LogDebug(
                "[TryTaxi] {Callsign}: prepended-path resolution failed ({Reason}); retrying without the current-taxiway prepend",
                aircraft.Callsign,
                failReason ?? "no route"
            );
            taxi = taxi with { Path = pathAsCleared, PathTurnHints = pathTurnHintsAsCleared };
            route = ResolveRoute(taxi, out failReason);
        }

        if (route is null)
        {
            Log.LogWarning("[TryTaxi] {Callsign}: route resolution failed — {Reason}", aircraft.Callsign, failReason ?? "no matching taxiways");

            if (failReason is not null)
            {
                return new CommandResult(false, failReason);
            }

            var pathStr = string.Join(" ", taxi.Path);
            return new CommandResult(false, $"Cannot resolve taxi route: {pathStr}");
        }

        // Compute dynamic hold-short positions based on aircraft fuselage length
        double aircraftLengthFt =
            FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(aircraft.AircraftType);
        HoldShortAnnotator.ComputeHoldShortPositions(groundLayout, route, aircraftLengthFt);

        var hsDetails = string.Join(", ", route.HoldShortPoints.Select(h => $"{h.TargetName}@{h.NodeId}({h.Reason})"));
        Log.LogInformation(
            "[TryTaxi] {Callsign}: route resolved — {SegCount} segments, {HsCount} hold-shorts [{HsDetails}], summary: {Summary}",
            aircraft.Callsign,
            route.Segments.Count,
            route.HoldShortPoints.Count,
            hsDetails,
            route.ToSummary()
        );

        // Implicit first-crossing clearance: when the aircraft is already holding short of a
        // runway — or is in the middle of exiting one (it must taxi past that runway's
        // hold-short bars to finish clearing the runway it just landed on, not hold short of
        // the runway it is leaving) — and the new route's first runway crossing is for that
        // same runway, the TAXI command itself authorizes it. No separate CTO needed.
        // Subsequent crossings still require explicit clearance.
        string? priorRwy = aircraft.Phases?.CurrentPhase switch
        {
            HoldingShortPhase priorHold when priorHold.HoldShort.TargetName is { Length: > 0 } heldRwy => heldRwy,
            RunwayExitPhase exitPhase when exitPhase.RunwayId is { Length: > 0 } exitRwy => exitRwy,
            HoldingAfterExitPhase afterExit when afterExit.RunwayId is { Length: > 0 } afterExitRwy => afterExitRwy,
            _ => null,
        };
        string? implicitCrossLabel = null;
        if (priorRwy is not null)
        {
            var firstCrossing = route.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.RunwayCrossing);
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

        TaxiRouteAutoCross.Apply(route, autoCrossRunway);

        // Pre-clear specific runway crossings from CROSS keywords in the TAXI command
        if (taxi.CrossRunways is { Count: > 0 })
        {
            foreach (var hs in route.HoldShortPoints)
            {
                if (hs.Reason == HoldShortReason.RunwayCrossing && hs.TargetName is not null)
                {
                    var hsRwyId = RunwayIdentifier.Parse(hs.TargetName);
                    foreach (var crossRwy in taxi.CrossRunways)
                    {
                        if (hsRwyId.Contains(crossRwy))
                        {
                            hs.IsCleared = true;
                            // Explicit user CROSS keyword owns the clearance — clear any
                            // AutoCross attribution so a future AutoCross-OFF toggle does
                            // not revert this user-issued crossing authorization.
                            hs.ClearedByAutoCross = false;
                            break;
                        }
                    }
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

        // Clear current phases
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        if (aircraft.Phases is not null)
        {
            aircraft.Phases.Clear(ctx);
        }

        // Set up the taxi route and phase
        aircraft.Ground.AssignedTaxiRoute = route;
        aircraft.Ground.Hold = null;

        if (taxi.NoDelete)
        {
            aircraft.Ground.AutoDeleteExempt = true;
        }

        aircraft.Phases = new PhaseList();
        if (detectedRunway is not null)
        {
            aircraft.Phases.AssignedRunway = detectedRunway;
            aircraft.Procedure.DepartureRunway = detectedRunway.Designator;
        }

        // Zero-segment route to parking: A* snapped the aircraft to the destination node.
        // Only skip TaxiingPhase when the aircraft is genuinely at the spot; if it's
        // physically distant (e.g. after pushback), re-route from the nearest neighbor
        // so TaxiingPhase drives the aircraft back to the parking position.
        var parkingName = route.DestinationParking ?? route.DestinationSpot;
        if (route.Segments.Count == 0 && parkingName is not null)
        {
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
                foreach (var edge in destNode.Edges)
                {
                    var neighbor = edge.OtherNode(destNode);
                    double d = GeoMath.DistanceNm(aircraft.Position, neighbor.Position);
                    if (d < bestNeighborDist)
                    {
                        bestNeighborDist = d;
                        bestNeighbor = neighbor;
                    }
                }

                if (bestNeighbor is not null)
                {
                    var reroute = TaxiPathfinder.FindRoute(
                        groundLayout,
                        bestNeighbor.Id,
                        destNode.Id,
                        AircraftCategorization.Categorize(aircraft.AircraftType)
                    );
                    if (reroute is not null && reroute.Segments.Count > 0)
                    {
                        route = SetDestination(reroute, taxi);
                        aircraft.Ground.AssignedTaxiRoute = route;
                        HoldShortAnnotator.ComputeHoldShortPositions(groundLayout, route, aircraftLengthFt);
                        rerouted = true;

                        Log.LogInformation(
                            "[TryTaxi] {Callsign}: zero-segment re-route via neighbor {NeighborId} to @{Parking} ({SegCount} segments)",
                            aircraft.Callsign,
                            bestNeighbor.Id,
                            parkingName,
                            route.Segments.Count
                        );
                    }
                }
            }

            if (!rerouted)
            {
                aircraft.Ground.ParkingSpot = parkingName;
                aircraft.Phases.Add(new AtParkingPhase());
                ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
                aircraft.Phases.Start(ctx);
                return CommandDispatcher.Ok($"Taxi via @{parkingName}");
            }
        }

        aircraft.Phases.Add(new TaxiingPhase());
        ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases.Start(ctx);

        string msg = $"Taxi via {route.ToSummary()}";
        if (route.Warnings.Count > 0)
        {
            msg += " [" + string.Join("; ", route.Warnings) + "]";
        }

        if (implicitCrossLabel is not null)
        {
            msg += $" (cross {implicitCrossLabel})";
        }

        return CommandDispatcher.Ok(msg);
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
    )
    {
        if (autoTaxi.DestinationRunway is null && autoTaxi.DestinationParking is null)
        {
            return new CommandResult(false, "TAXIAUTO requires a runway or @parking destination");
        }

        Log.LogDebug(
            "[TryTaxiAuto] {Callsign}: destRwy={Rwy} destParking={Parking}",
            aircraft.Callsign,
            autoTaxi.DestinationRunway ?? "(none)",
            autoTaxi.DestinationParking ?? "(none)"
        );

        var taxi = new TaxiCommand(
            Path: [],
            HoldShorts: [],
            DestinationRunway: autoTaxi.DestinationRunway,
            DestinationParking: autoTaxi.DestinationParking
        );

        return TryTaxi(aircraft, taxi, groundLayout, autoCrossRunway);
    }

    /// <summary>
    /// True when a single graph node carries edges on both <paramref name="fromTaxiway"/> and
    /// <paramref name="toTaxiway"/> — i.e. the two taxiways meet at a direct junction the explicit
    /// pathfinder can turn through (mirrors <c>SegmentExpander.FindJunctionCandidates</c>). False when
    /// they meet only across a runway (separate "X - RWY" / "Y - RWY" crossing arcs, no shared node),
    /// where prepending the current taxiway would mis-route the crossing.
    /// </summary>
    private static bool SharesDirectJunction(AirportGroundLayout groundLayout, string fromTaxiway, string toTaxiway)
    {
        foreach (var node in groundLayout.GetNodesOnTaxiway(fromTaxiway))
        {
            foreach (var edge in node.Edges)
            {
                if (edge.MatchesTaxiway(toTaxiway))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static TaxiRoute? ResolveStandardRoute(
        AirportGroundLayout groundLayout,
        GroundNode startNode,
        TaxiCommand taxi,
        out string? failReason,
        AircraftCategory category,
        double startHeadingTrueDeg
    )
    {
        // Empty path + destination runway → A* to nearest hold-short node
        if (taxi.Path.Count == 0 && taxi.DestinationRunway is not null)
        {
            return ResolveRunwayRouteByAStar(groundLayout, startNode, taxi.DestinationRunway, out failReason, category);
        }

        // Crossed-runway directional anchor (issue #172 W6): when CROSS <rwy> is the only directional
        // cue (no destination runway / parking / spot), route the named taxiway(s) toward and across
        // the crossed runway and stop just past it. The far-side hold-short becomes the route terminus,
        // which disambiguates the start direction the way a named destination would — without it,
        // "TAXI G CROSS 28R" from a taxiway that crosses two runways can head the wrong way.
        GroundNode? crossAnchor = ResolveCrossedRunwayAnchor(groundLayout, startNode, taxi);

        return TaxiPathfinder.ResolveExplicitPath(
            groundLayout,
            startNode.Id,
            taxi.Path,
            out failReason,
            new ExplicitPathOptions
            {
                ExplicitHoldShorts = taxi.HoldShorts,
                DestinationRunway = taxi.DestinationRunway,
                AirportId = groundLayout.AirportId,
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
        foreach (var node in layout.GetRunwayHoldShortNodes(crossedRunwayId))
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

    private static TaxiRoute? ResolveRunwayRouteByAStar(
        AirportGroundLayout groundLayout,
        GroundNode startNode,
        string runwayId,
        out string? failReason,
        AircraftCategory category
    )
    {
        failReason = null;
        var holdShortNodes = groundLayout.GetRunwayHoldShortNodes(runwayId);
        if (holdShortNodes.Count == 0)
        {
            failReason = $"No hold-short nodes for runway {RunwayIdentifier.ToDisplayDesignator(runwayId)}";
            return null;
        }

        var route = TaxiPathfinder.FindRunwayRoute(groundLayout, startNode, runwayId, category);
        if (route is null)
        {
            failReason = $"No route to runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} hold-short";
            return null;
        }

        return route;
    }

    private static TaxiRoute? ResolveParkingRoute(
        AirportGroundLayout groundLayout,
        GroundNode startNode,
        TaxiCommand taxi,
        out string? failReason,
        AircraftCategory category,
        double startHeadingTrueDeg
    )
    {
        failReason = null;

        // Resolve destination node: @ = parking/helipad only, $ = spot only
        GroundNode? destNode;
        string destLabel;
        if (taxi.DestinationSpot is not null)
        {
            destNode = groundLayout.FindSpotNodeByName(taxi.DestinationSpot);
            destLabel = taxi.DestinationSpot;
            if (destNode is null)
            {
                failReason = $"Cannot find spot '{taxi.DestinationSpot}'";
                return null;
            }
        }
        else
        {
            destNode = groundLayout.FindHelipadByName(taxi.DestinationParking!) ?? groundLayout.FindParkingByName(taxi.DestinationParking!);
            destLabel = taxi.DestinationParking!;
            if (destNode is null)
            {
                failReason = $"Cannot find parking '{taxi.DestinationParking}'";
                return null;
            }
        }

        if (taxi.Path.Count == 0)
        {
            // No explicit path — A* direct to destination
            var route = TaxiPathfinder.FindRoute(groundLayout, startNode.Id, destNode.Id, category);
            if (route is null)
            {
                failReason = $"No route to {(taxi.DestinationSpot is not null ? "spot" : "parking")} '{destLabel}'";
                return null;
            }

            return SetDestination(route, taxi);
        }

        // Explicit path given — resolve it, then extend to destination via A*
        var explicitRoute = TaxiPathfinder.ResolveExplicitPath(
            groundLayout,
            startNode.Id,
            taxi.Path,
            out failReason,
            new ExplicitPathOptions
            {
                ExplicitHoldShorts = taxi.HoldShorts,
                DestinationRunway = taxi.DestinationRunway,
                AirportId = groundLayout.AirportId,
                DestinationHintNode = destNode,
                PathTurnHints = taxi.PathTurnHints,
                StartHeadingTrue = startHeadingTrueDeg,
            },
            category
        );

        if (explicitRoute is null)
        {
            return null;
        }

        // Find where the explicit path ends. ResolveExplicitPath may have appended a
        // Shortest-A* extension to the parking destination (when SelectBestStopNode
        // cached one), so endNodeId may already be destNode.
        int endNodeId = explicitRoute.Segments.Count > 0 ? explicitRoute.Segments[^1].ToNodeId : startNode.Id;

        List<TaxiRouteSegment> combined;
        List<HoldShortPoint> holdShorts;
        if (endNodeId == destNode.Id)
        {
            combined = [.. explicitRoute.Segments];
            holdShorts = [.. explicitRoute.HoldShortPoints];
        }
        else
        {
            // Extend from end of explicit path to destination node via A*
            var extension = TaxiPathfinder.FindRoute(groundLayout, endNodeId, destNode.Id, category);
            if (extension is null)
            {
                Log.LogDebug("[TryTaxi] Cannot extend from node {EndNode} to {DestLabel}", endNodeId, destLabel);
                failReason = $"Cannot reach {(taxi.DestinationSpot is not null ? "spot" : "parking")} '{destLabel}' from end of taxi route";
                return null;
            }

            combined = new List<TaxiRouteSegment>(explicitRoute.Segments);
            combined.AddRange(extension.Segments);

            holdShorts = [.. explicitRoute.HoldShortPoints];
            HoldShortAnnotator.AddImplicitRunwayHoldShorts(groundLayout, extension.Segments, holdShorts);
        }

        // Safety net: the route resolver should eliminate reversals (a→b immediately
        // followed by b→a), but warn if one slips through so we notice regressions rather
        // than quietly producing U-turns.
        for (int i = 0; i + 1 < combined.Count; i++)
        {
            var a = combined[i];
            var b = combined[i + 1];
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

        return SetDestination(new TaxiRoute { Segments = combined, HoldShortPoints = holdShorts }, taxi);
    }

    private static TaxiRoute SetDestination(TaxiRoute route, TaxiCommand taxi)
    {
        // TaxiRoute uses init-only props, so return a new instance with destination set.
        return new TaxiRoute
        {
            Segments = route.Segments,
            HoldShortPoints = route.HoldShortPoints,
            Warnings = route.Warnings,
            DestinationParking = taxi.DestinationParking,
            DestinationSpot = taxi.DestinationSpot,
        };
    }

    internal static CommandResult TryPushback(AircraftState aircraft, PushbackCommand push, AirportGroundLayout? groundLayout)
    {
        // Mid-pushback amendment: heading-only PUSH (FACE/TAIL) updates the active
        // pushback phase's target heading in place, accepted until the nose has
        // begun rotating to the prior target (issue #167).
        if (aircraft.Phases?.CurrentPhase is PushbackPhase or PushbackToSpotPhase)
        {
            bool headingOnly =
                push.Taxiway is null
                && push.FacingTaxiway is null
                && push.DestinationParking is null
                && push.DestinationSpot is null
                && push.MagneticHeading is not null;

            if (!headingOnly)
            {
                return new CommandResult(false, "Unable, only face/tail amendment accepted during pushback");
            }

            int newHeading = push.MagneticHeading!.Value.ToDisplayInt();
            var amendCtx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
            bool updated = aircraft.Phases.CurrentPhase switch
            {
                PushbackPhase p => p.TryUpdateTargetHeading(newHeading, amendCtx),
                PushbackToSpotPhase p => p.TryUpdateTargetHeading(newHeading, amendCtx),
                _ => false,
            };

            if (!updated)
            {
                return new CommandResult(false, "Unable, pushback turn in progress");
            }

            return CommandDispatcher.Ok($"Pushback amended, face heading {newHeading:000}");
        }

        if (aircraft.Phases?.CurrentPhase is not (AtParkingPhase or HoldingAfterPushbackPhase))
        {
            return new CommandResult(false, "Pushback requires aircraft to be at parking");
        }

        if (push.DestinationParking is not null || push.DestinationSpot is not null)
        {
            return TryPushbackToSpot(aircraft, push, groundLayout);
        }

        double? targetLat = null;
        double? targetLon = null;
        int? resolvedHeading = push.MagneticHeading?.ToDisplayInt();

        if (push.Taxiway is not null)
        {
            if (groundLayout is null)
            {
                return new CommandResult(false, "No airport ground layout available");
            }

            var targetNode = groundLayout.FindExitByTaxiway(aircraft.Position, push.Taxiway);
            if (targetNode is null)
            {
                return new CommandResult(false, $"Cannot find taxiway '{push.Taxiway}' near aircraft");
            }

            targetLat = targetNode.Position.Lat;
            targetLon = targetNode.Position.Lon;

            Log.LogDebug(
                "[Pushback] {Callsign}: target taxiway {Twy} at node {NodeId} ({Lat:F6}, {Lon:F6})",
                aircraft.Callsign,
                push.Taxiway,
                targetNode.Id,
                targetLat,
                targetLon
            );

            // Resolve facing direction: face along the PUSH taxiway edge at the target node,
            // picking the direction closest to the hint.
            //   PUSH TE T   → hint = bearing toward FacingTaxiway T.
            //   PUSH TE <E  → hint = the cardinal magnetic heading (270 here, since '<E' = tail east → face west).
            if (push.FacingTaxiway is not null && resolvedHeading is null)
            {
                var facingNode = groundLayout.FindExitByTaxiway(targetNode.Position, push.FacingTaxiway);
                if (facingNode is not null)
                {
                    double bearingToFacing = GeoMath.BearingTo(targetNode.Position, facingNode.Position);
                    double? edgeBearing = groundLayout.GetEdgeBearingForTaxiway(targetNode, push.Taxiway!, bearingToFacing);

                    if (edgeBearing is not null)
                    {
                        resolvedHeading = FlightPhysics.BearingToDisplayInt(edgeBearing.Value);
                        Log.LogDebug(
                            "[Pushback] {Callsign}: facing {FTwy} → heading {Hdg:000} (along {PTwy}, bearingToFacing={Brg:F0})",
                            aircraft.Callsign,
                            push.FacingTaxiway,
                            resolvedHeading,
                            push.Taxiway,
                            bearingToFacing
                        );
                    }
                    else
                    {
                        Log.LogWarning(
                            "[Pushback] {Callsign}: no {PTwy} edges at target node to align toward {FTwy}",
                            aircraft.Callsign,
                            push.Taxiway,
                            push.FacingTaxiway
                        );
                    }
                }
                else
                {
                    Log.LogWarning(
                        "[Pushback] {Callsign}: cannot find facing taxiway '{FTwy}' near exit node",
                        aircraft.Callsign,
                        push.FacingTaxiway
                    );
                }
            }
            else if (resolvedHeading is not null)
            {
                // Cardinal hint provided: snap to the closest of the push-taxiway's two edge directions
                // at the target node so the aircraft ends up aligned with the taxiway.
                double? edgeBearing = groundLayout.GetEdgeBearingForTaxiway(targetNode, push.Taxiway!, resolvedHeading.Value);
                if (edgeBearing is not null)
                {
                    int snapped = FlightPhysics.BearingToDisplayInt(edgeBearing.Value);
                    Log.LogDebug(
                        "[Pushback] {Callsign}: cardinal hint {Hint:000} → snapped to {Hdg:000} along {PTwy}",
                        aircraft.Callsign,
                        resolvedHeading,
                        snapped,
                        push.Taxiway
                    );
                    resolvedHeading = snapped;
                }
                else
                {
                    Log.LogDebug(
                        "[Pushback] {Callsign}: no {PTwy} edges at target node — using cardinal hint {Hdg:000} directly",
                        aircraft.Callsign,
                        push.Taxiway,
                        resolvedHeading
                    );
                }
            }

            // Offset the target position away from the facing direction along the push taxiway.
            // This simulates the tug pushing and turning simultaneously — the aircraft ends up
            // slightly past the intersection rather than rotating fully in place.
            if (resolvedHeading is not null)
            {
                double awayBearing = (resolvedHeading.Value + 180) % 360;
                double? awayEdgeBearing = groundLayout.GetEdgeBearingForTaxiway(targetNode, push.Taxiway!, awayBearing);
                if (awayEdgeBearing is not null)
                {
                    double overshootNm = CategoryPerformance.PushbackOvershootNm(aircraft.AircraftType);
                    var offset = GeoMath.ProjectPointRaw(targetLat!.Value, targetLon!.Value, awayEdgeBearing.Value, overshootNm);
                    targetLat = offset.Lat;
                    targetLon = offset.Lon;
                    Log.LogDebug(
                        "[Pushback] {Callsign}: overshoot {Dist:F4}nm along bearing {Brg:F0} → ({Lat:F6}, {Lon:F6})",
                        aircraft.Callsign,
                        overshootNm,
                        awayEdgeBearing.Value,
                        targetLat,
                        targetLon
                    );
                }
            }
        }

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases.Clear(ctx);

        var phase = new PushbackPhase
        {
            TargetHeading = resolvedHeading,
            TargetLatitude = targetLat,
            TargetLongitude = targetLon,
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(ctx);

        var msg = "Pushing back";
        if (push.Taxiway is not null)
        {
            msg += $" onto {push.Taxiway}";
        }

        if (push.FacingTaxiway is not null)
        {
            msg += $" facing {push.FacingTaxiway}";
        }
        else if (resolvedHeading is not null)
        {
            msg += $", face heading {resolvedHeading:000}";
        }

        return CommandDispatcher.Ok(msg);
    }

    private static CommandResult TryPushbackToSpot(AircraftState aircraft, PushbackCommand push, AirportGroundLayout? groundLayout)
    {
        if (groundLayout is null)
        {
            return new CommandResult(false, "No airport ground layout available");
        }

        // Resolve destination: @ = parking/helipad only, $ = spot only
        GroundNode? destNode;
        string destLabel;
        if (push.DestinationSpot is not null)
        {
            destNode = groundLayout.FindSpotNodeByName(push.DestinationSpot);
            destLabel = push.DestinationSpot;
            if (destNode is null)
            {
                return new CommandResult(false, $"Cannot find spot '{push.DestinationSpot}'");
            }
        }
        else
        {
            destNode = groundLayout.FindHelipadByName(push.DestinationParking!) ?? groundLayout.FindParkingByName(push.DestinationParking!);
            destLabel = push.DestinationParking!;
            if (destNode is null)
            {
                return new CommandResult(false, $"Cannot find parking '{push.DestinationParking}'");
            }
        }

        var startNode = groundLayout.FindNearestNode(aircraft.Position);
        if (startNode is null)
        {
            return new CommandResult(false, "Cannot find position on taxiway graph");
        }

        var route = TaxiPathfinder.FindRoute(groundLayout, startNode.Id, destNode.Id, AircraftCategorization.Categorize(aircraft.AircraftType));
        if (route is null)
        {
            return new CommandResult(false, $"No route to {(push.DestinationSpot is not null ? "spot" : "parking")} '{destLabel}'");
        }

        // Resolve final heading: explicit heading, facing taxiway, or parking node's heading
        int? resolvedHeading = push.MagneticHeading?.ToDisplayInt();
        if (resolvedHeading is null && push.FacingTaxiway is not null)
        {
            var facingNode = groundLayout.FindExitByTaxiway(destNode.Position, push.FacingTaxiway);
            if (facingNode is not null)
            {
                double bearingToFacing = GeoMath.BearingTo(destNode.Position, facingNode.Position);
                double? edgeBearing = groundLayout.GetEdgeBearingForTaxiway(destNode, push.FacingTaxiway, bearingToFacing);
                resolvedHeading = edgeBearing is not null
                    ? FlightPhysics.BearingToDisplayInt(edgeBearing.Value)
                    : FlightPhysics.BearingToDisplayInt(bearingToFacing);
            }
        }

        resolvedHeading ??= destNode.TrueHeading is not null ? destNode.TrueHeading.Value.ToDisplayInt() : null;

        Log.LogDebug(
            "[Pushback] {Callsign}: push to {DestLabel} via {SegCount} segments, finalHdg={Hdg}",
            aircraft.Callsign,
            destLabel,
            route.Segments.Count,
            resolvedHeading?.ToString() ?? "none"
        );

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases!.Clear(ctx);

        var phase = new PushbackToSpotPhase(route, resolvedHeading);
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(ctx);

        aircraft.Ground.ParkingSpot = destLabel.ToUpperInvariant();

        var msg = $"Pushing back to {destLabel}";
        if (push.FacingTaxiway is not null)
        {
            msg += $" facing {push.FacingTaxiway}";
        }
        else if (push.MagneticHeading is not null)
        {
            msg += $", heading {push.MagneticHeading.Value.ToDisplayInt():000}";
        }

        return CommandDispatcher.Ok(msg);
    }

    internal static CommandResult TryAssignRunway(AircraftState aircraft, string runwayId)
    {
        var runway = CommandDispatcher.ResolveRunway(aircraft, runwayId);
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

        aircraft.Ground.Hold = HoldDirective.HoldPosition;
        aircraft.Ground.IsExpeditingTaxi = false;
        return CommandDispatcher.Ok(BuildHoldMessage(aircraft));
    }

    private static string BuildHoldMessage(AircraftState aircraft)
    {
        var phase = aircraft.Phases?.CurrentPhase;
        string? where = phase switch
        {
            TaxiingPhase => aircraft.Ground.CurrentTaxiway is { } twy ? $"on taxiway {twy}" : "while taxiing",
            CrossingRunwayPhase => "during runway crossing",
            PushbackPhase or PushbackToSpotPhase => "during pushback",
            LineUpPhase or LinedUpAndWaitingPhase => aircraft.Phases?.AssignedRunway?.Designator is { } rwy
                ? $"on runway {RunwayIdentifier.ToDisplayDesignator(rwy)}"
                : "lined up",
            RunwayExitPhase => "during runway exit",
            FollowingPhase => "while following",
            HoldingShortPhase hs => hs.HoldShort.TargetName is { } target
                ? $"already short of {RunwayIdentifier.ToDisplayDesignator(target)}"
                : "already holding short",
            HoldingInPositionPhase or HoldingAfterPushbackPhase or HoldingAfterExitPhase => "already in position",
            _ => null,
        };

        return where is null ? "Hold position" : $"Hold position ({where})";
    }

    internal static CommandResult TryResumeTaxi(AircraftState aircraft)
    {
        if (!aircraft.Ground.IsImmobile)
        {
            return new CommandResult(false, "Aircraft is not held");
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

        var route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        foreach (var rwy in runways)
        {
            bool matchedAny = false;
            foreach (var hs in route.HoldShortPoints)
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
        foreach (var rwy in runways)
        {
            foreach (var hs in route.HoldShortPoints)
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
    /// Adds or promotes explicit hold-short points on the aircraft's taxi route
    /// for each target in <paramref name="targets"/>. Runway targets promote an
    /// upcoming RunwayCrossing to ExplicitHoldShort (survives AutoCross); taxiway
    /// targets add a new ExplicitHoldShort at the first matching intersection.
    /// Strict: fails the entire command if any target can't be matched against
    /// the route. Empty list is a no-op success. Requires the ground layout for
    /// segment walks; will fail for targets that aren't already on the route's
    /// HoldShortPoints list when no layout is available.
    /// </summary>
    internal static CommandResult TryAddExplicitHoldShorts(AircraftState aircraft, AirportGroundLayout? layout, IReadOnlyList<string> targets)
    {
        if (targets.Count == 0)
        {
            return CommandDispatcher.Ok("");
        }

        var route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        // Slice the segment list to the part of the route ahead of the current
        // taxi position so we don't add a hold-short at a node the aircraft has
        // already passed. The promote pass inside AddExplicitHoldShort still
        // walks the full HoldShortPoints list — for already-satisfied past
        // crossings the upgrade is a no-op (the aircraft is past them).
        int startIdx = Math.Max(0, route.CurrentSegmentIndex);
        var upcomingSegments = startIdx == 0 ? route.Segments : route.Segments.GetRange(startIdx, route.Segments.Count - startIdx);

        foreach (var target in targets)
        {
            if (layout is null)
            {
                // Fall back to promote-only against the existing hold-short list.
                bool promoted = false;
                foreach (var hs in route.HoldShortPoints)
                {
                    if (hs.IsCleared || hs.Reason != HoldShortReason.RunwayCrossing || hs.TargetName is null)
                    {
                        continue;
                    }
                    if (!RunwayIdentifier.Parse(hs.TargetName).Contains(target))
                    {
                        continue;
                    }
                    hs.Reason = HoldShortReason.ExplicitHoldShort;
                    promoted = true;
                    break;
                }
                if (!promoted)
                {
                    return new CommandResult(false, $"No match for HS {target} in taxi route");
                }
                continue;
            }

            bool matched = HoldShortAnnotator.AddExplicitHoldShort(layout, upcomingSegments, route.HoldShortPoints, target);
            if (!matched)
            {
                return new CommandResult(false, $"No match for HS {target} in taxi route");
            }
        }

        return CommandDispatcher.Ok("");
    }

    internal static CommandResult TryCrossRunway(AircraftState aircraft, CrossRunwayCommand cross)
    {
        if (cross.RunwayId is null)
        {
            return TryCrossNextHoldShort(aircraft);
        }

        var target = cross.RunwayId;

        // Currently holding short AT the requested target: satisfy the clearance now.
        // Target match works for both runway designators (e.g. CROSS 28R against
        // "28R/10L") and taxiway/intersection names (e.g. CROSS B against "B").
        if (aircraft.Phases?.CurrentPhase is HoldingShortPhase holdPhase && HoldShortTargetMatches(holdPhase.HoldShort.TargetName, target))
        {
            // A DestinationRunway hold-short is a departure hold (use LUAW/CTO) only while the
            // taxi is still in progress. Once the route has completed at it, CROSS undesignates
            // the runway and taxis the aircraft across to the far-side hold-short instead.
            if (holdPhase.HoldShort.Reason == HoldShortReason.DestinationRunway && aircraft.Ground.AssignedTaxiRoute is not { IsComplete: true })
            {
                return new CommandResult(
                    false,
                    $"Cannot cross destination runway {RunwayIdentifier.ToDisplayDesignator(holdPhase.HoldShort.TargetName ?? "")}; use LUAW or CTO"
                );
            }

            var continuation = TryPrepareCompletedRouteCrossing(aircraft, holdPhase);
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
        var route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        bool matchedAny = false;
        foreach (var hs in route.HoldShortPoints)
        {
            if (!HoldShortTargetMatches(hs.TargetName, target))
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

        foreach (var hs in route.HoldShortPoints)
        {
            if (HoldShortTargetMatches(hs.TargetName, target) && hs.Reason != HoldShortReason.DestinationRunway)
            {
                hs.IsCleared = true;
            }
        }

        return CommandDispatcher.Ok($"Cross {target}");
    }

    /// <summary>
    /// Whether a hold-short target name matches a CROSS argument. Accepts both
    /// runway designators (parsed via <see cref="RunwayIdentifier"/>, so
    /// <c>28R</c> matches <c>28R/10L</c>) and taxiway/intersection names
    /// (case-insensitive string equality, so <c>B</c> matches <c>B</c>).
    /// </summary>
    private static bool HoldShortTargetMatches(string? targetName, string arg)
    {
        if (targetName is null)
        {
            return false;
        }

        if (RunwayIdentifier.Parse(targetName).Contains(arg))
        {
            return true;
        }

        return string.Equals(targetName, arg, StringComparison.OrdinalIgnoreCase);
    }

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

        if (!IsRunwayHoldShort(aircraft, holdPhase.HoldShort, out var runwayId))
        {
            return CommandDispatcher.Ok("");
        }

        var layout = aircraft.Ground.Layout;
        if (layout is null)
        {
            return new CommandResult(false, "No airport ground layout available");
        }

        var crossing = FindCompletedRouteCrossing(aircraft, layout, holdPhase.HoldShort.NodeId, runwayId);
        if (crossing is null)
        {
            return new CommandResult(false, $"No crossing route found for {holdPhase.HoldShort.TargetName ?? "runway"}");
        }

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
        var layout = aircraft.Ground.Layout;
        if (
            layout is null
            || !layout.Nodes.TryGetValue(holdShort.NodeId, out var node)
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
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        CompletedRouteCrossing? best = null;
        double bestDistance = double.MaxValue;

        foreach (var candidate in layout.Nodes.Values)
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

            var route = TaxiPathfinder.FindRoute(layout, holdShortNodeId, candidate.Id, category);
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
        foreach (var segment in route.Segments)
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
        if (!layout.Nodes.TryGetValue(nodeId, out var node))
        {
            return false;
        }

        foreach (var edge in node.Edges)
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
        var route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        if (!IsRunwayHoldShort(aircraft, terminalHoldShort, out var runwayId))
        {
            return new CommandResult(
                false,
                $"Cannot cross destination runway {RunwayIdentifier.ToDisplayDesignator(terminalHoldShort.TargetName ?? "")}; use LUAW or CTO"
            );
        }

        var layout = aircraft.Ground.Layout;
        if (layout is null)
        {
            return new CommandResult(false, "No airport ground layout available");
        }

        var crossing = FindCompletedRouteCrossing(aircraft, layout, terminalHoldShort.NodeId, runwayId);
        if (crossing is null)
        {
            return new CommandResult(false, $"No crossing route found for {terminalHoldShort.TargetName ?? "runway"}");
        }

        // Append the crossing (terminal hold-short → far-side hold-short) onto the live route
        // so TaxiingPhase finds a forward same-runway exit when it reaches the hold-short.
        route.Segments.AddRange(crossing.Route.Segments);
        foreach (var hs in crossing.Route.HoldShortPoints)
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
            if (holdPhase.HoldShort.Reason == HoldShortReason.DestinationRunway && aircraft.Ground.AssignedTaxiRoute is not { IsComplete: true })
            {
                return new CommandResult(
                    false,
                    $"Cannot cross destination runway {RunwayIdentifier.ToDisplayDesignator(holdPhase.HoldShort.TargetName ?? "")}; use LUAW or CTO"
                );
            }

            var continuation = TryPrepareCompletedRouteCrossing(aircraft, holdPhase);
            if (!continuation.Success)
            {
                return continuation;
            }

            holdPhase.SatisfyClearance(ClearanceType.RunwayCrossing);
            return CommandDispatcher.Ok($"Cross {holdPhase.HoldShort.TargetName ?? "next hold-short"}");
        }

        var route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        HoldShortPoint? next = null;
        foreach (var hs in route.HoldShortPoints)
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
        return CommandDispatcher.Ok($"Cross {next.TargetName ?? "next hold-short"}");
    }

    internal static CommandResult TryHoldShort(AircraftState aircraft, HoldShortCommand hs, AirportGroundLayout? groundLayout)
    {
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "Hold short requires aircraft on the ground");
        }

        var route = aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        if (groundLayout is null)
        {
            return new CommandResult(false, "No ground layout available");
        }

        bool added = route.AddHoldShortAtIntersection(hs.Target, groundLayout);
        if (!added)
        {
            return new CommandResult(false, $"No intersection with {hs.Target} along remaining taxi route");
        }

        // Compute offset position for the newly added hold-short
        double aircraftLengthFt =
            FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(aircraft.AircraftType);
        HoldShortAnnotator.ComputeHoldShortPositions(groundLayout, route, aircraftLengthFt);

        aircraft.Ground.IsExpeditingTaxi = false;

        Log.LogDebug("[HS] {Callsign}: added hold short of {Target}", aircraft.Callsign, hs.Target);
        return CommandDispatcher.Ok($"Hold short of {hs.Target}");
    }

    internal static CommandResult TryFollow(AircraftState aircraft, FollowGroundCommand follow, AirportGroundLayout? groundLayout)
    {
        var currentPhase = aircraft.Phases?.CurrentPhase;
        if (currentPhase is null)
        {
            return new CommandResult(false, "Aircraft has no active phase");
        }

        // Must be on the ground in a phase that accepts Follow
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "Follow requires the aircraft to be on the ground");
        }

        var acceptance = currentPhase.CanAcceptCommand(CanonicalCommandType.FollowGround);
        if (acceptance.IsRejected)
        {
            var reason = acceptance.Reason ?? $"Cannot follow during {currentPhase.Name}";
            return new CommandResult(false, reason);
        }

        // Replace phases with FollowingPhase. Clear() marks the active phase as Skipped
        // and advances CurrentIndex past the end, but does not remove the phase entries —
        // truncate the list before adding so Start() lands on the new FollowingPhase at index 0.
        var phases = aircraft.Phases!;
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
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
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

        if (!TryResolveAirTaxiDestination(groundLayout, destination, out double destLat, out double destLon))
        {
            return new CommandResult(
                false,
                $"Cannot find destination '{destination}' in airport layout (expected helipad, parking, taxiway spot, or runway)"
            );
        }

        string resolvedName = destination.ToUpperInvariant();

        // Clear current phases and chain air-taxi → land → at-parking so the heli
        // lifts off, cruises to the destination, descends, and stops on the spot.
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        if (aircraft.Phases is not null)
        {
            aircraft.Phases.Clear(ctx);
        }

        aircraft.Ground.Hold = null;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AirTaxiPhase(destLat, destLon, resolvedName));
        aircraft.Phases.Add(new HelicopterLandingPhase());
        aircraft.Phases.Add(new AtParkingPhase());
        ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases.Start(ctx);

        aircraft.Ground.ParkingSpot = resolvedName;

        return CommandDispatcher.Ok($"Air taxi to {resolvedName}");
    }

    /// <summary>
    /// Resolve an ATXI destination to a (lat, lon) by trying, in order:
    ///   1. <see cref="AirportGroundLayout.FindSpotByName"/> (helipad, parking, spot node)
    ///   2. <see cref="AirportGroundLayout.FindRunway"/> matched on either end designator,
    ///      using the threshold of the requested end as the target point.
    /// Returns false if neither lookup matches.
    /// </summary>
    private static bool TryResolveAirTaxiDestination(AirportGroundLayout layout, string destination, out double lat, out double lon)
    {
        var spot = layout.FindSpotByName(destination);
        if (spot is not null)
        {
            lat = spot.Position.Lat;
            lon = spot.Position.Lon;
            return true;
        }

        var runway = layout.FindRunway(destination);
        if (runway is not null && runway.Coordinates.Count >= 2)
        {
            // GroundRunway.Coordinates run from the first-named end to the second.
            // Target the threshold of whichever end the controller named.
            var ends = runway.EndDesignators;
            bool isFirstEnd = ends.Count == 2 && ends[0].Equals(destination, StringComparison.OrdinalIgnoreCase);
            var threshold = isFirstEnd ? runway.Coordinates[0] : runway.Coordinates[^1];
            lat = threshold.Lat;
            lon = threshold.Lon;
            return true;
        }

        lat = 0;
        lon = 0;
        return false;
    }

    internal static CommandResult TryLand(AircraftState aircraft, LandCommand land, AirportGroundLayout? groundLayout)
    {
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
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
            var node = groundLayout.FindExitByTaxiway(aircraft.Position, land.SpotName);
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
            var spot = groundLayout.FindSpotByName(land.SpotName);
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
        }

        // Clear current phases and set up air taxi → land sequence
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        if (aircraft.Phases is not null)
        {
            aircraft.Phases.Clear(ctx);
        }

        aircraft.Ground.Hold = null;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AirTaxiPhase(destLat, destLon, resolvedName));
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
        if (!layout.Nodes.TryGetValue(finalNodeId, out var node))
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
            foreach (var edge in node.Edges)
            {
                if (edge.IsRunwayCenterline)
                {
                    var rawDesignator = edge.TaxiwayName[3..];
                    rwyId = RunwayIdentifier.Parse(rawDesignator);
                    break;
                }
            }
        }

        if (rwyId is null)
        {
            return null;
        }

        var runway = ResolveClosestRunwayEnd(rwyId.Value, node.Position.Lat, node.Position.Lon, aircraft);
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
        var airportId = aircraft.FlightPlan.Departure;
        if (airportId is null)
        {
            return null;
        }

        var navDb = NavigationDatabase.Instance;
        var info = navDb.GetRunway(airportId, rwyId.End1) ?? navDb.GetRunway(airportId, rwyId.End2);
        if (info is null)
        {
            return null;
        }

        double dist1 = GeoMath.DistanceNm(nodeLat, nodeLon, info.Lat1, info.Lon1);
        double dist2 = GeoMath.DistanceNm(nodeLat, nodeLon, info.Lat2, info.Lon2);
        string closerDesignator = dist1 <= dist2 ? info.Id.End1 : info.Id.End2;
        return info.ForApproach(closerDesignator);
    }

    private const double BreakDurationSeconds = 15.0;

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
            return new CommandResult(false, "Unable to pull forward — runway-clearance geometry unavailable");
        }

        // Supersede the binding taxiway hold-short and release the hold (resolving the tail-over-runway
        // state, which releases the occupied runway node), then drive forward ½ aircraft length past the
        // runway bars and hold there — clear of the runway behind it (issue #172 W5).
        holdPhase.HoldShort.IsCleared = true;
        holdPhase.HoldShort.TailOverRunwayNodeId = null;

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
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

    internal static CommandResult TryExitCommand(AircraftState aircraft, ExitPreference preference, bool noDelete)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        // Require a landing or runway-exit context. EXIT during cruise/enroute
        // would silently store the preference for a landing that may never
        // happen. Real ATC issues EL/ER on short final or during rollout.
        bool hasLandingOrExit = false;
        foreach (var phase in aircraft.Phases.Phases)
        {
            if (phase is LandingPhase or HelicopterLandingPhase or RunwayExitPhase && phase.Status is PhaseStatus.Pending or PhaseStatus.Active)
            {
                hasLandingOrExit = true;
                break;
            }
        }

        if (!hasLandingOrExit)
        {
            return new CommandResult(false, "Exit requires a pending landing or active runway exit");
        }

        aircraft.Phases.RequestedExit = preference;
        if (noDelete)
        {
            aircraft.Ground.AutoDeleteExempt = true;
        }

        if (preference.Taxiway is not null)
        {
            var sideText = preference.Side switch
            {
                ExitSide.Left => "left ",
                ExitSide.Right => "right ",
                _ => "",
            };
            return CommandDispatcher.Ok($"Exit {sideText}at {preference.Taxiway}");
        }

        return CommandDispatcher.Ok(preference.Side == ExitSide.Left ? "Exit left" : "Exit right");
    }
}
