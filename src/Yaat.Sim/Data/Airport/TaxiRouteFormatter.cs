namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Turns a resolved <see cref="TaxiRoute"/> back into the human-readable taxiway-name form a
/// controller would issue. Junction edges in the ground graph carry a composite membership label
/// (a <see cref="GroundArc"/> with <c>TaxiwayNames = ["W", "W6"]</c> renders as <c>"W - W6"</c>),
/// so a naive walk of <see cref="TaxiRouteSegment.TaxiwayName"/> emits invalid tokens like
/// <c>"W - W6"</c>. This decomposes junctions into single taxiway names.
/// </summary>
public static class TaxiRouteFormatter
{
    /// <summary>
    /// The ordered, consecutive-deduped sequence of real taxiway names the route traverses.
    /// A junction keeps the current taxiway when it continues onto it; otherwise it advances to the
    /// new one. Runway and ramp names are dropped.
    /// </summary>
    public static List<string> CleanTaxiwaySequence(TaxiRoute route)
    {
        var names = new List<string>();
        foreach (var seg in route.Segments)
        {
            var members = CleanTaxiwayMembers(seg.Edge.Edge);
            if (members.Count == 0)
            {
                continue;
            }

            var current = names.Count > 0 ? names[^1] : null;
            var pick = (current is not null) && members.Contains(current, StringComparer.OrdinalIgnoreCase) ? current : members[0];
            if (!string.Equals(current, pick, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(pick);
            }
        }

        return names;
    }

    /// <summary>
    /// The readable path tokens for a drawn route: clean taxiway names, plus a terminal node-ref
    /// (<c>#NNNN</c>) to pin a mid-taxiway stop. Taxiway names alone have no stop-point semantics, so
    /// the server would run the aircraft to the natural end of the last named taxiway; the terminal
    /// pin holds it at the drawn endpoint. When the route ends at a named terminus (spot / parking /
    /// runway) pass <paramref name="hasNamedTerminus"/> true — that token pins the stop, so no
    /// node-ref is appended.
    /// </summary>
    public static string BuildReadableTaxiPath(TaxiRoute route, bool hasNamedTerminus)
    {
        var path = string.Join(" ", CleanTaxiwaySequence(route));
        if (hasNamedTerminus || route.Segments.Count == 0)
        {
            return path;
        }

        var endNode = route.Segments[^1].ToNodeId;
        return string.IsNullOrEmpty(path) ? $"#{endNode}" : $"{path} #{endNode}";
    }

    private static List<string> CleanTaxiwayMembers(IGroundEdge edge)
    {
        string[] raw = edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];
        var result = new List<string>();
        foreach (var name in raw)
        {
            if (!name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(name);
            }
        }

        return result;
    }
}
