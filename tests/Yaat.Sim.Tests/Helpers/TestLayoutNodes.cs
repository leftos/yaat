using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Resolves layout nodes by the taxiway/runway they belong to rather than by id.
///
/// Node ids are geometry-coupled: fillet generation and runway-crossing detection assign them from a running counter,
/// so they renumber whenever the layout is regenerated or a runway's dimensions change. A test that hardcodes an id
/// silently starts asserting against a different node. Look nodes up by name instead — see
/// <see cref="AirportGroundLayout.FindIntersectionNode"/> for the taxiway-junction equivalent.
/// </summary>
internal static class TestLayoutNodes
{
    /// <summary>
    /// Every hold-short node for <paramref name="runwayId"/> that sits on taxiway <paramref name="taxiway"/>,
    /// nearest-first is not guaranteed — callers wanting one node should have an unambiguous taxiway.
    /// </summary>
    internal static List<GroundNode> RunwayHoldShortsOnTaxiway(AirportGroundLayout layout, string runwayId, string taxiway)
    {
        List<GroundNode> matches = [];

        foreach (var node in layout.GetRunwayHoldShortNodes(runwayId))
        {
            foreach (var edge in node.Edges)
            {
                if (string.Equals(edge.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(node);
                    break;
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// The single hold-short node for <paramref name="runwayId"/> on taxiway <paramref name="taxiway"/>, or null when
    /// the layout has none (a fresh/offline checkout, or a taxiway that does not meet that runway).
    /// </summary>
    internal static GroundNode? RunwayHoldShortOnTaxiway(AirportGroundLayout layout, string runwayId, string taxiway)
    {
        var matches = RunwayHoldShortsOnTaxiway(layout, runwayId, taxiway);
        return matches.Count > 0 ? matches[0] : null;
    }
}
