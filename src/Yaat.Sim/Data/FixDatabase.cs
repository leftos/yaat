using Microsoft.Extensions.Logging;
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
    private readonly Dictionary<string, (double Lat, double Lon)> _fixes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, double> _elevations =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, HashSet<string>> _sidFixes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, HashSet<string>> _starFixes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<RunwayInfo>> _runways =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger? _logger;

    public FixDatabase(
        NavDataSet? navData,
        string? customFixesBaseDir = null,
        ILogger? logger = null)
    {
        _logger = logger;
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
            if (string.Equals(rwy.RunwayId, runwayId, StringComparison.OrdinalIgnoreCase))
            {
                return rwy;
            }
        }

        return null;
    }

    public IReadOnlyList<RunwayInfo> GetRunways(string airportCode)
    {
        return _runways.TryGetValue(airportCode, out var list) ? list : [];
    }

    /// <summary>
    /// Expands a route string into constituent fix names.
    /// SID/STAR identifiers are expanded to their body + transition
    /// fixes. Regular fix names pass through as-is.
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

            if (_sidFixes.TryGetValue(token, out var sidSet))
            {
                result.AddRange(sidSet);
            }
            else if (_starFixes.TryGetValue(token, out var starSet))
            {
                result.AddRange(starSet);
            }
            else
            {
                result.Add(token);
            }
        }

        return result;
    }

    private void BuildIndex(NavDataSet? navData)
    {
        if (navData is null)
        {
            _logger?.LogWarning(
                "No NavData available — fix lookup will be empty");
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
            foreach (var rwy in airport.Runways)
            {
                if (rwy.StartLocation is null || rwy.EndLocation is null)
                {
                    continue;
                }

                double elevation = airport.Elevation;
                var info = new RunwayInfo
                {
                    AirportId = airport.FaaId ?? airport.IcaoId ?? "",
                    RunwayId = rwy.Id,
                    ThresholdLatitude = rwy.StartLocation.Lat,
                    ThresholdLongitude = rwy.StartLocation.Lon,
                    TrueHeading = rwy.TrueHeading,
                    ElevationFt = elevation,
                    LengthFt = rwy.LandingDistanceAvailable,
                    WidthFt = rwy.Width,
                    EndLatitude = rwy.EndLocation.Lat,
                    EndLongitude = rwy.EndLocation.Lon,
                };

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

        _logger?.LogInformation(
            "Fix database built: {Count} entries "
            + "({Airports} airports + {Fixes} fixes + {Runways} runways)",
            _fixes.Count,
            navData.Airports.Count,
            navData.Fixes.Count,
            runwayCount);
    }

    private void BuildProcedureIndex(NavDataSet? navData)
    {
        if (navData is null)
        {
            return;
        }

        foreach (var sid in navData.Sids)
        {
            var fixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fix in sid.Body)
            {
                fixes.Add(fix);
            }

            foreach (var trans in sid.Transitions)
            {
                foreach (var fix in trans.Fixes)
                {
                    fixes.Add(fix);
                }
            }

            _sidFixes.TryAdd(sid.Id, fixes);
        }

        foreach (var star in navData.Stars)
        {
            var fixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fix in star.Body)
            {
                fixes.Add(fix);
            }

            foreach (var trans in star.Transitions)
            {
                foreach (var fix in trans.Fixes)
                {
                    fixes.Add(fix);
                }
            }

            _starFixes.TryAdd(star.Id, fixes);
        }

        _logger?.LogInformation(
            "Procedure index: {Sids} SIDs, {Stars} STARs",
            _sidFixes.Count, _starFixes.Count);
    }

    private void LoadCustomFixes(string? baseDir)
    {
        baseDir ??= Path.Combine(
            AppContext.BaseDirectory, "data", "custom_fixes");

        var loadResult = CustomFixLoader.LoadAll(baseDir);

        foreach (var warning in loadResult.Warnings)
        {
            _logger?.LogWarning("Custom fix: {Warning}", warning);
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
                    _logger?.LogWarning(
                        "Custom fix {Alias}: failed to resolve FRD '{Frd}'",
                        def.Aliases[0], def.Frd);
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
                    _logger?.LogWarning(
                        "Custom fix alias '{Alias}' conflicts with "
                        + "existing entry", alias);
                }
            }
        }

        _logger?.LogInformation(
            "Custom fixes: {Added} aliases added from {Total} definitions",
            added, loadResult.Fixes.Count);
    }

    private string[] BuildSortedNames()
    {
        var names = _fixes.Keys.ToArray();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }
}
