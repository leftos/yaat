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

        // Find starting node: nearest to aircraft's current position
        var startNode = groundLayout.FindNearestNode(aircraft.Position);

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

        TaxiRoute? route;
        string? failReason;

        if (taxi.DestinationParking is not null || taxi.DestinationSpot is not null)
        {
            route = ResolveParkingRoute(groundLayout, startNode, taxi, out failReason);
        }
        else
        {
            route = ResolveStandardRoute(groundLayout, startNode, taxi, out failReason);
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
        // runway and the new route's first runway crossing is for that same runway, the TAXI
        // command itself authorizes the crossing — no separate CTO needed. Subsequent crossings
        // still require explicit clearance.
        string? implicitCrossLabel = null;
        if (aircraft.Phases?.CurrentPhase is HoldingShortPhase priorHold && priorHold.HoldShort.TargetName is { Length: > 0 } priorRwy)
        {
            var firstCrossing = route.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.RunwayCrossing);
            if (firstCrossing is not null && firstCrossing.TargetName is { Length: > 0 } crossingRwy)
            {
                if (RunwayIdentifier.Parse(crossingRwy).Overlaps(RunwayIdentifier.Parse(priorRwy)))
                {
                    firstCrossing.IsCleared = true;
                    implicitCrossLabel = crossingRwy;
                    Log.LogInformation(
                        "[TryTaxi] {Callsign}: implicit cross of {Rwy} at node {NodeId} (already holding short of {PriorRwy})",
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
                    var reroute = TaxiPathfinder.FindRoute(groundLayout, bestNeighbor.Id, destNode.Id);
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

    private static TaxiRoute? ResolveStandardRoute(AirportGroundLayout groundLayout, GroundNode startNode, TaxiCommand taxi, out string? failReason)
    {
        // Empty path + destination runway → A* to nearest hold-short node
        if (taxi.Path.Count == 0 && taxi.DestinationRunway is not null)
        {
            return ResolveRunwayRouteByAStar(groundLayout, startNode, taxi.DestinationRunway, out failReason);
        }

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
            }
        );
    }

    private static TaxiRoute? ResolveRunwayRouteByAStar(
        AirportGroundLayout groundLayout,
        GroundNode startNode,
        string runwayId,
        out string? failReason
    )
    {
        failReason = null;
        var holdShortNodes = groundLayout.GetRunwayHoldShortNodes(runwayId);
        if (holdShortNodes.Count == 0)
        {
            failReason = $"No hold-short nodes for runway {runwayId}";
            return null;
        }

        var targetHs = TaxiPathfinder.FindFullLengthLineupHoldShort(groundLayout, startNode, runwayId, holdShortNodes);

        var route = TaxiPathfinder.FindRoute(groundLayout, startNode.Id, targetHs.Id);
        if (route is null)
        {
            failReason = $"No route to runway {runwayId} hold-short";
            return null;
        }

        return route;
    }

    private static TaxiRoute? ResolveParkingRoute(AirportGroundLayout groundLayout, GroundNode startNode, TaxiCommand taxi, out string? failReason)
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
            var route = TaxiPathfinder.FindRoute(groundLayout, startNode.Id, destNode.Id);
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
            }
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
            var extension = TaxiPathfinder.FindRoute(groundLayout, endNodeId, destNode.Id);
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

        // Safety net: the fix in TaxiPathfinder.SelectBestStopNode + ResolveExplicitPath
        // should eliminate reversals (a→b immediately followed by b→a), but warn if one
        // slips through so we notice regressions rather than quietly producing U-turns.
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

        var route = TaxiPathfinder.FindRoute(groundLayout, startNode.Id, destNode.Id);
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
            return new CommandResult(false, $"Unknown runway {runwayId}");
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

        return CommandDispatcher.Ok($"Runway {runway.Designator}");
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
            LineUpPhase or LinedUpAndWaitingPhase => aircraft.Phases?.AssignedRunway?.Designator is { } rwy ? $"on runway {rwy}" : "lined up",
            RunwayExitPhase => "during runway exit",
            FollowingPhase => "while following",
            HoldingShortPhase hs => hs.HoldShort.TargetName is { } target ? $"already short of {target}" : "already holding short",
            HoldingInPositionPhase or HoldingAfterPushbackPhase or HoldingAfterExitPhase => "already in position",
            AirTaxiPhase => "during air taxi",
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

                if (hs.Reason == HoldShortReason.DestinationRunway)
                {
                    return new CommandResult(false, $"Cannot cross destination runway {hs.TargetName}; use LUAW or CTO");
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
        // Currently holding short AT the requested runway: satisfy the clearance now.
        if (
            aircraft.Phases?.CurrentPhase is HoldingShortPhase holdPhase
            && holdPhase.HoldShort.TargetName is not null
            && RunwayIdentifier.Parse(holdPhase.HoldShort.TargetName).Contains(cross.RunwayId)
        )
        {
            if (holdPhase.HoldShort.Reason == HoldShortReason.DestinationRunway)
            {
                return new CommandResult(false, $"Cannot cross destination runway {holdPhase.HoldShort.TargetName}; use LUAW or CTO");
            }

            holdPhase.SatisfyClearance(ClearanceType.RunwayCrossing);
            return CommandDispatcher.Ok($"Cross {cross.RunwayId}");
        }

        // Either not holding short, or holding short of a *different* runway:
        // pre-clear the matching upcoming hold-short in the taxi route. Shares
        // dest-runway protection and route-match validation with RES CROSS X.
        var preClear = TryPreClearRouteCrossings(aircraft, [cross.RunwayId]);
        if (!preClear.Success)
        {
            return preClear;
        }

        return CommandDispatcher.Ok($"Cross {cross.RunwayId}");
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
