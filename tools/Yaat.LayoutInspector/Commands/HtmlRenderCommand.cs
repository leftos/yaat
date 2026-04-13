using Yaat.LayoutInspector.Tick;
using Yaat.Sim.Data.Airport;

namespace Yaat.LayoutInspector.Commands;

/// <summary>
/// Renders the layout to an interactive HTML page (via <see cref="HtmlRenderer"/>).
/// Honors the full set of --svg-* highlight flags and, when --ticks is present,
/// overlays tick data with an animation player.
/// </summary>
public sealed class HtmlRenderCommand : ICommand
{
    public int Execute(LayoutAnalyzer analyzer, CliOptions options)
    {
        string svgOutput = options.SvgOutputPath!;
        var htmlRenderer = new HtmlRenderer(analyzer.Layout);

        foreach (string t in options.SvgHighlightTaxiways)
        {
            htmlRenderer.HighlightTaxiway(t);
        }

        foreach (string r in options.SvgHighlightRunways)
        {
            htmlRenderer.HighlightRunway(r);
        }

        foreach (int n in options.SvgHighlightNodes)
        {
            htmlRenderer.HighlightNode(n);
        }

        foreach (var (nid, text) in options.SvgAnnotations)
        {
            htmlRenderer.AnnotateNode(nid, text);
        }

        foreach (int nid in options.SvgRouteNodes)
        {
            htmlRenderer.AddRouteNode(nid);
        }

        // --pathfinder route is also painted onto the HTML render when both flags
        // are present: resolve it here (non-diagnostic path) and emit the traversed
        // node ids as route overlays.
        if ((options.PathfinderNodeId is not null) && (options.PathfinderTaxiways.Count > 0))
        {
            var pfRoute = TaxiPathfinder.ResolveExplicitPath(
                analyzer.Layout,
                options.PathfinderNodeId.Value,
                options.PathfinderTaxiways,
                out string? _,
                new ExplicitPathOptions()
            );
            if (pfRoute is not null)
            {
                var routeNodeIds = new HashSet<int>();
                foreach (var seg in pfRoute.Segments)
                {
                    routeNodeIds.Add(seg.FromNodeId);
                    routeNodeIds.Add(seg.ToNodeId);
                }

                foreach (int nid in routeNodeIds)
                {
                    htmlRenderer.AddRouteNode(nid);
                }
            }
        }

        if (options.TicksCsvPath is not null)
        {
            var ticks = TickCsvReader.Read(options.TicksCsvPath);
            htmlRenderer.SetTickData(ticks);
            Console.Error.WriteLine($"Loaded {ticks.Count} ticks from {options.TicksCsvPath}");
        }

        string html = htmlRenderer.Render();
        File.WriteAllText(svgOutput, html);
        Console.Error.WriteLine($"Wrote interactive HTML to {svgOutput}");
        return 0;
    }
}
