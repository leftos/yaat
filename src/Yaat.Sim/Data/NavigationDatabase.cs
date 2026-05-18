using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Proto;

namespace Yaat.Sim.Data;

/// <summary>
/// Unified navigation data: NavData fixes/runways/airways/SID/STAR indexes
/// plus CIFP procedures (SIDs, STARs, approaches) parsed per-airport on demand.
/// Both NavData and CIFP are required at construction time.
/// </summary>
public sealed class NavigationDatabase
{
    private readonly Dictionary<string, (double Lat, double Lon)> _navDb = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _elevations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _airportNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _navaidNames = new(StringComparer.OrdinalIgnoreCase);

    // Spatial bucket of airports (1° × 1° grid; ~60nm bucket size). Used by
    // FindNearestAirportElevation for terrain-aware AGL lookups when the aircraft
    // is en route and no precise runway is assigned. Each airport is indexed once
    // under its canonical id; FAA/ICAO duplicates are de-duplicated.
    private readonly Dictionary<(int LatBucket, int LonBucket), List<(string Id, double Lat, double Lon, double Elevation)>> _airportSpatialIndex =
    [];
    private readonly Dictionary<string, List<string>> _sidBodies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _sidAllFixes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Name, List<string> Fixes)>> _sidTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _starBodies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _starAllFixes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Name, List<string> Fixes)>> _starTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _airways = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<RunwayInfo>> _runways = new(StringComparer.OrdinalIgnoreCase);

    // Maps every recognized airport identifier (FAA "OAK" or ICAO "KOAK") to the
    // canonical ICAO form (or FAA fallback when no ICAO is published). Built from
    // navData.Airports during BuildIndex; consumed by TryResolveAirport so commands
    // can validate user-typed airport codes and normalize storage.
    private readonly Dictionary<string, string> _airportCanonical = new(StringComparer.OrdinalIgnoreCase);

    // CIFP per-airport caches (parsed on first access from the CIFP file)
    private readonly ConcurrentDictionary<string, IReadOnlyList<CifpSidProcedure>> _sidCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<CifpStarProcedure>> _starCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<CifpApproachProcedure>> _approachCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cifpFilePath;

    private static readonly ILogger Log = SimLog.CreateLogger("NavigationDatabase");

    private static NavigationDatabase? _defaultInstance;
    private static readonly AsyncLocal<NavigationDatabase?> _scopedInstance = new();

    /// <summary>
    /// Global singleton instance. Returns the thread-local scoped override if set,
    /// otherwise the process-wide default. Throws if neither is initialized.
    /// </summary>
    public static NavigationDatabase Instance =>
        _scopedInstance.Value
        ?? _defaultInstance
        ?? throw new InvalidOperationException("NavigationDatabase not initialized. Call Initialize() first.");

    /// <summary>
    /// Initializes the global singleton with NavData + CIFP. Both are required.
    /// <paramref name="artccsBaseDir"/> overrides the per-ARTCC user-data root (custom fixes,
    /// fix pronunciations, taxi route presets); pass an empty string to skip loading entirely.
    /// </summary>
    public static void Initialize(NavDataSet navData, string cifpFilePath, string? artccsBaseDir = null)
    {
        _defaultInstance = new NavigationDatabase(navData, cifpFilePath, artccsBaseDir);
    }

    /// <summary>
    /// Sets the process-wide default instance (for production and test initialization).
    /// </summary>
    public static void SetInstance(NavigationDatabase db)
    {
        _defaultInstance = db;
    }

    /// <summary>
    /// Sets a thread-local override visible only to the current async execution context.
    /// Other threads and tests are unaffected. Returns an <see cref="IDisposable"/> that
    /// clears the override on dispose, falling back to the process-wide default.
    /// </summary>
    public static IDisposable ScopedOverride(NavigationDatabase db)
    {
        _scopedInstance.Value = db;
        return new OverrideScope();
    }

    private sealed class OverrideScope : IDisposable
    {
        public void Dispose() => _scopedInstance.Value = null;
    }

    /// <summary>
    /// Constructs a NavigationDatabase from NavData + CIFP plus an optional override path
    /// for the per-ARTCC user-data root. <paramref name="artccsBaseDir"/> defaults to
    /// <c>{AppContext.BaseDirectory}/Data/ARTCCs</c>; pass an empty string to skip loading
    /// per-ARTCC data entirely.
    /// </summary>
    public NavigationDatabase(NavDataSet navData, string cifpFilePath, string? artccsBaseDir = null)
    {
        _cifpFilePath = cifpFilePath;
        artccsBaseDir ??= Path.Combine(AppContext.BaseDirectory, "Data", "ARTCCs");

        BuildIndex(navData);
        BuildProcedureIndex(navData);
        LoadCifpNavaids(cifpFilePath);
        LoadCustomFixes(artccsBaseDir);
        LoadFixPronunciations(artccsBaseDir);
        InitialContactTransfers = LoadInitialContactTransfers(artccsBaseDir);
        WakeDirectives = LoadWakeDirectives(artccsBaseDir);
        TaxiRoutes = LoadTaxiRoutes(artccsBaseDir);
        AllFixNames = BuildSortedNames();
    }

    /// <summary>
    /// Private constructor for <see cref="ForTesting"/>. Skips NavData/CIFP loading.
    /// </summary>
    private NavigationDatabase()
    {
        _cifpFilePath = "";
        InitialContactTransfers = InitialContactTransferCatalog.Empty;
        WakeDirectives = WakeDirectiveCatalog.Empty;
        TaxiRoutes = TaxiRouteCatalog.Empty;
        AllFixNames = [];
    }

    /// <summary>
    /// Per-ARTCC custom taxi route presets, indexed by airport ICAO. Loaded from
    /// <c>Data/TaxiRoutes/{ARTCC}/*.json</c> at construction; consumed by the client's
    /// aircraft right-click menu to surface SOP-aligned taxi routes per airport.
    /// </summary>
    public TaxiRouteCatalog TaxiRoutes { get; }

    /// <summary>
    /// ARTCC-specific pilot initial-contact transfer exceptions, indexed by ARTCC, airport,
    /// and source/destination position type. Loaded from <c>Data/ARTCCs/{ARTCC}/InitialContactTransfers/*.json</c>.
    /// </summary>
    public InitialContactTransferCatalog InitialContactTransfers { get; }

    /// <summary>
    /// ARTCC-specific wake scoring directives and static waivers, loaded from
    /// <c>Data/ARTCCs/{ARTCC}/WakeDirectives/*.json</c>.
    /// </summary>
    public WakeDirectiveCatalog WakeDirectives { get; }

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
        IReadOnlyDictionary<string, IReadOnlyList<string>>? sidBodies = null,
        IReadOnlyDictionary<string, IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>>? sidTransitions = null,
        IReadOnlyDictionary<string, string>? airports = null,
        IReadOnlyDictionary<string, (double Lat, double Lon)>? airportPositions = null
    )
    {
        var db = new NavigationDatabase();

        // Auto-derive recognized airports from runways, approachesByAirport, and elevations
        // so tests that pre-seed those don't also have to hand-roll an airports map. Explicit
        // `airports` entries override.
        if (runways is not null)
        {
            foreach (var rwy in runways)
            {
                if (!string.IsNullOrEmpty(rwy.AirportId))
                {
                    db._airportCanonical.TryAdd(rwy.AirportId, rwy.AirportId);
                }
            }
        }

        if (approachesByAirport is not null)
        {
            foreach (var key in approachesByAirport.Keys)
            {
                if (!string.IsNullOrEmpty(key))
                {
                    db._airportCanonical.TryAdd(key, key);
                }
            }
        }

        if (elevations is not null)
        {
            foreach (var key in elevations.Keys)
            {
                if (!string.IsNullOrEmpty(key))
                {
                    db._airportCanonical.TryAdd(key, key);
                }
            }
        }

        if (airports is not null)
        {
            foreach (var (input, canonical) in airports)
            {
                if (!string.IsNullOrEmpty(input) && !string.IsNullOrEmpty(canonical))
                {
                    db._airportCanonical[input] = canonical;
                }
            }
        }

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

        if (airportPositions is not null)
        {
            foreach (var (code, pos) in airportPositions)
            {
                if (string.IsNullOrEmpty(code))
                {
                    continue;
                }
                db._navDb[code] = pos;
                var elev = elevations is not null && elevations.TryGetValue(code, out var e) ? e : 0;
                db.AddAirportToSpatialIndex(code, pos.Lat, pos.Lon, elev);
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

        if (sidTransitions is not null)
        {
            foreach (var (sidId, transitions) in sidTransitions)
            {
                db._sidTransitions[sidId] = transitions.Select(t => (t.Name, (List<string>)[.. t.Fixes])).ToList();
            }
        }

        db.AllFixNames = db.BuildSortedNames();
        return db;
    }

    private IReadOnlyList<(string Name, double Lat, double Lon)>? _fixTuples;

    /// <summary>
    /// Returns all fixes as (Name, Lat, Lon) tuples for FRD resolution. Lazily cached.
    /// </summary>
    public IReadOnlyList<(string Name, double Lat, double Lon)> GetFixTuples()
    {
        return _fixTuples ??= _navDb.Select(kv => (kv.Key, kv.Value.Lat, kv.Value.Lon)).ToArray();
    }

    public int Count => _navDb.Count;

    /// <summary>
    /// Sorted array of all fix names, for prefix-search autocomplete.
    /// </summary>
    public string[] AllFixNames { get; private set; }

    /// <summary>
    /// Speech-recognition patterns for custom fixes that declared a <c>spokenPatterns</c> field
    /// in their JSON. Used by the speech pipeline's <see cref="Speech.PhraseologyMapper"/> to
    /// collapse multi-token natural-language references (e.g. "the runway 30 numbers") into a
    /// single canonical-alias token before rule matching.
    /// </summary>
    public IReadOnlyList<Speech.CustomFixSpeechPattern> CustomFixSpeechPatterns => _customFixSpeechPatterns;

    private List<Speech.CustomFixSpeechPattern> _customFixSpeechPatterns = [];

    /// <summary>
    /// Phonetic pronunciation hints for fixes whose spelling doesn't match their spoken form.
    /// Keyed by canonical uppercase fix name; each entry lists one or more phonetic spellings.
    /// Used by <see cref="BuildWhisperPronunciationHint"/> to seed Whisper's <c>initial_prompt</c>
    /// when a programmed fix has a hint.
    /// </summary>
    private readonly Dictionary<string, List<string>> _fixPronunciations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Friendly natural-language names for custom fixes, keyed by every alias the fix declared.
    /// Sourced from the <c>name</c> field of <c>CustomFixes/*.json</c> definitions. Used by
    /// pilot speech to render aliases like <c>OAK30NUM</c> as "Oakland Runway 30 Numbers"
    /// instead of letter-by-letter.
    /// </summary>
    private readonly Dictionary<string, string> _customFixNames = new(StringComparer.OrdinalIgnoreCase);

    // ──────────────────────────────────────────────
    //  NavData lookups (eagerly built)
    // ──────────────────────────────────────────────

    public (double Lat, double Lon)? GetFixPosition(string name)
    {
        return _navDb.TryGetValue(name, out var pos) ? pos : null;
    }

    /// <summary>
    /// Returns the friendly natural-language name for a custom fix alias (e.g. <c>OAK30NUM</c>
    /// → "Oakland Runway 30 Numbers"), or null if the alias isn't a registered custom fix.
    /// </summary>
    public string? GetCustomFixName(string alias)
    {
        return _customFixNames.TryGetValue(alias, out var name) ? name : null;
    }

    /// <summary>
    /// Returns phonetic pronunciations for a fix, or an empty list if none are registered. Used
    /// by the speech pipeline to bias Whisper's decoder toward both canonical and phonetic spellings
    /// of non-obviously-pronounced fix names.
    /// </summary>
    public IReadOnlyList<string> GetFixPronunciations(string fix)
    {
        return _fixPronunciations.TryGetValue(fix, out var list) ? list : [];
    }

    /// <summary>
    /// Composes a Whisper <c>initial_prompt</c> fragment containing the phonetic pronunciations of
    /// any programmed fixes that have hints registered. The returned string is a space-separated
    /// list of pronunciations ready to be concatenated to the rest of the prompt — callers are
    /// responsible for the surrounding whitespace. Returns the empty string when no programmed
    /// fixes have hints.
    ///
    /// Fixes themselves are NOT emitted — the caller already includes canonical fix names in the
    /// prompt. This method only appends the phonetic variants so Whisper sees both forms.
    /// </summary>
    public string BuildWhisperPronunciationHint(IEnumerable<string> programmedFixes)
    {
        if (_fixPronunciations.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var fix in programmedFixes)
        {
            if (string.IsNullOrWhiteSpace(fix))
            {
                continue;
            }

            if (_fixPronunciations.TryGetValue(fix, out var pronunciations))
            {
                parts.AddRange(pronunciations);
            }
        }

        return parts.Count == 0 ? string.Empty : string.Join(' ', parts);
    }

    public double? GetAirportElevation(string code)
    {
        if (_elevations.TryGetValue(code, out var elev))
        {
            return elev;
        }

        // US ICAO (4-letter, K-prefix) → FAA (3-letter) fallback.
        if (code.Length == 4 && code.StartsWith('K') && _elevations.TryGetValue(code[1..], out var byFaa))
        {
            return byFaa;
        }

        // FAA (3-letter) → US ICAO (K-prefix) fallback.
        if (code.Length == 3 && _elevations.TryGetValue("K" + code, out var byIcao))
        {
            return byIcao;
        }

        return null;
    }

    /// <summary>
    /// Returns the published airport name (raw NavData form, typically all-caps like
    /// "METROPOLITAN OAKLAND INTL") for <paramref name="code"/>, or <c>null</c> if the
    /// code is unknown or the airport has no name. Callers that surface this to humans
    /// should title-case the result.
    /// </summary>
    public string? GetAirportName(string code)
    {
        if (_airportNames.TryGetValue(code, out var name))
        {
            return name;
        }
        if (code.Length == 4 && code.StartsWith('K') && _airportNames.TryGetValue(code[1..], out var byFaa))
        {
            return byFaa;
        }
        if (code.Length == 3 && _airportNames.TryGetValue("K" + code, out var byIcao))
        {
            return byIcao;
        }
        return null;
    }

    /// <summary>
    /// Returns the elevation (ft MSL) of the nearest airport to <paramref name="position"/>
    /// within <paramref name="maxRangeNm"/>, or <c>null</c> if no airport is within range.
    /// Used as a terrain proxy for STARS / AGL gating when the aircraft has no precise
    /// runway reference. Uses a 1°-bucketed grid; cost is O(airports in 3×3 neighborhood)
    /// for the default 100nm cap.
    /// </summary>
    public double? FindNearestAirportElevation(LatLon position, double maxRangeNm = 100)
    {
        var (latBucket, lonBucket) = AirportBucketKey(position.Lat, position.Lon);
        // 1° lat ≈ 60nm; expand bucket radius to cover maxRangeNm with margin.
        int radius = Math.Max(1, (int)Math.Ceiling(maxRangeNm / 60.0));

        double bestDist = double.MaxValue;
        double? bestElev = null;
        for (int dLat = -radius; dLat <= radius; dLat++)
        {
            for (int dLon = -radius; dLon <= radius; dLon++)
            {
                if (!_airportSpatialIndex.TryGetValue((latBucket + dLat, lonBucket + dLon), out var bucket))
                {
                    continue;
                }
                foreach (var (_, lat, lon, elev) in bucket)
                {
                    var dist = GeoMath.DistanceNm(position, new LatLon(lat, lon));
                    if ((dist < bestDist) && (dist <= maxRangeNm))
                    {
                        bestDist = dist;
                        bestElev = elev;
                    }
                }
            }
        }
        return bestElev;
    }

    /// <summary>
    /// Returns the nearest airport to <paramref name="position"/> within
    /// <paramref name="maxRangeNm"/> whose longest runway is at least
    /// <paramref name="minRunwayLengthFt"/>, or <c>null</c> if none qualifies.
    /// Used by pilot position reports to anchor against an airport a working
    /// controller (or RPO) is likely to recognize, instead of an arbitrary
    /// nearby RNAV waypoint. Walks the same 1°-bucket grid as
    /// <see cref="FindNearestAirportElevation"/>; cost is O(airports in
    /// neighborhood) plus one runway lookup per candidate.
    /// </summary>
    public (string Id, double Lat, double Lon)? FindNearestSizeableAirport(LatLon position, int minRunwayLengthFt, double maxRangeNm)
    {
        var (latBucket, lonBucket) = AirportBucketKey(position.Lat, position.Lon);
        int radius = Math.Max(1, (int)Math.Ceiling(maxRangeNm / 60.0));

        double bestDist = double.MaxValue;
        (string Id, double Lat, double Lon)? best = null;
        for (int dLat = -radius; dLat <= radius; dLat++)
        {
            for (int dLon = -radius; dLon <= radius; dLon++)
            {
                if (!_airportSpatialIndex.TryGetValue((latBucket + dLat, lonBucket + dLon), out var bucket))
                {
                    continue;
                }
                foreach (var (id, lat, lon, _) in bucket)
                {
                    var dist = GeoMath.DistanceNm(position, new LatLon(lat, lon));
                    if ((dist >= bestDist) || (dist > maxRangeNm))
                    {
                        continue;
                    }
                    if (!HasRunwayAtLeast(id, minRunwayLengthFt))
                    {
                        continue;
                    }
                    bestDist = dist;
                    best = (id, lat, lon);
                }
            }
        }
        return best;
    }

    private bool HasRunwayAtLeast(string airportId, int minLengthFt)
    {
        if (_runways.TryGetValue(airportId, out var list) && MaxRunwayLength(list) >= minLengthFt)
        {
            return true;
        }

        // FAA/ICAO key fallback, mirroring GetAirportElevation.
        if (airportId.Length == 4 && airportId.StartsWith('K') && _runways.TryGetValue(airportId[1..], out var byFaa))
        {
            return MaxRunwayLength(byFaa) >= minLengthFt;
        }
        if (airportId.Length == 3 && _runways.TryGetValue("K" + airportId, out var byIcao))
        {
            return MaxRunwayLength(byIcao) >= minLengthFt;
        }

        return false;
    }

    private static double MaxRunwayLength(IReadOnlyList<RunwayInfo> runways)
    {
        double max = 0;
        foreach (var r in runways)
        {
            if (r.LengthFt > max)
            {
                max = r.LengthFt;
            }
        }
        return max;
    }

    private static (int LatBucket, int LonBucket) AirportBucketKey(double lat, double lon) => ((int)Math.Floor(lat), (int)Math.Floor(lon));

    private void AddAirportToSpatialIndex(string id, double lat, double lon, double elevation)
    {
        var key = AirportBucketKey(lat, lon);
        if (!_airportSpatialIndex.TryGetValue(key, out var bucket))
        {
            bucket = [];
            _airportSpatialIndex[key] = bucket;
        }
        bucket.Add((id, lat, lon, elevation));
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
        return RouteExpander.Expand(route, this);
    }

    /// <summary>
    /// Expands a route string for navigation. Strips leading fixes within 1nm of the
    /// departure airport (departure vicinity). RouteExpander handles SID/STAR/airway
    /// expansion and adjacent deduplication.
    /// </summary>
    public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport)
    {
        // Flight-plan context: suppress the "emit all transitions on mismatch" fallback so a
        // radar-vectors SID with adapted-route hints (e.g. NIMI5) doesn't fabricate a turn-back
        // through every synthesized transition fix.
        var expanded = RouteExpander.Expand(route, this, includeAllTransitionsOnMismatch: false);
        if (expanded.Count == 0)
        {
            return expanded;
        }

        // Strip leading fixes within 1nm of departure airport
        if (departureAirport is not null)
        {
            var airportPos = GetFixPosition(departureAirport);
            if (airportPos is not null)
            {
                while (expanded.Count > 0)
                {
                    var fixPos = GetFixPosition(expanded[0]);
                    if (fixPos is null)
                    {
                        break;
                    }

                    double dist = GeoMath.DistanceNm(airportPos.Value.Lat, airportPos.Value.Lon, fixPos.Value.Lat, fixPos.Value.Lon);
                    if (dist > 1.0)
                    {
                        break;
                    }

                    expanded.RemoveAt(0);
                }
            }
        }

        return expanded;
    }

    // ──────────────────────────────────────────────
    //  CIFP lookups (parsed per-airport on first access)
    // ──────────────────────────────────────────────

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
    /// Returns all matching approach IDs for an ambiguous shorthand (e.g. "I17R" may match
    /// both I17RX and I17RZ). Used by <see cref="ApproachCommandHandler"/> to apply
    /// connectivity-based disambiguation when multiple variants match.
    /// </summary>
    public List<string> ResolveApproachCandidates(string airportCode, string shorthand)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(shorthand))
        {
            return result;
        }

        var approaches = GetApproaches(airportCode);
        if (approaches.Count == 0)
        {
            return result;
        }

        string upper = shorthand.ToUpperInvariant();

        // Exact match — unambiguous
        var exact = approaches.FirstOrDefault(a => a.ApproachId.Equals(upper, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            result.Add(exact.ApproachId);
            return result;
        }

        var parsed = ParseShorthand(upper);
        if (parsed is null)
        {
            return result;
        }

        var (typeCode, runway, variant) = parsed.Value;

        if (typeCode is not null)
        {
            var matches = approaches
                .Where(a =>
                    a.TypeCode == typeCode
                    && a.Runway is not null
                    && a.Runway.Equals(runway, StringComparison.OrdinalIgnoreCase)
                    && (variant is null || a.ApproachId.EndsWith(variant, StringComparison.OrdinalIgnoreCase))
                )
                .ToList();

            if (matches.Count > 0)
            {
                result.AddRange(matches.Select(m => m.ApproachId));
                return result;
            }

            // Try alternate type code (H↔R for RNAV variants)
            char? altCode = typeCode switch
            {
                'H' => 'R',
                'R' => 'H',
                _ => null,
            };

            if (altCode is not null)
            {
                matches = approaches
                    .Where(a =>
                        a.TypeCode == altCode
                        && a.Runway is not null
                        && a.Runway.Equals(runway, StringComparison.OrdinalIgnoreCase)
                        && (variant is null || a.ApproachId.EndsWith(variant, StringComparison.OrdinalIgnoreCase))
                    )
                    .ToList();

                if (matches.Count > 0)
                {
                    result.AddRange(matches.Select(m => m.ApproachId));
                    return result;
                }
            }
        }
        else if (runway is not null)
        {
            var candidates = approaches
                .Where(a => a.Runway is not null && a.Runway.Equals(runway, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => GetTypePriority(a.TypeCode))
                .ToList();

            result.AddRange(candidates.Select(c => c.ApproachId));
        }

        return result;
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

    private void BuildIndex(NavDataSet navData)
    {
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
                if (!string.IsNullOrEmpty(airport.Name))
                {
                    _airportNames.TryAdd(airport.FaaId, airport.Name);
                }
            }

            if (!string.IsNullOrEmpty(airport.IcaoId))
            {
                _navDb.TryAdd(airport.IcaoId, pos);
                _elevations.TryAdd(airport.IcaoId, airport.Elevation);
                if (!string.IsNullOrEmpty(airport.Name))
                {
                    _airportNames.TryAdd(airport.IcaoId, airport.Name);
                }
            }

            string canonical = !string.IsNullOrEmpty(airport.IcaoId) ? airport.IcaoId : airport.FaaId ?? "";
            if (!string.IsNullOrEmpty(canonical))
            {
                AddAirportToSpatialIndex(canonical, loc.Lat, loc.Lon, airport.Elevation);
                if (!string.IsNullOrEmpty(airport.FaaId))
                {
                    _airportCanonical.TryAdd(airport.FaaId, canonical);
                }

                if (!string.IsNullOrEmpty(airport.IcaoId))
                {
                    _airportCanonical.TryAdd(airport.IcaoId, canonical);
                }
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
                        TrueHeading1 = new TrueHeading(rwy1.TrueHeading),
                        Lat2 = rwy2.StartLocation!.Lat,
                        Lon2 = rwy2.StartLocation.Lon,
                        Elevation2Ft = airport.Elevation,
                        TrueHeading2 = new TrueHeading(rwy2.TrueHeading),
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
                        TrueHeading1 = new TrueHeading(rwy1.TrueHeading),
                        Lat2 = rwy1.EndLocation.Lat,
                        Lon2 = rwy1.EndLocation.Lon,
                        Elevation2Ft = airport.Elevation,
                        TrueHeading2 = new TrueHeading(rwy1.TrueHeading + 180.0),
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

    private void BuildProcedureIndex(NavDataSet navData)
    {
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

    private void LoadCustomFixes(string baseDir)
    {
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

                pos = (resolved.Value.Lat, resolved.Value.Lon);
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

                if (!string.IsNullOrWhiteSpace(def.Name))
                {
                    _customFixNames[alias] = def.Name;
                }
            }

            // Build the speech-pattern entries. Each raw phrase string is lowercased, split on
            // whitespace, and paired with the first alias. The phrases are fed through
            // AtcNumberParser so spoken number words ("three zero") collapse to digit form
            // ("30") — matching what PhraseologyMapper does to the transcript itself.
            if (def.SpokenPatterns is { Count: > 0 } patterns && def.Aliases.Count > 0)
            {
                var canonical = def.Aliases[0];
                foreach (var rawPhrase in patterns)
                {
                    if (string.IsNullOrWhiteSpace(rawPhrase))
                    {
                        continue;
                    }

                    var normalized = Speech.AtcNumberParser.NormalizeDigits(rawPhrase).ToLowerInvariant();
                    var tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (tokens.Length == 0)
                    {
                        continue;
                    }

                    _customFixSpeechPatterns.Add(new Speech.CustomFixSpeechPattern(tokens, canonical));
                }
            }
        }

        // Sort patterns by descending token count so the collapse step's longest-match scan
        // picks the most specific phrase first (e.g. "the oakland runway 30 numbers" wins over
        // "runway 30 numbers" when both would match).
        _customFixSpeechPatterns.Sort((a, b) => b.Tokens.Count.CompareTo(a.Tokens.Count));

        Log.LogInformation(
            "Custom fixes: {Added} aliases added from {Total} definitions, {Patterns} speech patterns",
            added,
            loadResult.Fixes.Count,
            _customFixSpeechPatterns.Count
        );
    }

    private void LoadFixPronunciations(string baseDir)
    {
        var loadResult = FixPronunciationLoader.LoadAll(baseDir);

        foreach (var warning in loadResult.Warnings)
        {
            Log.LogWarning("Fix pronunciation: {Warning}", warning);
        }

        foreach (var def in loadResult.Definitions)
        {
            if (!_fixPronunciations.TryGetValue(def.Fix, out var list))
            {
                list = [];
                _fixPronunciations[def.Fix] = list;
            }

            foreach (var pronunciation in def.Pronunciations)
            {
                if (!string.IsNullOrWhiteSpace(pronunciation))
                {
                    list.Add(pronunciation.Trim());
                }
            }
        }

        Log.LogInformation(
            "Fix pronunciations: {Count} fixes loaded from {Files} definitions",
            _fixPronunciations.Count,
            loadResult.Definitions.Count
        );
    }

    private static TaxiRouteCatalog LoadTaxiRoutes(string baseDir)
    {
        var loadResult = TaxiRouteLoader.LoadAll(baseDir);

        foreach (var warning in loadResult.Warnings)
        {
            Log.LogWarning("Taxi route: {Warning}", warning);
        }

        Log.LogInformation("Taxi routes: {Count} loaded from {BaseDir}", loadResult.Routes.Count, baseDir);

        return new TaxiRouteCatalog(loadResult.Routes);
    }

    private static InitialContactTransferCatalog LoadInitialContactTransfers(string baseDir)
    {
        var loadResult = InitialContactTransferLoader.LoadAll(baseDir);

        foreach (var warning in loadResult.Warnings)
        {
            Log.LogWarning("Initial contact transfer: {Warning}", warning);
        }

        Log.LogInformation("Initial contact transfers: {Count} rule(s) loaded from {BaseDir}", loadResult.Rules.Count, baseDir);

        return new InitialContactTransferCatalog(loadResult.Rules);
    }

    private static WakeDirectiveCatalog LoadWakeDirectives(string baseDir)
    {
        var loadResult = WakeDirectiveLoader.LoadAll(baseDir);

        foreach (var warning in loadResult.Warnings)
        {
            Log.LogWarning("Wake directive: {Warning}", warning);
        }

        Log.LogInformation("Wake directives: {Count} rule(s) loaded from {BaseDir}", loadResult.Rules.Count, baseDir);

        return new WakeDirectiveCatalog(loadResult.Rules);
    }

    private string[] BuildSortedNames()
    {
        var names = _navDb.Keys.ToArray();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }

    // ──────────────────────────────────────────────
    //  CIFP loaders (private, per-airport on first access)
    // ──────────────────────────────────────────────

    private IReadOnlyList<CifpSidProcedure> LoadSids(string normalizedAirport)
    {
        string icao = normalizedAirport.Length <= 3 ? $"K{normalizedAirport}" : normalizedAirport;
        return CifpParser.ParseSids(_cifpFilePath, icao);
    }

    private IReadOnlyList<CifpStarProcedure> LoadStars(string normalizedAirport)
    {
        string icao = normalizedAirport.Length <= 3 ? $"K{normalizedAirport}" : normalizedAirport;
        return CifpParser.ParseStars(_cifpFilePath, icao);
    }

    private IReadOnlyList<CifpApproachProcedure> LoadApproaches(string normalizedAirport)
    {
        string icao = normalizedAirport.Length <= 3 ? $"K{normalizedAirport}" : normalizedAirport;
        return CifpParser.ParseApproaches(_cifpFilePath, icao);
    }

    /// <summary>
    /// Supplements the fix database with VOR/DME/NDB navaids from CIFP.
    /// </summary>
    private void LoadCifpNavaids(string cifpFilePath)
    {
        if (!File.Exists(cifpFilePath))
        {
            Log.LogWarning("CIFP file not found: {Path}", cifpFilePath);
            return;
        }

        var navaids = CifpParser.ParseNavaids(cifpFilePath);
        foreach (var (ident, info) in navaids)
        {
            _navDb.TryAdd(ident, (info.Lat, info.Lon));
            if (!string.IsNullOrEmpty(info.Name))
            {
                _navaidNames.TryAdd(ident, info.Name);
            }
        }
    }

    /// <summary>
    /// Returns the published navaid name (e.g. "WOODSIDE" for OSI, "POINT REYES" for PYE)
    /// for <paramref name="code"/>, or <c>null</c> if the code isn't a known VHF navaid.
    /// Sourced from CIFP section D primary records.
    /// </summary>
    public string? GetNavaidName(string code)
    {
        return _navaidNames.TryGetValue(code, out var name) ? name : null;
    }

    /// <summary>
    /// Canonicalizes an airport identifier by uppercasing and stripping the CONUS
    /// K-prefix (e.g. "KOAK" → "OAK"). Used wherever two airport IDs must be
    /// compared without caring whether the caller wrote ICAO or FAA form. Scenario
    /// files and vNAS data use both interchangeably.
    /// </summary>
    public static string NormalizeAirport(string code)
    {
        string upper = code.ToUpperInvariant().Trim();
        return upper.StartsWith('K') && upper.Length == 4 ? upper[1..] : upper;
    }

    /// <summary>
    /// Resolves a user-supplied airport identifier in any common form (FAA "OAK",
    /// ICAO "KOAK", mixed case, surrounding whitespace) to the canonical ICAO form
    /// scenarios use in flight-plan fields. Returns false if no airport in the
    /// navigation database matches the input — callers should reject unknown
    /// airports with a clear error rather than storing them.
    /// </summary>
    /// <param name="input">User-typed airport identifier.</param>
    /// <param name="canonicalId">
    /// On success, the canonical ICAO id (or FAA id if the airport has no published
    /// ICAO). Empty string on failure.
    /// </param>
    public bool TryResolveAirport(string? input, out string canonicalId)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            canonicalId = string.Empty;
            return false;
        }

        string key = input.Trim().ToUpperInvariant();
        if (_airportCanonical.TryGetValue(key, out var resolved))
        {
            canonicalId = resolved;
            return true;
        }

        canonicalId = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns true when two airport identifiers refer to the same airport after
    /// canonicalization. Handles the common ICAO-vs-FAA mismatch: "KOAK" matches
    /// "OAK". Safe on null/empty — empty strings never match anything.
    /// </summary>
    public static bool AirportIdsMatch(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }
        return NormalizeAirport(a).Equals(NormalizeAirport(b), StringComparison.Ordinal);
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
