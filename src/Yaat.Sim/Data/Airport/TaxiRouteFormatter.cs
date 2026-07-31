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
    /// One entry per name change along the route. <paramref name="Segment"/> is the segment the name
    /// was first picked on, so a caller can classify the leg (e.g. render a runway as "on 28R").
    /// </summary>
    public readonly record struct TaxiwayLeg(string Name, bool IsRunway, TaxiRouteSegment Segment);

    /// <summary>
    /// The ordered, consecutive-deduped legs the route traverses. Composite junction labels are
    /// decomposed into their members and the walk <em>stays</em> on the name it is already emitting
    /// whenever the edge belongs to it — an arc is a transition between taxiways, not a leg of one, so
    /// it must never surface as its own token. Ramp edges are dropped (a parking / spot destination
    /// names the ramp far better than the word "RAMP"); runway legs are kept and flagged, since a
    /// runway taxied along is part of the clearance.
    ///
    /// <para>Shared by <see cref="CleanTaxiwaySequence"/> and <see cref="TaxiRoute.ToSummary()"/> so
    /// the readable command form, the TAXI readback, and the aircraft-list route string cannot drift
    /// apart.</para>
    /// </summary>
    public static List<TaxiwayLeg> TaxiwayLegs(TaxiRoute route)
    {
        var legs = new List<TaxiwayLeg>();
        foreach (var seg in route.Segments)
        {
            var members = TaxiwayMembers(seg.Edge.Edge);
            if (members.Count == 0)
            {
                continue;
            }

            string? current = legs.Count > 0 ? legs[^1].Name : null;
            string pick = (current is not null) && members.Contains(current, StringComparer.OrdinalIgnoreCase) ? current : members[0];
            if (!string.Equals(current, pick, StringComparison.OrdinalIgnoreCase))
            {
                legs.Add(new TaxiwayLeg(pick, IsRunwayName(pick), seg));
            }
        }

        return legs;
    }

    /// <summary>
    /// The ordered, consecutive-deduped sequence of real taxiway names the route traverses — the
    /// legs of <see cref="TaxiwayLegs"/> with runways removed, for the readable <c>TAXI</c> form.
    /// </summary>
    public static List<string> CleanTaxiwaySequence(TaxiRoute route)
    {
        var names = new List<string>();
        foreach (var leg in TaxiwayLegs(route))
        {
            string? last = names.Count > 0 ? names[^1] : null;
            if (!leg.IsRunway && !string.Equals(leg.Name, last, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(leg.Name);
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

    /// <summary>True for the internal combined runway-centerline name (<c>"RWY28R/10L"</c>).</summary>
    internal static bool IsRunwayName(string name) => name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The taxiway names an edge belongs to, ramp dropped and real taxiways ordered ahead of runways.
    /// The ordering matters for a runway-crossing arc (<c>["H", "RWY01L/19R"]</c>) reached from neither
    /// of its members: that arc <em>continues</em> H across the runway, so H is the name to show — the
    /// aircraft is not being cleared to taxi along 01L.
    /// </summary>
    private static List<string> TaxiwayMembers(IGroundEdge edge)
    {
        string[] raw = edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];
        var result = new List<string>();
        foreach (var name in raw)
        {
            if (!string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase) && !IsRunwayName(name))
            {
                result.Add(name);
            }
        }

        foreach (var name in raw)
        {
            if (IsRunwayName(name))
            {
                result.Add(name);
            }
        }

        return result;
    }
}
