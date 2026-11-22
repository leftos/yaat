using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="WorkingCopyExporter"/> turns a working copy's text into the vNAS JSON an author uploads: the full export
/// keeps every field and names each YAAT-only part it kept, the clean export drops the fields YAAT does not model and
/// the YAAT-only parts and names each one it dropped, and a copy with validation errors is refused.
/// </summary>
public class WorkingCopyExporterTests
{
    private const string Zoa = TrainingFixtures.Artcc;

    private static readonly string ExamplesDir = Path.Combine(TickRecorder.FindRepoRoot(), "docs", "atctrainer-scenario-examples");

    // A corpus scenario with two aircraft generators and no preset commands.
    private static readonly string GeneratorScenarioPath = Path.Combine(ExamplesDir, "01H08FSWF5NCDXTQMQ3BWB0BND.json");

    private static readonly Func<Scenario, IReadOnlyList<ExportIssue>> NoChecks = _ => [];

    public WorkingCopyExporterTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static JsonObject Source() => JsonNode.Parse(File.ReadAllText(GeneratorScenarioPath))!.AsObject();

    // The corpus scenario with an unmodelled field at the root, in an aircraft and in a generator.
    private static JsonObject WithUnmodelledFields()
    {
        JsonObject source = Source();
        source["unmodelledRootField"] = new JsonObject { ["nested"] = "kept" };
        source["aircraft"]![0]!["unmodelledAircraftField"] = 7;
        source["aircraftGenerators"]![0]!["unmodelledGeneratorField"] = true;
        return source;
    }

    // The corpus scenario with every kind of YAAT-only part: a VFR arrival generator with an id and one without, an
    // overflight generator, liveSession true and the first aircraft generator's enabled flag.
    private static JsonObject WithYaatOnlyParts()
    {
        JsonObject source = Source();
        source["vfrArrivalGenerators"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "VFR-NORTH",
                ["bearingFrom"] = 340,
                ["bearingTo"] = 20,
            },
            new JsonObject
            {
                ["id"] = "",
                ["bearingFrom"] = 90,
                ["bearingTo"] = 180,
            }
        );
        source["overflightGenerators"] = new JsonArray(new JsonObject { ["id"] = "OVER-1", ["fromBearingFrom"] = 0 });
        source["liveSession"] = true;
        source["aircraftGenerators"]![0]!["enabled"] = false;
        return source;
    }

    private static string GeneratorId(JsonObject source, int index) => source["aircraftGenerators"]![index]!["id"]!.GetValue<string>();

    private static ExportResult Written(string text, ExportKind kind)
    {
        ExportResult result = WorkingCopyExporter.Prepare(text, kind, NoChecks);
        Assert.Equal(ExportOutcome.Written, result.Outcome);
        Assert.NotNull(result.Json);
        return result;
    }

    [Fact]
    public void Export_Full_IsJsonEqualToTheWorkingCopyRewrite_UnmodelledFieldIncluded()
    {
        string text = WithUnmodelledFields().ToJsonString();

        ExportResult result = Written(text, ExportKind.Full);

        JsonNode exported = JsonNode.Parse(result.Json!)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ScenarioDocument.Read(text).Write()), exported));
        Assert.Equal("kept", exported["unmodelledRootField"]!["nested"]!.GetValue<string>());
        Assert.Equal(7, exported["aircraft"]![0]!["unmodelledAircraftField"]!.GetValue<int>());
        Assert.Empty(result.Parts);
        Assert.Empty(result.Issues);
        Assert.Null(WorkingCopyExporter.DescribeParts(ExportKind.Full, result.Parts));
    }

    [Fact]
    public void Export_Full_YaatOnlyParts_KeptAndListedEach()
    {
        JsonObject source = WithYaatOnlyParts();
        string text = source.ToJsonString();

        ExportResult result = Written(text, ExportKind.Full);

        JsonNode exported = JsonNode.Parse(result.Json!)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ScenarioDocument.Read(text).Write()), exported));
        Assert.Equal(2, exported["vfrArrivalGenerators"]!.AsArray().Count);
        Assert.Single(exported["overflightGenerators"]!.AsArray());
        Assert.True(exported["liveSession"]!.GetValue<bool>());
        Assert.False(exported["aircraftGenerators"]![0]!["enabled"]!.GetValue<bool>());
        YaatOnlyPart[] expected =
        [
            new("vfrArrivalGenerator", "VFR-NORTH"),
            new("vfrArrivalGenerator", "#1"),
            new("overflightGenerator", "OVER-1"),
            new("liveSession", "liveSession"),
            new("generatorEnabled", GeneratorId(source, 0)),
        ];
        Assert.Equal(expected, result.Parts);
        string warning = WorkingCopyExporter.DescribeParts(ExportKind.Full, result.Parts)!;
        Assert.StartsWith("Kept", warning, StringComparison.Ordinal);
        Assert.Contains("VFR-NORTH", warning, StringComparison.Ordinal);
        Assert.Contains("OVER-1", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_Clean_DropsUnmodelledFieldsAndYaatOnlyParts_ListsEach()
    {
        JsonObject source = WithUnmodelledFields();
        source["aircraftGenerators"]![1]!["enabled"] = true;
        source["vfrArrivalGenerators"] = new JsonArray();
        source["overflightGenerators"] = new JsonArray(new JsonObject { ["id"] = "OVER-1" });
        source["liveSession"] = false;

        ExportResult result = Written(source.ToJsonString(), ExportKind.Clean);

        JsonObject exported = JsonNode.Parse(result.Json!)!.AsObject();
        Assert.False(exported.ContainsKey("unmodelledRootField"));
        Assert.False(exported["aircraft"]![0]!.AsObject().ContainsKey("unmodelledAircraftField"));
        Assert.False(exported["aircraftGenerators"]![0]!.AsObject().ContainsKey("unmodelledGeneratorField"));
        Assert.False(exported.ContainsKey("vfrArrivalGenerators"));
        Assert.False(exported.ContainsKey("overflightGenerators"));
        Assert.False(exported.ContainsKey("liveSession"));
        Assert.All(exported["aircraftGenerators"]!.AsArray(), g => Assert.False(g!.AsObject().ContainsKey("enabled")));
        Assert.Equal(source["aircraft"]!.AsArray().Count, exported["aircraft"]!.AsArray().Count);
        Assert.Equal(source["name"]!.GetValue<string>(), exported["name"]!.GetValue<string>());
        YaatOnlyPart[] expected = [new("overflightGenerator", "OVER-1"), new("generatorEnabled", GeneratorId(source, 1))];
        Assert.Equal(expected, result.Parts);
        string warning = WorkingCopyExporter.DescribeParts(ExportKind.Clean, result.Parts)!;
        Assert.StartsWith("Dropped", warning, StringComparison.Ordinal);
        Assert.Contains("OVER-1", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_ValidationError_Refuses_WritesNothing()
    {
        JsonObject source = Source();
        source["aircraft"]![0]!["presetCommands"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "p1",
                ["command"] = "XYZZY PLUGH",
                ["timeOffset"] = 0,
            }
        );
        string dir = TrainingFixtures.NewRoot();
        string path = Path.Combine(dir, "export.json");
        try
        {
            ExportResult result = WorkingCopyExporter.Prepare(source.ToJsonString(), ExportKind.Full, WorkingCopyExporter.DefaultChecks);

            Assert.Equal(ExportOutcome.Refused, result.Outcome);
            Assert.Null(result.Json);
            ExportIssue error = Assert.Single(result.Issues, i => i.Severity == ExportIssueSeverity.Error);
            Assert.Contains("XYZZY PLUGH", error.Message, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => WorkingCopyExporter.WriteTo(result, path));
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            TrainingFixtures.DeleteDir(dir);
        }
    }

    [Fact]
    public void Export_ValidationWarning_WritesWithTheWarning()
    {
        JsonObject source = Source();
        JsonNode aircraft = source["aircraft"]![0]!;
        string filed = aircraft["flightplan"]!["aircraftType"]!.GetValue<string>();
        string other = filed.Contains("B738", StringComparison.Ordinal) ? "A320" : "B738";
        aircraft["aircraftType"] = other;

        ExportResult result = WorkingCopyExporter.Prepare(source.ToJsonString(), ExportKind.Full, WorkingCopyExporter.DefaultChecks);

        Assert.Equal(ExportOutcome.Written, result.Outcome);
        Assert.NotNull(result.Json);
        Assert.DoesNotContain(result.Issues, i => i.Severity == ExportIssueSeverity.Error);
        Assert.Contains(result.Issues, i => (i.Severity == ExportIssueSeverity.Warning) && i.Message.Contains(other, StringComparison.Ordinal));
    }

    [Fact]
    public void Export_CorruptText_IsCorrupt()
    {
        string text = File.ReadAllText(GeneratorScenarioPath)[..200];

        ExportResult result = WorkingCopyExporter.Prepare(text, ExportKind.Clean, _ => throw new InvalidOperationException("checks ran"));

        Assert.Equal(ExportOutcome.Corrupt, result.Outcome);
        Assert.Null(result.Json);
        Assert.Empty(result.Parts);
        ExportIssue issue = Assert.Single(result.Issues);
        Assert.Equal(ExportIssueSeverity.Error, issue.Severity);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
    }

    [Fact]
    public void EveryModelTypeReachableFromScenario_HasExtensionData()
    {
        var reached = new HashSet<Type>();
        var pending = new Queue<Type>([typeof(Scenario)]);
        while (pending.TryDequeue(out Type? type))
        {
            if (!reached.Add(type))
            {
                continue;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if ((property.GetGetMethod() is not null) && !property.IsDefined(typeof(JsonExtensionDataAttribute)))
                {
                    EnqueueModelTypes(property.PropertyType, pending);
                }
            }
        }

        Assert.Contains(typeof(OverflightGeneratorConfig), reached);
        Assert.Contains(typeof(AutoTrackConditions), reached);
        Assert.All(
            reached,
            type =>
                Assert.True(
                    type.GetProperties().Any(p => p.IsDefined(typeof(JsonExtensionDataAttribute))),
                    $"{type.Name} has no [JsonExtensionData] property, so the clean export cannot strip its unmodelled fields"
                )
        );
    }

    // Every scenario model class a property type names: itself, an array's element, and each generic argument at any
    // depth (a list's item, a dictionary's key and value, a list of lists).
    private static void EnqueueModelTypes(Type type, Queue<Type> pending)
    {
        if (type.IsArray)
        {
            EnqueueModelTypes(type.GetElementType()!, pending);
            return;
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                EnqueueModelTypes(argument, pending);
            }
        }

        if (type.IsClass && (type.Namespace == typeof(Scenario).Namespace))
        {
            pending.Enqueue(type);
        }
    }

    [Fact]
    public void Export_UnmodelledFieldsTwoLevelsDown_CleanStripsThem_FullKeepsThem()
    {
        JsonObject source = Source();
        JsonObject aircraft = source["aircraft"]![0]!.AsObject();
        aircraft["startingConditions"]!["unmodelledStartField"] = 1;
        aircraft["flightplan"]!["unmodelledPlanField"] = "two";
        aircraft["autoTrackConditions"] ??= new JsonObject { ["positionId"] = "1F" };
        aircraft["autoTrackConditions"]!["unmodelledTrackField"] = true;
        string text = source.ToJsonString();

        JsonNode full = JsonNode.Parse(Written(text, ExportKind.Full).Json!)!["aircraft"]![0]!;
        JsonNode clean = JsonNode.Parse(Written(text, ExportKind.Clean).Json!)!["aircraft"]![0]!;

        Assert.Equal(1, full["startingConditions"]!["unmodelledStartField"]!.GetValue<int>());
        Assert.Equal("two", full["flightplan"]!["unmodelledPlanField"]!.GetValue<string>());
        Assert.True(full["autoTrackConditions"]!["unmodelledTrackField"]!.GetValue<bool>());
        Assert.False(clean["startingConditions"]!.AsObject().ContainsKey("unmodelledStartField"));
        Assert.False(clean["flightplan"]!.AsObject().ContainsKey("unmodelledPlanField"));
        Assert.False(clean["autoTrackConditions"]!.AsObject().ContainsKey("unmodelledTrackField"));
    }

    [Fact]
    public void Export_Clean_IsTheFullExportWithTheStrippedKeysRemoved()
    {
        JsonObject source = WithYaatOnlyParts();
        source["unmodelledRootField"] = new JsonObject { ["nested"] = "dropped" };
        source["aircraft"]![0]!["unmodelledAircraftField"] = 7;
        source["aircraftGenerators"]![1]!["unmodelledGeneratorField"] = true;
        string text = source.ToJsonString();

        JsonObject full = JsonNode.Parse(Written(text, ExportKind.Full).Json!)!.AsObject();
        JsonNode clean = JsonNode.Parse(Written(text, ExportKind.Clean).Json!)!;

        foreach (string key in (string[])["vfrArrivalGenerators", "overflightGenerators", "liveSession", "unmodelledRootField"])
        {
            Assert.True(full.Remove(key), key);
        }

        Assert.True(full["aircraft"]![0]!.AsObject().Remove("unmodelledAircraftField"));
        Assert.True(full["aircraftGenerators"]![0]!.AsObject().Remove("enabled"));
        Assert.True(full["aircraftGenerators"]![1]!.AsObject().Remove("unmodelledGeneratorField"));
        Assert.True(JsonNode.DeepEquals(full, clean), clean.ToJsonString());
    }

    [Fact]
    public void Export_ObjectWithNoIdOrName_IsCorrupt()
    {
        ExportResult result = WorkingCopyExporter.Prepare("{}", ExportKind.Full, _ => throw new InvalidOperationException("checks ran"));

        Assert.Equal(ExportOutcome.Corrupt, result.Outcome);
        Assert.Null(result.Json);
        Assert.Equal(ExportIssueSeverity.Error, Assert.Single(result.Issues).Severity);
    }

    [Fact]
    public void ToLf_ChangesOnlyTheLineBreaks()
    {
        string crlf = "{\r\n  \"remarks\": \"line one\\r\\nline two\",\r\n  \"name\": \"x\"\r\n}";

        string lf = WorkingCopyExporter.ToLf(crlf);

        Assert.Equal("{\n  \"remarks\": \"line one\\r\\nline two\",\n  \"name\": \"x\"\n}", lf);
        Assert.Equal("line one\r\nline two", JsonNode.Parse(lf)!["remarks"]!.GetValue<string>());
    }

    [Fact]
    public void WriteTo_WritesUtf8NoBomLf()
    {
        JsonObject source = Source();
        source["name"] = "Zürich — export";
        ExportResult result = Written(source.ToJsonString(), ExportKind.Full);
        string dir = TrainingFixtures.NewRoot();
        string path = Path.Combine(dir, "nested", "export.json");
        try
        {
            WorkingCopyExporter.WriteTo(result, path);

            byte[] bytes = File.ReadAllBytes(path);
            Assert.False((bytes.Length >= 3) && (bytes[0] == 0xEF) && (bytes[1] == 0xBB) && (bytes[2] == 0xBF), "the file starts with a BOM");
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.Contains((byte)'\n', bytes);
            string written = new UTF8Encoding(false, true).GetString(bytes);
            Assert.Equal(result.Json, written);
            Assert.Equal("Zürich — export", ScenarioDocument.Read(written).Scenario.Name);
        }
        finally
        {
            TrainingFixtures.DeleteDir(dir);
        }
    }

    [Fact]
    public void Export_WritesOnlyTheGivenPath_NeverUnderTheStoreOrCache()
    {
        string dir = TrainingFixtures.NewRoot();
        string root = Path.Combine(dir, "scenarios");
        string path = Path.Combine(dir, "out", "export.json");
        try
        {
            var store = new LocalScenarioStore(root);
            string id = TrainingFixtures.IdA;
            store.ApplyServerBody(Zoa, id, TrainingFixtures.Read(id));
            store.CreateWorkingCopy(Zoa, id);
            string[] before = [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

            ExportResult result = WorkingCopyExporter.Prepare(store.ReadWorkingCopyText(Zoa, id), ExportKind.Clean, NoChecks);
            WorkingCopyExporter.WriteTo(result, path);

            string[] after = [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
            Assert.Equal([.. before.Append(path).Order(StringComparer.Ordinal)], after);
            Assert.False(Directory.Exists(Path.Combine(root, "cache")));
        }
        finally
        {
            TrainingFixtures.DeleteDir(dir);
        }
    }
}
