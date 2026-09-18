using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// Corpus guardrail: every JSON field a real vNAS scenario uses on an <c>aircraftGenerators[]</c> entry,
/// its <c>autoTrackConfiguration</c>, or an <c>aircraft[].autoTrackConditions</c> block must be modeled by
/// YAAT — an unmodeled field is silently dropped on deserialize (this is exactly how <c>interimAltitude</c>
/// was being lost). And every <c>weightCategory</c>/<c>engineType</c> string must map to a known enum value,
/// since <c>ResolveWeight</c>/<c>ResolveEngine</c> have a silent <c>_ =&gt;</c> fallback that would otherwise
/// mask a new upstream value. Skipped when the local corpus is absent.
/// </summary>
public class GeneratorAutoTrackRoundTripTests(ITestOutputHelper output)
{
    private static readonly string ScenariosRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "Scenarios")
    );

    private static readonly HashSet<string> GeneratorKeys = JsonKeysOf(typeof(ScenarioGeneratorConfig));
    private static readonly HashSet<string> AutoTrackKeys = JsonKeysOf(typeof(AutoTrackConditions));
    private static readonly HashSet<string> WeightNames = Enum.GetNames<WeightClass>().ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> EngineNames = Enum.GetNames<EngineKind>().ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> JsonKeysOf(Type t) =>
        t.GetProperties()
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EveryGeneratorAndAutoTrackFieldIsModeled_AndEnumsKnown()
    {
        if (!Directory.Exists(ScenariosRoot))
        {
            output.WriteLine($"No corpus at {ScenariosRoot} — skipping");
            return;
        }

        var files = Directory
            .EnumerateFiles(ScenariosRoot, "*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
        {
            output.WriteLine("Corpus dir present but empty — skipping");
            return;
        }

        var violations = new List<string>();
        int generators = 0;
        int autoTracks = 0;

        foreach (string? file in files)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(file));
            }
            catch (JsonException)
            {
                continue; // a malformed corpus file is not this test's concern
            }

            using (doc)
            {
                string name = Path.GetFileName(file);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("aircraftGenerators", out JsonElement gens) && gens.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement gen in gens.EnumerateArray())
                    {
                        generators++;
                        CheckObjectKeys(gen, GeneratorKeys, $"{name} aircraftGenerators[]", violations);
                        CheckEnum(gen, "engineType", EngineNames, $"{name} generator", violations);
                        CheckEnum(gen, "weightCategory", WeightNames, $"{name} generator", violations);
                        if (gen.TryGetProperty("autoTrackConfiguration", out JsonElement at) && at.ValueKind == JsonValueKind.Object)
                        {
                            autoTracks++;
                            CheckObjectKeys(at, AutoTrackKeys, $"{name} generator.autoTrackConfiguration", violations);
                        }
                    }
                }

                if (root.TryGetProperty("aircraft", out JsonElement acs) && acs.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement ac in acs.EnumerateArray())
                    {
                        if (ac.TryGetProperty("autoTrackConditions", out JsonElement at) && at.ValueKind == JsonValueKind.Object)
                        {
                            autoTracks++;
                            CheckObjectKeys(at, AutoTrackKeys, $"{name} aircraft.autoTrackConditions", violations);
                        }
                    }
                }
            }
        }

        output.WriteLine($"Checked {files.Count} scenarios: {generators} generators, {autoTracks} autotrack blocks");

        Assert.True(
            violations.Count == 0,
            $"{violations.Count} corpus field/enum violations (the model is dropping a field or an enum value is unmapped):\n"
                + string.Join("\n", violations.Take(50))
        );
    }

    private static void CheckObjectKeys(JsonElement obj, HashSet<string> known, string where, List<string> violations)
    {
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (!known.Contains(prop.Name))
            {
                violations.Add($"{where}: unmodeled field '{prop.Name}' (silently dropped on deserialize)");
            }
        }
    }

    private static void CheckEnum(JsonElement obj, string key, HashSet<string> known, string where, List<string> violations)
    {
        if (obj.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String)
        {
            string? s = v.GetString();
            if (s is not null && !known.Contains(s))
            {
                violations.Add($"{where}: {key}='{s}' is not a known enum value (silent fallback masks it)");
            }
        }
    }
}
