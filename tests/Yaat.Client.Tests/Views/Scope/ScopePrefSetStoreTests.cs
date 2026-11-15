using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Client.Views.Scope.Prefs;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// ScopePrefSetStore unit tests. Every test gets its own throwaway root directory, so tests are isolated from each
/// other and from the per-process YAAT_APPDATA_DIR the rest of the project shares.
/// </summary>
public class ScopePrefSetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-prefset-tests", Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SaveThenLoad_ReturnsTheSet()
    {
        NewStore().SaveSet("ZOA", Set("SR-DAY", 37.5));

        ScopePrefSet loaded = Assert.Single(NewStore().LoadSets("ZOA"));

        Assert.Equal("SR-DAY", loaded.Name);
        Assert.Equal(37.5, loaded.RangeNm);
    }

    [Fact]
    public void SecondSet_AppendsAfterTheFirst()
    {
        ScopePrefSetStore store = NewStore();
        store.SaveSet("ZOA", Set("SR-DAY"));
        store.SaveSet("ZOA", Set("SR-NIGHT"));

        Assert.Equal(["SR-DAY", "SR-NIGHT"], store.LoadSets("ZOA").Select(set => set.Name));
    }

    [Fact]
    public void SameNameInOtherCase_ReplacesInPlace()
    {
        ScopePrefSetStore store = NewStore();
        store.SaveSet("ZOA", Set("SR-DAY"));
        store.SaveSet("ZOA", Set("SR-NIGHT"));
        store.SaveSet("ZOA", Set("sr-day", 55));

        IReadOnlyList<ScopePrefSet> sets = store.LoadSets("ZOA");

        Assert.Equal(["SR-DAY", "SR-NIGHT"], sets.Select(set => set.Name));
        Assert.Equal(55.0, sets[0].RangeNm);
    }

    [Fact]
    public void Rename_KeepsPosition()
    {
        ScopePrefSetStore store = NewStore();
        store.SaveSet("ZOA", Set("SR-DAY"));
        store.SaveSet("ZOA", Set("SR-NIGHT"));
        store.SaveSet("ZOA", Set("SR-DUSK"));

        Assert.True(store.RenameSet("ZOA", "SR-NIGHT", "sr-evening"));

        Assert.Equal(["SR-DAY", "SR-EVENING", "SR-DUSK"], store.LoadSets("ZOA").Select(set => set.Name));
    }

    [Fact]
    public void Rename_ToATakenName_IsFalse_AndChangesNothing()
    {
        ScopePrefSetStore store = NewStore();
        store.SaveSet("ZOA", Set("SR-DAY"));
        store.SaveSet("ZOA", Set("SR-NIGHT"));

        Assert.False(store.RenameSet("ZOA", "SR-NIGHT", "SR-DAY"));
        Assert.Equal(["SR-DAY", "SR-NIGHT"], store.LoadSets("ZOA").Select(set => set.Name));
    }

    [Fact]
    public void Rename_OfAMissingName_IsFalse()
    {
        ScopePrefSetStore store = NewStore();
        store.SaveSet("ZOA", Set("SR-DAY"));

        Assert.False(store.RenameSet("ZOA", "SR-NONE", "SR-OTHER"));
    }

    [Fact]
    public void Delete_Works_AndASecondDeleteIsFalse()
    {
        ScopePrefSetStore store = NewStore();
        store.SaveSet("ZOA", Set("SR-DAY"));
        store.SaveSet("ZOA", Set("SR-NIGHT"));

        Assert.True(store.DeleteSet("ZOA", "sr-day"));
        Assert.False(store.DeleteSet("ZOA", "SR-DAY"));
        Assert.Equal(["SR-NIGHT"], store.LoadSets("ZOA").Select(set => set.Name));
    }

    [Fact]
    public void Facilities_StaySeparate()
    {
        ScopePrefSetStore store = NewStore();
        store.SaveSet("ZOA", Set("SR-DAY"));
        store.SaveSet("NCT", Set("SR-NIGHT"));

        Assert.Equal(["SR-DAY"], store.LoadSets("ZOA").Select(set => set.Name));
        Assert.Equal(["SR-NIGHT"], store.LoadSets("NCT").Select(set => set.Name));
    }

    [Fact]
    public void Current_RoundTrips_TheSavedStateAndTheActiveSet()
    {
        ScopePrefSetStore store = NewStore();
        store.SaveCurrent("ZOA", Set(null, 37.5));
        store.SaveCurrent("NCT", Set("SR-DAY", 22));

        ScopePrefSet? unsaved = NewStore().LoadCurrent("ZOA");
        ScopePrefSet? active = NewStore().LoadCurrent("NCT");

        Assert.NotNull(unsaved);
        Assert.Null(unsaved.Name);
        Assert.Equal(37.5, unsaved.RangeNm);
        Assert.NotNull(active);
        Assert.Equal("SR-DAY", active.Name);
        Assert.Equal(22.0, active.RangeNm);
    }

    [Fact]
    public void MissingFiles_GiveAnEmptyListAndANullCurrent()
    {
        ScopePrefSetStore store = NewStore();

        Assert.Empty(store.LoadSets("ZOA"));
        Assert.Null(store.LoadCurrent("ZOA"));
        Assert.Null(store.FindSet("ZOA", "SR-DAY"));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void CorruptFile_LoadsEmpty_LogsAndKeepsABackup_AndTheNextSaveWritesAgain()
    {
        string path = SetsPath("ZOA");
        byte[] corrupt = "{not json"u8.ToArray();
        WriteFile(path + ".bak", "an older backup");
        WriteFile(path, corrupt);

        ScopePrefSetStore store = NewStore();
        Assert.Empty(store.LoadSets("ZOA"));
        Assert.Contains(_log.Warnings, warning => warning.Contains("not valid JSON"));
        Assert.Contains(_log.Warnings, warning => warning.Contains("Kept the corrupt"));
        Assert.Equal(corrupt, File.ReadAllBytes(path + ".bak"));

        store.SaveSet("ZOA", Set("SR-DAY"));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("SR-DAY", Assert.Single(store.LoadSets("ZOA")).Name);
    }

    [Fact]
    public void CorruptCurrentFile_LoadsNull_LogsAWarning_AndKeepsABackup()
    {
        string path = CurrentPath("ZOA");
        byte[] corrupt = "{not json"u8.ToArray();
        WriteFile(path, corrupt);

        Assert.Null(NewStore().LoadCurrent("ZOA"));

        Assert.Contains(_log.Warnings, warning => warning.Contains("not valid JSON"));
        Assert.Contains(_log.Warnings, warning => warning.Contains("Kept the corrupt"));
        Assert.Equal(corrupt, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void RootNotAnObject_LoadsEmpty_AndKeepsABackup()
    {
        string path = SetsPath("ZOA");
        byte[] notAnObject = "[1,2]"u8.ToArray();
        WriteFile(path, notAnObject);

        Assert.Empty(NewStore().LoadSets("ZOA"));

        Assert.Contains(_log.Warnings, warning => warning.Contains("not valid JSON"));
        Assert.Equal(notAnObject, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void DuplicateVersionKeys_LoadEmpty_AndLog()
    {
        string path = SetsPath("ZOA");
        WriteFile(path, """{ "version": 1, "version": 1, "sets": [] }""");

        Assert.Empty(NewStore().LoadSets("ZOA"));

        Assert.NotEmpty(_log.Warnings);
    }

    [Fact]
    public void OneMalformedEntry_IsDropped_AndTheRestLoad()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            {
              "version": 1,
              "sets": [
                { "name": "SR-DAY", "rangeNm": 40, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} },
                { "name": "SR-BAD", "rangeNm": "x", "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} },
                { "name": "SR-NIGHT", "rangeNm": 30, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} }
              ]
            }
            """
        );

        Assert.Equal(["SR-DAY", "SR-NIGHT"], NewStore().LoadSets("ZOA").Select(set => set.Name));
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public void EntryWithAnExplicitNull_IsDropped_AndTheRestLoad()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            {
              "version": 1,
              "sets": [
                { "name": "SR-DAY", "rangeNm": 40, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} },
                {
                  "name": "SR-BAD",
                  "rangeNm": 30,
                  "templateId": "stars",
                  "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14},
                  "maps": null
                },
                { "name": "SR-NIGHT", "rangeNm": 20, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} }
              ]
            }
            """
        );

        Assert.Equal(["SR-DAY", "SR-NIGHT"], NewStore().LoadSets("ZOA").Select(set => set.Name));
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public void FileWithoutAVersion_Loads_AndIsRewrittenAtVersion1()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            { "sets": [
              { "name": "SR-DAY", "rangeNm": 40, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} }
            ] }
            """
        );

        Assert.Equal(["SR-DAY"], NewStore().LoadSets("ZOA").Select(set => set.Name));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("SR-DAY", document.RootElement.GetProperty("sets")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void CurrentFileWithoutAVersion_Loads_AndIsRewrittenAtVersion1()
    {
        string path = CurrentPath("ZOA");
        WriteFile(
            path,
            """
            { "state": {
                "name": "SR-DAY", "rangeNm": 22, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14}
            } }
            """
        );

        ScopePrefSet? loaded = NewStore().LoadCurrent("ZOA");

        Assert.NotNull(loaded);
        Assert.Equal("SR-DAY", loaded.Name);
        Assert.Equal(22.0, loaded.RangeNm);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void NewerVersion_Loads_LogsOneWarning_AndLeavesTheFileAlone()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            { "version": 9, "sets": [
              { "name": "SR-DAY", "rangeNm": 40, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} }
            ] }
            """
        );
        byte[] before = File.ReadAllBytes(path);

        Assert.Equal(["SR-DAY"], NewStore().LoadSets("ZOA").Select(set => set.Name));

        Assert.Single(_log.Warnings);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void SaveSet_OnANewerFile_KeepsItsVersion()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            { "version": 9, "sets": [
              { "name": "SR-DAY", "rangeNm": 40, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} }
            ] }
            """
        );

        NewStore().SaveSet("ZOA", Set("SR-NIGHT"));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(9, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("sets").GetArrayLength());
    }

    [Fact]
    public void UnknownVersion_LoadsItsSets_AndASaveThrows()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            { "version": "x", "sets": [
              { "name": "SR-DAY", "rangeNm": 40, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} }
            ] }
            """
        );

        ScopePrefSetStore store = NewStore();
        Assert.Equal(["SR-DAY"], store.LoadSets("ZOA").Select(set => set.Name));

        Assert.Throws<IOException>(() => store.SaveSet("ZOA", Set("SR-NIGHT")));
    }

    [Fact]
    public void UnknownSetField_SurvivesSavingAnotherSet()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            { "version": 1, "sets": [
              {
                "name": "SR-DAY",
                "rangeNm": 40,
                "templateId": "stars",
                "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14},
                "futureThing": 3
              }
            ] }
            """
        );

        NewStore().SaveSet("ZOA", Set("SR-NIGHT"));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement day = document.RootElement.GetProperty("sets")[0];
        Assert.Equal("SR-DAY", day.GetProperty("name").GetString());
        Assert.Equal(3, day.GetProperty("futureThing").GetInt32());
    }

    [Fact]
    public void UnknownSetField_SurvivesRenamingTheSetItself()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            { "version": 1, "sets": [
              {
                "name": "SR-DAY",
                "rangeNm": 40,
                "templateId": "stars",
                "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14},
                "futureThing": 3
              }
            ] }
            """
        );

        Assert.True(NewStore().RenameSet("ZOA", "SR-DAY", "SR-EVENING"));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement renamed = document.RootElement.GetProperty("sets")[0];
        Assert.Equal("SR-EVENING", renamed.GetProperty("name").GetString());
        Assert.Equal(3, renamed.GetProperty("futureThing").GetInt32());
    }

    [Fact]
    public void UnreadableFile_MakesASaveThrow_AndLeavesTheFileAlone()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            { "version": 1, "sets": [
              { "name": "SR-DAY", "rangeNm": 40, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} }
            ] }
            """
        );
        byte[] before = File.ReadAllBytes(path);

        FileStream held = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Assert.Throws<IOException>(() => NewStore().SaveSet("ZOA", Set("SR-NIGHT")));
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains(_log.Warnings, warning => warning.Contains("Refusing to write"));
    }

    [Fact]
    public void SetWithATearoffMissingItsButton_IsDropped_AndTheRestLoad()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            {
              "version": 1,
              "sets": [
                {
                  "name": "SR-BAD",
                  "rangeNm": 40,
                  "templateId": "stars",
                  "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14},
                  "tearoffs": [ { "corner": "tl", "dx": 1, "dy": 2 } ]
                },
                { "name": "SR-NIGHT", "rangeNm": 30, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} }
              ]
            }
            """
        );

        Assert.Equal(["SR-NIGHT"], NewStore().LoadSets("ZOA").Select(set => set.Name));
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public void CorruptFileWhoseBackupCannotBeWritten_LoadsEmpty_AndBothSavesThrow()
    {
        string setsPath = SetsPath("ZOA");
        WriteFile(setsPath, "{not json");
        Directory.CreateDirectory(setsPath + ".bak");
        string currentPath = CurrentPath("ZOA");
        WriteFile(currentPath, "{not json");
        Directory.CreateDirectory(currentPath + ".bak");

        ScopePrefSetStore store = NewStore();
        Assert.Empty(store.LoadSets("ZOA"));
        Assert.Null(store.LoadCurrent("ZOA"));
        Assert.Throws<IOException>(() => store.SaveSet("ZOA", Set("SR-DAY")));
        Assert.Throws<IOException>(() => store.SaveCurrent("ZOA", Set(null)));
        Assert.Contains(_log.Warnings, warning => warning.Contains("Could not back up"));
    }

    /// <summary>
    /// An unreadable current-state file is not refused the way an unreadable sets file is: the save is attempted. The
    /// replacing move needs delete sharing on Windows, which the held handle denies, so there it fails with the write's own
    /// error; elsewhere the state lands.
    /// </summary>
    [Fact]
    public void UnreadableCurrentFile_IsNotRefused_AndTheWriteIsAttempted()
    {
        string path = CurrentPath("ZOA");
        WriteFile(
            path,
            """
            { "version": 1, "state": {
                "name": "SR-DAY", "rangeNm": 22, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14}
            } }
            """
        );

        Exception? failure;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failure = Record.Exception(() => NewStore().SaveCurrent("ZOA", Set("SR-NIGHT", 33)));
        }

        Assert.True((failure is null) || (failure is IOException) || (failure is UnauthorizedAccessException), failure?.ToString());
        Assert.Contains(_log.Warnings, warning => warning.Contains("Could not read"));
        Assert.DoesNotContain(_log.Warnings, warning => warning.Contains("Refusing to write"));
        if (failure is null)
        {
            Assert.Equal("SR-NIGHT", NewStore().LoadCurrent("ZOA")!.Name);
        }
    }

    [Fact]
    public void UnknownVersion_Loads_LogsOneWarning_AndLeavesTheFileAlone()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            { "version": "x", "sets": [
              { "name": "SR-DAY", "rangeNm": 40, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} }
            ] }
            """
        );
        byte[] before = File.ReadAllBytes(path);

        Assert.Equal(["SR-DAY"], NewStore().LoadSets("ZOA").Select(set => set.Name));

        Assert.Single(_log.Warnings);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Version0FileWhoseRewriteFails_StillLoadsItsSets_WithAWarning()
    {
        string path = SetsPath("ZOA");
        WriteFile(
            path,
            """
            { "sets": [
              { "name": "SR-DAY", "rangeNm": 40, "templateId": "stars", "db": {"line4":true,"hoverDwell":false,"atpa":true,"charSize":14} }
            ] }
            """
        );

        // The rewrite's temporary file cannot be written, so the load's save-back fails after the sets have been read.
        Directory.CreateDirectory(path + ".tmp");

        Assert.Equal(["SR-DAY"], NewStore().LoadSets("ZOA").Select(set => set.Name));

        Assert.Contains(_log.Warnings, warning => warning.Contains("back at version"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(document.RootElement.TryGetProperty("version", out _));
    }

    [Theory]
    [InlineData("ZOA", true)]
    [InlineData("N90", true)]
    [InlineData("ABCDEFGHIJKLMNOP", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("../x", false)]
    [InlineData("a b", false)]
    [InlineData("ABCDEFGHIJKLMNOPQ", false)]
    [InlineData("CON", false)]
    [InlineData("con", false)]
    [InlineData("NUL", false)]
    [InlineData("COM1", false)]
    [InlineData("com9", false)]
    [InlineData("LPT9", false)]
    [InlineData("LPT0", true)]
    public void IsSafeFacility_AcceptsOnlyShortIdsOfLettersDigitsUnderScoreAndDash(string? facility, bool expected) =>
        Assert.Equal(expected, ScopePrefSetStore.IsSafeFacility(facility));

    [Theory]
    [InlineData("sr-day", "SR-DAY")]
    [InlineData("ZOA", "ZOA")]
    [InlineData("abcdefghijklmnop", "ABCDEFGHIJKLMNOP")]
    public void TryNormalizeName_UpperCasesAValidName(string name, string expected)
    {
        Assert.True(ScopePrefSetStore.TryNormalizeName(name, out string? normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A B")]
    [InlineData("x/y")]
    [InlineData("ABCDEFGHIJKLMNOPQ")]
    [InlineData("con")]
    [InlineData("NUL")]
    public void TryNormalizeName_RefusesAnInvalidName(string name)
    {
        Assert.False(ScopePrefSetStore.TryNormalizeName(name, out string? normalized));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("LoadSets")]
    [InlineData("FindSet")]
    [InlineData("SaveSet")]
    [InlineData("RenameSet")]
    [InlineData("DeleteSet")]
    [InlineData("LoadCurrent")]
    [InlineData("SaveCurrent")]
    public void UnsafeFacility_ThrowsArgumentExceptionNamingIt(string call)
    {
        ScopePrefSetStore store = NewStore();
        const string unsafeId = "../x";

        ArgumentException error = call switch
        {
            "LoadSets" => Assert.Throws<ArgumentException>(() => store.LoadSets(unsafeId)),
            "FindSet" => Assert.Throws<ArgumentException>(() => store.FindSet(unsafeId, "SR-DAY")),
            "SaveSet" => Assert.Throws<ArgumentException>(() => store.SaveSet(unsafeId, Set("SR-DAY"))),
            "RenameSet" => Assert.Throws<ArgumentException>(() => store.RenameSet(unsafeId, "SR-DAY", "SR-NIGHT")),
            "DeleteSet" => Assert.Throws<ArgumentException>(() => store.DeleteSet(unsafeId, "SR-DAY")),
            "LoadCurrent" => Assert.Throws<ArgumentException>(() => store.LoadCurrent(unsafeId)),
            "SaveCurrent" => Assert.Throws<ArgumentException>(() => store.SaveCurrent(unsafeId, Set(null))),
            _ => throw new ArgumentOutOfRangeException(nameof(call), call, "unknown call"),
        };

        Assert.Contains(unsafeId, error.Message);
    }

    private ScopePrefSetStore NewStore() => new(_root, _log);

    private string SetsPath(string facility) => Path.Combine(_root, "prefsets", $"{facility}.json");

    private string CurrentPath(string facility) => Path.Combine(_root, "current", $"{facility}.json");

    private static void WriteFile(string path, string contents) => WriteFile(path, System.Text.Encoding.UTF8.GetBytes(contents));

    private static void WriteFile(string path, byte[] contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
    }

    private static ScopePrefSet Set(string? name, double rangeNm = 40) =>
        new()
        {
            Name = name,
            RangeNm = rangeNm,
            CenterLat = 37.5,
            CenterLon = -122.5,
            TemplateId = "stars",
            Db = new ScopePrefDb(true, false, true, 14),
        };

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
