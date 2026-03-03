using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data;

public sealed class ApproachDatabase : IApproachLookup
{
    private readonly string? _cifpFilePath;
    private readonly ILogger? _logger;

    // Lazy per-airport cache: FAA code → list of procedures
    private readonly ConcurrentDictionary<string, IReadOnlyList<CifpApproachProcedure>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ApproachDatabase(string? cifpFilePath, ILogger? logger = null)
    {
        _cifpFilePath = cifpFilePath;
        _logger = logger;
    }

    public CifpApproachProcedure? GetApproach(string airportCode, string approachId)
    {
        var approaches = GetApproaches(airportCode);
        return approaches.FirstOrDefault(a => a.ApproachId.Equals(approachId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<CifpApproachProcedure> GetApproaches(string airportCode)
    {
        string normalized = NormalizeAirport(airportCode);
        return _cache.GetOrAdd(normalized, LoadApproaches);
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

        // Try mapping common type names to type codes
        // "ILS28R" → type 'I', runway "28R"
        // "RNAV17LZ" → type 'H' (RNAV(GPS)) or 'R' (RNAV), runway "17L", variant "Z"
        // "LOC30" → type 'L', runway "30"
        // "28R" → any approach for runway 28R
        var parsed = ParseShorthand(upper);
        if (parsed is null)
        {
            return null;
        }

        var (typeCode, runway, variant) = parsed.Value;

        if (typeCode is not null)
        {
            // Search by type code + runway + optional variant
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

            // Some types map to multiple codes (RNAV can be 'H' or 'R')
            // Try alternate codes
            char? altCode = typeCode switch
            {
                'H' => 'R', // RNAV(GPS) vs RNAV
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
            // Runway-only search: return first approach for this runway
            // Priority: ILS > LOC > RNAV(GPS) > RNAV > GPS > rest
            var candidates = approaches.Where(a => a.Runway is not null && a.Runway.Equals(runway, StringComparison.OrdinalIgnoreCase)).ToList();

            if (candidates.Count > 0)
            {
                var best = candidates.OrderBy(a => GetTypePriority(a.TypeCode)).First();
                return best.ApproachId;
            }
        }

        return null;
    }

    private IReadOnlyList<CifpApproachProcedure> LoadApproaches(string normalizedAirport)
    {
        if (_cifpFilePath is null || !File.Exists(_cifpFilePath))
        {
            return [];
        }

        // ParseApproaches expects ICAO code (KOAK), but we store FAA code (OAK)
        string icao = normalizedAirport.Length <= 3 ? $"K{normalizedAirport}" : normalizedAirport;
        return CifpParser.ParseApproaches(_cifpFilePath, icao, _logger);
    }

    private static string NormalizeAirport(string code)
    {
        string upper = code.ToUpperInvariant().Trim();
        return upper.StartsWith('K') && upper.Length == 4 ? upper[1..] : upper;
    }

    private static (char? TypeCode, string? Runway, string? Variant)? ParseShorthand(string s)
    {
        // Try named approach type prefix
        var (typeCode, rest) = TryStripTypePrefix(s);

        if (typeCode is not null && rest.Length > 0)
        {
            // Extract runway: 1-2 digits + optional L/R/C
            int i = 0;
            while (i < rest.Length && char.IsDigit(rest[i]))
            {
                i++;
            }

            if (i == 0)
            {
                return null;
            }

            // Check for L/R/C suffix
            if (i < rest.Length && rest[i] is 'L' or 'R' or 'C')
            {
                i++;
            }

            string runway = rest[..i];
            string? variant = i < rest.Length ? rest[i..] : null;

            return (typeCode, runway, variant);
        }

        // No type prefix — try runway-only (e.g., "28R")
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
        // Try full-word prefixes first: "ILS28R", "LOC30", "RNAV17LZ", "GPS17L"
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

        // Try single-char CIFP type code: "I28R", "H17LZ", "L30"
        if (s.Length >= 2 && char.IsLetter(s[0]) && char.IsDigit(s[1]))
        {
            char code = char.ToUpperInvariant(s[0]);
            return (code, s[1..]);
        }

        return (null, s);
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
