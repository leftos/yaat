using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data;

public sealed class ProcedureDatabase : IProcedureLookup
{
    private string? _cifpFilePath;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, IReadOnlyList<CifpSidProcedure>> _sidCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<CifpStarProcedure>> _starCache = new(StringComparer.OrdinalIgnoreCase);

    public ProcedureDatabase(string? cifpFilePath = null, ILogger? logger = null)
    {
        _cifpFilePath = cifpFilePath;
        _logger = logger;
    }

    public void SetCifpPath(string path)
    {
        _cifpFilePath = path;
        _sidCache.Clear();
        _starCache.Clear();
    }

    public CifpSidProcedure? GetSid(string airportCode, string sidId)
    {
        var sids = GetSids(airportCode);
        return sids.FirstOrDefault(s => s.ProcedureId.Equals(sidId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<CifpSidProcedure> GetSids(string airportCode)
    {
        string normalized = NormalizeAirport(airportCode);
        return _sidCache.GetOrAdd(normalized, LoadSids);
    }

    public CifpStarProcedure? GetStar(string airportCode, string starId)
    {
        var stars = GetStars(airportCode);
        return stars.FirstOrDefault(s => s.ProcedureId.Equals(starId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<CifpStarProcedure> GetStars(string airportCode)
    {
        string normalized = NormalizeAirport(airportCode);
        return _starCache.GetOrAdd(normalized, LoadStars);
    }

    private IReadOnlyList<CifpSidProcedure> LoadSids(string normalizedAirport)
    {
        if (_cifpFilePath is null || !File.Exists(_cifpFilePath))
        {
            return [];
        }

        string icao = normalizedAirport.Length <= 3 ? $"K{normalizedAirport}" : normalizedAirport;
        return CifpParser.ParseSids(_cifpFilePath, icao, _logger);
    }

    private IReadOnlyList<CifpStarProcedure> LoadStars(string normalizedAirport)
    {
        if (_cifpFilePath is null || !File.Exists(_cifpFilePath))
        {
            return [];
        }

        string icao = normalizedAirport.Length <= 3 ? $"K{normalizedAirport}" : normalizedAirport;
        return CifpParser.ParseStars(_cifpFilePath, icao, _logger);
    }

    private static string NormalizeAirport(string code)
    {
        string upper = code.ToUpperInvariant().Trim();
        return upper.StartsWith('K') && upper.Length == 4 ? upper[1..] : upper;
    }
}
