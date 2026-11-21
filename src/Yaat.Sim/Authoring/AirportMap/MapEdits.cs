using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.AirportMap;

/// <summary>
/// The editor's pure edit operations over an <see cref="AirportMapDocument"/>: each returns a new document sharing the
/// features it does not change, leaves the document it is given alone, and throws on an invalid target. Undo is the caller's:
/// it records the document each edit replaces on an <see cref="UndoStack{TState}"/>.
/// </summary>
public static class MapEdits
{
    private const int MinLineVertices = 2;

    private static readonly ILogger Log = SimLog.CreateLogger("MapEdits");

    /// <summary>
    /// Moves one vertex of a feature to <paramref name="to"/>; a point feature has the one vertex 0.
    /// </summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="featureIndex">The feature's index in <see cref="AirportMapDocument.Features"/>.</param>
    /// <param name="vertexIndex">The vertex's index in the feature's coordinates.</param>
    /// <param name="to">The vertex's new position.</param>
    /// <returns>The new document.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such feature or vertex, or the position is not finite.</exception>
    /// <exception cref="InvalidOperationException">The feature's geometry is not one the editor can change.</exception>
    public static AirportMapDocument MoveVertex(AirportMapDocument document, int featureIndex, int vertexIndex, MapPosition to)
    {
        MapFeature feature = FeatureAt(document, featureIndex);
        ThrowIfForeignGeometry(feature, featureIndex);
        ThrowIfVertexOutOfRange(feature, vertexIndex);
        LogEdit("move vertex", featureIndex);
        return Replace(document, featureIndex, Rebuild(feature, feature.Coordinates.SetItem(vertexIndex, to)));
    }

    /// <summary>
    /// Inserts a vertex into a line string before <paramref name="vertexIndex"/>: 0 puts it first and the coordinate count
    /// appends it.
    /// </summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="featureIndex">The feature's index in <see cref="AirportMapDocument.Features"/>.</param>
    /// <param name="vertexIndex">Where to insert, from 0 to the coordinate count inclusive.</param>
    /// <param name="at">The new vertex's position.</param>
    /// <returns>The new document.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// There is no such feature, the index is outside the line string, or the position is not finite.
    /// </exception>
    /// <exception cref="InvalidOperationException">The feature is not a line string.</exception>
    public static AirportMapDocument InsertVertex(AirportMapDocument document, int featureIndex, int vertexIndex, MapPosition at)
    {
        MapFeature feature = LineAt(document, featureIndex);
        if ((vertexIndex < 0) || (vertexIndex > feature.Coordinates.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(vertexIndex), vertexIndex, "The insertion index is outside the line string's vertices");
        }

        LogEdit("insert vertex", featureIndex);
        return Replace(document, featureIndex, Rebuild(feature, feature.Coordinates.Insert(vertexIndex, at)));
    }

    /// <summary>
    /// Deletes one vertex of a line string, refusing an edit that would leave it with fewer than two positions.
    /// </summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="featureIndex">The feature's index in <see cref="AirportMapDocument.Features"/>.</param>
    /// <param name="vertexIndex">The vertex's index in the feature's coordinates.</param>
    /// <returns>The new document.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such feature or vertex.</exception>
    /// <exception cref="InvalidOperationException">The feature is not a line string, or the edit would leave it with one position.</exception>
    public static AirportMapDocument DeleteVertex(AirportMapDocument document, int featureIndex, int vertexIndex)
    {
        MapFeature feature = LineAt(document, featureIndex);
        ThrowIfVertexOutOfRange(feature, vertexIndex);
        if (feature.Coordinates.Length <= MinLineVertices)
        {
            throw new InvalidOperationException(
                $"Deleting a vertex from feature {featureIndex} would leave fewer than two positions; delete the feature instead"
            );
        }

        LogEdit("delete vertex", featureIndex);
        return Replace(document, featureIndex, Rebuild(feature, feature.Coordinates.RemoveAt(vertexIndex)));
    }

    /// <summary>
    /// Adds a feature, at <paramref name="atIndex"/> or at the end when it is null.
    /// </summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="feature">The feature to add.</param>
    /// <param name="atIndex">Where to insert it, from 0 to the feature count inclusive; null appends it.</param>
    /// <returns>The new document.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The insertion index is outside the feature list.</exception>
    public static AirportMapDocument AddFeature(AirportMapDocument document, MapFeature feature, int? atIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(feature);
        int index = atIndex ?? document.Features.Count;
        if ((index < 0) || (index > document.Features.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(atIndex), atIndex, "The insertion index is outside the feature list");
        }

        LogEdit("add feature", index);
        return new AirportMapDocument(document.Features.Insert(index, feature), document.ForeignMembers);
    }

    /// <summary>
    /// Deletes a feature.
    /// </summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="featureIndex">The feature's index in <see cref="AirportMapDocument.Features"/>.</param>
    /// <returns>The new document.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such feature.</exception>
    public static AirportMapDocument DeleteFeature(AirportMapDocument document, int featureIndex)
    {
        FeatureAt(document, featureIndex);
        LogEdit("delete feature", featureIndex);
        return new AirportMapDocument(document.Features.RemoveAt(featureIndex), document.ForeignMembers);
    }

    /// <summary>
    /// Sets one property of a feature's <c>properties</c>, replacing it in place, appending a key it does not have, or
    /// removing the key when <paramref name="value"/> is null. An edit that changes nothing returns the document it was given.
    /// </summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="featureIndex">The feature's index in <see cref="AirportMapDocument.Features"/>.</param>
    /// <param name="key">
    /// The property's name, matched case-insensitively; an existing key is replaced under the case it was authored with.
    /// </param>
    /// <param name="value">The property's new value, or null to remove it.</param>
    /// <returns>The new document, or <paramref name="document"/> when nothing changes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such feature.</exception>
    /// <exception cref="InvalidOperationException">The feature has no typed <c>properties</c> object.</exception>
    public static AirportMapDocument SetProperty(AirportMapDocument document, int featureIndex, string key, JsonNode? value)
    {
        MapFeature feature = FeatureAt(document, featureIndex);
        ArgumentException.ThrowIfNullOrEmpty(key);
        JsonObject properties =
            feature.CloneProperties() ?? throw new InvalidOperationException($"Feature {featureIndex} has no properties object to set a property on");
        string? authored = FindKey(properties, key);
        if (value is null)
        {
            if (authored is null)
            {
                return document;
            }

            properties.Remove(authored);
        }
        else if ((authored is not null) && JsonNode.DeepEquals(properties[authored], value))
        {
            return document;
        }
        else
        {
            properties[authored ?? key] = value.DeepClone();
        }

        LogEdit("set property", featureIndex);
        return Replace(document, featureIndex, new MapFeature(properties, feature.GeometryType, feature.Coordinates, feature.ForeignMembers));
    }

    /// <summary>
    /// Splits a taxiway at an interior vertex into two features that share it, the second inserted right after the first with
    /// the same properties and members. Runways do not split.
    /// </summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="featureIndex">The taxiway's index in <see cref="AirportMapDocument.Features"/>.</param>
    /// <param name="vertexIndex">The interior vertex to split at, from 1 to the coordinate count minus 2.</param>
    /// <returns>The new document and the index of its new second feature.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such feature, or the vertex is not interior.</exception>
    /// <exception cref="InvalidOperationException">The feature is not a taxiway line string.</exception>
    public static (AirportMapDocument Document, int NewFeatureIndex) SplitTaxiway(AirportMapDocument document, int featureIndex, int vertexIndex)
    {
        MapFeature feature = FeatureAt(document, featureIndex);
        if (!string.Equals(feature.FeatureType, "taxiway", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Feature {featureIndex} is not a taxiway; only taxiways split");
        }

        if (!string.Equals(feature.GeometryType, "LineString", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Taxiway feature {featureIndex} is not a line string");
        }

        if ((vertexIndex < 1) || (vertexIndex > feature.Coordinates.Length - 2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(vertexIndex),
                vertexIndex,
                "A split vertex must be interior: the first and last vertices cannot be split at"
            );
        }

        ImmutableArray<MapPosition> first = [.. feature.Coordinates.Take(vertexIndex + 1)];
        ImmutableArray<MapPosition> second = [.. feature.Coordinates.Skip(vertexIndex)];
        ImmutableList<MapFeature> features = document
            .Features.SetItem(featureIndex, Rebuild(feature, first))
            .Insert(featureIndex + 1, Rebuild(feature, second));
        LogEdit("split taxiway", featureIndex);
        return (new AirportMapDocument(features, document.ForeignMembers), featureIndex + 1);
    }

    /// <summary>
    /// Builds a new feature of <paramref name="featureType"/> carrying the property keys of the first feature of that type in
    /// <paramref name="template"/>, with every copied value emptied except <c>type</c> and <c>name</c> set to
    /// <paramref name="name"/>; an airport with no feature of the type gets just <c>type</c> and <c>name</c>.
    /// </summary>
    /// <param name="template">The map whose existing feature of the type supplies the property keys.</param>
    /// <param name="featureType">The new feature's <c>type</c>: parking, spot, helipad, taxiway or runway.</param>
    /// <param name="name">The new feature's <c>name</c>.</param>
    /// <param name="positions">
    /// The geometry's positions: one for a point feature, at least two for a line string.
    /// </param>
    /// <returns>The new feature, not yet in any document.</returns>
    /// <exception cref="ArgumentException">The type is empty, or the positions do not fit the type's geometry.</exception>
    /// <exception cref="InvalidOperationException">The editor does not create features of that type.</exception>
    public static MapFeature NewFeature(AirportMapDocument template, string featureType, string name, ImmutableArray<MapPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrEmpty(featureType);
        ArgumentNullException.ThrowIfNull(name);
        string geometryType = featureType switch
        {
            "taxiway" or "runway" => "LineString",
            "parking" or "spot" or "helipad" => "Point",
            _ => throw new InvalidOperationException($"The editor does not create {featureType} features"),
        };
        if (string.Equals(geometryType, "LineString", StringComparison.Ordinal) && (positions.IsDefault || (positions.Length < MinLineVertices)))
        {
            throw new ArgumentException($"A {featureType} needs at least {MinLineVertices} positions", nameof(positions));
        }

        JsonObject properties = TemplateProperties(template, featureType);
        SetKey(properties, "type", featureType);
        SetKey(properties, "name", name);
        return new MapFeature(properties, geometryType, positions, []);
    }

    private static JsonObject TemplateProperties(AirportMapDocument template, string featureType)
    {
        var properties = new JsonObject();
        MapFeature? source = template.Features.FirstOrDefault(feature => string.Equals(feature.FeatureType, featureType, StringComparison.Ordinal));
        if (source?.CloneProperties() is not { } sourceProperties)
        {
            return properties;
        }

        foreach (KeyValuePair<string, JsonNode?> member in sourceProperties)
        {
            properties[member.Key] = string.Equals(member.Key, "type", StringComparison.Ordinal) ? member.Value?.DeepClone() : null;
        }

        return properties;
    }

    // The caller's template keeps its authored key case (ATCTrainer exports use "Type"), so a wanted key replaces its
    // case-insensitive match rather than adding a second member.
    private static void SetKey(JsonObject properties, string wanted, string value)
    {
        foreach (string key in properties.Select(member => member.Key))
        {
            if (string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase))
            {
                properties[key] = value;
                return;
            }
        }

        properties[wanted] = value;
    }

    private static string? FindKey(JsonObject properties, string wanted) =>
        properties.Select(member => member.Key).FirstOrDefault(key => string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase));

    private static MapFeature FeatureAt(AirportMapDocument document, int featureIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        if ((featureIndex < 0) || (featureIndex >= document.Features.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(featureIndex), featureIndex, "No feature at that index");
        }

        return document.Features[featureIndex];
    }

    private static MapFeature LineAt(AirportMapDocument document, int featureIndex)
    {
        MapFeature feature = FeatureAt(document, featureIndex);
        if (!string.Equals(feature.GeometryType, "LineString", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Feature {featureIndex} is not a line string");
        }

        return feature;
    }

    private static void ThrowIfForeignGeometry(MapFeature feature, int featureIndex)
    {
        if (feature.GeometryType is null)
        {
            throw new InvalidOperationException($"Feature {featureIndex} has a geometry the editor cannot change");
        }
    }

    private static void ThrowIfVertexOutOfRange(MapFeature feature, int vertexIndex)
    {
        if ((vertexIndex < 0) || (vertexIndex >= feature.Coordinates.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(vertexIndex), vertexIndex, "The feature has no vertex at that index");
        }
    }

    private static MapFeature Rebuild(MapFeature feature, ImmutableArray<MapPosition> coordinates) =>
        new(feature.CloneProperties(), feature.GeometryType, coordinates, feature.ForeignMembers);

    private static AirportMapDocument Replace(AirportMapDocument document, int featureIndex, MapFeature feature) =>
        new(document.Features.SetItem(featureIndex, feature), document.ForeignMembers);

    private static void LogEdit(string edit, int featureIndex)
    {
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("{Edit} on feature {Feature}", edit, featureIndex);
        }
    }
}
