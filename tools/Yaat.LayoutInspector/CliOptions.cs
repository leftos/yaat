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

    /// <summary>
    /// Destination runway passed to <c>ExplicitPathOptions.DestinationRunway</c>.
    /// Drives <c>WalkTaxiway</c>'s effective-hint steering at ambiguous junctions —
    /// without it, LI can pick a different route than runtime. Set via <c>--pf-dest-rwy</c>.
    /// </summary>
    public string? PathfinderDestinationRunway { get; init; }

    /// <summary>
    /// Explicit hold-short targets passed to <c>ExplicitPathOptions.ExplicitHoldShorts</c>.
    /// Each entry is a bare target string — runtime matches RHS nodes whose RunwayId
    /// contains it, then falls back to first-taxiway-intersection. Set via <c>--pf-hold-shorts</c>.
    /// </summary>
    public List<string> PathfinderHoldShorts { get; init; } = [];

    /// <summary>
    /// Destination parking/helipad name for <c>ExplicitPathOptions.DestinationHintNode</c>.
    /// Resolved via <c>FindHelipadByName</c> ?? <c>FindParkingByName</c>. Mirrors the
    /// runtime <c>TAXI &lt;tw...&gt; @&lt;parking&gt;</c> command. Set via <c>--pf-dest-parking</c>.
    /// </summary>
    public string? PathfinderDestParking { get; init; }

    /// <summary>
    /// Destination spot name for <c>ExplicitPathOptions.DestinationHintNode</c>.
    /// Resolved via <c>FindSpotNodeByName</c>. Mirrors the runtime
    /// <c>TAXI &lt;tw...&gt; $&lt;spot&gt;</c> command. Set via <c>--pf-dest-spot</c>.
    /// </summary>
    public string? PathfinderDestSpot { get; init; }

    /// <summary>
    /// Raw destination node id for <c>ExplicitPathOptions.DestinationHintNode</c>.
    /// Escape hatch when the destination has no parking/spot name. Set via <c>--pf-dest-node</c>.
    /// </summary>
    public int? PathfinderDestNodeId { get; init; }

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

    public string? HtmlOutputPath { get; init; }
    public string? TicksCsvPath { get; init; }

    /// <summary>
    /// Aircraft fuselage length in feet for 1:1 rendering of the aircraft
    /// silhouette at tick positions. Default 110 ft ≈ narrow-body jet
    /// (B737/A320). Set via <c>--tick-aircraft-length-ft</c>.
    /// </summary>
    public double TickAircraftLengthFt { get; init; } = 110.0;

    /// <summary>
    /// Aircraft wingspan in feet for 1:1 rendering. Default 110 ft matches
    /// a typical narrow-body span. Set via <c>--tick-aircraft-wingspan-ft</c>.
    /// </summary>
    public double TickAircraftWingspanFt { get; init; } = 110.0;

    public List<string> HtmlHighlightTaxiways { get; init; } = [];
    public List<string> HtmlHighlightRunways { get; init; } = [];
    public List<int> HtmlHighlightNodes { get; init; } = [];
    public List<(int NodeId, string Text)> HtmlAnnotations { get; init; } = [];
    public List<int> HtmlRouteNodes { get; init; } = [];

    // --- Tick-table output mode (see Commands/TickTableCommand) ---
    public bool TickTable { get; init; }
    public bool TickSummary { get; init; }
    public (int Lo, int Hi)? TickRange { get; init; }
    public string? TickRefRunway { get; init; }
    public List<string> TickHoldShorts { get; init; } = [];

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
        string? pfDestRwy = null;
        var pfHoldShorts = new List<string>();
        string? pfDestParking = null;
        string? pfDestSpot = null;
        int? pfDestNodeId = null;
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
        string? htmlOutput = null;
        string? ticksCsvPath = null;
        var htmlHighlightTaxiways = new List<string>();
        var htmlHighlightRunways = new List<string>();
        var htmlHighlightNodes = new List<int>();
        var htmlAnnotations = new List<(int NodeId, string Text)>();
        var htmlRouteNodes = new List<int>();
        bool tickTable = false;
        bool tickSummary = false;
        (int Lo, int Hi)? tickRange = null;
        string? tickRefRunway = null;
        var tickHoldShorts = new List<string>();
        double tickAircraftLengthFt = 110.0;
        double tickAircraftWingspanFt = 110.0;

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
                case "--html" when i + 1 < args.Length:
                    htmlOutput = args[++i];
                    break;
                case "--ticks" when i + 1 < args.Length:
                    ticksCsvPath = args[++i];
                    break;
                case "--html-taxiway" when i + 1 < args.Length:
                    htmlHighlightTaxiways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--html-runway" when i + 1 < args.Length:
                    htmlHighlightRunways.Add(args[++i].ToUpperInvariant());
                    break;
                case "--html-node" when i + 1 < args.Length:
                    htmlHighlightNodes.Add(int.Parse(args[++i]));
                    break;
                case "--html-annotate" when i + 2 < args.Length:
                    htmlAnnotations.Add((int.Parse(args[++i]), args[++i]));
                    break;
                case "--html-route" when i + 1 < args.Length:
                {
                    foreach (string nid in args[++i].Split(','))
                    {
                        htmlRouteNodes.Add(int.Parse(nid.Trim()));
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
                case "--pf-dest-rwy" when i + 1 < args.Length:
                    pfDestRwy = args[++i].ToUpperInvariant();
                    break;
                case "--pf-hold-shorts" when i + 1 < args.Length:
                    foreach (string hs in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        pfHoldShorts.Add(hs.ToUpperInvariant());
                    }

                    break;
                case "--pf-dest-parking" when i + 1 < args.Length:
                    pfDestParking = args[++i];
                    break;
                case "--pf-dest-spot" when i + 1 < args.Length:
                    pfDestSpot = args[++i];
                    break;
                case "--pf-dest-node" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int pfDestNode))
                    {
                        error = "--pf-dest-node expects an integer node id";
                        return false;
                    }

                    pfDestNodeId = pfDestNode;
                    break;
                case "--tick-table":
                    tickTable = true;
                    break;
                case "--tick-summary":
                    tickSummary = true;
                    break;
                case "--tick-range" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split('-');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out int lo) || !int.TryParse(parts[1], out int hi))
                    {
                        error = $"--tick-range expects 'START-END', got {args[i]}";
                        return false;
                    }

                    tickRange = (lo, hi);
                    break;
                }
                case "--tick-ref" when i + 1 < args.Length:
                    tickRefRunway = args[++i];
                    break;
                case "--tick-hold-shorts" when i + 1 < args.Length:
                    foreach (string twy in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        tickHoldShorts.Add(twy);
                    }

                    break;
                case "--tick-aircraft-length-ft" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out tickAircraftLengthFt))
                    {
                        error = "--tick-aircraft-length-ft expects a numeric value";
                        return false;
                    }

                    break;
                case "--tick-aircraft-wingspan-ft" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out tickAircraftWingspanFt))
                    {
                        error = "--tick-aircraft-wingspan-ft expects a numeric value";
                        return false;
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
            PathfinderDestinationRunway = pfDestRwy,
            PathfinderHoldShorts = pfHoldShorts,
            PathfinderDestParking = pfDestParking,
            PathfinderDestSpot = pfDestSpot,
            PathfinderDestNodeId = pfDestNodeId,
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
            HtmlOutputPath = htmlOutput,
            TicksCsvPath = ticksCsvPath,
            HtmlHighlightTaxiways = htmlHighlightTaxiways,
            HtmlHighlightRunways = htmlHighlightRunways,
            HtmlHighlightNodes = htmlHighlightNodes,
            HtmlAnnotations = htmlAnnotations,
            HtmlRouteNodes = htmlRouteNodes,
            TickTable = tickTable,
            TickSummary = tickSummary,
            TickRange = tickRange,
            TickRefRunway = tickRefRunway,
            TickHoldShorts = tickHoldShorts,
            TickAircraftLengthFt = tickAircraftLengthFt,
            TickAircraftWingspanFt = tickAircraftWingspanFt,
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
