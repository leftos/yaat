using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim;

/// <summary>
/// Tracks aircraft proximity to tower list airports. Each STARS area defines
/// tower list configurations with an airportId and range. Aircraft within range
/// appear in the tower P-list, sorted by entry time.
/// </summary>
public sealed class TowerListTracker
{
    private readonly record struct TowerListAirport(string ListId, string AirportId, double Lat, double Lon, double RangeNm);

    private readonly record struct TowerListEntry(string Callsign, double EnteredAtSeconds);

    // All tower list airports resolved from ARTCC config, keyed by listId
    private readonly List<TowerListAirport> _airports = [];

    // Key: listId, Value: entries sorted by entry time (ascending)
    private readonly Dictionary<string, List<TowerListEntry>> _entries = [];

    /// <summary>
    /// Initialize from ARTCC config. Collects all tower list configurations
    /// from all STARS facilities, resolving airport positions via NavigationDatabase.
    /// Clears any previous state.
    /// </summary>
    public void Initialize(ArtccConfigRoot config)
    {
        _airports.Clear();
        _entries.Clear();

        CollectTowerListAirports(config.Facility);
    }

    /// <summary>
    /// Update tower list entries based on current aircraft positions.
    /// Adds aircraft newly within range, removes those that left or were deleted.
    /// Called once per tick. Returns true if any entries were added or removed.
    /// </summary>
    public bool Update(List<AircraftState> snapshot, double elapsedSeconds)
    {
        if (_airports.Count == 0)
        {
            return false;
        }

        var changed = false;

        var activeCallsigns = new HashSet<string>(snapshot.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var ac in snapshot)
        {
            activeCallsigns.Add(ac.Callsign);
        }

        // Remove entries for deleted aircraft
        foreach (var (_, entries) in _entries)
        {
            var removed = entries.RemoveAll(e => !activeCallsigns.Contains(e.Callsign));
            if (removed > 0)
            {
                changed = true;
            }
        }

        // Update proximity for each tower list airport
        foreach (var airport in _airports)
        {
            if (!_entries.TryGetValue(airport.ListId, out var entries))
            {
                entries = [];
                _entries[airport.ListId] = entries;
            }

            var inRangeCallsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ac in snapshot)
            {
                var dist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, airport.Lat, airport.Lon);
                if (dist <= airport.RangeNm)
                {
                    inRangeCallsigns.Add(ac.Callsign);
                }
            }

            // Add newly-in-range aircraft (preserving arrival order)
            foreach (var callsign in inRangeCallsigns)
            {
                var alreadyPresent = false;
                foreach (var e in entries)
                {
                    if (e.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyPresent = true;
                        break;
                    }
                }

                if (!alreadyPresent)
                {
                    entries.Add(new TowerListEntry(callsign, elapsedSeconds));
                    changed = true;
                }
            }

            // Remove aircraft that left the range
            var leftRange = entries.RemoveAll(e => !inRangeCallsigns.Contains(e.Callsign));
            if (leftRange > 0)
            {
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Returns the current tower list entries for a specific list ID,
    /// sorted by entry time (oldest first — DropZoneEntryTime ordering).
    /// </summary>
    public List<(string Callsign, double EnteredAtSeconds)> GetEntries(string listId)
    {
        if (!_entries.TryGetValue(listId, out var entries))
        {
            return [];
        }

        return entries.OrderBy(e => e.EnteredAtSeconds).Select(e => (e.Callsign, e.EnteredAtSeconds)).ToList();
    }

    /// <summary>Returns all configured tower list IDs (matching StarsListConfig.Id values).</summary>
    public List<string> GetListIds() => _airports.Select(a => a.ListId).Distinct().ToList();

    // --- Private ---

    private void CollectTowerListAirports(FacilityConfig facility)
    {
        if (facility.StarsConfiguration is { } stars)
        {
            // Collect this facility's P-lists in declaration order.
            // A P-list is a list with no coordinationChannel and sortField=DropZoneEntryTime.
            var pLists = new List<StarsListConfig>();
            foreach (var list in stars.Lists)
            {
                if (list.CoordinationChannel is null && list.SortField == "DropZoneEntryTime")
                {
                    pLists.Add(list);
                }
            }

            if (pLists.Count > 0)
            {
                // For each area, the N-th TowerListConfig maps to the N-th P-list of this
                // facility's STARS config (by position order in the config).
                var pListIndex = 0;
                foreach (var area in stars.Areas)
                {
                    foreach (var towerListConfig in area.TowerListConfigurations)
                    {
                        if (pListIndex >= pLists.Count)
                        {
                            break;
                        }

                        var listConfig = pLists[pListIndex];
                        pListIndex++;

                        var pos = NavigationDatabase.Instance.GetFixPosition(towerListConfig.AirportId);
                        if (pos is null)
                        {
                            continue;
                        }

                        _airports.Add(
                            new TowerListAirport(listConfig.Id, towerListConfig.AirportId, pos.Value.Lat, pos.Value.Lon, towerListConfig.Range)
                        );
                    }
                }
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            CollectTowerListAirports(child);
        }
    }
}
