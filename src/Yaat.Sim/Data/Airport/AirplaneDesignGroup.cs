using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Data.Airport;

/// <summary>The FAA Airplane Design Group, by wingspan (AC 150/5300-13B Table 1-2).</summary>
public enum AirplaneDesignGroup
{
    I = 1,
    II = 2,
    III = 3,
    IV = 4,
    V = 5,
    VI = 6,
}

/// <summary>
/// The Airplane Design Group figures the sim uses, in one place, and the design group of an airport and of one of its
/// taxiways. The layout carries no design group, taxiway width or object-free area, so both are derived: the airport's
/// from its widest runway, a taxiway's from that capped by the spacing to its nearest parallel taxiway.
///
/// <para>Sources: the span ceilings are AC 150/5300-13B Table 1-2. The taxiway centreline-to-object and
/// taxiway-to-taxiway separations are AC 150/5300-13B Table 4-1 (0.7·W + 10 ft and 1.2·W + 10 ft of the group's span
/// ceiling W).</para>
/// </summary>
public static class AirplaneDesignGroups
{
    /// <summary>
    /// How far either side of a taxiway's centreline two parallel taxiways' headings may differ and still count as
    /// parallel, degrees; a judgement call.
    /// </summary>
    public const double ParallelToleranceDeg = 15.0;

    /// <summary>How far two taxiways must run side by side to count as parallel, feet; a judgement call.</summary>
    public const double ParallelOverlapFt = 300.0;

    /// <summary>The spacing between samples along a taxiway when its parallels are measured, feet.</summary>
    private const double SampleSpacingFt = 25.0;

    /// <summary>
    /// How far off a taxiway a parallel taxiway is looked for, feet: past the ADG VI taxiway-to-taxiway separation, a
    /// parallel no longer caps anything.
    /// </summary>
    private const double ParallelSearchFt = 400.0;

    private const double DegToRad = Math.PI / 180.0;

    /// <summary>The least |cos| between two pieces' directions that still counts as parallel.</summary>
    private static readonly double ParallelMinCos = Math.Cos(ParallelToleranceDeg * DegToRad);

    private static readonly ILogger Log = SimLog.CreateLogger("AirplaneDesignGroups");

    private static readonly ConditionalWeakTable<AirportGroundLayout, ConditionalWeakTable<MovementAreaClassification, LayoutTaxiways>> Cache = [];

    /// <summary>The group's span ceiling, feet: its spans lie under it (AC 150/5300-13B Table 1-2).</summary>
    /// <param name="group">The design group.</param>
    /// <returns>The span ceiling, feet.</returns>
    public static double MaxWingspanFt(AirplaneDesignGroup group) =>
        group switch
        {
            AirplaneDesignGroup.I => 49.0,
            AirplaneDesignGroup.II => 79.0,
            AirplaneDesignGroup.III => 118.0,
            AirplaneDesignGroup.IV => 171.0,
            AirplaneDesignGroup.V => 214.0,
            AirplaneDesignGroup.VI => 262.0,
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown Airplane Design Group"),
        };

    /// <summary>Parses a roman design group, <c>I</c> to <c>VI</c>, ordinal and ignoring case; anything else is false.</summary>
    /// <param name="text">The text, as an FAA record's ADG column carries it.</param>
    /// <param name="group">The group parsed, or <see langword="default"/> when the text is not one.</param>
    /// <returns>True when the text names a group.</returns>
    public static bool TryParseRoman(string? text, out AirplaneDesignGroup group)
    {
        foreach (AirplaneDesignGroup candidate in Enum.GetValues<AirplaneDesignGroup>())
        {
            if (string.Equals(text, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                group = candidate;
                return true;
            }
        }

        group = default;
        return false;
    }

    /// <summary>
    /// The smallest group whose span ceiling (<see cref="MaxWingspanFt"/>) lies above <paramref name="spanFt"/>: AC
    /// 150/5300-13B Table 1-2 bounds each group's span exclusively (group I is under 49 ft, group II 49 ft to under 79 ft).
    /// A span at or past group VI's ceiling also returns VI, which the caller warns about.
    /// </summary>
    /// <param name="spanFt">The wingspan, feet.</param>
    /// <returns>The group.</returns>
    public static AirplaneDesignGroup SmallestCoveringSpan(double spanFt)
    {
        foreach (AirplaneDesignGroup group in Enum.GetValues<AirplaneDesignGroup>())
        {
            if (spanFt < MaxWingspanFt(group))
            {
                return group;
            }
        }

        return AirplaneDesignGroup.VI;
    }

    /// <summary>
    /// The design group of an FAA record: its roman ADG; else, when the ADG is blank, the smallest group covering its span
    /// (<see cref="SmallestCoveringSpan"/>); else, for any other ADG value, the same fallback with a warning. The span is
    /// the with-winglets figure, else the without-winglets one. Null when the ADG is not a group and the record has no span.
    /// </summary>
    /// <param name="record">The FAA record.</param>
    /// <param name="warning">Why the group was not the record's own ADG and is worth a warning, or null.</param>
    /// <returns>The group, or null when the record gives neither a group nor a span.</returns>
    public static AirplaneDesignGroup? OfRecord(FaaAircraftRecord record, out string? warning)
    {
        warning = null;
        if (TryParseRoman(record.Adg, out AirplaneDesignGroup parsed))
        {
            return parsed;
        }

        if ((record.WingspanFtWithWinglets ?? record.WingspanFtWithoutWinglets) is not { } spanFt)
        {
            warning = string.IsNullOrWhiteSpace(record.Adg)
                ? $"{record.IcaoCode}: ADG is blank and the record has no wingspan; it has no design group"
                : $"ADG '{record.Adg}' on {record.IcaoCode} is not I-VI and it has no span; it has no design group";
            return null;
        }

        AirplaneDesignGroup group = SmallestCoveringSpan(spanFt);
        if (!string.IsNullOrWhiteSpace(record.Adg))
        {
            warning = $"ADG '{record.Adg}' on {record.IcaoCode} is not I-VI; using group {group} from its {spanFt} ft span";
        }
        else if (spanFt >= MaxWingspanFt(AirplaneDesignGroup.VI))
        {
            warning = $"{record.IcaoCode} has no ADG and its {spanFt} ft span exceeds group VI's ceiling; using group VI";
        }

        return group;
    }

    /// <summary>
    /// The design group of an aircraft type: the group of its FAA record (<see cref="OfRecord"/>), when the database has
    /// a record for the type and it gives a group, else the smallest group covering its span
    /// (<see cref="SmallestCoveringSpan"/> over <see cref="AircraftFootprint.ResolveWingspanFt"/>, which falls back by
    /// category when the record gives none).
    /// </summary>
    /// <param name="aircraftType">ICAO type designator, prefixes and suffixes allowed.</param>
    /// <returns>The design group.</returns>
    public static AirplaneDesignGroup GroupForType(string aircraftType)
    {
        FaaAircraftRecord? record = FaaAircraftDatabase.Get(aircraftType);
        string? warning = null;
        AirplaneDesignGroup? group = record is null ? null : OfRecord(record, out warning);
        if (warning is not null)
        {
            Log.LogDebug("{Warning}", warning);
        }

        return group ?? SmallestCoveringSpan(AircraftFootprint.ResolveWingspanFt(aircraftType));
    }

    /// <summary>
    /// The taxiway centreline-to-object separation for the group, feet: half the taxiway object-free area, the
    /// distance from the centreline inside which nothing may stand while an aircraft of the group taxis on it
    /// (AC 150/5300-13B Table 4-1, 0.7·W + 10 ft).
    /// </summary>
    /// <param name="group">The taxiway's design group.</param>
    /// <returns>The half-width, feet.</returns>
    public static double TaxiwayObjectFreeHalfWidthFt(AirplaneDesignGroup group) =>
        group switch
        {
            AirplaneDesignGroup.I => 44.5,
            AirplaneDesignGroup.II => 65.5,
            AirplaneDesignGroup.III => 93.0,
            AirplaneDesignGroup.IV => 129.5,
            AirplaneDesignGroup.V => 160.0,
            AirplaneDesignGroup.VI => 193.0,
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown Airplane Design Group"),
        };

    /// <summary>
    /// The taxiway centreline-to-taxiway centreline separation for the group, feet (AC 150/5300-13B Table 4-1,
    /// 1.2·W + 10 ft).
    /// </summary>
    /// <param name="group">The design group.</param>
    /// <returns>The separation, feet.</returns>
    public static double TaxiwaySeparationFt(AirplaneDesignGroup group) =>
        group switch
        {
            AirplaneDesignGroup.I => 69.0,
            AirplaneDesignGroup.II => 105.0,
            AirplaneDesignGroup.III => 152.0,
            AirplaneDesignGroup.IV => 215.0,
            AirplaneDesignGroup.V => 267.0,
            AirplaneDesignGroup.VI => 324.0,
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown Airplane Design Group"),
        };

    /// <summary>
    /// The design group an airport is built for, read from its widest runway: under 75 ft I, under 100 ft II, under
    /// 150 ft III, under 200 ft V, else VI — the same width buckets as
    /// <see cref="RunwayCrossingDetector.HoldShortDistanceForWidth"/>. A 150 ft runway reads as V on purpose, not IV:
    /// KOAK 30 is 150 ft wide and carries MD-11s and B744Fs.
    /// </summary>
    /// <param name="widestRunwayWidthFt">Width of the airport's widest runway, feet.</param>
    /// <returns>The airport's design group.</returns>
    public static AirplaneDesignGroup FromRunwayWidth(double widestRunwayWidthFt)
    {
        if (widestRunwayWidthFt < 75.0)
        {
            return AirplaneDesignGroup.I;
        }

        if (widestRunwayWidthFt < 100.0)
        {
            return AirplaneDesignGroup.II;
        }

        if (widestRunwayWidthFt < 150.0)
        {
            return AirplaneDesignGroup.III;
        }

        return widestRunwayWidthFt < 200.0 ? AirplaneDesignGroup.V : AirplaneDesignGroup.VI;
    }

    /// <summary>
    /// The design group of one of the airport's movement-area taxiways: the smaller of the airport's
    /// (<see cref="FromRunwayWidth"/> of its widest runway with a recorded width; VI when the layout has none) and the
    /// geometric cap — the largest group whose <see cref="TaxiwaySeparationFt"/> fits the least spacing between this
    /// taxiway's centreline and a parallel movement-area taxiway's. A parallel runs within
    /// <see cref="ParallelToleranceDeg"/> of this taxiway's heading beside it for at least
    /// <see cref="ParallelOverlapFt"/>; its spacing is the least lateral distance that holds along that much of this
    /// taxiway. With no parallel the airport's group applies; with a parallel closer than even the ADG I separation,
    /// ADG I. Cached per layout and movement-area classification.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="classification">The movement-area classification that says which taxiways are parallels.</param>
    /// <param name="taxiway">The taxiway's name.</param>
    /// <returns>The taxiway's design group.</returns>
    public static AirplaneDesignGroup ForTaxiway(AirportGroundLayout layout, MovementAreaClassification classification, string taxiway)
    {
        LayoutTaxiways taxiways = Cache.GetOrCreateValue(layout).GetValue(classification, c => new LayoutTaxiways(layout, c));
        return taxiways.GroupByName.GetOrAdd(taxiway, name => Derive(layout, taxiways, name));
    }

    /// <summary>
    /// The flat frame every per-layout measurement is made in: centred on the mean of the layout's node positions, so
    /// one frame serves every plan and taxiway at the airport.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <returns>The frame.</returns>
    internal static GroundOutlineFrame LayoutFrame(AirportGroundLayout layout)
    {
        if (layout.Nodes.Count == 0)
        {
            return new GroundOutlineFrame(new LatLon(0.0, 0.0));
        }

        return new GroundOutlineFrame(new LatLon(layout.Nodes.Values.Average(n => n.Position.Lat), layout.Nodes.Values.Average(n => n.Position.Lon)));
    }

    private static AirplaneDesignGroup Derive(AirportGroundLayout layout, LayoutTaxiways taxiways, string taxiway)
    {
        AirplaneDesignGroup ceiling = AirportCeiling(layout);
        double? spacingFt = ParallelSpacingFt(layout, taxiways, taxiway);
        AirplaneDesignGroup cap = spacingFt is { } s ? LargestFitting(s) : AirplaneDesignGroup.VI;
        var group = (AirplaneDesignGroup)Math.Min((int)ceiling, (int)cap);
        Log.LogDebug(
            "{Airport} taxiway {Taxiway}: airport ceiling ADG {Ceiling}, nearest parallel {Spacing}, ADG {Group}",
            layout.AirportId,
            taxiway,
            ceiling,
            spacingFt is { } ft ? $"{ft:F0} ft away (cap ADG {cap})" : "none",
            group
        );
        return group;
    }

    /// <summary>
    /// The airport's own group: <see cref="FromRunwayWidth"/> of its widest runway with a recorded width. A runway of
    /// zero width has none recorded, so it does not read as a 0 ft (ADG I) runway; a layout with no runway of recorded
    /// width — or none at all — takes VI, capped only by the parallels.
    /// </summary>
    private static AirplaneDesignGroup AirportCeiling(AirportGroundLayout layout)
    {
        double widestFt = layout.Runways.Select(r => r.WidthFt).DefaultIfEmpty(0.0).Max();
        if (widestFt > 0.0)
        {
            return FromRunwayWidth(widestFt);
        }

        if (layout.Runways.Count > 0)
        {
            Log.LogDebug(
                "{Airport}: none of its {Count} runway(s) records a width; the airport ceiling is ADG VI",
                layout.AirportId,
                layout.Runways.Count
            );
        }

        return AirplaneDesignGroup.VI;
    }

    private static AirplaneDesignGroup LargestFitting(double spacingFt)
    {
        AirplaneDesignGroup fitting = AirplaneDesignGroup.I;
        foreach (AirplaneDesignGroup group in Enum.GetValues<AirplaneDesignGroup>())
        {
            if (TaxiwaySeparationFt(group) <= spacingFt)
            {
                fitting = group;
            }
        }

        return fitting;
    }

    /// <summary>
    /// The least spacing from <paramref name="taxiway"/>'s straight centreline to a parallel movement-area taxiway that
    /// holds along at least <see cref="ParallelOverlapFt"/> of it, feet, or null when no taxiway runs parallel that far
    /// within <see cref="ParallelSearchFt"/>: the per-sample nearest distances (<see cref="NearestBySample"/>), then the
    /// pick over them (<see cref="LeastHeldSpacingFt"/>).
    /// </summary>
    private static double? ParallelSpacingFt(AirportGroundLayout layout, LayoutTaxiways taxiways, string taxiway)
    {
        List<(OutlinePoint A, OutlinePoint B)> ownPieces =
        [
            .. layout.Edges.Where(e => e.MatchesTaxiway(taxiway)).SelectMany(e => Pieces(e, taxiways.Frame)),
        ];
        if (ownPieces.Count == 0)
        {
            return null;
        }

        List<(string Name, OutlinePoint A, OutlinePoint B)> others =
        [
            .. taxiways.Protectable.Where(p => !p.Edge.MatchesTaxiway(taxiway)).Select(p => (p.Edge.TaxiwayName, p.A, p.B)),
        ];
        return LeastHeldSpacingFt(layout, taxiway, NearestBySample(ownPieces, others));
    }

    /// <summary>
    /// The taxiway's pieces sampled every <see cref="SampleSpacingFt"/>: at each sample, every other taxiway's piece
    /// within the heading tolerance whose extent the sample projects onto gives a lateral distance, and the nearest per
    /// taxiway name is kept. Returns each name's distances, one per sample it was found at.
    /// </summary>
    private static Dictionary<string, List<double>> NearestBySample(
        List<(OutlinePoint A, OutlinePoint B)> ownPieces,
        List<(string Name, OutlinePoint A, OutlinePoint B)> others
    )
    {
        var distancesByName = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach ((OutlinePoint a, OutlinePoint b) in ownPieces)
        {
            double lengthFt = OutlinePoint.Distance(a, b);
            if (lengthFt <= 0.0)
            {
                continue;
            }

            OutlinePoint along = (1.0 / lengthFt) * (b - a);
            for (double s = SampleSpacingFt / 2.0; s < lengthFt; s += SampleSpacingFt)
            {
                foreach ((string name, double d) in NearestAt(a + (s * along), along, others))
                {
                    if (!distancesByName.TryGetValue(name, out List<double>? list))
                    {
                        distancesByName[name] = list = [];
                    }

                    list.Add(d);
                }
            }
        }

        return distancesByName;
    }

    /// <summary>The nearest parallel piece of each other taxiway to one sample, within <see cref="ParallelSearchFt"/>, by name.</summary>
    private static Dictionary<string, double> NearestAt(
        OutlinePoint sample,
        OutlinePoint along,
        List<(string Name, OutlinePoint A, OutlinePoint B)> others
    )
    {
        var nearest = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, OutlinePoint oa, OutlinePoint ob) in others)
        {
            if ((LateralDistanceFt(sample, along, oa, ob) is not { } d) || (d > ParallelSearchFt))
            {
                continue;
            }

            nearest[name] = nearest.TryGetValue(name, out double held) ? Math.Min(held, d) : d;
        }

        return nearest;
    }

    /// <summary>
    /// The final pick: a taxiway's spacing is the distance that enough of its samples lie within to cover
    /// <see cref="ParallelOverlapFt"/>, and the least such spacing over the taxiways is the answer; null when none has
    /// enough samples.
    /// </summary>
    private static double? LeastHeldSpacingFt(AirportGroundLayout layout, string taxiway, Dictionary<string, List<double>> distancesByName)
    {
        int needed = (int)Math.Ceiling(ParallelOverlapFt / SampleSpacingFt);
        double? least = null;
        foreach ((string name, List<double> distances) in distancesByName.Where(p => p.Value.Count >= needed))
        {
            distances.Sort();
            double spacingFt = distances[needed - 1];
            Log.LogDebug(
                "{Airport} taxiway {Taxiway}: {Other} runs parallel {Spacing:F0} ft away (least {Least:F0} ft, {Count} samples)",
                layout.AirportId,
                taxiway,
                name,
                spacingFt,
                distances[0],
                distances.Count
            );
            least = Math.Min(least ?? double.PositiveInfinity, spacingFt);
        }

        return least;
    }

    /// <summary>
    /// The lateral distance from <paramref name="sample"/> to the line of the piece <paramref name="a"/>–<paramref name="b"/>,
    /// feet, when that piece runs within <see cref="ParallelToleranceDeg"/> of <paramref name="along"/> (either way) and
    /// the sample projects onto its extent; null otherwise.
    /// </summary>
    private static double? LateralDistanceFt(OutlinePoint sample, OutlinePoint along, OutlinePoint a, OutlinePoint b)
    {
        double lengthFt = OutlinePoint.Distance(a, b);
        if (lengthFt <= 0.0)
        {
            return null;
        }

        OutlinePoint direction = (1.0 / lengthFt) * (b - a);
        double cos = Math.Abs((direction.EastFt * along.EastFt) + (direction.NorthFt * along.NorthFt));
        if (cos < ParallelMinCos)
        {
            return null;
        }

        OutlinePoint offset = sample - a;
        double t = (offset.EastFt * direction.EastFt) + (offset.NorthFt * direction.NorthFt);
        if ((t < 0.0) || (t > lengthFt))
        {
            return null;
        }

        return Math.Abs((offset.EastFt * direction.NorthFt) - (offset.NorthFt * direction.EastFt));
    }

    /// <summary>
    /// Whether an edge is a movement-area taxiway's centreline, the pavement the design group is about: not apron, not a
    /// runway or a runway-crossing link, and movement area by <see cref="MovementAreaClassification.IsMovementArea"/>.
    /// </summary>
    /// <param name="edge">The edge.</param>
    /// <param name="classification">The airport's movement-area classification.</param>
    /// <returns>True for a movement-area taxiway edge.</returns>
    internal static bool IsProtectable(GroundEdge edge, MovementAreaClassification classification) =>
        !edge.IsRamp && !edge.IsRunwayCenterline && !edge.IsRunwayCrossingLink && classification.IsMovementArea(edge.TaxiwayName);

    /// <summary>The straight pieces of an edge, its intermediate points included, in <paramref name="frame"/>.</summary>
    /// <param name="edge">The edge.</param>
    /// <param name="frame">The flat frame.</param>
    /// <returns>Each piece's two ends.</returns>
    internal static IEnumerable<(OutlinePoint A, OutlinePoint B)> Pieces(GroundEdge edge, GroundOutlineFrame frame)
    {
        List<OutlinePoint> points = [.. TugMovePlanner.EdgePointsFrom(edge, edge.Nodes[0]).Select(frame.ToLocal)];
        for (int i = 0; i + 1 < points.Count; i++)
        {
            yield return (points[i], points[i + 1]);
        }
    }

    /// <summary>
    /// One layout's movement-area taxiway pieces under one classification, built once: the frame they are laid out in,
    /// every protectable edge's straight pieces with the edge they belong to, and the taxiways' design groups as they
    /// are derived.
    /// </summary>
    private sealed class LayoutTaxiways
    {
        internal LayoutTaxiways(AirportGroundLayout layout, MovementAreaClassification classification)
        {
            Frame = LayoutFrame(layout);
            GroundOutlineFrame frame = Frame;
            Protectable = [.. layout.Edges.Where(e => IsProtectable(e, classification)).SelectMany(e => Pieces(e, frame).Select(p => (e, p.A, p.B)))];
        }

        internal GroundOutlineFrame Frame { get; }

        internal List<(GroundEdge Edge, OutlinePoint A, OutlinePoint B)> Protectable { get; }

        internal ConcurrentDictionary<string, AirplaneDesignGroup> GroupByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
