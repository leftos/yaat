namespace Yaat.Sim.Data.Airport;

/// <summary>What <see cref="GeoJsonParser"/> did with a ground-map feature it could not use.</summary>
public enum GroundMapIssueKind
{
    /// <summary>
    /// The feature's <c>type</c> property is missing or names no feature type the parser knows (UNV's <c>"patking"</c>); it is dropped.
    /// </summary>
    UnknownFeatureType,

    /// <summary>The feature has a known type but its geometry or properties could not be read; it is skipped.</summary>
    MalformedFeature,
}

/// <summary>
/// One feature of an airport ground map that <see cref="GeoJsonParser.ParseWithDiagnostics"/> left out of the layout.
/// </summary>
/// <param name="Kind">Why the feature was left out.</param>
/// <param name="FeatureIndex">The feature's 0-based position in the map's <c>features</c> array.</param>
/// <param name="FeatureType">The feature's <c>type</c> property as written (empty when it has none).</param>
/// <param name="FeatureName">The feature's <c>name</c> property, or null when it has none.</param>
/// <param name="Message">What went wrong, for a facility engineer.</param>
public sealed record GroundMapIssue(GroundMapIssueKind Kind, int FeatureIndex, string FeatureType, string? FeatureName, string Message);
