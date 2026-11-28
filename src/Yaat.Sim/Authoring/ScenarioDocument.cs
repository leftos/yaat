using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring;

/// <summary>
/// A vNAS scenario read into the typed model so the editor can change it, and written back JSON-equal to the source:
/// every field vNAS sent is kept (unmodelled ones in each model's extension data), and a member the source left out is
/// written only once code gives it a value other than the model's default.
/// </summary>
/// <remarks>
/// The read is case-insensitive and skips comments like <see cref="ScenarioLoader"/>, so a source key differing only in
/// case from a modelled name is written in the model's casing, and comments are dropped.
/// </remarks>
public sealed class ScenarioDocument
{
    // Per object read from JSON: the JSON names of the members its source held. An object not in the table was built in
    // code and writes every member.
    private static readonly ConditionalWeakTable<object, HashSet<string>> PresentMembers = [];

    private static readonly JsonSerializerOptions ValueOptions = new();

    private static readonly DefaultJsonTypeInfoResolver Resolver = new() { Modifiers = { TrackPresence } };

    private static readonly JsonSerializerOptions ReadOptions = new(ScenarioLoader.JsonOptions) { TypeInfoResolver = Resolver };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, TypeInfoResolver = Resolver };

    private ScenarioDocument(Scenario scenario)
    {
        Scenario = scenario;
    }

    /// <summary>
    /// Gets the typed scenario the editor changes in place.
    /// </summary>
    public Scenario Scenario { get; }

    /// <summary>
    /// Reads a vNAS scenario JSON, remembering which members each object's source held. The empty numbers an older
    /// YAATEdit file holds as null are repaired first (see <see cref="ScenarioNullNormalizer.Repair"/>), so such a file
    /// reads; a text that cannot be repaired is read exactly as it always was.
    /// </summary>
    /// <param name="json">The scenario JSON.</param>
    /// <returns>The document over the read scenario.</returns>
    /// <exception cref="JsonException">The JSON is malformed, is not a scenario object, or is <c>null</c>.</exception>
    public static ScenarioDocument Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        Scenario scenario =
            JsonSerializer.Deserialize<Scenario>(ScenarioNullNormalizer.Repair(json, out _), ReadOptions)
            ?? throw new JsonException("Scenario JSON is null; expected an object.");
        return new ScenarioDocument(scenario);
    }

    /// <summary>
    /// Wraps a scenario built in code; every member of an object not read from JSON is written.
    /// </summary>
    /// <param name="scenario">The scenario to wrap.</param>
    /// <returns>The document over <paramref name="scenario"/>.</returns>
    public static ScenarioDocument FromScenario(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return new ScenarioDocument(scenario);
    }

    /// <summary>
    /// Writes the scenario as indented vNAS JSON: a member of an object read from JSON is written when its source held
    /// it or its value differs from the model's default; an object built in code writes every member.
    /// </summary>
    /// <returns>The scenario JSON.</returns>
    public string Write() => JsonSerializer.Serialize(Scenario, WriteOptions);

    private static void TrackPresence(JsonTypeInfo typeInfo)
    {
        // Every vNAS model class carries extension data; the loader-derived and wire-only types in the namespace do not.
        if (
            (typeInfo.Kind != JsonTypeInfoKind.Object)
            || (typeInfo.Type.Namespace != typeof(Scenario).Namespace)
            || !typeInfo.Properties.Any(p => p.IsExtensionData)
        )
        {
            return;
        }

        Action<object>? onDeserializing = typeInfo.OnDeserializing;
        typeInfo.OnDeserializing = owner =>
        {
            onDeserializing?.Invoke(owner);
            PresentMembers.AddOrUpdate(owner, new HashSet<string>(StringComparer.Ordinal));
        };

        object fresh =
            Activator.CreateInstance(typeInfo.Type)
            ?? throw new InvalidOperationException($"{typeInfo.Type.Name} could not be constructed; its default member values are unknown");
        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (!property.IsExtensionData)
            {
                TrackProperty(property, fresh);
            }
        }
    }

    private static void TrackProperty(JsonPropertyInfo property, object fresh)
    {
        string name = property.Name;
        Type type = property.PropertyType;
        Action<object, object?> set =
            property.Set
            ?? throw new InvalidOperationException($"{property.DeclaringType.Name}.{property.Name} has no setter; presence cannot be tracked");
        property.Set = (owner, value) =>
        {
            set(owner, value);
            MarkPresent(owner, name);
        };

        Func<object, object?>? get = property.Get;
        var defaultValue = new Lazy<JsonNode?>(() => get is null ? null : ToNode(get(fresh), type));
        property.ShouldSerialize = PresenceAwareShouldSerialize(name, type, defaultValue, property.ShouldSerialize);
    }

    // A member of an object read from JSON is written when its source held it or its value differs from a fresh
    // instance's; any other object keeps the resolver's own rule (its JsonIgnore conditions).
    private static Func<object, object?, bool> PresenceAwareShouldSerialize(
        string name,
        Type type,
        Lazy<JsonNode?> defaultValue,
        Func<object, object?, bool>? original
    ) =>
        (owner, value) =>
            PresentMembers.TryGetValue(owner, out HashSet<string>? present)
                ? (present.Contains(name) || !JsonNode.DeepEquals(ToNode(value, type), defaultValue.Value))
                : (original?.Invoke(owner, value) ?? true);

    private static void MarkPresent(object owner, string name)
    {
        if (PresentMembers.TryGetValue(owner, out HashSet<string>? present))
        {
            present.Add(name);
        }
    }

    private static JsonNode? ToNode(object? value, Type type) => JsonSerializer.SerializeToNode(value, type, ValueOptions);
}
