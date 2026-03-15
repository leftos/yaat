using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Proto;

namespace Yaat.Sim.Data;

/// <summary>
/// Unified navigation data: NavData fixes/runways/airways/SID/STAR indexes
/// plus lazy-loaded CIFP procedures (SIDs, STARs, approaches).
/// Replaces FixDatabase + ProcedureDatabase + ApproachDatabase.
/// </summary>
public sealed class NavigationDatabase
{
    private readonly Dictionary<string, (double Lat, double Lon)> _navDb = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _elevations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _sidBodies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _sidAllFixes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Name, List<string> Fixes)>> _sidTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _starBodies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _starAllFixes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Name, List<string> Fixes)>> _starTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _airways = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<RunwayInfo>> _runways = new(StringComparer.OrdinalIgnoreCase);

    // CIFP lazy caches
    private readonly ConcurrentDictionary<string, IReadOnlyList<CifpSidProcedure>> _sidCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<CifpStarProcedure>> _starCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<CifpApproachProcedure>> _approachCache = new(StringComparer.OrdinalIgnoreCase);

    private string? _cifpFilePath;

    private static readonly ILogger Log = SimLog.CreateLogger("NavigationDatabase");

    public NavigationDatabase(NavDataSet? navData, string? customFixesBaseDir = null)
    {
        BuildIndex(navData);
        BuildProcedureIndex(navData);
        LoadCustomFixes(customFixesBaseDir);
        AllFixNames = BuildSortedNames();
    }

    /// <summary>
    /// Creates a NavigationDatabase pre-populated with test data. Intended only for unit tests.
    /// </summary>
    public static NavigationDatabase ForTesting(
        IReadOnlyDictionary<string, (double Lat, double Lon)>? fixes = null,
        IReadOnlyList<RunwayInfo>? runways = null,
        IReadOnlyDictionary<string, IReadOnlyList<CifpApproachProcedure>>? approachesByAirport = null,
        IReadOnlyDictionary<string, double>? elevations = null,
        IReadOnlyList<CifpSidProcedure>? sids = null,
        IReadOnlyList<CifpStarProcedure>? stars = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? starBodies = null,
        IReadOnlyDictionary<string, IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>>? starTransitions = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? airways = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? sidBodies = null
    )
    {
        var db = new NavigationDatabase(null, customFixesBaseDir: "");

        if (fixes is not null)
        {
            foreach (var (name, pos) in fixes)
            {
                db._navDb[name] = pos;
            }
        }

        if (runways is not null)
        {
            foreach (var rwy in runways)
            {
                void Index(string key)
                {
                    if (!db._runways.TryGetValue(key, out var list))
                    {
                        list = [];
                        db._runways[key] = list;
                    }

                    list.Add(rwy);
                }

                Index(rwy.AirportId);
            }
        }

        if (approachesByAirport is not null)
        {
            foreach (var (airportCode, procedures) in approachesByAirport)
            {
                string normalized = NormalizeAirport(airportCode);
                db._approachCache[normalized] = procedures;
            }
        }

        if (elevations is not null)
        {
            foreach (var (code, elev) in elevations)
            {
                db._elevations[code] = elev;
            }
        }

        if (sids is not null)
        {
            foreach (var sid in sids)
            {
                string key = NormalizeAirport(sid.Airport);
                db._sidCache.AddOrUpdate(key, [sid], (_, existing) => [.. existing, sid]);
            }
        }

        if (stars is not null)
        {
            foreach (var star in stars)
            {
                string key = NormalizeAirport(star.Airport);
                db._starCache.AddOrUpdate(key, [star], (_, existing) => [.. existing, star]);
            }
        }

        if (starBodies is not null)
        {
            foreach (var (starId, body) in starBodies)
            {
                db._starBodies[starId] = [.. body];
            }
        }

        if (starTransitions is not null)
        {
            foreach (var (starId, transitions) in starTransitions)
            {
                db._starTransitions[starId] = transitions.Select(t => (t.Name, (List<string>)[.. t.Fixes])).ToList();
            }
        }

        if (airways is not null)
        {
            foreach (var (airwayId, airwayFixes) in airways)
            {
                db._airways[airwayId] = [.. airwayFixes];
            }
        }

        if (sidBodies is not null)
        {
            foreach (var (sidId, body) in sidBodies)
            {
                db._sidBodies[sidId] = [.. body];
            }
        }

        return db;
    }

    public int Count => _navDb.Count;

    /// <summary>
    /// Sorted array of all fix names, for prefix-search autocomplete.
    /// </summary>
    public string[] AllFixNames { get; }

    // ──────────────────────────────────────────────
    //  NavData lookups (eagerly built)
    // ──────────────────────────────────────────────

    public (double Lat, double Lon)? GetFixPosition(string name)
    {
        return _navDb.TryGetValue(name, out var pos) ? pos : null;
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

    /// <summary>
    /// Resolves a potentially outdated SID ID to the current version.
    /// Returns the input if it matches exactly, the current version if the base name matches
    /// (e.g., "CNDEL5" → "CNDEL6"), or null if no match exists.
    /// </summary>
    public string? ResolveSidId(string rawId)
    {
        if (_sidBodies.ContainsKey(rawId))
        {
            return rawId;
        }

        string baseName = StripTrailingDigits(rawId);
        if (baseName == rawId)
        {
            return null;
        }

        foreach (var key in _sidBodies.Keys)
        {
            if (StripTrailingDigits(key).Equals(baseName, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
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

    /// <summary>
    /// Resolves a potentially outdated STAR ID to the current version.
    /// Returns the input if it matches exactly, the current version if the base name matches
    /// (e.g., "BDEGA3" → "BDEGA4"), or null if no match exists.
    /// </summary>
    public string? ResolveStarId(string rawId)
    {
        if (_starBodies.ContainsKey(rawId))
        {
            return rawId;
        }

        string baseName = StripTrailingDigits(rawId);
        if (baseName == rawId)
        {
            return null;
        }

        foreach (var key in _starBodies.Keys)
        {
            if (StripTrailingDigits(key).Equals(baseName, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
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

    // ──────────────────────────────────────────────
    //  CIFP lookups (lazy-loaded per airport)
    // ──────────────────────────────────────────────

    /// <summary>Sets the CIFP file path after initialization (e.g., after async download). Clears all CIFP caches
    /// and supplements the fix database with VOR/DME/NDB navaids from CIFP.</summary>
    public void SetCifpPath(string path)
    {
        _cifpFilePath = path;
        _sidCache.Clear();
        _starCache.Clear();
        _approachCache.Clear();

        // Supplement the fix database with CIFP navaids (VOR/DME/NDB).
        // NavData may not include all navaids; CIFP has a comprehensive list.
        if (File.Exists(path))
        {
            var navaids = CifpParser.ParseNavaids(path);
            foreach (var (ident, pos) in navaids)
            {
                _navDb.TryAdd(ident, pos);
            }
        }
    }

    public CifpSidProcedure? GetSid(string airportCode, string sidId)
    {
        var sids = GetSids(airportCode);
        var exact = sids.FirstOrDefault(s => s.ProcedureId.Equals(sidId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        string baseName = StripTrailingDigits(sidId);
        if (baseName == sidId)
        {
            return null;
        }

        return sids.FirstOrDefault(s => StripTrailingDigits(s.ProcedureId).Equals(baseName, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<CifpSidProcedure> GetSids(string airportCode)
    {
        string normalized = NormalizeAirport(airportCode);
        return _sidCache.GetOrAdd(normalized, LoadSids);
    }

    public CifpStarProcedure? GetStar(string airportCode, string starId)
    {
        var stars = GetStars(airportCode);
        var exact = stars.FirstOrDefault(s => s.ProcedureId.Equals(starId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        string baseName = StripTrailingDigits(starId);
        if (baseName == starId)
        {
            return null;
        }

        return stars.FirstOrDefault(s => StripTrailingDigits(s.ProcedureId).Equals(baseName, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<CifpStarProcedure> GetStars(string airportCode)
    {
        string normalized = NormalizeAirport(airportCode);
        return _starCache.GetOrAdd(normalized, LoadStars);
    }

    public CifpApproachProcedure? GetApproach(string airportCode, string approachId)
    {
        var approaches = GetApproaches(airportCode);
        return approaches.FirstOrDefault(a => a.ApproachId.Equals(approachId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<CifpApproachProcedure> GetApproaches(string airportCode)
    {
        string normalized = NormalizeAirport(airportCode);
        return _approachCache.GetOrAdd(normalized, LoadApproaches);
    }

    public string? ResolveApproachId(string airportCode, string shorthand)
    {
        if (string.IsNullOrWhiteSpace(shorthand))
        {
            return null;
        }

        var approaches = GetApproaches(airportCode);
        if (approaches.Count == 0)
        {
            return null;
        }

        string upper = shorthand.ToUpperInvariant();

        // Exact match first
        var exact = approaches.FirstOrDefault(a => a.ApproachId.Equals(upper, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.ApproachId;
        }

        var parsed = ParseShorthand(upper);
        if (parsed is null)
        {
            return null;
        }

        var (typeCode, runway, variant) = parsed.Value;

        if (typeCode is not null)
        {
            var match = approaches.FirstOrDefault(a =>
                a.TypeCode == typeCode
                && a.Runway is not null
                && a.Runway.Equals(runway, StringComparison.OrdinalIgnoreCase)
                && (variant is null || a.ApproachId.EndsWith(variant, StringComparison.OrdinalIgnoreCase))
            );

            if (match is not null)
            {
                return match.ApproachId;
            }

            char? altCode = typeCode switch
            {
                'H' => 'R',
                'R' => 'H',
                _ => null,
            };

            if (altCode is not null)
            {
                match = approaches.FirstOrDefault(a =>
                    a.TypeCode == altCode
                    && a.Runway is not null
                    && a.Runway.Equals(runway, StringComparison.OrdinalIgnoreCase)
                    && (variant is null || a.ApproachId.EndsWith(variant, StringComparison.OrdinalIgnoreCase))
                );

                if (match is not null)
                {
                    return match.ApproachId;
                }
            }
        }
        else if (runway is not null)
        {
            var candidates = approaches.Where(a => a.Runway is not null && a.Runway.Equals(runway, StringComparison.OrdinalIgnoreCase)).ToList();

            if (candidates.Count > 0)
            {
                var best = candidates.OrderBy(a => GetTypePriority(a.TypeCode)).First();
                return best.ApproachId;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the CIFP STAR runway transition fix names for a given airport/STAR/runway.
    /// These are the fixes in the runway-specific transition segment (e.g., EMZOH4 RW30 transition).
    /// </summary>
    public IReadOnlyList<string>? GetStarRunwayTransitions(string airportCode, string starId, string runwayId)
    {
        var star = GetStar(airportCode, starId);
        if (star is null)
        {
            return null;
        }

        // Normalize runway ID: "30" → "RW30", "30L" → "RW30L"
        string rwyKey = runwayId.StartsWith("RW", StringComparison.OrdinalIgnoreCase) ? runwayId : $"RW{runwayId}";

        foreach (var (key, transition) in star.RunwayTransitions)
        {
            if (key.Equals(rwyKey, StringComparison.OrdinalIgnoreCase))
            {
                var fixes = new List<string>();
                foreach (var leg in transition.Legs)
                {
                    if (!string.IsNullOrEmpty(leg.FixIdentifier))
                    {
                        fixes.Add(leg.FixIdentifier);
                    }
                }

                return fixes;
            }
        }

        return null;
    }

    // ──────────────────────────────────────────────
    //  NavData index building (private)
    // ──────────────────────────────────────────────

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
                _navDb.TryAdd(airport.FaaId, pos);
                _elevations.TryAdd(airport.FaaId, airport.Elevation);
            }

            if (!string.IsNullOrEmpty(airport.IcaoId))
            {
                _navDb.TryAdd(airport.IcaoId, pos);
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

            _navDb.TryAdd(fix.Id, (loc.Lat, loc.Lon));
        }

        int runwayCount = 0;
        foreach (var airport in navData.Airports)
        {
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
            "Navigation database built: {Count} entries " + "({Airports} airports + {Fixes} fixes + {Runways} runways + {Airways} airways)",
            _navDb.Count,
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
                if (_navDb.TryAdd(alias, pos.Value))
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
        var names = _navDb.Keys.ToArray();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }

    // ──────────────────────────────────────────────
    //  CIFP loaders (private)
    // ──────────────────────────────────────────────

    private IReadOnlyList<CifpSidProcedure> LoadSids(string normalizedAirport)
    {
        if (_cifpFilePath is null || !File.Exists(_cifpFilePath))
        {
            return [];
        }

        string icao = normalizedAirport.Length <= 3 ? $"K{normalizedAirport}" : normalizedAirport;
        return CifpParser.ParseSids(_cifpFilePath, icao);
    }

    private IReadOnlyList<CifpStarProcedure> LoadStars(string normalizedAirport)
    {
        if (_cifpFilePath is null || !File.Exists(_cifpFilePath))
        {
            return [];
        }

        string icao = normalizedAirport.Length <= 3 ? $"K{normalizedAirport}" : normalizedAirport;
        return CifpParser.ParseStars(_cifpFilePath, icao);
    }

    private IReadOnlyList<CifpApproachProcedure> LoadApproaches(string normalizedAirport)
    {
        if (_cifpFilePath is null || !File.Exists(_cifpFilePath))
        {
            return [];
        }

        string icao = normalizedAirport.Length <= 3 ? $"K{normalizedAirport}" : normalizedAirport;
        return CifpParser.ParseApproaches(_cifpFilePath, icao);
    }

    private static string NormalizeAirport(string code)
    {
        string upper = code.ToUpperInvariant().Trim();
        return upper.StartsWith('K') && upper.Length == 4 ? upper[1..] : upper;
    }

    private static (char? TypeCode, string? Runway, string? Variant)? ParseShorthand(string s)
    {
        var (typeCode, rest) = TryStripTypePrefix(s);

        if (typeCode is not null && rest.Length > 0)
        {
            int i = 0;
            while (i < rest.Length && char.IsDigit(rest[i]))
            {
                i++;
            }

            if (i == 0)
            {
                return null;
            }

            if (i < rest.Length && rest[i] is 'L' or 'R' or 'C')
            {
                i++;
            }

            string runway = rest[..i];
            string? variant = i < rest.Length ? rest[i..] : null;

            return (typeCode, runway, variant);
        }

        if (s.Length >= 1 && char.IsDigit(s[0]))
        {
            int i = 0;
            while (i < s.Length && char.IsDigit(s[i]))
            {
                i++;
            }

            if (i < s.Length && s[i] is 'L' or 'R' or 'C')
            {
                i++;
            }

            if (i > 0 && i == s.Length)
            {
                return (null, s, null);
            }
        }

        return null;
    }

    private static (char? Code, string Remainder) TryStripTypePrefix(string s)
    {
        ReadOnlySpan<(string Prefix, char Code)> prefixes =
        [
            ("ILS", 'I'),
            ("LOC", 'L'),
            ("RNAV", 'H'),
            ("GPS", 'P'),
            ("VOR", 'V'),
            ("NDB", 'N'),
            ("LDA", 'X'),
            ("TACAN", 'T'),
            ("SDF", 'U'),
        ];

        foreach (var (prefix, code) in prefixes)
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (code, s[prefix.Length..]);
            }
        }

        if (s.Length >= 2 && char.IsLetter(s[0]) && char.IsDigit(s[1]))
        {
            char code = char.ToUpperInvariant(s[0]);
            return (code, s[1..]);
        }

        return (null, s);
    }

    /// <summary>
    /// Strips trailing digits from a procedure ID to get the base name.
    /// E.g., "BDEGA4" → "BDEGA", "CNDEL5" → "CNDEL". Preserves at least 2 characters.
    /// Returns the input unchanged if no trailing digits exist.
    /// </summary>
    public static string StripTrailingDigits(string s)
    {
        int end = s.Length;
        while (end > 2 && char.IsDigit(s[end - 1]))
        {
            end--;
        }

        return end == s.Length ? s : s[..end];
    }

    private static int GetTypePriority(char typeCode)
    {
        return typeCode switch
        {
            'I' => 0, // ILS
            'L' => 1, // LOC
            'H' => 2, // RNAV(GPS)
            'R' => 3, // RNAV
            'P' => 4, // GPS
            _ => 10,
        };
    }
}
