using System.IO.Compression;
using System.Text.Json;
using Yaat.Sim.Data.Vnas;

namespace Yaat.CifpInspector;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string? cifpPath = null;
        string? airport = null;
        bool listApproaches = false;
        string? approachId = null;
        string? finalCourseApproachId = null;
        string? compareA = null;
        string? compareB = null;
        bool jsonOutput = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--cifp" when i + 1 < args.Length:
                    cifpPath = args[++i];
                    break;
                case "--airport" when i + 1 < args.Length:
                    airport = args[++i].ToUpperInvariant();
                    break;
                case "--list-approaches":
                    listApproaches = true;
                    break;
                case "--approach" when i + 1 < args.Length:
                    approachId = args[++i].ToUpperInvariant();
                    break;
                case "--final-course" when i + 1 < args.Length:
                    finalCourseApproachId = args[++i].ToUpperInvariant();
                    break;
                case "--compare" when i + 2 < args.Length:
                    compareA = args[++i].ToUpperInvariant();
                    compareB = args[++i].ToUpperInvariant();
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown flag: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        if (airport is null)
        {
            Console.Error.WriteLine("--airport <ICAO> is required");
            return 2;
        }

        cifpPath ??= FindDefaultCifp();
        if (cifpPath is null)
        {
            Console.Error.WriteLine("CIFP file not found. Pass --cifp <path> or run from the yaat repo.");
            return 1;
        }

        string decompressed;
        try
        {
            decompressed = cifpPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? DecompressGzip(cifpPath) : cifpPath;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read CIFP file: {ex.Message}");
            return 1;
        }

        IReadOnlyList<CifpApproachProcedure> approaches;
        try
        {
            approaches = CifpParser.ParseApproaches(decompressed, airport);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse CIFP: {ex.Message}");
            return 1;
        }

        if (approaches.Count == 0)
        {
            Console.Error.WriteLine($"No approaches parsed for airport {airport}");
            return 1;
        }

        if (listApproaches)
        {
            PrintApproachList(approaches, jsonOutput);
            return 0;
        }

        if (approachId is not null)
        {
            var match = FindApproach(approaches, approachId);
            if (match is null)
            {
                Console.Error.WriteLine($"Approach not found: {approachId}");
                return 1;
            }
            PrintApproachDetail(match, jsonOutput);
            return 0;
        }

        if (finalCourseApproachId is not null)
        {
            var match = FindApproach(approaches, finalCourseApproachId);
            if (match is null)
            {
                Console.Error.WriteLine($"Approach not found: {finalCourseApproachId}");
                return 1;
            }
            PrintFinalCourseAnalysis(match, jsonOutput);
            return 0;
        }

        if (compareA is not null && compareB is not null)
        {
            var a = FindApproach(approaches, compareA);
            var b = FindApproach(approaches, compareB);
            if (a is null)
            {
                Console.Error.WriteLine($"Approach not found: {compareA}");
                return 1;
            }
            if (b is null)
            {
                Console.Error.WriteLine($"Approach not found: {compareB}");
                return 1;
            }
            PrintCompare(a, b);
            return 0;
        }

        // No subcommand → default to list
        PrintApproachList(approaches, jsonOutput);
        return 0;
    }

    private static CifpApproachProcedure? FindApproach(IReadOnlyList<CifpApproachProcedure> approaches, string id)
    {
        // Exact match first
        foreach (var a in approaches)
        {
            if (a.ApproachId.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }
        }
        // Case-insensitive after trimming (handles trailing space padding from CIFP records)
        foreach (var a in approaches)
        {
            if (a.ApproachId.Trim().Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }
        }
        return null;
    }

    private static void PrintApproachList(IReadOnlyList<CifpApproachProcedure> approaches, bool json)
    {
        if (json)
        {
            var rows = approaches
                .OrderBy(a => a.ApproachId, StringComparer.Ordinal)
                .Select(a => new
                {
                    a.ApproachId,
                    Type = a.TypeCode.ToString(),
                    a.ApproachTypeName,
                    a.Runway,
                    CommonLegCount = a.CommonLegs.Count,
                    TransitionCount = a.Transitions.Count,
                    a.HasHoldInLieu,
                });
            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"{"ID", -10} {"Type", -12} {"Runway", -8} {"#legs", 6} {"#trans", 7} HiLo");
        Console.WriteLine(new string('-', 50));
        foreach (var a in approaches.OrderBy(a => a.ApproachId, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"{a.ApproachId, -10} {a.ApproachTypeName, -12} {a.Runway ?? "?", -8} {a.CommonLegs.Count, 6} {a.Transitions.Count, 7} {(a.HasHoldInLieu ? "yes" : "no")}"
            );
        }
        Console.WriteLine($"\nTotal: {approaches.Count} approaches");
    }

    private static void PrintApproachDetail(CifpApproachProcedure a, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(a, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"=== {a.Airport} {a.ApproachId} ({a.ApproachTypeName}) ===");
        Console.WriteLine($"Runway: {a.Runway}");
        Console.WriteLine($"TypeCode: {a.TypeCode}");
        Console.WriteLine($"HasHoldInLieu: {a.HasHoldInLieu}");
        Console.WriteLine();

        Console.WriteLine($"Common legs ({a.CommonLegs.Count}):");
        PrintLegTable(a.CommonLegs);

        if (a.Transitions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Transitions ({a.Transitions.Count}):");
            foreach (var (name, t) in a.Transitions)
            {
                Console.WriteLine($"  [{name}] ({t.Legs.Count} legs):");
                PrintLegTable(t.Legs, indent: "    ");
            }
        }

        if (a.MissedApproachLegs.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Missed approach legs ({a.MissedApproachLegs.Count}):");
            PrintLegTable(a.MissedApproachLegs);
        }

        if (a.HoldInLieuLeg is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"Hold-in-lieu leg: {a.HoldInLieuLeg.FixIdentifier} {a.HoldInLieuLeg.PathTerminator}");
        }
    }

    private static void PrintLegTable(IReadOnlyList<CifpLeg> legs, string indent = "  ")
    {
        Console.WriteLine($"{indent}{"seq", 3} {"fix", -7} {"role", -5} {"pt", -3} {"course", -7} {"dist", -6} {"arcR", -6} {"alt", -18} flags");
        foreach (var leg in legs)
        {
            string course = leg.OutboundCourse?.ToString("F1") ?? "-";
            string dist = leg.LegDistanceNm?.ToString("F1") ?? "-";
            string arcR = leg.ArcRadiusNm?.ToString("F2") ?? "-";
            string alt = FormatAltitude(leg.Altitude);
            string flags = string.Concat(leg.IsFlyOver ? "F" : ".", leg.TurnDirection?.ToString() ?? ".");
            Console.WriteLine(
                $"{indent}{leg.Sequence, 3} {leg.FixIdentifier, -7} {leg.FixRole, -5} {leg.PathTerminator, -3} {course, -7} {dist, -6} {arcR, -6} {alt, -18} {flags}"
            );
        }
    }

    private static string FormatAltitude(CifpAltitudeRestriction? r)
    {
        if (r is null)
        {
            return "-";
        }
        return r.Type switch
        {
            CifpAltitudeRestrictionType.At => $"@{r.Altitude1Ft}",
            CifpAltitudeRestrictionType.AtOrAbove => $"≥{r.Altitude1Ft}",
            CifpAltitudeRestrictionType.AtOrBelow => $"≤{r.Altitude1Ft}",
            CifpAltitudeRestrictionType.Between => $"{r.Altitude1Ft}-{r.Altitude2Ft}",
            CifpAltitudeRestrictionType.GlideSlopeIntercept => $"GS@{r.Altitude1Ft}",
            _ => "?",
        };
    }

    private static void PrintFinalCourseAnalysis(CifpApproachProcedure a, bool json)
    {
        // Identify the "final approach leg" using a few candidate strategies and report each.
        // The "Extractor (MAP leg itself)" strategy mirrors what FinalApproachCourseExtractor
        // actually uses in production — the others are diagnostic alternatives.
        var finalByExtractor = ExtractorMapLeg(a);
        var finalByMahp = FinalLegBeforeMap(a);
        var finalByRwFix = FinalLegToRwFix(a);
        var finalByLastBeforeCa = FinalLegBeforeCa(a);

        if (json)
        {
            var report = new
            {
                a.ApproachId,
                a.ApproachTypeName,
                a.Runway,
                Strategies = new
                {
                    Extractor = LegSummary(finalByExtractor),
                    BeforeMap = LegSummary(finalByMahp),
                    ToRwFix = LegSummary(finalByRwFix),
                    LastBeforeCa = LegSummary(finalByLastBeforeCa),
                },
            };
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"=== Final-course analysis: {a.ApproachId} ({a.ApproachTypeName}, runway {a.Runway}) ===");
        Console.WriteLine();
        Console.WriteLine("Strategy candidates:");
        PrintStrategy("Extractor (MAP leg itself)", finalByExtractor);
        PrintStrategy("Leg before MAP fix", finalByMahp);
        PrintStrategy("Leg terminating at RW## fix", finalByRwFix);
        PrintStrategy("Last leg before CA missed start", finalByLastBeforeCa);
        Console.WriteLine();
        Console.WriteLine("Common legs (for context):");
        PrintLegTable(a.CommonLegs);
    }

    private static void PrintStrategy(string label, CifpLeg? leg)
    {
        if (leg is null)
        {
            Console.WriteLine($"  {label, -32}: (no leg matched)");
            return;
        }
        string course = leg.OutboundCourse?.ToString("F1") ?? "(no OutboundCourse — TF/DF/RF)";
        Console.WriteLine($"  {label, -32}: seq={leg.Sequence} fix={leg.FixIdentifier} pt={leg.PathTerminator} course={course}");
    }

    private static object? LegSummary(CifpLeg? leg)
    {
        if (leg is null)
        {
            return null;
        }
        return new
        {
            leg.Sequence,
            leg.FixIdentifier,
            PathTerminator = leg.PathTerminator.ToString(),
            FixRole = leg.FixRole.ToString(),
            leg.OutboundCourse,
            leg.LegDistanceNm,
            leg.ArcRadiusNm,
        };
    }

    /// <summary>
    /// Strategy 0 (production): the MAP-marked leg itself. This is what
    /// FinalApproachCourseExtractor uses — its OutboundCourse (CF/FA) or computed bearing
    /// from the previous fix (TF/DF) is the published final approach course.
    /// </summary>
    private static CifpLeg? ExtractorMapLeg(CifpApproachProcedure a)
    {
        foreach (var leg in a.CommonLegs)
        {
            if (leg.FixRole == CifpFixRole.MAP)
            {
                return leg;
            }
        }
        return null;
    }

    /// <summary>
    /// Strategy 1: walk back to find the leg whose fix is marked MAP, then return the leg
    /// IMMEDIATELY BEFORE it. Diagnostic alternative to the extractor's logic.
    /// </summary>
    private static CifpLeg? FinalLegBeforeMap(CifpApproachProcedure a)
    {
        for (int i = 0; i < a.CommonLegs.Count; i++)
        {
            if (a.CommonLegs[i].FixRole == CifpFixRole.MAP && i > 0)
            {
                return a.CommonLegs[i - 1];
            }
        }
        return null;
    }

    /// <summary>
    /// Strategy 2: find the leg whose fix identifier looks like a runway pseudo-fix (RW##).
    /// In CIFP, the missed approach point often terminates at a synthetic "RW10L"-style fix.
    /// </summary>
    private static CifpLeg? FinalLegToRwFix(CifpApproachProcedure a)
    {
        for (int i = a.CommonLegs.Count - 1; i >= 0; i--)
        {
            var leg = a.CommonLegs[i];
            if (leg.FixIdentifier.StartsWith("RW", StringComparison.Ordinal))
            {
                // The leg-to-RW is itself the final leg; return the one with course info if any.
                return leg;
            }
        }
        return null;
    }

    /// <summary>
    /// Strategy 3: find the first CA (Course-to-Altitude) leg (start of missed approach climb)
    /// and return the leg immediately before it.
    /// </summary>
    private static CifpLeg? FinalLegBeforeCa(CifpApproachProcedure a)
    {
        for (int i = 0; i < a.CommonLegs.Count; i++)
        {
            if (a.CommonLegs[i].PathTerminator == CifpPathTerminator.CA && i > 0)
            {
                return a.CommonLegs[i - 1];
            }
        }
        return null;
    }

    private static void PrintCompare(CifpApproachProcedure a, CifpApproachProcedure b)
    {
        Console.WriteLine($"=== {a.ApproachId} vs {b.ApproachId} ===");
        Console.WriteLine();
        Console.WriteLine($"--- {a.ApproachId} ({a.ApproachTypeName}, rwy {a.Runway}) common legs ---");
        PrintLegTable(a.CommonLegs);
        Console.WriteLine();
        Console.WriteLine($"--- {b.ApproachId} ({b.ApproachTypeName}, rwy {b.Runway}) common legs ---");
        PrintLegTable(b.CommonLegs);
    }

    private static string DecompressGzip(string gzPath)
    {
        var decompressedPath = Path.Combine(Path.GetTempPath(), $"yaat-cifpinspector-{Path.GetFileNameWithoutExtension(gzPath)}");
        if (File.Exists(decompressedPath))
        {
            // Reuse if newer than source
            if (File.GetLastWriteTimeUtc(decompressedPath) >= File.GetLastWriteTimeUtc(gzPath))
            {
                return decompressedPath;
            }
        }
        using var inputStream = File.OpenRead(gzPath);
        using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
        using var outputStream = File.Create(decompressedPath);
        gzipStream.CopyTo(outputStream);
        return decompressedPath;
    }

    private static string? FindDefaultCifp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                var bundled = Path.Combine(dir.FullName, "tests", "Yaat.Sim.Tests", "TestData", "FAACIFP18.gz");
                return File.Exists(bundled) ? bundled : null;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: Yaat.CifpInspector --airport <ICAO> [subcommand]");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  --list-approaches            List all approaches at the airport");
        Console.WriteLine("  --approach <id>              Show full parsed detail for one approach");
        Console.WriteLine("  --final-course <id>          Analyse FAC extraction strategies for one approach");
        Console.WriteLine("  --compare <id1> <id2>        Side-by-side common-leg comparison of two approaches");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --cifp <path>                CIFP file (.dat or .gz). Defaults to tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz");
        Console.WriteLine("  --json                       Output as JSON");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  Yaat.CifpInspector --airport KSFO --list-approaches");
        Console.WriteLine("  Yaat.CifpInspector --airport KSFO --approach R10L");
        Console.WriteLine("  Yaat.CifpInspector --airport KCCR --final-course S19R");
        Console.WriteLine("  Yaat.CifpInspector --airport KSFO --compare R10L I28R");
    }
}
