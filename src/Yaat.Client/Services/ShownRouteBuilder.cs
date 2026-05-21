using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Client.Services;

/// <summary>
/// Builds the polyline + optional vector-tail segments that the radar's "Show Route" overlay
/// draws for an aircraft. Pure helpers — no UI dependency beyond <see cref="DrawnWaypoint"/>
/// and <see cref="VectorTail"/>.
///
/// Two segments may exist per aircraft:
/// <list type="bullet">
///   <item><see cref="BuildPrimary"/> — the current <see cref="AircraftModel.NavigationRoute"/>,
///         with an optional vector tail off the last fix when the active SID/STAR's final leg
///         is a published radar vector (VM/VA) or the aircraft is being vectored with no
///         upcoming fix.</item>
///   <item><see cref="BuildExpectedApproach"/> — when <see cref="AircraftModel.ExpectedApproach"/>
///         is set (EA/CAPP-pending) but the approach has not yet been merged into
///         NavigationRoute, returns the approach geometry as a disjoint second polyline.</item>
/// </list>
///
/// Mirrors the leg-walking logic in <c>ApproachCommandHandler.BuildApproachFixes</c> but
/// returns coordinates only — none of the altitude/speed/fly-over metadata is needed for
/// rendering.
/// </summary>
internal static class ShownRouteBuilder
{
    /// <summary>Length of the vector tail arrow drawn off a procedure's terminating VM/VA leg.</summary>
    public const double TailLengthNm = 20.0;

    /// <summary>
    /// Length of the FAC extension drawn back from the FAF when the expected approach has
    /// no published transition (VECTORS-to-final case). Same length as a vector tail so the
    /// two visualizations read as a matched pair.
    /// </summary>
    public const double FacExtensionNm = 20.0;

    public static (List<DrawnWaypoint> Waypoints, VectorTail? Tail) BuildPrimary(AircraftModel ac, NavigationDatabase navDb)
    {
        var waypoints = ResolveNavigationRouteWaypoints(ac, navDb);

        // Tail from procedure VM/VA, if any
        var tail = TryGetProcedureVectorTail(ac, navDb, waypoints);

        // Pure-vector aircraft: no fixes in the route, but the controller has assigned a
        // heading — draw the heading from the aircraft position. (When the route still has
        // fixes we suppress this; the polyline already shows where the aircraft is going.)
        if (waypoints.Count == 0 && tail is null && ac.AssignedHeading is { } hdg)
        {
            tail = new VectorTail(ac.Position.Lat, ac.Position.Lon, hdg, TailLengthNm);
        }

        return (waypoints, tail);
    }

    public static (List<DrawnWaypoint> Waypoints, VectorTail? Tail)? BuildExpectedApproach(AircraftModel ac, NavigationDatabase navDb)
    {
        // Only show while the EA hint is meaningful — once cleared, the approach is in NavigationRoute already.
        if (!string.IsNullOrEmpty(ac.ActiveApproachId))
        {
            return null;
        }

        var rawHint = ac.ExpectedApproach;
        if (string.IsNullOrWhiteSpace(rawHint))
        {
            return null;
        }

        var airport = ac.Destination;
        if (string.IsNullOrWhiteSpace(airport))
        {
            return null;
        }

        // Parse "ILS 30" or "ILS 30.SHARK" → (approachShorthand, optional transition)
        var (approachShorthand, transitionName) = ParseApproachHint(rawHint);

        var candidates = navDb.ResolveApproachCandidates(airport, approachShorthand);
        if (candidates.Count == 0)
        {
            return null;
        }

        // Pick the first candidate. Disambiguation (route-connectivity, etc.) is the server's
        // job at CAPP time — we just want a best-effort visualization here.
        var procedure = navDb.GetApproach(airport, candidates[0]);
        if (procedure?.Runway is null)
        {
            return null;
        }

        var runway = navDb.GetRunway(airport, procedure.Runway);
        if (runway is null)
        {
            return null;
        }

        CifpTransition? transition = null;
        if (transitionName is not null)
        {
            procedure.Transitions.TryGetValue(transitionName, out transition);
        }

        var waypoints = transition is not null
            ? BuildApproachWaypoints(transition.Legs, procedure.CommonLegs, navDb)
            : BuildCommonLegWaypoints(procedure.CommonLegs, navDb);

        if (waypoints.Count == 0)
        {
            return null;
        }

        // No transition: prepend a synthetic anchor 20 nm back along the published final
        // approach course reciprocal so the controller sees the line they need to vector the
        // aircraft onto. Use the FAC extractor so offset approaches (LDA, parallel-offset LOC,
        // VOR offset, RNAV with offset legs) show the correct course rather than the runway
        // centerline.
        if (transition is null)
        {
            var fac = FinalApproachCourseExtractor.Extract(procedure, runway, navDb);
            var reciprocal = fac.Course.ToReciprocal();
            double anchorOriginLat = fac.AnchorLat ?? waypoints[0].Lat;
            double anchorOriginLon = fac.AnchorLon ?? waypoints[0].Lon;
            var (anchorLat, anchorLon) = GeoMath.ProjectPoint(anchorOriginLat, anchorOriginLon, reciprocal, FacExtensionNm);
            waypoints.Insert(0, new DrawnWaypoint("", anchorLat, anchorLon));
        }

        // Append runway threshold so the segment terminates at the touchdown point.
        var thresholdName = $"RW{runway.Designator}";
        var last = waypoints[^1];
        if (
            !last.ResolvedName.Equals(thresholdName, StringComparison.OrdinalIgnoreCase)
            && !(Math.Abs(last.Lat - runway.ThresholdLatitude) < 1e-6 && Math.Abs(last.Lon - runway.ThresholdLongitude) < 1e-6)
        )
        {
            waypoints.Add(new DrawnWaypoint(thresholdName, runway.ThresholdLatitude, runway.ThresholdLongitude));
        }

        return (waypoints, null);
    }

    private static List<DrawnWaypoint> ResolveNavigationRouteWaypoints(AircraftModel ac, NavigationDatabase navDb)
    {
        var result = new List<DrawnWaypoint>(ac.NavigationRoute.Count);
        foreach (var name in ac.NavigationRoute)
        {
            var pos = navDb.GetFixPosition(name);
            if (pos.HasValue)
            {
                result.Add(new DrawnWaypoint(name, pos.Value.Lat, pos.Value.Lon));
            }
        }
        return result;
    }

    private static VectorTail? TryGetProcedureVectorTail(AircraftModel ac, NavigationDatabase navDb, List<DrawnWaypoint> waypoints)
    {
        // STAR takes priority when arriving; SID for departures. ActiveSidId clears once the
        // aircraft sequences off the SID, ActiveStarId once the approach is loaded.
        if (!string.IsNullOrEmpty(ac.ActiveStarId) && !string.IsNullOrWhiteSpace(ac.Destination))
        {
            var procedure = navDb.GetStar(ac.Destination, ac.ActiveStarId);
            if (procedure is not null)
            {
                var legs = AssembleStarLegs(procedure, ac.DestinationRunway);
                if (TryExtractTrailingVector(legs, navDb) is { } tail)
                {
                    return tail;
                }
            }
        }

        if (!string.IsNullOrEmpty(ac.ActiveSidId) && !string.IsNullOrWhiteSpace(ac.Departure))
        {
            var procedure = navDb.GetSid(ac.Departure, ac.ActiveSidId);
            if (procedure is not null)
            {
                var legs = AssembleSidLegs(procedure, ac.DepartureRunway);
                if (TryExtractTrailingVector(legs, navDb) is { } tail)
                {
                    return tail;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<CifpLeg> AssembleStarLegs(CifpStarProcedure procedure, string? destinationRunway)
    {
        // STAR leg flow: enroute transition → common legs → runway transition.
        // The trailing vector (if any) lives at the very end of the runway transition when
        // present (per-runway VMs), otherwise at the end of the common legs.
        if (!string.IsNullOrWhiteSpace(destinationRunway))
        {
            var key = MatchRunwayTransitionKey(procedure.RunwayTransitions, destinationRunway);
            if (key is not null && procedure.RunwayTransitions.TryGetValue(key, out var rwyTransition) && rwyTransition.Legs.Count > 0)
            {
                var combined = new List<CifpLeg>(procedure.CommonLegs.Count + rwyTransition.Legs.Count);
                combined.AddRange(procedure.CommonLegs);
                combined.AddRange(rwyTransition.Legs);
                return combined;
            }
        }
        return procedure.CommonLegs;
    }

    private static IReadOnlyList<CifpLeg> AssembleSidLegs(CifpSidProcedure procedure, string? departureRunway)
    {
        // SID leg flow: runway transition → common legs → enroute transition. Trailing vectors
        // for SIDs are uncommon (most SIDs end at a fix or an explicit enroute transition fix),
        // but radar-vector SIDs put the VM/VA on the runway transition's tail or common legs.
        var legs = new List<CifpLeg>();
        if (!string.IsNullOrWhiteSpace(departureRunway))
        {
            var key = MatchRunwayTransitionKey(procedure.RunwayTransitions, departureRunway);
            if (key is not null && procedure.RunwayTransitions.TryGetValue(key, out var rwyTransition))
            {
                legs.AddRange(rwyTransition.Legs);
            }
        }
        legs.AddRange(procedure.CommonLegs);
        return legs;
    }

    private static string? MatchRunwayTransitionKey(IReadOnlyDictionary<string, CifpTransition> transitions, string runway)
    {
        // CIFP runway-transition keys are typically "RW28R", "RW10L", etc. The caller passes
        // the bare designator ("28R") — accept both forms.
        if (transitions.ContainsKey(runway))
        {
            return runway;
        }
        var prefixed = $"RW{runway}";
        if (transitions.ContainsKey(prefixed))
        {
            return prefixed;
        }
        foreach (var key in transitions.Keys)
        {
            if (key.EndsWith(runway, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }
        return null;
    }

    private static VectorTail? TryExtractTrailingVector(IReadOnlyList<CifpLeg> legs, NavigationDatabase navDb)
    {
        // Walk from the end backward looking for the most recent VM/VA leg with a usable
        // OutboundCourse. The anchor is the last preceding leg with a resolvable fix.
        for (int i = legs.Count - 1; i >= 0; i--)
        {
            var leg = legs[i];
            if (leg.PathTerminator is not (CifpPathTerminator.VM or CifpPathTerminator.VA))
            {
                continue;
            }
            if (leg.OutboundCourse is not { } magCourse)
            {
                continue;
            }

            // Find the anchor — the most recent leg before this one with a known fix position.
            for (int j = i - 1; j >= 0; j--)
            {
                var prev = legs[j];
                if (string.IsNullOrEmpty(prev.FixIdentifier))
                {
                    continue;
                }
                var pos = navDb.GetFixPosition(prev.FixIdentifier);
                if (pos.HasValue)
                {
                    return new VectorTail(pos.Value.Lat, pos.Value.Lon, magCourse, TailLengthNm);
                }
            }
            // No anchor fix found before the vector leg — can't render the tail meaningfully.
            return null;
        }
        return null;
    }

    private static List<DrawnWaypoint> BuildApproachWaypoints(
        IReadOnlyList<CifpLeg> transitionLegs,
        IReadOnlyList<CifpLeg> commonLegs,
        NavigationDatabase navDb
    )
    {
        var transitionFixes = BuildLegWaypoints(transitionLegs, stopAtMap: false, navDb);
        var commonFixes = BuildLegWaypoints(commonLegs, stopAtMap: true, navDb);

        // Mirror BuildApproachFixesWithTransition: trim common fixes already consumed by the
        // transition (e.g. transition ending at the IAF that also opens the common segment).
        if (transitionFixes.Count > 0 && commonFixes.Count > 0)
        {
            string transitionEnd = transitionFixes[^1].ResolvedName;
            int idx = commonFixes.FindIndex(f => f.ResolvedName.Equals(transitionEnd, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                commonFixes.RemoveRange(0, idx + 1);
            }
        }

        transitionFixes.AddRange(commonFixes);
        return transitionFixes;
    }

    private static List<DrawnWaypoint> BuildCommonLegWaypoints(IReadOnlyList<CifpLeg> commonLegs, NavigationDatabase navDb) =>
        BuildLegWaypoints(commonLegs, stopAtMap: true, navDb);

    private static List<DrawnWaypoint> BuildLegWaypoints(IReadOnlyList<CifpLeg> legs, bool stopAtMap, NavigationDatabase navDb)
    {
        var result = new List<DrawnWaypoint>(legs.Count);
        foreach (var leg in legs)
        {
            if (string.IsNullOrEmpty(leg.FixIdentifier))
            {
                continue;
            }
            if (stopAtMap && leg.FixRole == CifpFixRole.MAP)
            {
                break;
            }
            // Skip course-reversal and hold legs — they don't add useful geometry to the radar overlay.
            if (leg.PathTerminator is CifpPathTerminator.PI or CifpPathTerminator.HM or CifpPathTerminator.HF or CifpPathTerminator.HA)
            {
                continue;
            }
            var pos = navDb.GetFixPosition(leg.FixIdentifier);
            if (pos.HasValue)
            {
                result.Add(new DrawnWaypoint(leg.FixIdentifier, pos.Value.Lat, pos.Value.Lon));
            }
        }
        return result;
    }

    /// <summary>
    /// Parses an ExpectedApproach hint into (approach shorthand, optional transition name).
    /// Accepts "ILS 30", "I30", "I30.SHARK", "RNAV 28L.STAR.HEMAN", etc.
    /// </summary>
    internal static (string Approach, string? Transition) ParseApproachHint(string hint)
    {
        var trimmed = hint.Trim();
        int dotIdx = trimmed.IndexOf('.');
        if (dotIdx < 0)
        {
            return (trimmed, null);
        }
        return (trimmed[..dotIdx], trimmed[(dotIdx + 1)..]);
    }
}
