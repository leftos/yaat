using Yaat.LayoutInspector.Tick;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.LayoutInspector.Commands;

/// <summary>
/// Renders the layout to an interactive HTML page (via <see cref="HtmlRenderer"/>).
/// Honors the full set of --html-* highlight flags and, when --ticks is present,
/// overlays tick data with an animation player.
/// </summary>
public sealed class HtmlRenderCommand : ICommand
{
    public int Execute(LayoutAnalyzer analyzer, CliOptions options)
    {
        string htmlOutput = options.HtmlOutputPath!;
        var htmlRenderer = new HtmlRenderer(analyzer.Layout);

        foreach (string t in options.HtmlHighlightTaxiways)
        {
            htmlRenderer.HighlightTaxiway(t);
        }

        foreach (string r in options.HtmlHighlightRunways)
        {
            htmlRenderer.HighlightRunway(r);
        }

        foreach (int n in options.HtmlHighlightNodes)
        {
            htmlRenderer.HighlightNode(n);
        }

        foreach (var (nid, text) in options.HtmlAnnotations)
        {
            htmlRenderer.AnnotateNode(nid, text);
        }

        foreach (int nid in options.HtmlRouteNodes)
        {
            htmlRenderer.AddRouteNode(nid);
        }

        // --pathfinder route is also painted onto the HTML render when both flags
        // are present: resolve it here (non-diagnostic path) and emit the traversed
        // node ids as route overlays.
        if ((options.PathfinderNodeId is not null) && (options.PathfinderTaxiways.Count > 0))
        {
            var pfRoute = TaxiPathfinderV2.ResolveExplicitPath(
                analyzer.Layout,
                options.PathfinderNodeId.Value,
                options.PathfinderTaxiways,
                out string? _,
                new ExplicitPathOptions
                {
                    DestinationRunway = options.PathfinderDestinationRunway,
                    ExplicitHoldShorts = options.PathfinderHoldShorts.Count > 0 ? options.PathfinderHoldShorts : null,
                    AirportId = analyzer.AirportId,
                },
                AircraftCategory.Jet
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

        if (options.TicksJsonPath is not null)
        {
            var recording = TickJsonReader.Read(options.TicksJsonPath);
            if (recording is null)
            {
                Console.Error.WriteLine($"warning: --ticks {options.TicksJsonPath} is empty or unreadable; HTML will render without tick overlay");
            }
            else
            {
                htmlRenderer.SetTickRecording(recording);
                Console.Error.WriteLine(
                    $"Loaded {recording.Ticks.Count} tick events for {recording.Aircraft.Count} aircraft from {options.TicksJsonPath}"
                );
            }
        }

        string html = htmlRenderer.Render();
        File.WriteAllText(htmlOutput, html);
        Console.Error.WriteLine($"Wrote interactive HTML to {htmlOutput}");
        return 0;
    }
}
