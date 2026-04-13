namespace Yaat.LayoutInspector;

/// <summary>
/// Parsed command-line options for Yaat.LayoutInspector. All collection fields are
/// populated during parsing and then frozen; callers should treat the record as
/// read-only after <see cref="TryParse"/> returns.
/// </summary>
public sealed record CliOptions
{
    public required string GeoJsonPath { get; init; }
    public string? AirportCode { get; init; }
    public string? NavDataDir { get; init; }

    public List<string> Taxiways { get; init; } = [];
    public List<string> Runways { get; init; } = [];
    public List<int> NodeIds { get; init; } = [];
    public List<string> ExitsRunways { get; init; } = [];

    public int? BfsNodeId { get; init; }
    public string? BfsTaxiway { get; init; }

    public int? PathfinderNodeId { get; init; }
    public List<string> PathfinderTaxiways { get; init; } = [];

    public bool ShowParking { get; init; }
    public bool ShowSpots { get; init; }
    public bool Validate { get; init; }

    public string? IntersectionTaxiway1 { get; init; }
    public string? IntersectionTaxiway2 { get; init; }

    public bool JsonOutput { get; init; }
    public bool DumpAll { get; init; }

    public bool NoFillets { get; init; }
    public bool DebugFillets { get; init; }
    public bool DebugExits { get; init; }

    public List<(string Runway, string Taxiway, string? Side)> ExitQueries { get; init; } = [];

    public string? SvgOutputPath { get; init; }
    public string? TicksCsvPath { get; init; }

    public List<string> SvgHighlightTaxiways { get; init; } = [];
    public List<string> SvgHighlightRunways { get; init; } = [];
    public List<int> SvgHighlightNodes { get; init; } = [];
    public List<(int NodeId, string Text)> SvgAnnotations { get; init; } = [];
    public List<int> SvgRouteNodes { get; init; } = [];

    /// <summary>
    /// Parses command-line arguments into a <see cref="CliOptions"/> instance.
    /// Returns false and populates <paramref name="error"/> if args are malformed;
    /// caller should print usage and exit 2.
    /// </summary>
    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        options = null!;
        error = null;

        if (args.Length == 0)
        {
            error = "missing <geojson-path>";
            return false;
        }

        string geoJsonPath = args[0];

        // Mutable scratch state used while walking the arg list. Copied into the
        // frozen record at the end.
        string? airportCode = null;
        string? navdataDir = null;
        var taxiways = new List<string>();
        var runways = new List<string>();
        var nodeIds = new List<int>();
        var exitsRunways = new List<string>();
        int? pathNodeId = null;
        string? pathTaxiway = null;
        int? pfNodeId = null;
        var pfTaxiways = new List<string>();
        bool showParking = false;
        bool showSpots = false;
        bool validate = false;
        string? intersectTwy1 = null;
        string? intersectTwy2 = null;
        bool jsonOutput = false;
        bool dumpAll = false;
        bool noFillets = false;
        bool debugFillets = false;
        bool debugExits = false;
        var exitQueries = new List<(string Runway, string Taxiway, string? Side)>();
        string? svgOutput = null;
        string? ticksCsvPath = null;
        var svgHighlightTaxiways = new List<string>();
        var svgHighlightRunways = new List<string>();
        var svgHighlightNodes = new List<int>();
        var svgAnnotations = new List<(int NodeId, string Text)>();
        var svgRouteNodes = new List<int>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--airport-code" when i + 1 < args.Length:
                    airportCode = args[++i];
                    break;
                case "--navdata" when i + 1 < args.Length:
                    navdataDir = args[++i];
                    break;
                case "--taxiway" when i + 1 < args.Length:
                    taxiways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--runway" when i + 1 < args.Length:
                    runways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--node" when i + 1 < args.Length:
                    nodeIds.Add(int.Parse(args[++i]));
                    break;
                case "--exits" when i + 1 < args.Length:
                    exitsRunways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--bfs" when i + 2 < args.Length:
                    pathNodeId = int.Parse(args[++i]);
                    pathTaxiway = args[++i].ToUpperInvariant();
                    break;
                case "--parking":
                    showParking = true;
                    break;
                case "--spots":
                    showSpots = true;
                    break;
                case "--validate":
                    validate = true;
                    break;
                case "--intersection" when i + 2 < args.Length:
                    intersectTwy1 = args[++i].ToUpperInvariant();
                    intersectTwy2 = args[++i].ToUpperInvariant();
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "--dump":
                    dumpAll = true;
                    jsonOutput = true;
                    break;
                case "--no-fillets":
                    noFillets = true;
                    break;
                case "--debug-fillets":
                    debugFillets = true;
                    break;
                case "--debug-exits":
                    debugExits = true;
                    break;
                case "--exit-query" when i + 2 < args.Length:
                {
                    string qRwy = args[++i].ToUpperInvariant();
                    string qTwy = args[++i].ToUpperInvariant();
                    string? qSide = null;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        qSide = args[++i];
                    }

                    exitQueries.Add((qRwy, qTwy, qSide));
                    debugExits = true;
                    break;
                }
                case "--svg" when i + 1 < args.Length:
                    svgOutput = args[++i];
                    break;
                case "--ticks" when i + 1 < args.Length:
                    ticksCsvPath = args[++i];
                    break;
                case "--svg-taxiway" when i + 1 < args.Length:
                    svgHighlightTaxiways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--svg-runway" when i + 1 < args.Length:
                    svgHighlightRunways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--svg-node" when i + 1 < args.Length:
                    svgHighlightNodes.Add(int.Parse(args[++i]));
                    break;
                case "--svg-annotate" when i + 2 < args.Length:
                    svgAnnotations.Add((int.Parse(args[++i]), args[++i]));
                    break;
                case "--svg-route" when i + 1 < args.Length:
                {
                    foreach (string nid in args[++i].Split(','))
                    {
                        svgRouteNodes.Add(int.Parse(nid.Trim()));
                    }

                    break;
                }
                case "--pathfinder" when i + 2 < args.Length:
                    pfNodeId = int.Parse(args[++i]);
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        pfTaxiways.Add(args[++i].ToUpperInvariant());
                    }

                    break;
                default:
                    error = $"Unknown flag: {args[i]}";
                    return false;
            }
        }

        options = new CliOptions
        {
            GeoJsonPath = geoJsonPath,
            AirportCode = airportCode,
            NavDataDir = navdataDir,
            Taxiways = taxiways,
            Runways = runways,
            NodeIds = nodeIds,
            ExitsRunways = exitsRunways,
            BfsNodeId = pathNodeId,
            BfsTaxiway = pathTaxiway,
            PathfinderNodeId = pfNodeId,
            PathfinderTaxiways = pfTaxiways,
            ShowParking = showParking,
            ShowSpots = showSpots,
            Validate = validate,
            IntersectionTaxiway1 = intersectTwy1,
            IntersectionTaxiway2 = intersectTwy2,
            JsonOutput = jsonOutput,
            DumpAll = dumpAll,
            NoFillets = noFillets,
            DebugFillets = debugFillets,
            DebugExits = debugExits,
            ExitQueries = exitQueries,
            SvgOutputPath = svgOutput,
            TicksCsvPath = ticksCsvPath,
            SvgHighlightTaxiways = svgHighlightTaxiways,
            SvgHighlightRunways = svgHighlightRunways,
            SvgHighlightNodes = svgHighlightNodes,
            SvgAnnotations = svgAnnotations,
            SvgRouteNodes = svgRouteNodes,
        };
        return true;
    }

    /// <summary>
    /// Returns true when at least one query filter flag is present — the caller
    /// uses this to decide whether to default to an overview dump.
    /// </summary>
    public bool HasAnyQueryFilter =>
        (Taxiways.Count > 0)
        || (Runways.Count > 0)
        || (NodeIds.Count > 0)
        || (ExitsRunways.Count > 0)
        || (BfsNodeId is not null)
        || (PathfinderNodeId is not null)
        || ShowParking
        || ShowSpots
        || Validate
        || (IntersectionTaxiway1 is not null);
}
