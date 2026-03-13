using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Proto;

namespace Yaat.Sim.Data;

/// <summary>
/// Indexes all airports, fixes, and custom fixes from
/// VNAS NavData protobuf. Also indexes SID/STAR procedures
/// for route expansion (autocomplete prioritization).
/// </summary>
public sealed class FixDatabase : IFixLookup, IRunwayLookup
{
    private readonly Dictionary<string, (double Lat, double Lon)> _fixes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, double> _elevations = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<string>> _sidBodies = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<string>> _sidAllFixes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<(string Name, List<string> Fixes)>> _sidTransitions = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<string>> _starBodies = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<string>> _starAllFixes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<(string Name, List<string> Fixes)>> _starTransitions = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<string>> _airways = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<RunwayInfo>> _runways = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ILogger Log = SimLog.CreateLogger<FixDatabase>();

    public FixDatabase(NavDataSet? navData, string? customFixesBaseDir = null)
    {
        BuildIndex(navData);
        BuildProcedureIndex(navData);
        LoadCustomFixes(customFixesBaseDir);
        AllFixNames = BuildSortedNames();
    }

    public int Count => _fixes.Count;

    /// <summary>
    /// Sorted array of all fix names, for prefix-search autocomplete.
    /// </summary>
    public string[] AllFixNames { get; }

    public (double Lat, double Lon)? GetFixPosition(string name)
    {
        return _fixes.TryGetValue(name, out var pos) ? pos : null;
    }

    public double? GetAirportElevation(string code)
    {
        return _elevations.TryGetValue(code, out var elev) ? elev : null;
    }

    public RunwayInfo? GetRunway(string airportCode, string runwayId)
    {
        if (!_runways.TryGetValue(airportCode, out var list))
        {
            return null;
        }

        foreach (var rwy in list)
        {
            if (rwy.Id.Contains(runwayId))
            {
                return rwy.ForApproach(runwayId);
            }
        }

        return null;
    }

    public IReadOnlyList<RunwayInfo> GetRunways(string airportCode)
    {
        return _runways.TryGetValue(airportCode, out var list) ? list : [];
    }

    public IReadOnlyList<string>? GetSidBody(string sidId)
    {
        return _sidBodies.TryGetValue(sidId, out var body) ? body : null;
    }

    public IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetSidTransitions(string sidId)
    {
        if (!_sidTransitions.TryGetValue(sidId, out var transitions))
        {
            return null;
        }

        return transitions.Select(t => (t.Name, (IReadOnlyList<string>)t.Fixes)).ToList();
    }

    public IReadOnlyList<string>? GetStarBody(string starId)
    {
        return _starBodies.TryGetValue(starId, out var body) ? body : null;
    }

    public IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetStarTransitions(string starId)
    {
        if (!_starTransitions.TryGetValue(starId, out var transitions))
        {
            return null;
        }

        return transitions.Select(t => (t.Name, (IReadOnlyList<string>)t.Fixes)).ToList();
    }

    public IReadOnlyList<string>? GetAirwayFixes(string airwayId)
    {
        return _airways.TryGetValue(airwayId, out var fixes) ? fixes : null;
    }

    public bool IsAirway(string id)
    {
        return _airways.ContainsKey(id);
    }

    public IReadOnlyList<string> ExpandAirwaySegment(string airwayId, string fromFix, string toFix)
    {
        if (!_airways.TryGetValue(airwayId, out var fixes))
        {
            return [];
        }

        int fromIdx = -1;
        int toIdx = -1;
        for (int i = 0; i < fixes.Count; i++)
        {
            if (fromIdx < 0 && string.Equals(fixes[i], fromFix, StringComparison.OrdinalIgnoreCase))
            {
                fromIdx = i;
            }

            if (toIdx < 0 && string.Equals(fixes[i], toFix, StringComparison.OrdinalIgnoreCase))
            {
                toIdx = i;
            }
        }

        if (fromIdx < 0 || toIdx < 0)
        {
            return [];
        }

        var result = new List<string>();
        int step = fromIdx <= toIdx ? 1 : -1;
        for (int i = fromIdx; i != toIdx + step; i += step)
        {
            result.Add(fixes[i]);
        }

        return result;
    }

    /// <summary>
    /// Returns true if the token is a known SID or STAR identifier.
    /// </summary>
    public bool IsSidOrStar(string token)
    {
        return _sidAllFixes.ContainsKey(token) || _starAllFixes.ContainsKey(token);
    }

    /// <summary>
    /// Expands a route string into constituent fix names.
    /// SID/STAR identifiers are expanded to all body + transition
    /// fixes (ordered). Used for autocomplete highlighting.
    /// </summary>
    public IReadOnlyList<string> ExpandRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return [];
        }

        var result = new List<string>();
        var tokens = route.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (double.TryParse(token, out _))
            {
                continue;
            }

            if (_sidAllFixes.TryGetValue(token, out var sidList))
            {
                result.AddRange(sidList);
            }
            else if (_starAllFixes.TryGetValue(token, out var starList))
            {
                result.AddRange(starList);
            }
            else
            {
                result.Add(token);
            }
        }

        return result;
    }

    /// <summary>
    /// Expands a route string for navigation. Only emits ordered body
    /// fixes for published SIDs/STARs. Skips radar-vector procedures
    /// (body ≤ 1 fix). Strips leading fixes within 1nm of the departure
    /// airport and deduplicates adjacent identical names.
    /// </summary>
    public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return [];
        }

        var raw = new List<string>();
        var tokens = route.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (double.TryParse(token, out _))
            {
                continue;
            }

            if (_sidBodies.TryGetValue(token, out var sidBody))
            {
                if (sidBody.Count > 1)
                {
                    raw.AddRange(sidBody);
                }
            }
            else if (_starBodies.TryGetValue(token, out var starBody))
            {
                if (starBody.Count > 1)
                {
                    raw.AddRange(starBody);
                }
            }
            else
            {
                raw.Add(token);
            }
        }

        // Deduplicate adjacent identical names
        var deduped = new List<string>(raw.Count);
        foreach (var name in raw)
        {
            if (deduped.Count == 0 || !string.Equals(deduped[^1], name, StringComparison.OrdinalIgnoreCase))
            {
                deduped.Add(name);
            }
        }

        // Strip leading fixes within 1nm of departure airport
        if (departureAirport is not null)
        {
            var airportPos = GetFixPosition(departureAirport);
            if (airportPos is not null)
            {
                while (deduped.Count > 0)
                {
                    var fixPos = GetFixPosition(deduped[0]);
                    if (fixPos is null)
                    {
                        break;
                    }

                    double dist = GeoMath.DistanceNm(airportPos.Value.Lat, airportPos.Value.Lon, fixPos.Value.Lat, fixPos.Value.Lon);
                    if (dist > 1.0)
                    {
                        break;
                    }

                    deduped.RemoveAt(0);
                }
            }
        }

        return deduped;
    }

    private void BuildIndex(NavDataSet? navData)
    {
        if (navData is null)
        {
            Log.LogWarning("No NavData available — fix lookup will be empty");
            return;
        }

        foreach (var airport in navData.Airports)
        {
            var loc = airport.Location;
            if (loc is null)
            {
                continue;
            }

            var pos = (loc.Lat, loc.Lon);

            if (!string.IsNullOrEmpty(airport.FaaId))
            {
                _fixes.TryAdd(airport.FaaId, pos);
                _elevations.TryAdd(airport.FaaId, airport.Elevation);
            }

            if (!string.IsNullOrEmpty(airport.IcaoId))
            {
                _fixes.TryAdd(airport.IcaoId, pos);
                _elevations.TryAdd(airport.IcaoId, airport.Elevation);
            }
        }

        foreach (var fix in navData.Fixes)
        {
            var loc = fix.Location;
            if (loc is null)
            {
                continue;
            }

            _fixes.TryAdd(fix.Id, (loc.Lat, loc.Lon));
        }

        int runwayCount = 0;
        foreach (var airport in navData.Airports)
        {
            // Build paired RunwayInfo: match opposite ends by location proximity
            var paired = new HashSet<int>();
            var runwayInfos = new List<RunwayInfo>();

            for (int i = 0; i < airport.Runways.Count; i++)
            {
                if (paired.Contains(i))
                {
                    continue;
                }

                var rwy1 = airport.Runways[i];
                if (rwy1.StartLocation is null || rwy1.EndLocation is null)
                {
                    continue;
                }

                // Look for the opposite end: rwy2.StartLocation ≈ rwy1.EndLocation
                int matchIdx = -1;
                for (int j = i + 1; j < airport.Runways.Count; j++)
                {
                    if (paired.Contains(j))
                    {
                        continue;
                    }

                    var rwy2 = airport.Runways[j];
                    if (rwy2.StartLocation is null || rwy2.EndLocation is null)
                    {
                        continue;
                    }

                    double dist = GeoMath.DistanceNm(rwy1.EndLocation.Lat, rwy1.EndLocation.Lon, rwy2.StartLocation.Lat, rwy2.StartLocation.Lon);
                    if (dist < 0.05)
                    {
                        matchIdx = j;
                        break;
                    }
                }

                RunwayInfo info;
                if (matchIdx >= 0)
                {
                    var rwy2 = airport.Runways[matchIdx];
                    paired.Add(matchIdx);
                    info = new RunwayInfo
                    {
                        AirportId = airport.FaaId ?? airport.IcaoId ?? "",
                        Id = new RunwayIdentifier(rwy1.Id, rwy2.Id),
                        Designator = rwy1.Id,
                        Lat1 = rwy1.StartLocation.Lat,
                        Lon1 = rwy1.StartLocation.Lon,
                        Elevation1Ft = airport.Elevation,
                        Heading1 = rwy1.TrueHeading,
                        Lat2 = rwy2.StartLocation!.Lat,
                        Lon2 = rwy2.StartLocation.Lon,
                        Elevation2Ft = airport.Elevation,
                        Heading2 = rwy2.TrueHeading,
                        LengthFt = rwy1.LandingDistanceAvailable,
                        WidthFt = rwy1.Width,
                    };
                }
                else
                {
                    // Unpaired (single designator); use EndLocation for the other end
                    info = new RunwayInfo
                    {
                        AirportId = airport.FaaId ?? airport.IcaoId ?? "",
                        Id = new RunwayIdentifier(rwy1.Id, rwy1.Id),
                        Designator = rwy1.Id,
                        Lat1 = rwy1.StartLocation.Lat,
                        Lon1 = rwy1.StartLocation.Lon,
                        Elevation1Ft = airport.Elevation,
                        Heading1 = rwy1.TrueHeading,
                        Lat2 = rwy1.EndLocation.Lat,
                        Lon2 = rwy1.EndLocation.Lon,
                        Elevation2Ft = airport.Elevation,
                        Heading2 = (rwy1.TrueHeading + 180.0) % 360.0,
                        LengthFt = rwy1.LandingDistanceAvailable,
                        WidthFt = rwy1.Width,
                    };
                }

                runwayInfos.Add(info);
                paired.Add(i);
            }

            foreach (var info in runwayInfos)
            {
                void IndexRunway(string key)
                {
                    if (!_runways.TryGetValue(key, out var list))
                    {
                        list = [];
                        _runways[key] = list;
                    }
                    list.Add(info);
                }

                if (!string.IsNullOrEmpty(airport.FaaId))
                {
                    IndexRunway(airport.FaaId);
                }

                if (!string.IsNullOrEmpty(airport.IcaoId))
                {
                    IndexRunway(airport.IcaoId);
                }

                runwayCount++;
            }
        }

        foreach (var airway in navData.Airways)
        {
            if (!string.IsNullOrEmpty(airway.Id) && airway.Fixes.Count > 0)
            {
                _airways.TryAdd(airway.Id, new List<string>(airway.Fixes));
            }
        }

        Log.LogInformation(
            "Fix database built: {Count} entries " + "({Airports} airports + {Fixes} fixes + {Runways} runways + {Airways} airways)",
            _fixes.Count,
            navData.Airports.Count,
            navData.Fixes.Count,
            runwayCount,
            _airways.Count
        );
    }

    private void BuildProcedureIndex(NavDataSet? navData)
    {
        if (navData is null)
        {
            return;
        }

        foreach (var sid in navData.Sids)
        {
            var body = new List<string>(sid.Body);
            _sidBodies.TryAdd(sid.Id, body);

            var transitions = new List<(string Name, List<string> Fixes)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allFixes = new List<string>();
            foreach (var fix in sid.Body)
            {
                if (seen.Add(fix))
                {
                    allFixes.Add(fix);
                }
            }

            foreach (var trans in sid.Transitions)
            {
                if (trans.Fixes.Count > 0)
                {
                    transitions.Add((trans.Fixes[^1], new List<string>(trans.Fixes)));
                }

                foreach (var fix in trans.Fixes)
                {
                    if (seen.Add(fix))
                    {
                        allFixes.Add(fix);
                    }
                }
            }

            _sidTransitions.TryAdd(sid.Id, transitions);
            _sidAllFixes.TryAdd(sid.Id, allFixes);
        }

        foreach (var star in navData.Stars)
        {
            var body = new List<string>(star.Body);
            _starBodies.TryAdd(star.Id, body);

            var transitions = new List<(string Name, List<string> Fixes)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allFixes = new List<string>();
            foreach (var fix in star.Body)
            {
                if (seen.Add(fix))
                {
                    allFixes.Add(fix);
                }
            }

            foreach (var trans in star.Transitions)
            {
                if (trans.Fixes.Count > 0)
                {
                    transitions.Add((trans.Fixes[0], new List<string>(trans.Fixes)));
                }

                foreach (var fix in trans.Fixes)
                {
                    if (seen.Add(fix))
                    {
                        allFixes.Add(fix);
                    }
                }
            }

            _starTransitions.TryAdd(star.Id, transitions);
            _starAllFixes.TryAdd(star.Id, allFixes);
        }

        Log.LogInformation("Procedure index: {Sids} SIDs, {Stars} STARs", _sidBodies.Count, _starBodies.Count);
    }

    private void LoadCustomFixes(string? baseDir)
    {
        baseDir ??= Path.Combine(AppContext.BaseDirectory, "data", "custom_fixes");

        var loadResult = CustomFixLoader.LoadAll(baseDir);

        foreach (var warning in loadResult.Warnings)
        {
            Log.LogWarning("Custom fix: {Warning}", warning);
        }

        int added = 0;
        foreach (var def in loadResult.Fixes)
        {
            (double Lat, double Lon)? pos = null;

            if (def.Lat.HasValue && def.Lon.HasValue)
            {
                pos = (def.Lat.Value, def.Lon.Value);
            }
            else if (def.Frd is not null)
            {
                var resolved = FrdResolver.Resolve(def.Frd, this);
                if (resolved is null)
                {
                    Log.LogWarning("Custom fix {Alias}: failed to resolve FRD '{Frd}'", def.Aliases[0], def.Frd);
                    continue;
                }

                pos = (resolved.Latitude, resolved.Longitude);
            }

            if (pos is null)
            {
                continue;
            }

            foreach (var alias in def.Aliases)
            {
                if (_fixes.TryAdd(alias, pos.Value))
                {
                    added++;
                }
                else
                {
                    Log.LogWarning("Custom fix alias '{Alias}' conflicts with " + "existing entry", alias);
                }
            }
        }

        Log.LogInformation("Custom fixes: {Added} aliases added from {Total} definitions", added, loadResult.Fixes.Count);
    }

    private string[] BuildSortedNames()
    {
        var names = _fixes.Keys.ToArray();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }
}
