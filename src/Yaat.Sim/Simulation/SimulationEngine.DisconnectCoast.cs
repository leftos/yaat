using System.Collections.Immutable;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation.Coast;
using Yaat.Sim.Simulation.Spine;

namespace Yaat.Sim.Simulation;

// The disconnect-coast lifecycle: a track taken out of the world keeps coasting on the displays that were showing it
// (ERAM, and each ASDE-X / SAAB SAID display it was a member of) until a sim-time deadline. Every Sim removal body
// registers the entry just before the aircraft leaves the world, the post-physics expiry step retires facets on sim
// time, and a spawn under a coasting callsign clears its entry. All of it is scenario state, so every run kind
// computes the same coasts.
public sealed partial class SimulationEngine
{
    /// <summary>Callsigns whose coast entry a spawn cleared since the last drain, in the order they were cleared.</summary>
    private readonly List<string> _pendingDisconnectCoastClears = [];

    /// <summary>
    /// Records what <paramref name="ac"/> — about to leave the world — coasts on: an ERAM facet when the en-route radar
    /// still sees it and its track is not QH-frozen, then one facet per ASDE-X airport and per SAID airport it is a
    /// member of, a surface facet at its destination marked a drop. The entry replaces any the callsign already has;
    /// a track with no facet leaves no entry. With no scenario this does nothing.
    /// </summary>
    public void RegisterDisconnectCoast(AircraftState ac)
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        ImmutableSortedDictionary<string, AircraftDisconnectCoast> coasts = scenario.DisconnectCoasts;
        ImmutableArray<DisconnectCoastFacet> facets = BuildDisconnectCoastFacets(ac, scenario.ElapsedSeconds);
        if (facets.IsEmpty)
        {
            if (coasts.ContainsKey(ac.Callsign))
            {
                scenario.DisconnectCoasts = coasts.Remove(ac.Callsign);
            }

            return;
        }

        var coast = new AircraftDisconnectCoast(ac.Position, ac.TrueTrack.Degrees, ac.GroundSpeed, scenario.ElapsedSeconds, facets);
        scenario.DisconnectCoasts = coasts.SetItem(ac.Callsign, coast);
    }

    private static ImmutableArray<DisconnectCoastFacet> BuildDisconnectCoastFacets(AircraftState ac, double nowSimSeconds)
    {
        ImmutableArray<DisconnectCoastFacet>.Builder facets = ImmutableArray.CreateBuilder<DisconnectCoastFacet>();
        if (DisconnectCoastRules.IsVisibleOnEram(ac, NavigationDatabase.Instance) && !ac.Eram.IsFrozen)
        {
            facets.Add(new DisconnectCoastFacet(DisconnectCoastScope.Eram, null, false, nowSimSeconds + SimScenarioState.EramDisconnectCoastSeconds));
        }

        string destination = NavigationDatabase.NormalizeAirport(ac.FlightPlan.Destination);
        double surfaceDeadline = nowSimSeconds + SimScenarioState.SurfaceDisconnectCoastSeconds;
        AddSurfaceFacets(facets, DisconnectCoastScope.Asdex, ac.Stars.VisibleAsdexAirports, destination, surfaceDeadline);
        AddSurfaceFacets(facets, DisconnectCoastScope.Said, ac.Stars.VisibleSaidAirports, destination, surfaceDeadline);
        return facets.ToImmutable();
    }

    private static void AddSurfaceFacets(
        ImmutableArray<DisconnectCoastFacet>.Builder facets,
        DisconnectCoastScope scope,
        ImmutableSortedSet<string> airports,
        string destination,
        double deadlineSimSeconds
    )
    {
        foreach (string facilityId in airports)
        {
            bool isDrop = DisconnectCoastRules.IsDestinationFacility(destination, facilityId);
            facets.Add(new DisconnectCoastFacet(scope, facilityId, isDrop, deadlineSimSeconds));
        }
    }

    /// <summary>
    /// The post-physics expiry pass: every facet whose deadline the scenario clock has reached leaves its entry, and an
    /// entry with no facet left goes. The host hears what expired once, ordered by callsign then facet order, and only
    /// on a tick that expired something; a tick that expires nothing leaves the set's reference alone. With no scenario
    /// this does nothing.
    /// </summary>
    public void TickDisconnectCoastExpiry(IHostConsumers host)
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        ImmutableSortedDictionary<string, AircraftDisconnectCoast> coasts = scenario.DisconnectCoasts;
        if (coasts.Count == 0)
        {
            return;
        }

        var expired = new List<ExpiredDisconnectCoastFacet>();
        ImmutableSortedDictionary<string, AircraftDisconnectCoast>.Builder? next = null;
        foreach (KeyValuePair<string, AircraftDisconnectCoast> entry in coasts)
        {
            if (TakeExpiredFacets(entry.Key, entry.Value, scenario.ElapsedSeconds, expired) is not { } remaining)
            {
                continue;
            }

            next ??= coasts.ToBuilder();
            if (remaining.IsEmpty)
            {
                next.Remove(entry.Key);
            }
            else
            {
                next[entry.Key] = entry.Value with { Facets = remaining };
            }
        }

        if (next is null)
        {
            return;
        }

        scenario.DisconnectCoasts = next.ToImmutable();
        host.OnDisconnectCoastExpired(expired);
    }

    /// <summary>
    /// Moves the facets of <paramref name="coast"/> whose deadline has passed onto <paramref name="expired"/> and returns
    /// the ones still standing, or null when none had passed.
    /// </summary>
    private static ImmutableArray<DisconnectCoastFacet>? TakeExpiredFacets(
        string callsign,
        AircraftDisconnectCoast coast,
        double nowSimSeconds,
        List<ExpiredDisconnectCoastFacet> expired
    )
    {
        if (!coast.Facets.Any(facet => facet.DeadlineSimSeconds <= nowSimSeconds))
        {
            return null;
        }

        ImmutableArray<DisconnectCoastFacet>.Builder remaining = ImmutableArray.CreateBuilder<DisconnectCoastFacet>(coast.Facets.Length);
        foreach (DisconnectCoastFacet facet in coast.Facets)
        {
            if (facet.DeadlineSimSeconds <= nowSimSeconds)
            {
                expired.Add(new ExpiredDisconnectCoastFacet(callsign, facet));
            }
            else
            {
                remaining.Add(facet);
            }
        }

        return remaining.ToImmutable();
    }

    /// <summary>
    /// A spawn under a coasting callsign re-associates it: the entry goes, silently, and the callsign waits for the next
    /// state-change drain to reach the host.
    /// </summary>
    private void ClearDisconnectCoast(SimScenarioState scenario, string callsign)
    {
        ImmutableSortedDictionary<string, AircraftDisconnectCoast> coasts = scenario.DisconnectCoasts;
        if (!coasts.ContainsKey(callsign))
        {
            return;
        }

        scenario.DisconnectCoasts = coasts.Remove(callsign);
        _pendingDisconnectCoastClears.Add(callsign);
    }

    private void DrainDisconnectCoastClearsInto(IStateChangeConsumer host)
    {
        if (_pendingDisconnectCoastClears.Count == 0)
        {
            return;
        }

        List<string> cleared = [.. _pendingDisconnectCoastClears];
        _pendingDisconnectCoastClears.Clear();
        host.OnDisconnectCoastsCleared(cleared);
    }
}
