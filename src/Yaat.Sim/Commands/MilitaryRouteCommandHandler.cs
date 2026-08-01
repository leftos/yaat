using Yaat.Sim.Data;
using Yaat.Sim.Data.MilitaryRoutes;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Commands;

/// <summary>
/// Handlers for the AP/1B military training route clearances (FAA JO 7110.65 §9-2-6).
///
/// Separate from <see cref="NavigationCommandHandler"/>, which is already large and mixes JAWY,
/// JARR, JFAC, RFIS, RTIS and REPORT; military routes are a self-contained domain with their own
/// database dependency.
/// </summary>
public static class MilitaryRouteCommandHandler
{
    /// <summary>
    /// §9-2-6.a "CLEARED INTO IR (designator)". Installs <see cref="MilitaryRoutePhase"/> over the
    /// span from the cleared entry point to the cleared exit point.
    /// </summary>
    internal static CommandResult DispatchClearedInto(ClearedIntoMilitaryRouteCommand cmd, AircraftState aircraft)
    {
        var navDb = NavigationDatabase.Instance;
        var route = navDb.GetMilitaryRoute(cmd.Designator);
        if (route is null)
        {
            return new CommandResult(false, $"Unknown military route: {cmd.Designator}");
        }

        // A route is one-way and course reversals are prohibited (AP/1B chapter 1 §V.B.1), so the
        // aircraft can only join at or ahead of its present position along the published order.
        int joinIndex = FindJoinIndex(aircraft, route);
        if (joinIndex < 0)
        {
            return new CommandResult(false, $"Unable, {route.Printed} is one-way and the aircraft is past its exit point");
        }

        var pointNames = route.Points.Skip(joinIndex).Select(p => p.Name).ToList();
        var exitPointId = route.ExitPoints.Count > 0 ? route.ExitPoints[0] : route.Points[^1].Id;

        // Populated here rather than left to the phase's OnStart: a phase does not start until the
        // next tick, and the clearance has to be readable the moment the command is accepted — for
        // the readback, the strip, and any command issued in the same compound.
        var state = aircraft.MilitaryRoute;
        state.Clear();
        state.Designator = route.Designator;
        state.Kind = route.Type;
        state.EntryPointId = route.Points[joinIndex].Id;
        state.ExitPointId = exitPointId;
        state.Marsa = IsMarsa(route);
        state.AltitudeSource =
            cmd.AltitudeFt is null ? MilitaryRouteAltitudeSource.RouteAltitudes
            : cmd.AtOrBelow ? MilitaryRouteAltitudeSource.AtOrBelow
            : MilitaryRouteAltitudeSource.AssignedAltitude;
        state.AssignedOverrideFt = cmd.AltitudeFt;
        state.Status = MilitaryRouteStatus.ClearedIn;

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(
            new MilitaryRoutePhase
            {
                Designator = route.Designator,
                Kind = route.Type,
                EntryPointId = state.EntryPointId,
                ExitPointId = exitPointId,
                Marsa = state.Marsa,
                PointNames = pointNames,
            }
        );

        // The route takes over steering, exactly as an approach clearance does.
        aircraft.Targets.AssignedMagneticHeading = null;

        if (cmd.AltitudeFt is { } altitude)
        {
            if (cmd.AtOrBelow)
            {
                aircraft.Targets.AltitudeCeiling = altitude;
                aircraft.Targets.TargetAltitude = aircraft.Altitude > altitude ? altitude : null;
                return CommandDispatcher.Ok($"Cleared into {route.Printed}, at or below {altitude:N0}");
            }

            aircraft.Targets.AssignedAltitude = altitude;
            aircraft.Targets.TargetAltitude = altitude;
            aircraft.Targets.AltitudeFloor = null;
            aircraft.Targets.AltitudeCeiling = null;
            return CommandDispatcher.Ok($"Cleared into {route.Printed}, maintain {altitude:N0}");
        }

        return CommandDispatcher.Ok($"Cleared into {route.Printed}, maintain {route.Designator} altitudes");
    }

    /// <summary>§9-2-6.a "MAINTAIN IR (designator) ALTITUDE(S)".</summary>
    internal static CommandResult DispatchMaintainRouteAltitudes(AircraftState aircraft)
    {
        var state = aircraft.MilitaryRoute;
        if (!state.IsActive || state.Designator is null)
        {
            return new CommandResult(false, "Not established on a military training route");
        }

        state.AltitudeSource = MilitaryRouteAltitudeSource.RouteAltitudes;
        state.AssignedOverrideFt = null;
        aircraft.Targets.AssignedAltitude = null;
        aircraft.Targets.TargetAltitude = null;
        return CommandDispatcher.Ok($"Maintain {state.Designator} altitudes");
    }

    /// <summary>
    /// §9-2-6.b "CLEARED TO (destination) FROM IR (designator/exit fix) VIA (route)". Ends the route
    /// clearance and, when a route of flight is given, loads it.
    /// </summary>
    internal static CommandResult DispatchClearedOutOf(ClearedOutOfMilitaryRouteCommand cmd, AircraftState aircraft)
    {
        var state = aircraft.MilitaryRoute;
        if (state.Designator is null)
        {
            return new CommandResult(false, "Not on a military training route");
        }

        var designator = state.Designator;
        var exitPoint = state.ExitPointId;
        state.Status = MilitaryRouteStatus.Exited;
        aircraft.Phases = null;
        aircraft.Targets.AltitudeFloor = null;
        aircraft.Targets.AltitudeCeiling = null;

        if (cmd.Route is not null)
        {
            LoadRouteOfFlight(aircraft, cmd.Route);
        }

        var via = cmd.Route is null ? "" : $" via {cmd.Route}";
        var from = exitPoint is null ? designator : $"{designator} {exitPoint}";
        return CommandDispatcher.Ok($"Cleared to {cmd.Destination} from {from}{via}");
    }

    private static void LoadRouteOfFlight(AircraftState aircraft, string route)
    {
        var navDb = NavigationDatabase.Instance;
        aircraft.Targets.NavigationRoute.Clear();
        foreach (var name in navDb.ExpandRouteForNavigation(route, aircraft.FlightPlan.Departure))
        {
            var position = navDb.ResolveFixOrFrd(name);
            if (position is not null)
            {
                aircraft.Targets.NavigationRoute.Add(
                    new NavigationTarget { Name = name, Position = new LatLon(position.Value.Lat, position.Value.Lon) }
                );
            }
        }
    }

    /// <summary>
    /// The first published point at or ahead of the aircraft, or -1 when it is already past the end.
    /// Ahead is judged by the leg's own direction, so a route running back past the aircraft laterally
    /// does not read as joinable.
    /// </summary>
    private static int FindJoinIndex(AircraftState aircraft, MilitaryRoute route)
    {
        int nearest = -1;
        double best = double.MaxValue;
        for (int i = 0; i < route.Points.Count; i++)
        {
            var point = route.Points[i].Position;
            double distance = GeoMath.DistanceNm(aircraft.Position.Lat, aircraft.Position.Lon, point.Lat, point.Lon);
            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }

        if (nearest < 0)
        {
            return -1;
        }

        // Joining at the nearest point would fly backwards down the leg the aircraft has already
        // passed, so join the next one when the nearest is behind. The final point has nothing
        // ahead of it — there is no forward span left to clear the aircraft into.
        if (IsBehind(aircraft, route.Points[nearest].Position))
        {
            return nearest + 1 < route.Points.Count ? nearest + 1 : -1;
        }

        return nearest;
    }

    private static bool IsBehind(AircraftState aircraft, LatLon point)
    {
        double bearing = GeoMath.BearingTo(aircraft.Position, point);
        double delta = Math.Abs(((bearing - aircraft.TrueHeading.Degrees + 540) % 360) - 180);
        return delta > 90;
    }

    /// <summary>
    /// MARSA comes from the route's published procedures, never from a typed command: §9-2-6.c
    /// establishes it by letter of agreement between the scheduling unit and the ATC facility.
    /// Aerial refueling tracks are MARSA by normal practice.
    /// </summary>
    private static bool IsMarsa(MilitaryRoute route) =>
        route.Type == MilitaryRouteType.Ar || route.OriginatingActivity.Contains("MARSA", StringComparison.OrdinalIgnoreCase);
}
