using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.AirportMap;

/// <summary>
/// What a snappable map point belongs to. The values are in the order a tie at equal distance resolves in: parking, spot,
/// helipad, runway vertex, then taxiway vertex.
/// </summary>
public enum SnapTargetKind
{
    /// <summary>A parking feature's point.</summary>
    Parking,

    /// <summary>A spot feature's point.</summary>
    Spot,

    /// <summary>A helipad feature's point.</summary>
    Helipad,

    /// <summary>A runway line string's vertex.</summary>
    RunwayVertex,

    /// <summary>A taxiway line string's vertex.</summary>
    TaxiwayVertex,
}

/// <summary>
/// What snapping may target and how close a target must be, built by the editor from the user's preferences.
/// </summary>
/// <param name="ThresholdFt">The snap distance in feet: a target exactly this far away snaps, and 0 turns snapping off.</param>
/// <param name="TaxiwayVertices">Whether a taxiway feature's vertices are targets.</param>
/// <param name="RunwayVertices">Whether a runway feature's vertices are targets.</param>
/// <param name="Parking">Whether a parking feature's point is a target.</param>
/// <param name="Spot">Whether a spot feature's point is a target.</param>
/// <param name="Helipad">Whether a helipad feature's point is a target.</param>
public sealed record SnapOptions(double ThresholdFt, bool TaxiwayVertices, bool RunwayVertices, bool Parking, bool Spot, bool Helipad)
{
    /// <summary>The brief's defaults: a 10 ft threshold with every target kind on.</summary>
    public static SnapOptions Default { get; } = new(10.0, true, true, true, true, true);
}

/// <summary>A snap target the caller holds outside the document, such as a vertex of the drawing in progress.</summary>
/// <param name="Position">The target's position.</param>
/// <param name="Kind">What the target is.</param>
public readonly record struct SnapTarget(MapPosition Position, SnapTargetKind Kind);

/// <summary>The vertex being dragged, which a snap never returns.</summary>
/// <param name="FeatureIndex">The dragged vertex's feature index.</param>
/// <param name="VertexIndex">The dragged vertex's index within its feature.</param>
public readonly record struct SnapExclusion(int FeatureIndex, int VertexIndex);

/// <summary>Where a snap lands: the target's exact position, what it is, and its distance from the cursor in feet.</summary>
/// <param name="Position">The target's position, exactly.</param>
/// <param name="Kind">What the target is.</param>
/// <param name="FeatureIndex">The target feature's index, or null for a caller-supplied target.</param>
/// <param name="VertexIndex">The target vertex's index in its feature, or null for a caller-supplied target.</param>
/// <param name="DistanceFt">The cursor's distance from the target in feet.</param>
public sealed record SnapResult(MapPosition Position, SnapTargetKind Kind, int? FeatureIndex, int? VertexIndex, double DistanceFt);

/// <summary>
/// Finds the map point the cursor snaps to: the nearest enabled target within the threshold, ties broken by
/// <see cref="SnapTargetKind"/> priority, then the lower feature index, then the lower vertex index.
/// </summary>
public static class SnapEngine
{
    private static readonly ILogger Log = SimLog.CreateLogger("SnapEngine");

    /// <summary>
    /// Finds the target <paramref name="cursor"/> snaps to.
    /// </summary>
    /// <param name="document">The map being edited.</param>
    /// <param name="cursor">The cursor's position.</param>
    /// <param name="options">The threshold and the enabled target kinds.</param>
    /// <param name="exclude">The vertex being dragged, which is never a target; null when nothing is excluded.</param>
    /// <param name="extraTargets">The caller's own targets outside the document, such as a drawing's unsaved vertices; null for none.</param>
    /// <returns>The snap, or null when no enabled target lies within the threshold.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The cursor or an extra target is not finite, or the threshold is negative or not a number.
    /// </exception>
    public static SnapResult? Find(
        AirportMapDocument document,
        MapPosition cursor,
        SnapOptions options,
        SnapExclusion? exclude,
        IReadOnlyList<SnapTarget>? extraTargets
    )
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfNotFinite(cursor, nameof(cursor));
        var search = new Search(cursor, options, exclude, Threshold(options));
        if (search.Threshold == 0.0)
        {
            return null;
        }

        Candidate? best = ExtraTargets(extraTargets, DocumentTargets(document, search), search);
        return best is { } snap ? ToResult(snap) : null;
    }

    private readonly record struct Candidate(MapPosition Position, SnapTargetKind Kind, int FeatureIndex, int VertexIndex, double DistanceFt);

    // One Find call's cursor, options, dragged vertex and threshold.
    private readonly record struct Search(MapPosition Cursor, SnapOptions Options, SnapExclusion? Exclude, double Threshold);

    private static Candidate? DocumentTargets(AirportMapDocument document, Search search)
    {
        Candidate? best = null;
        for (int featureIndex = 0; featureIndex < document.Features.Count; featureIndex++)
        {
            MapFeature feature = document.Features[featureIndex];
            if ((KindOf(feature) is not { } kind) || !IsEnabled(search.Options, kind) || (feature.Coordinates.Length == 0))
            {
                continue;
            }

            best = FeatureTargets(best, feature, kind, featureIndex, search);
        }

        return best;
    }

    private static Candidate? FeatureTargets(Candidate? best, MapFeature feature, SnapTargetKind kind, int featureIndex, Search search)
    {
        int count = IsPointKind(kind) ? 1 : feature.Coordinates.Length;
        for (int vertexIndex = 0; vertexIndex < count; vertexIndex++)
        {
            if (!IsExcluded(search, featureIndex, vertexIndex))
            {
                best = Consider(best, feature.Coordinates[vertexIndex], kind, (featureIndex, vertexIndex), search);
            }
        }

        return best;
    }

    private static Candidate? ExtraTargets(IReadOnlyList<SnapTarget>? targets, Candidate? best, Search search)
    {
        if (targets is null)
        {
            return best;
        }

        for (int index = 0; index < targets.Count; index++)
        {
            SnapTarget target = targets[index];
            ThrowIfNotFinite(target.Position, nameof(targets));
            if (!IsEnabled(search.Options, target.Kind))
            {
                continue;
            }

            best = Consider(best, target.Position, target.Kind, (-1, index), search);
        }

        return best;
    }

    private static Candidate? Consider(
        Candidate? best,
        MapPosition position,
        SnapTargetKind kind,
        (int FeatureIndex, int VertexIndex) origin,
        Search search
    )
    {
        double distanceFt = GeoMath.DistanceNm(search.Cursor.Lat, search.Cursor.Lon, position.Lat, position.Lon) * GeoMath.FeetPerNm;
        if (distanceFt > search.Threshold)
        {
            return best;
        }

        var candidate = new Candidate(position, kind, origin.FeatureIndex, origin.VertexIndex, distanceFt);
        return ((best is { } current) && !IsBetter(candidate, current)) ? best : candidate;
    }

    private static bool IsExcluded(Search search, int featureIndex, int vertexIndex) =>
        (search.Exclude is { } dragged) && (dragged.FeatureIndex == featureIndex) && (dragged.VertexIndex == vertexIndex);

    private static double Threshold(SnapOptions options)
    {
        double threshold = options.ThresholdFt;
        if (!double.IsFinite(threshold) || (threshold < 0.0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                threshold,
                "The snap threshold must be a finite distance in feet, or 0 to turn snapping off"
            );
        }

        return threshold;
    }

    private static SnapResult ToResult(Candidate snap)
    {
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "Snap to {Kind} feature {Feature} vertex {Vertex} at {Distance:F1} ft",
                snap.Kind,
                snap.FeatureIndex,
                snap.VertexIndex,
                snap.DistanceFt
            );
        }

        return new SnapResult(
            snap.Position,
            snap.Kind,
            (snap.FeatureIndex >= 0) ? snap.FeatureIndex : null,
            (snap.FeatureIndex >= 0) ? snap.VertexIndex : null,
            snap.DistanceFt
        );
    }

    private static bool IsBetter(Candidate candidate, Candidate current)
    {
        if (candidate.DistanceFt != current.DistanceFt)
        {
            return candidate.DistanceFt < current.DistanceFt;
        }

        if (candidate.Kind != current.Kind)
        {
            return candidate.Kind < current.Kind;
        }

        if (FeatureOrder(candidate) != FeatureOrder(current))
        {
            return FeatureOrder(candidate) < FeatureOrder(current);
        }

        return candidate.VertexIndex < current.VertexIndex;
    }

    // A caller-supplied target has no feature index and sorts after every document target at an equal distance.
    private static int FeatureOrder(Candidate candidate) => (candidate.FeatureIndex >= 0) ? candidate.FeatureIndex : int.MaxValue;

    private static SnapTargetKind? KindOf(MapFeature feature) =>
        feature.FeatureType switch
        {
            "parking" => SnapTargetKind.Parking,
            "spot" => SnapTargetKind.Spot,
            "helipad" => SnapTargetKind.Helipad,
            "runway" => SnapTargetKind.RunwayVertex,
            "taxiway" => SnapTargetKind.TaxiwayVertex,
            _ => null,
        };

    private static bool IsPointKind(SnapTargetKind kind) => kind is SnapTargetKind.Parking or SnapTargetKind.Spot or SnapTargetKind.Helipad;

    private static bool IsEnabled(SnapOptions options, SnapTargetKind kind) =>
        kind switch
        {
            SnapTargetKind.Parking => options.Parking,
            SnapTargetKind.Spot => options.Spot,
            SnapTargetKind.Helipad => options.Helipad,
            SnapTargetKind.RunwayVertex => options.RunwayVertices,
            SnapTargetKind.TaxiwayVertex => options.TaxiwayVertices,
            _ => false,
        };

    private static void ThrowIfNotFinite(MapPosition position, string paramName)
    {
        if (!double.IsFinite(position.Lon) || !double.IsFinite(position.Lat))
        {
            throw new ArgumentOutOfRangeException(paramName, position, "A snap position needs a finite longitude and latitude");
        }
    }
}
