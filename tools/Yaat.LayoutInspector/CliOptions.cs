using Yaat.Sim.Data.Airport;

namespace Yaat.LayoutInspector;

/// <summary>
/// Parsed command-line options for Yaat.LayoutInspector. All collection fields are
/// populated during parsing and then frozen; callers should treat the record as
/// read-only after <see cref="TryParse"/> returns.
/// </summary>
public sealed record CliOptions
{
    /// <summary>
    /// Path to a GeoJSON file on disk. Null when <see cref="DownloadAirportId"/> is set;
    /// Program.cs resolves the downloaded cache path before passing the options on.
    /// </summary>
    public string? GeoJsonPath { get; init; }

    /// <summary>
    /// FAA code (e.g. <c>OAK</c>) requested via <c>--airport</c>. When set, the layout is
    /// fetched from the vNAS training-airports API and cached under
    /// <c>%LOCALAPPDATA%/yaat/cache/airports/</c>. Mutually exclusive with a positional path.
    /// Note: distinct from <see cref="AirportCode"/>, which is the <c>--airport-code</c> override
    /// used during GeoJSON parsing.
    /// </summary>
    public string? DownloadAirportId { get; init; }

    public string? AirportCode { get; init; }
    public string? NavDataDir { get; init; }

    public List<string> Taxiways { get; init; } = [];
    public List<string> Runways { get; init; } = [];
    public List<int> NodeIds { get; init; } = [];
    public List<string> ExitsRunways { get; init; } = [];

    public int? BfsNodeId { get; init; }
    public string? BfsTaxiway { get; init; }

    /// <summary>
    /// When >0, expand each <see cref="NodeIds"/> entry to also include every node
    /// reachable within this many graph hops. Useful for dumping a fillet cluster
    /// without listing every member id by hand. Set via <c>--node-depth N</c>.
    /// </summary>
    public int NodeDepth { get; init; }

    /// <summary>
    /// When set, each printed <c>--node</c> also gets a pairwise fan/turn-angle breakdown of its
    /// edges plus the bridging-taxiway between each pair (the alternate path avoiding the node).
    /// Diagnoses un-filletable corners and redundant corner-chords. Set via <c>--node-angles</c>.
    /// </summary>
    public bool NodeAngles { get; init; }

    public int? WalkTraceNodeId { get; init; }
    public string? WalkTraceTaxiway { get; init; }

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

    /// <summary>
    /// Start node id for <c>--auto-route</c> — runs the same auto-route resolution
    /// that <c>TAXIAUTO &lt;RWY&gt;</c> uses (full-length lineup hold-short + A*).
    /// Paired with <see cref="AutoRouteRunway"/>.
    /// </summary>
    public int? AutoRouteNodeId { get; init; }

    /// <summary>
    /// Destination runway designator for <c>--auto-route</c>. Used together with
    /// <see cref="AutoRouteNodeId"/> to print the resolved auto-route.
    /// </summary>
    public string? AutoRouteRunway { get; init; }

    public bool ShowParking { get; init; }
    public bool ShowSpots { get; init; }
    public bool Validate { get; init; }

    public string? IntersectionTaxiway1 { get; init; }
    public string? IntersectionTaxiway2 { get; init; }

    /// <summary>Node-id pair for <c>--distance N1 N2</c> — great-circle straight-line distance between two nodes.</summary>
    public int? DistanceFromNodeId { get; init; }
    public int? DistanceToNodeId { get; init; }

    /// <summary>
    /// Node sequence for <c>--path-distance N1 N2 N3 …</c> — cumulative travel distance, using graph edges
    /// (arc-aware) where they exist and great-circle as a fallback. Accepts repeated args and CSV.
    /// </summary>
    public List<int> PathDistanceNodes { get; init; } = [];

    public bool JsonOutput { get; init; }
    public bool DumpAll { get; init; }

    public FilletMode FilletMode { get; init; } = FilletMode.V2;
    public bool DebugFillets { get; init; }
    public bool DebugExits { get; init; }

    public List<(string Runway, string Taxiway, string? Side)> ExitQueries { get; init; } = [];

    public string? HtmlOutputPath { get; init; }
    public string? TicksJsonPath { get; init; }

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
    /// When set, tick-table filters to a single callsign. Required when the
    /// JSON contains multiple aircraft and the user wants a single-aircraft
    /// view; left null to print one block per aircraft in the JSON.
    /// </summary>
    public string? TickCallsign { get; init; }

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
            error = "missing <geojson-path> or --airport <code>";
            return false;
        }

        // First arg is the positional <geojson-path> unless it's a flag. When the user
        // invokes with --airport <CODE>, the path is resolved later from the cache.
        string? geoJsonPath = args[0].StartsWith("--") ? null : args[0];
        int startIndex = (geoJsonPath is null) ? 0 : 1;

        // Mutable scratch state used while walking the arg list. Copied into the
        // frozen record at the end.
        string? downloadAirportId = null;
        string? airportCode = null;
        string? navdataDir = null;
        var taxiways = new List<string>();
        var runways = new List<string>();
        var nodeIds = new List<int>();
        var exitsRunways = new List<string>();
        int? pathNodeId = null;
        string? pathTaxiway = null;
        int nodeDepth = 0;
        bool nodeAngles = false;
        int? walkTraceNodeId = null;
        string? walkTraceTaxiway = null;
        int? pfNodeId = null;
        var pfTaxiways = new List<string>();
        string? pfDestRwy = null;
        var pfHoldShorts = new List<string>();
        string? pfDestParking = null;
        string? pfDestSpot = null;
        int? pfDestNodeId = null;
        int? autoRouteNodeId = null;
        string? autoRouteRunway = null;
        bool showParking = false;
        bool showSpots = false;
        bool validate = false;
        string? intersectTwy1 = null;
        string? intersectTwy2 = null;
        int? distanceFrom = null;
        int? distanceTo = null;
        var pathDistanceNodes = new List<int>();
        bool jsonOutput = false;
        bool dumpAll = false;
        var filletMode = FilletMode.V2;
        bool debugFillets = false;
        bool debugExits = false;
        var exitQueries = new List<(string Runway, string Taxiway, string? Side)>();
        string? htmlOutput = null;
        string? ticksJsonPath = null;
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
        string? tickCallsign = null;

        for (int i = startIndex; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--airport" when i + 1 < args.Length:
                    downloadAirportId = args[++i];
                    break;
                case "--airport-code" when i + 1 < args.Length:
                    airportCode = args[++i];
                    break;
                case "--navdata" when i + 1 < args.Length:
                    navdataDir = args[++i];
                    break;
                case "--taxiway" when i + 1 < args.Length:
                    foreach (string twToken in SplitCsv(args[++i]))
                    {
                        taxiways.Add(twToken.ToUpperInvariant());
                    }

                    break;
                case "--runway" when i + 1 < args.Length:
                    foreach (string rwToken in SplitCsv(args[++i]))
                    {
                        runways.Add(rwToken.ToUpperInvariant());
                    }

                    break;
                case "--node" when i + 1 < args.Length:
                    foreach (string nodeToken in SplitCsv(args[++i]))
                    {
                        nodeIds.Add(int.Parse(nodeToken));
                    }

                    break;
                case "--node-depth" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out int depth) || depth < 0)
                    {
                        error = "--node-depth expects a non-negative integer";
                        return false;
                    }

                    nodeDepth = depth;
                    break;
                case "--node-angles":
                    nodeAngles = true;
                    break;
                case "--exits" when i + 1 < args.Length:
                    foreach (string exToken in SplitCsv(args[++i]))
                    {
                        exitsRunways.Add(exToken.ToUpperInvariant());
                    }

                    break;
                case "--bfs" when i + 2 < args.Length:
                    pathNodeId = int.Parse(args[++i]);
                    pathTaxiway = args[++i].ToUpperInvariant();
                    break;
                case "--walk-trace" when i + 2 < args.Length:
                    walkTraceNodeId = int.Parse(args[++i]);
                    walkTraceTaxiway = args[++i].ToUpperInvariant();
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
                case "--distance" when i + 2 < args.Length:
                    if (!int.TryParse(args[++i], out int distFrom) || !int.TryParse(args[++i], out int distTo))
                    {
                        error = "--distance expects two integer node ids";
                        return false;
                    }

                    distanceFrom = distFrom;
                    distanceTo = distTo;
                    break;
                case "--path-distance" when i + 1 < args.Length:
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        foreach (string pdToken in SplitCsv(args[++i]))
                        {
                            if (!int.TryParse(pdToken, out int pdNode))
                            {
                                error = $"--path-distance expects integer node ids, got '{pdToken}'";
                                return false;
                            }

                            pathDistanceNodes.Add(pdNode);
                        }
                    }

                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "--dump":
                    dumpAll = true;
                    jsonOutput = true;
                    break;
                case "--no-fillets":
                    filletMode = FilletMode.None;
                    break;
                case "--fillet-mode" when i + 1 < args.Length:
                    filletMode = ParseFilletMode(args[++i]);
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
                    ticksJsonPath = args[++i];
                    break;
                case "--html-taxiway" when i + 1 < args.Length:
                    foreach (string ht in SplitCsv(args[++i]))
                    {
                        htmlHighlightTaxiways.Add(ht.ToUpperInvariant());
                    }

                    break;
                case "--html-runway" when i + 1 < args.Length:
                    foreach (string hr in SplitCsv(args[++i]))
                    {
                        htmlHighlightRunways.Add(hr.ToUpperInvariant());
                    }

                    break;
                case "--html-node" when i + 1 < args.Length:
                    foreach (string hn in SplitCsv(args[++i]))
                    {
                        htmlHighlightNodes.Add(int.Parse(hn));
                    }

                    break;
                case "--html-annotate" when i + 2 < args.Length:
                    htmlAnnotations.Add((int.Parse(args[++i]), args[++i]));
                    break;
                case "--html-route" when i + 1 < args.Length:
                    foreach (string nid in SplitCsv(args[++i]))
                    {
                        htmlRouteNodes.Add(int.Parse(nid));
                    }

                    break;
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
                    foreach (string hs in SplitCsv(args[++i]))
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
                case "--auto-route" when i + 2 < args.Length:
                    if (!int.TryParse(args[++i], out int autoNode))
                    {
                        error = "--auto-route expects: <start-node-id> <runway>";
                        return false;
                    }

                    autoRouteNodeId = autoNode;
                    autoRouteRunway = args[++i].ToUpperInvariant();
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
                    foreach (string twy in SplitCsv(args[++i]))
                    {
                        tickHoldShorts.Add(twy);
                    }

                    break;
                case "--tick-callsign" when i + 1 < args.Length:
                    tickCallsign = args[++i].ToUpperInvariant();
                    break;
                default:
                    error = $"Unknown flag: {args[i]}";
                    return false;
            }
        }

        if ((geoJsonPath is null) && (downloadAirportId is null))
        {
            error = "missing <geojson-path> or --airport <code>";
            return false;
        }

        if ((geoJsonPath is not null) && (downloadAirportId is not null))
        {
            error = "<geojson-path> and --airport are mutually exclusive";
            return false;
        }

        options = new CliOptions
        {
            GeoJsonPath = geoJsonPath,
            DownloadAirportId = downloadAirportId,
            AirportCode = airportCode,
            NavDataDir = navdataDir,
            Taxiways = taxiways,
            Runways = runways,
            NodeIds = nodeIds,
            ExitsRunways = exitsRunways,
            BfsNodeId = pathNodeId,
            BfsTaxiway = pathTaxiway,
            NodeDepth = nodeDepth,
            NodeAngles = nodeAngles,
            WalkTraceNodeId = walkTraceNodeId,
            WalkTraceTaxiway = walkTraceTaxiway,
            PathfinderNodeId = pfNodeId,
            PathfinderTaxiways = pfTaxiways,
            PathfinderDestinationRunway = pfDestRwy,
            PathfinderHoldShorts = pfHoldShorts,
            PathfinderDestParking = pfDestParking,
            PathfinderDestSpot = pfDestSpot,
            PathfinderDestNodeId = pfDestNodeId,
            AutoRouteNodeId = autoRouteNodeId,
            AutoRouteRunway = autoRouteRunway,
            ShowParking = showParking,
            ShowSpots = showSpots,
            Validate = validate,
            IntersectionTaxiway1 = intersectTwy1,
            IntersectionTaxiway2 = intersectTwy2,
            DistanceFromNodeId = distanceFrom,
            DistanceToNodeId = distanceTo,
            PathDistanceNodes = pathDistanceNodes,
            JsonOutput = jsonOutput,
            DumpAll = dumpAll,
            FilletMode = filletMode,
            DebugFillets = debugFillets,
            DebugExits = debugExits,
            ExitQueries = exitQueries,
            HtmlOutputPath = htmlOutput,
            TicksJsonPath = ticksJsonPath,
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
            TickCallsign = tickCallsign,
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
        || (WalkTraceNodeId is not null)
        || (PathfinderNodeId is not null)
        || (AutoRouteNodeId is not null)
        || ShowParking
        || ShowSpots
        || Validate
        || (IntersectionTaxiway1 is not null)
        || (DistanceFromNodeId is not null)
        || (PathDistanceNodes.Count > 0);

    private static IEnumerable<string> SplitCsv(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static FilletMode ParseFilletMode(string value)
    {
        switch (value.ToLowerInvariant())
        {
            case "v2":
                return FilletMode.V2;
            case "none":
                return FilletMode.None;
            default:
                Console.Error.WriteLine($"Unknown --fillet-mode '{value}', using v2 (expected: v2, none)");
                return FilletMode.V2;
        }
    }
}
