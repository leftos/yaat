namespace Yaat.Sim.Data.Airport;

/// <summary>
/// The taxiway, gate and spot names of a ground layout as a controller says them: uppercase, distinct and sorted. Shared by the
/// client's speech and autocomplete lists and by the speech context decoder's airport entities.
/// </summary>
public static class GroundLayoutSpeechNames
{
    /// <summary>Taxiway names carried by the layout's edges, leaving out runway centerlines and ramps.</summary>
    /// <param name="layout">The airport ground layout.</param>
    /// <returns>The uppercase taxiway names, distinct and sorted ordinally.</returns>
    public static IReadOnlyList<string> TaxiwayNames(AirportGroundLayout layout)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GroundEdge edge in layout.Edges)
        {
            if (edge.IsRunwayCenterline || edge.IsRamp)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(edge.TaxiwayName))
            {
                names.Add(edge.TaxiwayName.ToUpperInvariant());
            }
        }

        return Sorted(names);
    }

    /// <summary>Names of the layout's parking and helipad nodes.</summary>
    /// <param name="layout">The airport ground layout.</param>
    /// <returns>The uppercase gate names, distinct and sorted ordinally.</returns>
    public static IReadOnlyList<string> GateNames(AirportGroundLayout layout) =>
        NodeNames(layout, type => type is GroundNodeType.Parking or GroundNodeType.Helipad);

    /// <summary>Names of the layout's spot nodes, without any "SPOT" prefix.</summary>
    /// <param name="layout">The airport ground layout.</param>
    /// <returns>The uppercase spot names, distinct and sorted ordinally.</returns>
    public static IReadOnlyList<string> SpotNames(AirportGroundLayout layout) => NodeNames(layout, type => type is GroundNodeType.Spot);

    private static IReadOnlyList<string> NodeNames(AirportGroundLayout layout, Func<GroundNodeType, bool> include)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if (include(node.Type) && !string.IsNullOrEmpty(node.Name))
            {
                names.Add(node.Name.ToUpperInvariant());
            }
        }

        return Sorted(names);
    }

    private static List<string> Sorted(HashSet<string> names)
    {
        var sorted = new List<string>(names);
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }
}
