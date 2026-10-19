using System.Reflection;
using System.Text.Json;
using Xunit.Sdk;
using Xunit.v3;

[assembly: Xunit.TestCollectionOrderer(typeof(Yaat.Sim.Tests.CollectionWeightOrderer))]

namespace Yaat.Sim.Tests;

/// <summary>
/// Starts the heaviest test collections first so the parallel run's slow tail shrinks: the runner
/// hands collections to its worker pool in this order and starts each next one as a lane frees up,
/// which makes longest-first the greedy schedule the suite wants. Weights come from the committed
/// <c>collection-weights.json</c> (rebuilt by <c>tools/refresh-sim-collection-weights.py</c>); a
/// collection missing from it weighs 0 and runs after the known ones, and every tie sorts by key so
/// the order is deterministic from run to run.
/// </summary>
internal sealed class CollectionWeightOrderer : ITestCollectionOrderer
{
    internal const string DefaultCollectionPrefix = "Test collection for ";
    internal const string TableResourceFileName = "collection-weights.json";
    internal const string ExpectedTableShape = """{ "collections": { "<collection key>": <whole seconds> } }""";

    private const string UniqueIdInfix = " (id: ";

    private static readonly Lazy<IReadOnlyDictionary<string, int>> LoadedWeights = new(LoadWeights);

    /// <summary>The committed weight table, keyed by <see cref="CollectionKey"/>, in whole seconds.</summary>
    internal static IReadOnlyDictionary<string, int> Weights => LoadedWeights.Value;

    /// <inheritdoc/>
    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : ITestCollection => Order(testCollections, collection => CollectionKey(collection), Weights);

    /// <summary>Orders collections by weight descending, then by key ordinal ascending.</summary>
    /// <typeparam name="T">The collection type.</typeparam>
    /// <param name="collections">The collections to order.</param>
    /// <param name="keyOf">The weight-table key to look each collection up by.</param>
    /// <param name="weights">The weight table; a key it lacks weighs 0.</param>
    /// <returns>The same collections, heaviest first.</returns>
    internal static IReadOnlyList<T> Order<T>(IReadOnlyCollection<T> collections, Func<T, string> keyOf, IReadOnlyDictionary<string, int> weights) =>
        [
            .. collections
                .Select(collection => (Collection: collection, Key: keyOf(collection)))
                .OrderByDescending(entry => weights.GetValueOrDefault(entry.Key))
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => entry.Collection),
        ];

    /// <summary>The weight-table key for a collection.</summary>
    /// <param name="collection">The collection to key.</param>
    /// <returns>
    /// The key <c>tools/refresh-sim-collection-weights.py</c> writes for it: the class full name for a
    /// default (per-class) collection, the collection name for a <c>[Collection]</c> one.
    /// </returns>
    internal static string CollectionKey(ITestCollection collection) => CollectionKeyFromDisplayName(collection.TestCollectionDisplayName);

    /// <summary>
    /// The key for a collection display name. xunit names a per-class collection
    /// <c>"Test collection for &lt;class full name&gt; (id: &lt;uid&gt;)"</c> and a named one by its
    /// name alone, so the wrapper is stripped here and by the refresh script alike.
    /// </summary>
    /// <param name="displayName">The collection's display name.</param>
    /// <returns>The weight-table key.</returns>
    internal static string CollectionKeyFromDisplayName(string displayName)
    {
        if (!displayName.StartsWith(DefaultCollectionPrefix, StringComparison.Ordinal))
        {
            return displayName;
        }

        string key = displayName[DefaultCollectionPrefix.Length..];
        int idStart = key.LastIndexOf(UniqueIdInfix, StringComparison.Ordinal);
        return idStart >= 0 ? key[..idStart] : key;
    }

    /// <summary>Reads a weight table, failing with the table's shape and the command that rebuilds it.</summary>
    /// <param name="stream">The table's JSON.</param>
    /// <param name="resourceName">The table's embedded resource name, for the error message.</param>
    /// <returns>The weights by collection key.</returns>
    /// <exception cref="InvalidOperationException">The table is not readable, or not shaped as expected.</exception>
    internal static IReadOnlyDictionary<string, int> ParseWeights(Stream stream, string resourceName)
    {
        try
        {
            using var document = JsonDocument.Parse(stream);
            var weights = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.GetProperty("collections").EnumerateObject())
            {
                weights[property.Name] = property.Value.GetInt32();
            }

            return weights;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException(
                $"Embedded resource {resourceName} is not a weight table ({exception.Message}). It must hold {ExpectedTableShape}, "
                    + "with whole seconds per collection key. Regenerate it with "
                    + "`uv run --no-project python tools/refresh-sim-collection-weights.py <trx>` (that script's docstring has the TRX command).",
                exception
            );
        }
    }

    private static IReadOnlyDictionary<string, int> LoadWeights()
    {
        Assembly assembly = typeof(CollectionWeightOrderer).Assembly;
        string resourceName = $"{assembly.GetName().Name}.{TableResourceFileName}";
        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource {resourceName} is missing: it is the collection orderer's weight table, which must hold {ExpectedTableShape}. "
                    + "Add tests/Yaat.Sim.Tests/collection-weights.json back as an EmbeddedResource in Yaat.Sim.Tests.csproj and regenerate it with "
                    + "`uv run --no-project python tools/refresh-sim-collection-weights.py <trx>`."
            );

        return ParseWeights(stream, resourceName);
    }
}
