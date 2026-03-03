using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Commands;

internal static class GroundCommandHandler
{
    internal static CommandResult TryTaxi(
        AircraftState aircraft,
        TaxiCommand taxi,
        AirportGroundLayout? groundLayout,
        IRunwayLookup? runways,
        ILogger logger
    )
    {
        if (groundLayout is null)
        {
            logger.LogWarning(
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
            logger.LogWarning(
                "[TryTaxi] {Callsign}: no nearest node at ({Lat:F6}, {Lon:F6})",
                aircraft.Callsign,
                aircraft.Latitude,
                aircraft.Longitude
            );
            return new CommandResult(false, "Cannot find position on taxiway graph");
        }

        logger.LogDebug(
            "[TryTaxi] {Callsign}: nearest node {NodeId} at ({NLat:F6}, {NLon:F6}), dist={Dist:F4}nm, path=[{Path}], destRwy={Rwy}",
            aircraft.Callsign,
            startNode.Id,
            startNode.Latitude,
            startNode.Longitude,
            GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, startNode.Latitude, startNode.Longitude),
            string.Join(" ", taxi.Path),
            taxi.DestinationRunway ?? "(none)"
        );

        // Resolve the taxi route using explicit path
        var route = TaxiPathfinder.ResolveExplicitPath(
            groundLayout,
            startNode.Id,
            taxi.Path,
            out string? failReason,
            taxi.HoldShorts,
            taxi.DestinationRunway,
            runways,
            groundLayout.AirportId
        );

        if (route is null)
        {
            logger.LogWarning("[TryTaxi] {Callsign}: route resolution failed — {Reason}", aircraft.Callsign, failReason ?? "no matching taxiways");

            if (failReason is not null)
            {
                return new CommandResult(false, failReason);
            }

            var pathStr = string.Join(" ", taxi.Path);
            return new CommandResult(false, $"Cannot resolve taxi route: {pathStr}");
        }

        logger.LogInformation(
            "[TryTaxi] {Callsign}: route resolved — {SegCount} segments, {HsCount} hold-shorts, summary: {Summary}",
            aircraft.Callsign,
            route.Segments.Count,
            route.HoldShortPoints.Count,
            route.ToSummary()
        );

        // Clear current phases
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger, groundLayout);
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
        aircraft.Phases.Add(new TaxiingPhase());
        ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger, groundLayout);
        aircraft.Phases.Start(ctx);

        return CommandDispatcher.Ok($"Taxi via {route.ToSummary()}");
    }

    internal static CommandResult TryPushback(AircraftState aircraft, PushbackCommand push, AirportGroundLayout? groundLayout, ILogger logger)
    {
        if (aircraft.Phases?.CurrentPhase is not AtParkingPhase)
        {
            return new CommandResult(false, "Pushback requires aircraft to be at parking");
        }

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger, groundLayout);
        aircraft.Phases.Clear(ctx);

        var phase = new PushbackPhase { TargetHeading = push.Heading };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Start(ctx);

        var msg = "Pushing back";
        if (push.Heading is not null)
        {
            msg += $", face heading {push.Heading:000}";
        }

        return CommandDispatcher.Ok(msg);
    }

    internal static CommandResult TryHoldPosition(AircraftState aircraft)
    {
        if (!aircraft.IsOnGround)
        {
            return new CommandResult(false, "Hold position requires aircraft on the ground");
        }

        aircraft.IsHeld = true;
        return CommandDispatcher.Ok("Hold position");
    }

    internal static CommandResult TryResumeTaxi(AircraftState aircraft)
    {
        if (!aircraft.IsHeld)
        {
            return new CommandResult(false, "Aircraft is not held");
        }

        aircraft.IsHeld = false;
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

    internal static CommandResult TryHoldShort(AircraftState aircraft, HoldShortCommand hs, AirportGroundLayout? groundLayout, ILogger logger)
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

        logger.LogDebug("[HS] {Callsign}: added hold short of {Target}", aircraft.Callsign, hs.Target);
        return CommandDispatcher.Ok($"Hold short of {hs.Target}");
    }

    internal static CommandResult TryFollow(AircraftState aircraft, FollowCommand follow, AirportGroundLayout? groundLayout, ILogger logger)
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
        phases.Clear(CommandDispatcher.BuildMinimalContext(aircraft, logger, groundLayout));
        phases.Phases.Add(new FollowingPhase(follow.TargetCallsign));
        phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, logger, groundLayout));

        return CommandDispatcher.Ok($"Follow {follow.TargetCallsign}");
    }

    internal static CommandResult TryAirTaxi(
        AircraftState aircraft,
        string? destination,
        AirportGroundLayout? groundLayout,
        ILogger logger
    )
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
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger, groundLayout);
        if (aircraft.Phases is not null)
        {
            aircraft.Phases.Clear(ctx);
        }

        aircraft.IsHeld = false;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AirTaxiPhase(destLat, destLon, resolvedName));
        ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger, groundLayout);
        aircraft.Phases.Start(ctx);

        return CommandDispatcher.Ok($"Air taxi to {resolvedName}");
    }

    internal static CommandResult TryLand(
        AircraftState aircraft,
        LandCommand land,
        AirportGroundLayout? groundLayout,
        ILogger logger
    )
    {
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        if (cat != AircraftCategory.Helicopter)
        {
            return new CommandResult(false, "LAND is only available for helicopters (use CTL for fixed-wing)");
        }

        if (groundLayout is null)
        {
            return new CommandResult(false, "No airport ground layout available");
        }

        var spot = groundLayout.FindSpotByName(land.SpotName);
        if (spot is null)
        {
            return new CommandResult(false, $"Cannot find spot '{land.SpotName}' in airport layout");
        }

        double destLat = spot.Latitude;
        double destLon = spot.Longitude;
        string resolvedName = land.SpotName.ToUpperInvariant();

        if (land.NoDelete)
        {
            aircraft.AutoDeleteExempt = true;
        }

        // Clear current phases and set up air taxi → land sequence
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger, groundLayout);
        if (aircraft.Phases is not null)
        {
            aircraft.Phases.Clear(ctx);
        }

        aircraft.IsHeld = false;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AirTaxiPhase(destLat, destLon, resolvedName));
        aircraft.Phases.Add(new Phases.Tower.HelicopterLandingPhase());
        aircraft.Phases.Add(new AtParkingPhase());
        ctx = CommandDispatcher.BuildMinimalContext(aircraft, logger, groundLayout);
        aircraft.Phases.Start(ctx);

        aircraft.ParkingSpot = resolvedName;

        return CommandDispatcher.Ok($"Land at {resolvedName}");
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
