using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// ScopeProfileStore: the shipped profiles written when absent and never over an edit, a missing field taking the shipped
/// value, an unknown field kept, and a corrupt file set aside. Every test gets its own throwaway root directory.
/// </summary>
public class ScopeProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-profile-tests", Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ScopeProfileStore NewStore() => new(_root, _log);

    private string FileOf(string name) => Path.Combine(_root, $"{name}.json");

    [Fact]
    public void AbsentFiles_AreWritten()
    {
        NewStore().Get(ScopeProfileKind.Mixed);

        Assert.True(File.Exists(FileOf("stars")));
        Assert.True(File.Exists(FileOf("eram")));
        Assert.True(File.Exists(FileOf("mixed")));
        JsonObject written = JsonNode.Parse(File.ReadAllText(FileOf("eram")))!.AsObject();
        Assert.Equal("eram", (string?)written["kind"]);
    }

    [Fact]
    public void EditedFile_IsNotOverwritten_AndItsValuesLoad()
    {
        Directory.CreateDirectory(_root);
        JsonObject edited = JsonNode.Parse(File.ReadAllText(WriteShippedAndRead("stars")))!.AsObject();
        edited["template"] = "euroscope";
        File.WriteAllText(FileOf("stars"), edited.ToJsonString());

        ScopeProfile loaded = NewStore().Get(ScopeProfileKind.Stars);

        Assert.Equal("euroscope", loaded.Template);
        Assert.Equal("euroscope", (string?)JsonNode.Parse(File.ReadAllText(FileOf("stars")))!["template"]);
    }

    [Fact]
    public void FileMissingBands_TakesTheShippedBands()
    {
        JsonObject edited = JsonNode.Parse(File.ReadAllText(WriteShippedAndRead("mixed")))!.AsObject();
        edited.Remove("bands");
        edited["template"] = "stars";
        File.WriteAllText(FileOf("mixed"), edited.ToJsonString());

        ScopeProfile loaded = NewStore().Get(ScopeProfileKind.Mixed);

        Assert.Equal(ScopeProfileStore.Shipped(ScopeProfileKind.Mixed).Bands, loaded.Bands);
        Assert.Equal("stars", loaded.Template);
    }

    [Fact]
    public void UnknownField_SurvivesALoad()
    {
        JsonObject edited = JsonNode.Parse(File.ReadAllText(WriteShippedAndRead("eram")))!.AsObject();
        edited["colors"] = "navy";
        File.WriteAllText(FileOf("eram"), edited.ToJsonString());

        ScopeProfile loaded = NewStore().Get(ScopeProfileKind.Eram);

        Assert.Equal("navy", loaded.Extra!["colors"].GetString());
        Assert.Equal("navy", (string?)JsonNode.Parse(File.ReadAllText(FileOf("eram")))!["colors"]);
    }

    [Fact]
    public void CorruptFile_BecomesBak_AndTheShippedProfileLoads()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FileOf("mixed"), "{ not json");

        ScopeProfile loaded = NewStore().Get(ScopeProfileKind.Mixed);

        Assert.Equal(ScopeProfileStore.Shipped(ScopeProfileKind.Mixed).Bands, loaded.Bands);
        Assert.Equal("{ not json", File.ReadAllText(FileOf("mixed") + ".bak"));
        Assert.NotEmpty(_log.Warnings);
        Assert.Equal("mixed", (string?)JsonNode.Parse(File.ReadAllText(FileOf("mixed")))!["kind"]);
    }

    [Fact]
    public void BandMissingAField_TakesTheShippedBands_WithAWarning_AndNoBak()
    {
        JsonObject edited = JsonNode.Parse(File.ReadAllText(WriteShippedAndRead("mixed")))!.AsObject();
        edited["bands"]![0]!.AsObject().Remove("history");
        edited["template"] = "eram";
        File.WriteAllText(FileOf("mixed"), edited.ToJsonString());
        _log.Warnings.Clear();

        ScopeProfile loaded = NewStore().Get(ScopeProfileKind.Mixed);

        Assert.Equal(ScopeProfileStore.Shipped(ScopeProfileKind.Mixed).Bands, loaded.Bands);
        Assert.Equal("eram", loaded.Template);
        Assert.Single(_log.Warnings);
        Assert.False(File.Exists(FileOf("mixed") + ".bak"));
    }

    [Fact]
    public void ExplicitNullTemplate_TakesTheShippedTemplate_AndNoBak()
    {
        JsonObject edited = JsonNode.Parse(File.ReadAllText(WriteShippedAndRead("eram")))!.AsObject();
        edited["template"] = null;
        File.WriteAllText(FileOf("eram"), edited.ToJsonString());
        _log.Warnings.Clear();

        ScopeProfile loaded = NewStore().Get(ScopeProfileKind.Eram);

        Assert.Equal("eram", loaded.Template);
        Assert.Single(_log.Warnings);
        Assert.False(File.Exists(FileOf("eram") + ".bak"));
    }

    [Fact]
    public void UnknownKindWord_TakesTheShippedKind_WithAWarning()
    {
        JsonObject edited = JsonNode.Parse(File.ReadAllText(WriteShippedAndRead("stars")))!.AsObject();
        edited["kind"] = "tower";
        File.WriteAllText(FileOf("stars"), edited.ToJsonString());
        _log.Warnings.Clear();

        ScopeProfile loaded = NewStore().Get(ScopeProfileKind.Stars);

        Assert.Equal("stars", loaded.Kind);
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public void TopLevelFieldOfTheWrongType_BecomesBak()
    {
        JsonObject edited = JsonNode.Parse(File.ReadAllText(WriteShippedAndRead("stars")))!.AsObject();
        edited["bands"] = "none";
        File.WriteAllText(FileOf("stars"), edited.ToJsonString());

        ScopeProfile loaded = NewStore().Get(ScopeProfileKind.Stars);

        Assert.Equal(ScopeProfileStore.Shipped(ScopeProfileKind.Stars).Bands, loaded.Bands);
        Assert.True(File.Exists(FileOf("stars") + ".bak"));
    }

    [Fact]
    public void BandsWithANullElement_TakeTheShippedBands_WithAWarning_AndNoBak()
    {
        JsonObject edited = JsonNode.Parse(File.ReadAllText(WriteShippedAndRead("mixed")))!.AsObject();
        edited["bands"] = new JsonArray((JsonNode?)null);
        File.WriteAllText(FileOf("mixed"), edited.ToJsonString());
        _log.Warnings.Clear();

        ScopeProfile loaded = NewStore().Get(ScopeProfileKind.Mixed);

        Assert.Equal(ScopeProfileStore.Shipped(ScopeProfileKind.Mixed).Bands, loaded.Bands);
        Assert.Single(_log.Warnings);
        Assert.False(File.Exists(FileOf("mixed") + ".bak"));
    }

    [Fact]
    public void BandsNull_TakeTheShippedBands()
    {
        JsonObject edited = JsonNode.Parse(File.ReadAllText(WriteShippedAndRead("eram")))!.AsObject();
        edited["bands"] = null;
        File.WriteAllText(FileOf("eram"), edited.ToJsonString());

        ScopeProfile loaded = NewStore().Get(ScopeProfileKind.Eram);

        Assert.Equal(ScopeProfileStore.Shipped(ScopeProfileKind.Eram).Bands, loaded.Bands);
        Assert.False(File.Exists(FileOf("eram") + ".bak"));
    }

    [Fact]
    public void TemplateANumber_MovesTheFileToBak()
    {
        JsonObject edited = JsonNode.Parse(File.ReadAllText(WriteShippedAndRead("stars")))!.AsObject();
        edited["template"] = 5;
        File.WriteAllText(FileOf("stars"), edited.ToJsonString());

        ScopeProfile loaded = NewStore().Get(ScopeProfileKind.Stars);

        Assert.Equal(ScopeProfileStore.Shipped(ScopeProfileKind.Stars).Template, loaded.Template);
        Assert.True(File.Exists(FileOf("stars") + ".bak"));
    }

    /// <summary>Has a fresh store write the shipped files, and returns the path of the one named <paramref name="name"/>.</summary>
    private string WriteShippedAndRead(string name)
    {
        new ScopeProfileStore(_root, _log).Get(ScopeProfileKind.Mixed);
        return FileOf(name);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
