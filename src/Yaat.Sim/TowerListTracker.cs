using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim;

/// <summary>
/// A tower list's identity: the STARS list id (<see cref="StarsListConfig.Id"/>) under the facility that declares it.
/// A list id is unique only within its facility — ZOA's FAT and NCT both declare a <c>P1</c> — so the facility is
/// part of the key. Keyed by the list id alone, two facilities' proximity passes evict each other's entries every
/// tick, which re-stamps the P-list's <c>DropZoneEntryTime</c> order and re-broadcasts the coordination topic every
/// second.
/// <para>
/// Both halves come verbatim from the ARTCC configuration and are compared as such — the record's equality is
/// ordinal and case-sensitive, unlike the callsign comparisons in <see cref="TowerListTracker"/>.
/// </para>
/// </summary>
public readonly record struct TowerListKey(string FacilityId, string ListId);

/// <summary>
/// Tracks aircraft proximity to tower list airports. Each STARS area defines
/// tower list configurations with an airportId and range. Aircraft within range
/// appear in the tower P-list, sorted by entry time.
/// <para>
/// Engine-owned (<see cref="Simulation.SimulationEngine.TowerListTracker"/>) and split like the vTDLS session: the
/// airports come from the ARTCC at scenario load and are NOT snapshotted, while the dwell entries are — so
/// <see cref="ClearSession"/> is what a restore clears and the configured lists survive it. A run that restores
/// mid-stream therefore keeps each aircraft's original entry second and the P-list's
/// <c>DropZoneEntryTime</c> order with it.
/// </para>
/// </summary>
public sealed class TowerListTracker
{
    private static readonly ILogger Log = SimLog.CreateLogger("TowerListTracker");

    private readonly record struct TowerListAirport(TowerListKey Key, string AirportId, double Lat, double Lon, double RangeNm);

    private readonly record struct TowerListEntry(string Callsign, double EnteredAtSeconds);

    // All tower list airports resolved from ARTCC config, keyed by (facility, listId)
    private readonly List<TowerListAirport> _airports = [];

    // Key: (facility, listId), Value: entries sorted by entry time (ascending)
    private readonly Dictionary<TowerListKey, List<TowerListEntry>> _entries = [];

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
            if (!_entries.TryGetValue(airport.Key, out var entries))
            {
                entries = [];
                _entries[airport.Key] = entries;
            }

            var inRangeCallsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ac in snapshot)
            {
                var dist = GeoMath.DistanceNm(ac.Position, new LatLon(airport.Lat, airport.Lon));
                if (dist <= airport.RangeNm)
                {
                    inRangeCallsigns.Add(ac.Callsign);
                }
            }

            // Add newly-in-range aircraft. Ordered ordinally rather than in set order: two aircraft that come into
            // range in the same second carry the same dwell second, so the order they are appended in is the order
            // they render and snapshot in — and a hash set's is per-process, which a byte-identical replay cannot use.
            foreach (var callsign in inRangeCallsigns.OrderBy(c => c, StringComparer.Ordinal))
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
    /// Returns the current tower list entries for one facility's list,
    /// sorted by entry time (oldest first — DropZoneEntryTime ordering).
    /// </summary>
    public List<(string Callsign, double EnteredAtSeconds)> GetEntries(TowerListKey key)
    {
        if (!_entries.TryGetValue(key, out var entries))
        {
            return [];
        }

        // Callsign breaks a dwell-second tie, so a list two aircraft entered in the same second reads back in one
        // fixed order on every run.
        return entries
            .OrderBy(e => e.EnteredAtSeconds)
            .ThenBy(e => e.Callsign, StringComparer.Ordinal)
            .Select(e => (e.Callsign, e.EnteredAtSeconds))
            .ToList();
    }

    /// <summary>
    /// Returns every configured tower list as its (facility, list id) key. Two facilities that declare the same list
    /// id are two keys, and the CRC list id both are stamped with is <see cref="TowerListKey.ListId"/>. One key per
    /// entry: <see cref="CollectTowerListAirports"/> rejects a duplicate at collect time, so there is nothing to
    /// de-duplicate here.
    /// </summary>
    public List<TowerListKey> GetLists() => [.. _airports.Select(a => a.Key)];

    /// <summary>
    /// Drops every dwell entry and leaves the configured airports alone. This is what a snapshot restore replaces:
    /// the airports were re-derived from the ARTCC by the load that ran just before it.
    /// </summary>
    public void ClearSession() => _entries.Clear();

    /// <summary>
    /// Puts one list's dwell entries back as a snapshot recorded them, replacing whatever that list held, and returns
    /// whether it took. The restore path's writer (<see cref="Simulation.Snapshots.TowerListSnapshotMapper"/>); the
    /// entry second is the captured run's, never the restore's.
    /// <para>
    /// A key this tracker does not hold an airport for is refused rather than written: the restoring room resolved a
    /// different ARTCC, or could not resolve the list airport's position, so nothing would ever read those entries —
    /// <see cref="Update"/>, <see cref="GetLists"/> and the capture all work off the configured lists, and the
    /// entries would sit in the dictionary until the next <see cref="ClearSession"/>.
    /// </para>
    /// </summary>
    public bool RestoreEntries(TowerListKey key, IEnumerable<(string Callsign, double EnteredAtSeconds)> entries)
    {
        if (!_airports.Any(a => a.Key == key))
        {
            return false;
        }

        _entries[key] = [.. entries.Select(e => new TowerListEntry(e.Callsign, e.EnteredAtSeconds))];
        return true;
    }

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

                        var key = new TowerListKey(facility.Id, listConfig.Id);
                        if (_airports.Any(a => a.Key == key))
                        {
                            // One facility declaring a list id twice: the second would share the first's dwell
                            // entries and evict them every tick, which is the bug this key exists to prevent.
                            Log.LogWarning(
                                "Facility {FacilityId} declares tower list '{ListId}' more than once; the airport {AirportId} is ignored",
                                facility.Id,
                                listConfig.Id,
                                towerListConfig.AirportId
                            );
                            continue;
                        }

                        _airports.Add(new TowerListAirport(key, towerListConfig.AirportId, pos.Value.Lat, pos.Value.Lon, towerListConfig.Range));
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
