using System.Collections.Immutable;

namespace Yaat.Sim.Authoring.AirportMap;

/// <summary>
/// A vNAS airport map (one combined GeoJSON FeatureCollection) read so the editor can change it, and written back
/// JSON-equal to the source: the raw features are the source of truth, kept in source order with exact coordinates.
/// </summary>
/// <remarks>
/// Immutable: an edit builds a new document that shares the unchanged features, so an undo snapshot is a reference.
/// The read accepts what <c>GeoJsonParser</c> accepts (comments, trailing commas, leading-zero number literals); comments
/// and trailing commas are dropped and a leading-zero literal is written without its zeros. No file I/O happens here;
/// <see cref="GeoJsonMapWriter.WriteFile"/> writes a document to disk.
/// </remarks>
public sealed class AirportMapDocument
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AirportMapDocument"/> class.
    /// </summary>
    /// <param name="features">The features in file order; a parser issue's feature index points into this order.</param>
    /// <param name="foreignMembers">The FeatureCollection's members other than <c>type</c> and <c>features</c>, in source order.</param>
    public AirportMapDocument(ImmutableList<MapFeature> features, ImmutableList<ForeignMember> foreignMembers)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(foreignMembers);
        Features = features;
        ForeignMembers = foreignMembers;
    }

    /// <summary>
    /// Gets the map's features in file order, unknown <c>properties.type</c> values included.
    /// </summary>
    public ImmutableList<MapFeature> Features { get; }

    /// <summary>
    /// Gets the FeatureCollection's members other than <c>type</c> and <c>features</c> (<c>name</c>, <c>crs</c>, ...), verbatim.
    /// </summary>
    public ImmutableList<ForeignMember> ForeignMembers { get; }

    /// <summary>
    /// Reads an airport map's combined GeoJSON.
    /// </summary>
    /// <param name="json">The GeoJSON FeatureCollection text.</param>
    /// <returns>The document over every feature of the map.</returns>
    /// <exception cref="System.Text.Json.JsonException">The text is malformed or is not a FeatureCollection of Features.</exception>
    public static AirportMapDocument Read(string json) => GeoJsonMapReader.Read(json);

    /// <summary>
    /// Writes the map as indented GeoJSON.
    /// </summary>
    /// <returns>The FeatureCollection text.</returns>
    public string Write() => GeoJsonMapWriter.Write(this);
}
