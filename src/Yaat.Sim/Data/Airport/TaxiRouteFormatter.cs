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
    ///
    /// <para>A runway leg the route only crosses (<see cref="IsCrossingLeg"/>) is dropped: it is a crossing, never a
    /// runway taxied along, so it must not read as "on 33" (OAK D → F across a 100 ft jog of 15/33).</para>
    /// </summary>
    public static List<TaxiwayLeg> TaxiwayLegs(TaxiRoute route)
    {
        var walked = new List<(TaxiwayLeg Leg, int FirstSegment)>();
        for (int i = 0; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            List<string> members = TaxiwayMembers(seg.Edge.Edge);
            if (members.Count == 0)
            {
                continue;
            }

            string? current = walked.Count > 0 ? walked[^1].Leg.Name : null;
            string pick = (current is not null) && members.Contains(current, StringComparer.OrdinalIgnoreCase) ? current : members[0];
            if (!string.Equals(current, pick, StringComparison.OrdinalIgnoreCase))
            {
                walked.Add((new TaxiwayLeg(pick, IsRunwayName(pick), seg), i));
            }
            else if ((i == route.Segments.Count - 1) && (FinalArcOtherTaxiway(seg.Edge.Edge, pick) is { } other))
            {
                walked.Add((new TaxiwayLeg(other, false, seg), i));
            }
        }

        var legs = new List<TaxiwayLeg>(walked.Count);
        for (int k = 0; k < walked.Count; k++)
        {
            TaxiwayLeg leg = walked[k].Leg;
            if ((leg.IsRunway && IsCrossingLeg(route, walked, k)) || ((legs.Count > 0) && SameName(legs[^1].Name, leg.Name)))
            {
                continue;
            }

            legs.Add(leg);
        }

        return legs;
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The taxiway a route's final segment, a junction arc followed on <paramref name="current"/>, ends on: its one taxiway
    /// name other than <paramref name="current"/> ("C - G" on C ends on G, e.g. at a runway bar on the arc's G tangent).
    /// Null for a straight edge, or an arc naming no other taxiway or more than one.
    /// </summary>
    private static string? FinalArcOtherTaxiway(IGroundEdge edge, string current)
    {
        if (edge is not GroundArc)
        {
            return null;
        }

        List<string> others =
        [
            .. TaxiwayMembers(edge).Where(name => !IsRunwayName(name) && !SameName(name, current)).Distinct(StringComparer.OrdinalIgnoreCase),
        ];
        return (others.Count == 1) ? others[0] : null;
    }

    /// <summary>
    /// True when the runway leg <c>walked[k]</c> is a crossing of its runway, read from the route's own structure: the leg's
    /// centreline runs at most <see cref="Pathfinding.SegmentExpander.MaxOffsetRunwayCrossingFt"/>, measured by the
    /// pathfinder's own <see cref="Pathfinding.SegmentExpander.CenterlineLengthFt"/> (a far-side fillet the walk keeps on
    /// the runway's name does not count), the last hold-short bar on the previous leg is this runway's and carries one of the route's hold-short
    /// points, the first bar on the next leg is this runway's too, and the two bars stand on opposite sides of the leg. A runway cleared to taxi
    /// along can keep an explicit hold short at its entry bar (<c>TAXI A 28L B HS 28L</c>), so a longer leg is never a crossing; neither is a leg
    /// entered with no bar of its own on the previous leg, nor one leaving on the side it entered from. Each of those keeps reading "on (runway)".
    /// </summary>
    private static bool IsCrossingLeg(TaxiRoute route, List<(TaxiwayLeg Leg, int FirstSegment)> walked, int k)
    {
        if ((k == 0) || (k + 1 >= walked.Count))
        {
            return false;
        }

        int first = walked[k].FirstSegment;
        int last = walked[k + 1].FirstSegment - 1;
        IEnumerable<IGroundEdge> legEdges = route.Segments.Skip(first).Take(last - first + 1).Select(s => s.Edge.Edge);
        if (Pathfinding.SegmentExpander.CenterlineLengthFt(legEdges) > Pathfinding.SegmentExpander.MaxOffsetRunwayCrossingFt)
        {
            return false;
        }

        GroundNode? entryBar = null;
        for (int s = first - 1; (s >= walked[k - 1].FirstSegment) && (entryBar is null); s--)
        {
            entryBar = AsHoldShortBar(route.Segments[s].Edge.FromNode);
        }

        int nextLast = (k + 2 < walked.Count ? walked[k + 2].FirstSegment : route.Segments.Count) - 1;
        GroundNode? exitBar = null;
        for (int s = last + 1; (s <= nextLast) && (exitBar is null); s++)
        {
            exitBar = AsHoldShortBar(route.Segments[s].Edge.ToNode);
        }

        var runway = RunwayIdentifier.Parse(walked[k].Leg.Name[3..]);
        if ((entryBar is null) || (exitBar is null) || !IsBarOf(entryBar, runway) || !IsBarOf(exitBar, runway))
        {
            return false;
        }

        if (!HasHoldShortAt(route, entryBar.Id))
        {
            return false;
        }

        // A stitched crossing's link hops lead off the runway line to each taxiway's end; the line is the run between them.
        List<TaxiRouteSegment> legSegments = [.. route.Segments.Skip(first).Take(last - first + 1)];
        List<TaxiRouteSegment> lineSegments = [.. legSegments.Where(s => !GroundEdge.IsRunwayCrossingLinkName(s.Edge.Edge.TaxiwayName))];
        if (lineSegments.Count == 0)
        {
            lineSegments = legSegments;
        }

        GroundNode lineStart = lineSegments[0].Edge.FromNode;
        GroundNode lineEnd = lineSegments[^1].Edge.ToNode;
        double lineBearing = GeoMath.BearingTo(lineStart.Position, lineEnd.Position);
        return SideOfLine(lineStart, lineBearing, entryBar) * SideOfLine(lineStart, lineBearing, exitBar) < 0;
    }

    private static bool HasHoldShortAt(TaxiRoute route, int nodeId)
    {
        foreach (HoldShortPoint point in route.HoldShortPoints)
        {
            if (point.NodeId == nodeId)
            {
                return true;
            }
        }

        return false;
    }

    private static GroundNode? AsHoldShortBar(GroundNode node) => node.Type == GroundNodeType.RunwayHoldShort ? node : null;

    private static bool IsBarOf(GroundNode bar, RunwayIdentifier runway) => (bar.RunwayId is { } id) && id.Overlaps(runway);

    private static int SideOfLine(GroundNode lineStart, double lineBearing, GroundNode node) =>
        Math.Sign(Math.Sin((GeoMath.BearingTo(lineStart.Position, node.Position) - lineBearing) * Math.PI / 180.0));

    /// <summary>
    /// The ordered, consecutive-deduped sequence of real taxiway names the route traverses — the
    /// legs of <see cref="TaxiwayLegs"/> with runways removed, for the readable <c>TAXI</c> form.
    /// </summary>
    public static List<string> CleanTaxiwaySequence(TaxiRoute route)
    {
        var names = new List<string>();
        foreach (TaxiwayLeg leg in TaxiwayLegs(route))
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
        string path = string.Join(" ", CleanTaxiwaySequence(route));
        if (hasNamedTerminus || route.Segments.Count == 0)
        {
            return path;
        }

        int endNode = route.Segments[^1].ToNodeId;
        return string.IsNullOrEmpty(path) ? $"#{endNode}" : $"{path} #{endNode}";
    }

    /// <summary>True for the internal combined runway-centerline name (<c>"RWY28R/10L"</c>).</summary>
    internal static bool IsRunwayName(string name) => name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The runway centreline name of a stitched runway-crossing link's edge name (<c>"RWY10L/28R:link"</c> →
    /// <c>"RWY10L/28R"</c>); any other name unchanged.
    /// </summary>
    internal static string RunwayCenterlineName(string name) =>
        IsRunwayName(name) && GroundEdge.IsRunwayCrossingLinkName(name)
            ? name[..name.IndexOf(GroundEdge.RunwayCrossingLinkSuffix, StringComparison.OrdinalIgnoreCase)]
            : name;

    /// <summary>
    /// The taxiway names an edge belongs to, ramp dropped and real taxiways ordered ahead of runways.
    /// The ordering matters for a runway-crossing arc (<c>["H", "RWY01L/19R"]</c>) reached from neither
    /// of its members: that arc <em>continues</em> H across the runway, so H is the name to show — the
    /// aircraft is not being cleared to taxi along 01L. A stitched <c>RWY…:link</c> edge carries its runway's name, so
    /// link, centreline and link read as one runway leg.
    /// </summary>
    private static List<string> TaxiwayMembers(IGroundEdge edge)
    {
        string[] raw = edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];
        var result = new List<string>();
        foreach (string name in raw)
        {
            if (!string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase) && !IsRunwayName(name))
            {
                result.Add(name);
            }
        }

        foreach (string name in raw)
        {
            if (IsRunwayName(name))
            {
                result.Add(RunwayCenterlineName(name));
            }
        }

        return result;
    }
}
