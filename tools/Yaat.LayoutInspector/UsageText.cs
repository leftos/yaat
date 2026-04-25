namespace Yaat.LayoutInspector;

/// <summary>
/// Command-line help text for Yaat.LayoutInspector. Kept separate from Program.cs
/// so the entry point stays a thin dispatcher.
/// </summary>
public static class UsageText
{
    public static void Print()
    {
        Console.WriteLine("Usage: Yaat.LayoutInspector <geojson-path> [flags]");
        Console.WriteLine();
        Console.WriteLine("Flags:");
        Console.WriteLine("  --taxiway <name>         Show nodes/edges for a taxiway");
        Console.WriteLine("  --runway <designator>    Show centerline/hold-shorts for a runway");
        Console.WriteLine("  --node <id>[,<id>...]    Show detail for one or more nodes (also repeatable)");
        Console.WriteLine("  --exits <designator>     Show all exits for a runway (BFS, repeatable)");
        Console.WriteLine("  --bfs <node-id> <twy>    BFS trace from node through taxiway to hold-short");
        Console.WriteLine("  --pathfinder <node-id> <twy1> [twy2 ...]  Resolve taxi route with diagnostic trace");
        Console.WriteLine("  --pf-dest-rwy <runway>   Destination runway for pathfinder (matches runtime ExplicitPathOptions.DestinationRunway)");
        Console.WriteLine("  --pf-hold-shorts <list>  Comma-separated hold-short targets (e.g. 1L,B) for pathfinder");
        Console.WriteLine(
            "  --pf-dest-parking <name> Destination parking/helipad name for pathfinder (e.g. NEW1); reproduces TAXI <tw...> @<parking>"
        );
        Console.WriteLine("  --pf-dest-spot <name>    Destination spot name for pathfinder; reproduces TAXI <tw...> $<spot>");
        Console.WriteLine("  --pf-dest-node <id>      Raw destination node id for pathfinder (escape hatch)");
        Console.WriteLine("  --parking                Show all parking nodes");
        Console.WriteLine("  --spots                  Show all spot/named nodes");
        Console.WriteLine("  --intersection <T1> <T2> Show nodes where two taxiways meet");
        Console.WriteLine("  --validate               Run validation and print warnings to stdout");
        Console.WriteLine("  --no-fillets             Skip fillet arc generation (unfilleted graph for comparison)");
        Console.WriteLine("  --debug-fillets          Enable debug logging for FilletArcGenerator");
        Console.WriteLine("  --dump                   Dump everything (nodes, taxiways, runways, exits) as JSON");
        Console.WriteLine("  --json                   Output as JSON");
        Console.WriteLine("  --airport-code <ICAO>    Airport code for NavData runway widths");
        Console.WriteLine("  --navdata <dir>          Path to NavData.dat + FAACIFP18.gz directory");
        Console.WriteLine();
        Console.WriteLine("HTML output:");
        Console.WriteLine("  --html <path>            Render interactive HTML map to file (pan/zoom/tooltips)");
        Console.WriteLine("  --html-taxiway <name>    Highlight a taxiway (repeatable)");
        Console.WriteLine("  --html-runway <desig>    Highlight a runway (repeatable)");
        Console.WriteLine("  --html-node <id>         Highlight a node (repeatable)");
        Console.WriteLine("  --html-annotate <id> <text>  Add annotation label to a node");
        Console.WriteLine("  --html-route <ids>       Highlight a comma-separated route of node ids");
        Console.WriteLine("  --ticks <csv>            Overlay tick data (CSV from TickRecorder) with animation player");
        Console.WriteLine();
        Console.WriteLine("Tick-table output:");
        Console.WriteLine("  --tick-table             Compact per-tick table to stdout (requires --ticks)");
        Console.WriteLine("  --tick-summary           Per-segment summary to stdout (requires --ticks)");
        Console.WriteLine("  --tick-range START-END   Filter ticks to a range (inclusive)");
        Console.WriteLine("  --tick-ref ICAO/RWY      Reference runway for xteFt / hdgErr columns");
        Console.WriteLine("  --tick-hold-shorts K,D,Q Add along-track distance columns to named hold-shorts (requires --tick-ref)");
    }
}
