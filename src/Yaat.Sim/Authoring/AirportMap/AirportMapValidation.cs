using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Authoring.AirportMap;

/// <summary>One issue the editor reports for an airport map, with the document feature it belongs to where one is known.</summary>
/// <param name="Code">
/// What is wrong: the parser's <c>unknown-feature-type</c> or <c>malformed-feature</c>, <c>parse-failure</c> when the map did
/// not parse at all, or a <see cref="ValidationWarning"/> code such as <c>orphan-node</c>.
/// </param>
/// <param name="Message">What went wrong, for a facility engineer.</param>
/// <param name="FeatureIndex">The index of the feature in <see cref="AirportMapDocument.Features"/> the issue belongs to, or null.</param>
public sealed record AirportMapIssue(string Code, string Message, int? FeatureIndex);

/// <summary>
/// What validating an <see cref="AirportMapDocument"/> found: the ground layout the map parsed into, or null when it did not
/// parse, and every issue, in parser order then validator order.
/// </summary>
/// <param name="Layout">The parsed layout, or null when the map did not parse.</param>
/// <param name="Issues">The parser's dropped features, a parse failure, and the layout validator's warnings.</param>
public sealed record AirportMapValidationResult(AirportGroundLayout? Layout, IReadOnlyList<AirportMapIssue> Issues);

/// <summary>
/// Builds the ground layout an airport map describes and reports what is wrong with it, as the editor's live validation does:
/// <see cref="GeoJsonParser.ParseWithDiagnostics"/> for the map and <see cref="LayoutValidator"/> for the graph, each issue
/// carrying the document feature it belongs to where one can be resolved.
/// </summary>
public static partial class AirportMapValidation
{
    private static readonly ILogger Log = SimLog.CreateLogger("AirportMapValidation");

    /// <summary>
    /// Parses and validates a map document.
    /// </summary>
    /// <param name="document">The map to validate.</param>
    /// <param name="airportId">The airport id the layout is built for.</param>
    /// <param name="runwayAirportCode">The airport code runway widths are looked up by, or null for the default width.</param>
    /// <param name="inputs">The airport's stand lead-ins, spot-edge chains and tower-cab videomap.</param>
    /// <returns>The parsed layout and the issues found.</returns>
    public static AirportMapValidationResult Run(AirportMapDocument document, string airportId, string? runwayAirportCode, LayoutInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(airportId);
        ArgumentNullException.ThrowIfNull(inputs);
        if (FindUnreadableFeature(document) is { } unreadable)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(
                    "Validating {Airport}: feature {Feature} could not be parsed: {Message}",
                    airportId,
                    unreadable.FeatureIndex,
                    unreadable.Message
                );
            }

            return new AirportMapValidationResult(null, [unreadable]);
        }

        AirportGroundLayout layout;
        IReadOnlyList<GroundMapIssue> mapIssues;
        try
        {
            (layout, mapIssues) = GeoJsonParser.ParseWithDiagnostics(airportId, document.Write(), runwayAirportCode, inputs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Live validation runs after every edit: the parser's whole surface over user-edited input is reported, never thrown.
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Validating {Airport}: the map did not parse", airportId);
            }

            return new AirportMapValidationResult(
                null,
                [new AirportMapIssue("parse-failure", $"the airport map could not be parsed: {ex.Message}", null)]
            );
        }

        List<ValidationWarning> warnings = new LayoutValidator(layout).Validate();
        Dictionary<int, int> featuresByNode = MapNodesToFeatures(layout, document);
        var issues = new List<AirportMapIssue>(mapIssues.Count + warnings.Count);
        foreach (GroundMapIssue issue in mapIssues)
        {
            issues.Add(
                new AirportMapIssue(
                    (issue.Kind == GroundMapIssueKind.UnknownFeatureType) ? "unknown-feature-type" : "malformed-feature",
                    issue.Message,
                    issue.FeatureIndex
                )
            );
        }

        foreach (ValidationWarning warning in warnings)
        {
            issues.Add(new AirportMapIssue(warning.Code, warning.Message, ResolveFeature(warning.Message, featuresByNode)));
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Validated {Airport}: {FeatureCount} features, {IssueCount} issues", airportId, document.Features.Count, issues.Count);
        }

        return new AirportMapValidationResult(layout, issues);
    }

    // The features whose written form makes the parser throw before it classifies anything: one with no properties member (or
    // a properties member that is not an object), or one with no geometry member. Naming the feature is what a facility
    // engineer needs; a geometry that is not an object is instead a per-feature malformed issue from the parser itself.
    private static AirportMapIssue? FindUnreadableFeature(AirportMapDocument document)
    {
        for (int index = 0; index < document.Features.Count; index++)
        {
            MapFeature feature = document.Features[index];
            if (!feature.HasProperties)
            {
                return new AirportMapIssue("parse-failure", $"Feature {index} has no properties.", index);
            }

            if ((feature.GeometryType is null) && !HasMember(feature, "geometry"))
            {
                return new AirportMapIssue("parse-failure", $"Feature {index} has no geometry.", index);
            }
        }

        return null;
    }

    private static bool HasMember(MapFeature feature, string name) =>
        feature.ForeignMembers.Any(member => string.Equals(member.Name, name, StringComparison.Ordinal));

    // Every layout node by its exact position, so a warning naming a node id can name the document feature whose vertex the
    // node was built from (the writer emits and the parser reads the same double for an unedited feature).
    private static Dictionary<int, int> MapNodesToFeatures(AirportGroundLayout layout, AirportMapDocument document)
    {
        var nodesByPosition = new Dictionary<(double Lat, double Lon), List<int>>();
        foreach (GroundNode node in layout.Nodes.Values)
        {
            (double Lat, double Lon) key = (node.Position.Lat, node.Position.Lon);
            if (!nodesByPosition.TryGetValue(key, out List<int>? ids))
            {
                ids = [];
                nodesByPosition[key] = ids;
            }

            ids.Add(node.Id);
        }

        var featuresByNode = new Dictionary<int, int>();
        for (int featureIndex = 0; featureIndex < document.Features.Count; featureIndex++)
        {
            foreach (MapPosition position in document.Features[featureIndex].Coordinates)
            {
                if (!nodesByPosition.TryGetValue((position.Lat, position.Lon), out List<int>? ids))
                {
                    continue;
                }

                foreach (int id in ids)
                {
                    featuresByNode.TryAdd(id, featureIndex);
                }
            }
        }

        return featuresByNode;
    }

    private static int? ResolveFeature(string message, Dictionary<int, int> featuresByNode)
    {
        Match match = NodeIdRegex().Match(message);
        if (!match.Success)
        {
            return null;
        }

        return (
            int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int nodeId)
            && featuresByNode.TryGetValue(nodeId, out int featureIndex)
        )
            ? featureIndex
            : null;
    }

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex NodeIdRegex();
}
