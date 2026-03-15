using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

internal static class GroundCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("GroundCommandHandler");

    internal static CommandResult TryTaxi(AircraftState aircraft, TaxiCommand taxi, AirportGroundLayout? groundLayout, bool autoCrossRunway = false)
    {
        if (groundLayout is null)
        {
            Log.LogWarning(
                "[TryTaxi] {Callsign}: no ground layout (departure={Dep}, destination={Dest})",
                aircraft.Callsign,
                aircraft.Departure,
                aircraft.Destination
            );
            return new CommandResult(false, "No airport ground layout available");
        }

        // Find starting node: nearest to aircraft's current position
        var startNode = groundLayout.FindNearestNode(aircraft.Latitude, aircraft.Longitude);

        if (startNode is null)
        {
            Log.LogWarning("[TryTaxi] {Callsign}: no nearest node at ({Lat:F6}, {Lon:F6})", aircraft.Callsign, aircraft.Latitude, aircraft.Longitude);
            return new CommandResult(false, "Cannot find position on taxiway graph");
        }

        Log.LogDebug(
            "[TryTaxi] {Callsign}: nearest node {NodeId} at ({NLat:F6}, {NLon:F6}), dist={Dist:F4}nm, path=[{Path}], destRwy={Rwy}, destParking={Pkg}, destSpot={Spot}",
            aircraft.Callsign,
            startNode.Id,
            startNode.Latitude,
            startNode.Longitude,
            GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, startNode.Latitude, startNode.Longitude),
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

        var hsDetails = string.Join(", ", route.HoldShortPoints.Select(h => $"{h.TargetName}@{h.NodeId}({h.Reason})"));
        Log.LogInformation(
            "[TryTaxi] {Callsign}: route resolved — {SegCount} segments, {HsCount} hold-shorts [{HsDetails}], summary: {Summary}",
            aircraft.Callsign,
            route.Segments.Count,
            route.HoldShortPoints.Count,
            hsDetails,
            route.ToSummary()
        );

        if (autoCrossRunway)
        {
            foreach (var hs in route.HoldShortPoints)
            {
                if (hs.Reason == HoldShortReason.RunwayCrossing)
                {
                    hs.IsCleared = true;
                }
            }
        }

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
        aircraft.AssignedTaxiRoute = route;
        aircraft.IsHeld = false;

        if (taxi.NoDelete)
        {
            aircraft.AutoDeleteExempt = true;
        }

        aircraft.Phases = new PhaseList();
        if (detectedRunway is not null)
        {
            aircraft.Phases.AssignedRunway = detectedRunway;
            aircraft.DepartureRunway = detectedRunway.Designator;
        }

        aircraft.Phases.Add(new TaxiingPhase());
        ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases.Start(ctx);

        string msg = $"Taxi via {route.ToSummary()}";
        if (route.Warnings.Count > 0)
        {
            msg += " [" + string.Join("; ", route.Warnings) + "]";
        }

        return CommandDispatcher.Ok(msg);
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
            taxi.HoldShorts,
            taxi.DestinationRunway,
            groundLayout.AirportId
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

        // Pick the nearest hold-short node
        GroundNode? bestNode = null;
        double bestDist = double.MaxValue;
        foreach (var node in holdShortNodes)
        {
            double dist = GeoMath.DistanceNm(startNode.Latitude, startNode.Longitude, node.Latitude, node.Longitude);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestNode = node;
            }
        }

        var route = TaxiPathfinder.FindRoute(groundLayout, startNode.Id, bestNode!.Id);
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
            }

            return route;
        }

        // Explicit path given — resolve it, then extend to destination via A*
        var explicitRoute = TaxiPathfinder.ResolveExplicitPath(
            groundLayout,
            startNode.Id,
            taxi.Path,
            out failReason,
            taxi.HoldShorts,
            taxi.DestinationRunway,
            groundLayout.AirportId
        );

        if (explicitRoute is null)
        {
            return null;
        }

        // Find where the explicit path ends
        int endNodeId = explicitRoute.Segments.Count > 0 ? explicitRoute.Segments[^1].ToNodeId : startNode.Id;

        if (endNodeId == destNode.Id)
        {
            return explicitRoute;
        }

        // Extend from end of explicit path to destination node via A*
        var extension = TaxiPathfinder.FindRoute(groundLayout, endNodeId, destNode.Id);
        if (extension is null)
        {
            Log.LogDebug("[TryTaxi] Cannot extend from node {EndNode} to {DestLabel}", endNodeId, destLabel);
            failReason = $"Cannot reach {(taxi.DestinationSpot is not null ? "spot" : "parking")} '{destLabel}' from end of taxi route";
            return null;
        }

        // Combine: explicit segments + extension segments
        var combined = new List<TaxiRouteSegment>(explicitRoute.Segments);
        combined.AddRange(extension.Segments);

        var holdShorts = new List<HoldShortPoint>(explicitRoute.HoldShortPoints);
        HoldShortAnnotator.AddImplicitRunwayHoldShorts(groundLayout, extension.Segments, holdShorts);

        return new TaxiRoute { Segments = combined, HoldShortPoints = holdShorts };
    }

    internal static CommandResult TryPushback(AircraftState aircraft, PushbackCommand push, AirportGroundLayout? groundLayout)
    {
        if (aircraft.Phases?.CurrentPhase is not AtParkingPhase)
        {
            return new CommandResult(false, "Pushback requires aircraft to be at parking");
        }

        if (push.DestinationParking is not null || push.DestinationSpot is not null)
        {
            return TryPushbackToSpot(aircraft, push, groundLayout);
        }

        double? targetLat = null;
        double? targetLon = null;
        int? resolvedHeading = push.Heading;

        if (push.Taxiway is not null)
        {
            if (groundLayout is null)
            {
                return new CommandResult(false, "No airport ground layout available");
            }

            var targetNode = groundLayout.FindExitByTaxiway(aircraft.Latitude, aircraft.Longitude, push.Taxiway);
            if (targetNode is null)
            {
                return new CommandResult(false, $"Cannot find taxiway '{push.Taxiway}' near aircraft");
            }

            targetLat = targetNode.Latitude;
            targetLon = targetNode.Longitude;

            Log.LogDebug(
                "[Pushback] {Callsign}: target taxiway {Twy} at node {NodeId} ({Lat:F6}, {Lon:F6})",
                aircraft.Callsign,
                push.Taxiway,
                targetNode.Id,
                targetLat,
                targetLon
            );

            // Resolve facing direction: face along the PUSH taxiway edge at the target node,
            // picking the direction closest to the FACING taxiway.
            // e.g., PUSH TE T → push onto TE, face along TE toward T.
            if (push.FacingTaxiway is not null && resolvedHeading is null)
            {
                // Find bearing from target node toward the facing taxiway
                var facingNode = groundLayout.FindExitByTaxiway(targetNode.Latitude, targetNode.Longitude, push.FacingTaxiway);
                if (facingNode is not null)
                {
                    double bearingToFacing = GeoMath.BearingTo(targetNode.Latitude, targetNode.Longitude, facingNode.Latitude, facingNode.Longitude);

                    // Pick the push-taxiway edge direction closest to the facing taxiway
                    double? edgeHeading = groundLayout.GetEdgeHeadingForTaxiway(targetNode, push.Taxiway!, bearingToFacing);

                    if (edgeHeading is not null)
                    {
                        resolvedHeading = FlightPhysics.NormalizeHeadingInt(edgeHeading.Value);
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

        var startNode = groundLayout.FindNearestNode(aircraft.Latitude, aircraft.Longitude);
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
        int? resolvedHeading = push.Heading;
        if (resolvedHeading is null && push.FacingTaxiway is not null)
        {
            var facingNode = groundLayout.FindExitByTaxiway(destNode.Latitude, destNode.Longitude, push.FacingTaxiway);
            if (facingNode is not null)
            {
                double bearingToFacing = GeoMath.BearingTo(destNode.Latitude, destNode.Longitude, facingNode.Latitude, facingNode.Longitude);
                double? edgeHeading = groundLayout.GetEdgeHeadingForTaxiway(destNode, push.FacingTaxiway, bearingToFacing);
                resolvedHeading = edgeHeading is not null
                    ? FlightPhysics.NormalizeHeadingInt(edgeHeading.Value)
                    : FlightPhysics.NormalizeHeadingInt(bearingToFacing);
            }
        }

        resolvedHeading ??= destNode.Heading is not null ? FlightPhysics.NormalizeHeadingInt(destNode.Heading.Value) : null;

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

        aircraft.ParkingSpot = destLabel.ToUpperInvariant();

        var msg = $"Pushing back to {destLabel}";
        if (push.FacingTaxiway is not null)
        {
            msg += $" facing {push.FacingTaxiway}";
        }
        else if (push.Heading is not null)
        {
            msg += $", heading {push.Heading:000}";
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
        aircraft.DepartureRunway = runway.Designator;
        return CommandDispatcher.Ok($"Runway {runway.Designator}");
    }

    internal static CommandResult TryHoldPosition(AircraftState aircraft)
    {
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "Hold position requires aircraft on the ground");
        }

        aircraft.IsHeld = true;
        aircraft.GiveWayTarget = null;
        return CommandDispatcher.Ok("Hold position");
    }

    internal static CommandResult TryResumeTaxi(AircraftState aircraft)
    {
        if (!aircraft.IsHeld)
        {
            return new CommandResult(false, "Aircraft is not held");
        }

        aircraft.IsHeld = false;
        aircraft.GiveWayTarget = null;
        return CommandDispatcher.Ok("Resume taxi");
    }

    internal static CommandResult TryCrossRunway(AircraftState aircraft, CrossRunwayCommand cross)
    {
        // If currently holding short, satisfy the clearance immediately
        if (aircraft.Phases?.CurrentPhase is HoldingShortPhase holdPhase)
        {
            holdPhase.SatisfyClearance(ClearanceType.RunwayCrossing);
            return CommandDispatcher.Ok($"Cross {cross.RunwayId}");
        }

        // Otherwise, pre-clear the matching hold-short in the taxi route
        var route = aircraft.AssignedTaxiRoute;
        if (route is null)
        {
            return new CommandResult(false, "No taxi route assigned");
        }

        bool cleared = false;
        foreach (var hs in route.HoldShortPoints)
        {
            if (hs.TargetName is not null && RunwayIdentifier.Parse(hs.TargetName).Contains(cross.RunwayId) && !hs.IsCleared)
            {
                hs.IsCleared = true;
                cleared = true;
            }
        }

        if (!cleared)
        {
            return new CommandResult(false, $"No hold-short for {cross.RunwayId} in taxi route");
        }

        return CommandDispatcher.Ok($"Cross {cross.RunwayId}");
    }

    internal static CommandResult TryHoldShort(AircraftState aircraft, HoldShortCommand hs, AirportGroundLayout? groundLayout)
    {
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "Hold short requires aircraft on the ground");
        }

        var route = aircraft.AssignedTaxiRoute;
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

        Log.LogDebug("[HS] {Callsign}: added hold short of {Target}", aircraft.Callsign, hs.Target);
        return CommandDispatcher.Ok($"Hold short of {hs.Target}");
    }

    internal static CommandResult TryFollow(AircraftState aircraft, FollowCommand follow, AirportGroundLayout? groundLayout)
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

        var acceptance = currentPhase.CanAcceptCommand(CanonicalCommandType.Follow);
        if (acceptance == CommandAcceptance.Rejected)
        {
            return new CommandResult(false, $"Cannot follow during {currentPhase.Name}");
        }

        // Replace phases with FollowingPhase
        var phases = aircraft.Phases!;
        phases.Clear(CommandDispatcher.BuildMinimalContext(aircraft, groundLayout));
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

        if (aircraft.AssignedTaxiRoute is null)
        {
            return new CommandResult(false, "Aircraft must have a taxi route assigned");
        }

        aircraft.IsHeld = true;
        aircraft.GiveWayTarget = targetCallsign;
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
            return new CommandResult(false, "ATXI requires a destination (helipad, parking, or spot name)");
        }

        // Resolve destination to coordinates
        if (groundLayout is null)
        {
            return new CommandResult(false, "No airport ground layout available");
        }

        var spot = groundLayout.FindSpotByName(destination);
        if (spot is null)
        {
            return new CommandResult(false, $"Cannot find spot '{destination}' in airport layout");
        }

        double destLat = spot.Latitude;
        double destLon = spot.Longitude;
        string resolvedName = destination.ToUpperInvariant();

        // Clear current phases and start air taxi
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        if (aircraft.Phases is not null)
        {
            aircraft.Phases.Clear(ctx);
        }

        aircraft.IsHeld = false;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AirTaxiPhase(destLat, destLon, resolvedName));
        ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases.Start(ctx);

        return CommandDispatcher.Ok($"Air taxi to {resolvedName}");
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
            var node = groundLayout.FindExitByTaxiway(aircraft.Latitude, aircraft.Longitude, land.SpotName);
            if (node is null)
            {
                return new CommandResult(false, $"Cannot find taxiway '{land.SpotName}' near aircraft");
            }

            destLat = node.Latitude;
            destLon = node.Longitude;
            resolvedName = land.SpotName.ToUpperInvariant();
        }
        else
        {
            var spot = groundLayout.FindSpotByName(land.SpotName);
            if (spot is null)
            {
                return new CommandResult(false, $"Cannot find spot '{land.SpotName}' in airport layout");
            }

            destLat = spot.Latitude;
            destLon = spot.Longitude;
            resolvedName = land.SpotName.ToUpperInvariant();
        }

        if (land.NoDelete)
        {
            aircraft.AutoDeleteExempt = true;
        }

        // Clear current phases and set up air taxi → land sequence
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        if (aircraft.Phases is not null)
        {
            aircraft.Phases.Clear(ctx);
        }

        aircraft.IsHeld = false;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AirTaxiPhase(destLat, destLon, resolvedName));
        aircraft.Phases.Add(new HelicopterLandingPhase());
        aircraft.Phases.Add(new AtParkingPhase());
        ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout);
        aircraft.Phases.Start(ctx);

        aircraft.ParkingSpot = resolvedName;

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
                if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
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

        var runway = ResolveClosestRunwayEnd(rwyId.Value, node.Latitude, node.Longitude, aircraft);
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
        var airportId = aircraft.Departure;
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

        aircraft.ConflictBreakRemainingSeconds = BreakDurationSeconds;
        aircraft.GroundSpeedLimit = null;
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

        aircraft.Phases.RequestedExit = preference;
        if (noDelete)
        {
            aircraft.AutoDeleteExempt = true;
        }

        if (preference.Taxiway is not null)
        {
            return CommandDispatcher.Ok($"Exit at {preference.Taxiway}");
        }

        return CommandDispatcher.Ok(preference.Side == ExitSide.Left ? "Exit left" : "Exit right");
    }
}
