using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The merge window's Apply over real scenario JSON: the merged text is built from the choices, text that does not read as a
/// scenario is refused with the footer naming the field, the checks' findings are shown, written and returned, a failed write
/// keeps the window open, Cancel writes nothing, and Apply waits while a MERGED VALUE editor is open.
/// </summary>
public sealed class ScenarioMergeApplyTests : IDisposable
{
    private const string PresetId = "01HPRESET00000000000000001";

    private static readonly string SamplePath = Path.Combine(
        TestRepo.Root("the scenario examples are read from the checkout."),
        "docs",
        "atctrainer-scenario-examples",
        "01H06NVK7VN8BS7MCDXHKJZ7MQ.json"
    );

    private static readonly ScenarioMergeCaptions Captions = new(
        "Merge server changes",
        "working copy",
        "server version",
        "server version of 2 Oct 2026",
        "There is no base version: choose a side for every difference."
    );

    private readonly ScenarioEditorFixture _fx = new();

    private readonly List<string> _events = [];

    private readonly List<string> _written = [];

    public ScenarioMergeApplyTests() => TestVnasData.EnsureInitialized();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task AllResolved_WritesTheExpectedText_AndReturnsNoWarnings()
    {
        ScenarioMergeViewModel vm = ResolvedSampleMerge(Recorded);

        ScenarioMergeResult? result = await vm.ApplyAsync(Checks([]));

        JsonObject expected = SampleTexts.Mine();
        expected["autoDeleteMode"] = "Parked";
        expected["name"] = "Theirs";
        string written = Assert.Single(_written);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(written)));
        Assert.NotNull(result);
        Assert.Equal(written, result.MergedJson);
        Assert.Empty(result.Warnings);
        Assert.Equal(["checks", "write"], _events);
        Assert.Same(result, await vm.Completion);
        Assert.Null(vm.ApplyError);
    }

    [Fact]
    public async Task ChecksReturnTwoWarnings_TheyAreShownThenWrittenThenReturned()
    {
        ScenarioMergeViewModel vm = ResolvedSampleMerge(Recorded);
        ScenarioEditIssue[] warnings =
        [
            new(ScenarioEditIssueSeverity.Warning, null, null, ScenarioEditFields.Scenario, null, "first finding"),
            new(ScenarioEditIssueSeverity.Error, null, null, ScenarioEditFields.Scenario, null, "second finding"),
        ];

        ScenarioMergeResult? result = await vm.ApplyAsync(Checks(warnings));

        Assert.Equal(["checks", "show 2", "write"], _events);
        Assert.NotNull(result);
        Assert.Equal(warnings, result.Warnings);
        Assert.Same(result, await vm.Completion);
    }

    [Fact]
    public async Task JsonBoxEdit_StringForSpawnDelay_IsRefused_NothingWritten_FooterNamesSpawnDelay()
    {
        JsonObject theirs = Sample();
        JsonArray aircraft = theirs["aircraft"]!.AsArray();
        JsonObject added = aircraft[0]!.DeepClone().AsObject();
        added["callsign"] = "NEW123";
        aircraft.Add(added);
        ScenarioMergeViewModel vm = Merge(Text(Sample()), Sample(), theirs, Recorded);
        ScenarioMergeRowViewModel row = Assert.Single(vm.Rows, r => (r.Section == "Aircraft") && r.IsItemRow);
        vm.EditRowCommand.Execute(row);
        ScenarioMergedValueViewModel editor = Assert.IsType<ScenarioMergedValueViewModel>(row.Editor);
        Assert.Equal(ScenarioMergeEditorKind.Json, editor.Kind);
        JsonObject edited = JsonNode.Parse(editor.Text)!.AsObject();
        edited["spawnDelay"] = "soon";
        editor.Text = edited.ToJsonString();
        editor.KeepCommand.Execute(null);
        Assert.Equal(MergeChoiceKind.Edit, vm.Choices[row.Path].Kind);

        ScenarioMergeResult? result = await vm.ApplyAsync(Checks([]));

        Assert.Null(result);
        Assert.Empty(_written);
        Assert.Empty(_events);
        Assert.Contains("Spawn delay", vm.ApplyError, StringComparison.Ordinal);
        Assert.False(vm.Completion.IsCompleted);
    }

    [Fact]
    public async Task BlankName_IsRefused()
    {
        JsonObject theirs = Sample();
        theirs["name"] = "  ";
        ScenarioMergeViewModel vm = Merge(Text(Sample()), Sample(), theirs, Recorded);
        Assert.True(vm.CanApply);

        ScenarioMergeResult? result = await vm.ApplyAsync(Checks([]));

        Assert.Null(result);
        Assert.Empty(_written);
        Assert.Empty(_events);
        Assert.Contains("no name", vm.ApplyError, StringComparison.Ordinal);
        Assert.False(vm.Completion.IsCompleted);
    }

    [Fact]
    public async Task WriteThrows_StaysOpen_ChoicesKept_AndShowsTheError()
    {
        ScenarioMergeViewModel vm = ResolvedSampleMerge(_ => throw new IOException("The disk is full."));
        var before = vm.Choices.ToDictionary(c => c.Key, c => c.Value.Kind);

        ScenarioMergeResult? result = await vm.ApplyAsync(Checks([]));

        Assert.Null(result);
        Assert.False(vm.Completion.IsCompleted);
        Assert.Equal(before, vm.Choices.ToDictionary(c => c.Key, c => c.Value.Kind));
        Assert.True(vm.CanApply);
        Assert.Contains("The disk is full.", vm.ApplyError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_WriteIsNeverCalled()
    {
        ScenarioMergeViewModel vm = ResolvedSampleMerge(Recorded);

        vm.CancelCommand.Execute(null);

        Assert.Null(await vm.Completion);
        Assert.Null(await vm.ApplyAsync(Checks([])));
        Assert.Empty(_written);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task UnmodelledField_Survives()
    {
        JsonObject mine = SampleTexts.Mine();
        mine["unmodelledSetting"] = new JsonObject { ["kept"] = true };
        mine["aircraft"]![0]!["unmodelledNote"] = "mine only";
        JsonObject theirs = SampleTexts.Theirs();
        theirs["unmodelledSetting"] = new JsonObject { ["kept"] = true };
        ScenarioMergeViewModel vm = Resolved(Merge(Text(SampleTexts.Base()), mine, theirs, Recorded));

        ScenarioMergeResult? result = await vm.ApplyAsync(Checks([]));

        Assert.NotNull(result);
        JsonObject written = JsonNode.Parse(Assert.Single(_written))!.AsObject();
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["kept"] = true }, written["unmodelledSetting"]));
        Assert.Equal("mine only", written["aircraft"]![0]!["unmodelledNote"]!.GetValue<string>());
    }

    [Fact]
    public async Task Apply_IsDisabledWhileAnEditorIsOpen_AndEnabledAgainAfterKeepOrCancel()
    {
        ScenarioMergeViewModel vm = ResolvedSampleMerge(Recorded);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        ScenarioMergeRowViewModel row = Assert.Single(vm.Rows, r => r.Path == "/autoDeleteMode");
        Assert.True(vm.CanApply);

        vm.EditRowCommand.Execute(row);
        Assert.False(vm.CanApply);
        Assert.Contains(nameof(ScenarioMergeViewModel.CanApply), changes);
        Assert.Null(await vm.ApplyAsync(Checks([])));
        Assert.Empty(_written);

        row.Editor!.CancelCommand.Execute(null);
        Assert.True(vm.CanApply);

        vm.EditRowCommand.Execute(row);
        Assert.False(vm.CanApply);
        row.Editor!.KeepCommand.Execute(null);
        Assert.True(vm.CanApply);
        changes.Clear();

        vm.EditEveryClashCommand.Execute(null);
        Assert.False(vm.CanApply);
        Assert.Contains(nameof(ScenarioMergeViewModel.CanApply), changes);
        foreach (ScenarioMergeRowViewModel open in vm.Rows.Where(r => r.IsEditing).ToList())
        {
            open.Editor!.CancelCommand.Execute(null);
        }

        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task Cancel_WhileWriteIsPending_CannotExecute_AndCompletionCarriesTheResult()
    {
        var pending = new TaskCompletionSource();
        ScenarioMergeViewModel vm = ResolvedSampleMerge(json =>
        {
            _written.Add(json);
            return pending.Task;
        });
        Assert.True(vm.CancelCommand.CanExecute(null));

        Task<ScenarioMergeResult?> apply = vm.ApplyAsync(Checks([]));

        Assert.False(apply.IsCompleted);
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.False(vm.CanApply);
        vm.CancelCommand.Execute(null);
        Assert.False(vm.Completion.IsCompleted);
        pending.SetResult();
        ScenarioMergeResult? result = await apply;
        Assert.NotNull(result);
        Assert.Same(result, await vm.Completion);
        Assert.True(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task ApplyError_ClearsWhenARowIsFixed_BeforeTheNextApply()
    {
        JsonObject theirs = Sample();
        theirs["name"] = "  ";
        ScenarioMergeViewModel vm = Merge(Text(Sample()), Sample(), theirs, Recorded);
        Assert.Null(await vm.ApplyAsync(Checks([])));
        Assert.NotNull(vm.ApplyError);

        vm.Choose(Assert.Single(vm.Rows, r => r.Path == "/name"), MergeChoice.KeepMine);

        Assert.Null(vm.ApplyError);
        Assert.Empty(_written);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChecksThrow_ShowsTheError_WritesNothing_AndKeepsEveryChoice(bool runThrows)
    {
        ScenarioMergeViewModel vm = ResolvedSampleMerge(Recorded);
        var before = vm.Choices.ToDictionary(c => c.Key, c => c.Value.Kind);
        ScenarioEditIssue warning = new(ScenarioEditIssueSeverity.Warning, null, null, ScenarioEditFields.Scenario, null, "finding");
        var checks = new ScenarioMergeChecks(
            _ =>
                runThrows
                    ? Task.FromException<IReadOnlyList<ScenarioEditIssue>>(new InvalidOperationException("The checks broke."))
                    : Task.FromResult<IReadOnlyList<ScenarioEditIssue>>([warning]),
            _ => throw new InvalidOperationException("The checks broke.")
        );

        ScenarioMergeResult? result = await vm.ApplyAsync(checks);

        Assert.Null(result);
        Assert.Empty(_written);
        Assert.Equal("Couldn't check the merged scenario: The checks broke.", vm.ApplyError);
        Assert.Equal(before, vm.Choices.ToDictionary(c => c.Key, c => c.Value.Kind));
        Assert.False(vm.Completion.IsCompleted);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task HeldChecks_KeepApplyInProgress_ThenShowTheWarningsAndWrite()
    {
        ScenarioMergeViewModel vm = ResolvedSampleMerge(Recorded);
        var release = new TaskCompletionSource<IReadOnlyList<ScenarioEditIssue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = new ScenarioMergeChecks(
            _ =>
                Task.Run<IReadOnlyList<ScenarioEditIssue>>(() =>
                {
                    started.SetResult();
                    return release.Task;
                }),
            shown =>
            {
                _events.Add("show " + shown.Count);
                return Task.CompletedTask;
            }
        );

        Task<ScenarioMergeResult?> apply = vm.ApplyAsync(checks);

        await started.Task.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.False(apply.IsCompleted);
        Assert.False(vm.CanApply);
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.False(vm.Completion.IsCompleted);
        Assert.Empty(_written);

        ScenarioEditIssue warning = new(ScenarioEditIssueSeverity.Warning, null, null, ScenarioEditFields.Scenario, null, "finding");
        release.SetResult([warning]);
        ScenarioMergeResult? result = await apply;

        Assert.NotNull(result);
        Assert.Equal([warning], result.Warnings);
        Assert.Equal(["show 1", "write"], _events);
        Assert.Single(_written);
        Assert.Same(result, await vm.Completion);
        Assert.True(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChecksTaskFaulting_SetsTheFooterError_AndWritesNothing()
    {
        ScenarioMergeViewModel vm = ResolvedSampleMerge(Recorded);
        var checks = new ScenarioMergeChecks(
            _ => Task.FromException<IReadOnlyList<ScenarioEditIssue>>(new InvalidOperationException("The checks broke.")),
            _ => Task.CompletedTask
        );

        ScenarioMergeResult? result = await vm.ApplyAsync(checks);

        Assert.Null(result);
        Assert.Empty(_written);
        Assert.Equal("Couldn't check the merged scenario: The checks broke.", vm.ApplyError);
        Assert.False(vm.Completion.IsCompleted);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task WhileTheChecksRun_EveryChoiceIsLocked_AndUnlockedOnceTheyReturn()
    {
        ScenarioMergeViewModel vm = ResolvedSampleMerge(Recorded);
        var release = new TaskCompletionSource<IReadOnlyList<ScenarioEditIssue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = new ScenarioMergeChecks(
            _ =>
                Task.Run<IReadOnlyList<ScenarioEditIssue>>(() =>
                {
                    started.SetResult();
                    return release.Task;
                }),
            _ => Task.CompletedTask
        );
        ScenarioMergeRowViewModel row = vm.Rows[0];
        var services = new ScenarioMergedAircraftServices(_fx.Services(), ScenarioEditorFixture.NoLayouts, Task.CompletedTask);
        string itemKey = vm.AircraftLines[0].ItemKey;
        Assert.True(vm.NextClashCommand.CanExecute(null));

        Task<ScenarioMergeResult?> apply = vm.ApplyAsync(checks);

        await started.Task.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.False(row.KeepMineCommand.CanExecute(null));
        Assert.False(row.KeepTheirsCommand.CanExecute(null));
        Assert.False(vm.KeepTheirsForEveryClashCommand.CanExecute(null));
        Assert.False(vm.TakeMineForEverythingCommand.CanExecute(null));
        Assert.False(vm.NextClashCommand.CanExecute(null));
        Assert.False(vm.PreviousClashCommand.CanExecute(null));
        Assert.Null(vm.EditAircraft(itemKey, services));

        release.SetException(new InvalidOperationException("The checks broke."));
        Assert.Null(await apply);

        Assert.True(row.KeepMineCommand.CanExecute(null));
        Assert.True(row.KeepTheirsCommand.CanExecute(null));
        Assert.True(vm.KeepTheirsForEveryClashCommand.CanExecute(null));
        Assert.True(vm.TakeMineForEverythingCommand.CanExecute(null));
        Assert.True(vm.NextClashCommand.CanExecute(null));
        while (vm.NextClashCommand.CanExecute(null))
        {
            vm.NextClashCommand.Execute(null);
        }

        Assert.True(vm.PreviousClashCommand.CanExecute(null));
        Assert.NotNull(vm.EditAircraft(itemKey, services));
    }

    [Fact]
    public void FieldNamed_NullListElement_NamesTheListHoldingIt()
    {
        JsonObject scenario = Sample();
        scenario["aircraft"]![1]!["presetCommands"] = new JsonArray((JsonNode?)null);

        string label = ScenarioMergeViewModel.FieldNamed(scenario.ToJsonString(), "$.aircraft[1].presetCommands[0]");

        Assert.Equal("Preset commands", label);
    }

    [Fact]
    public async Task SecondApply_WhileTheFirstAwaits_ReturnsNull_AndWritesOnce()
    {
        var pending = new TaskCompletionSource();
        ScenarioMergeViewModel vm = ResolvedSampleMerge(json =>
        {
            _written.Add(json);
            return pending.Task;
        });

        Task<ScenarioMergeResult?> first = vm.ApplyAsync(Checks([]));
        ScenarioMergeResult? second = await vm.ApplyAsync(Checks([]));
        pending.SetResult();

        Assert.Null(second);
        Assert.NotNull(await first);
        Assert.Single(_written);
    }

    [Fact]
    public async Task RetryAfterAFailedWrite_Succeeds_ClearsApplyError_AndReturnsTheResult()
    {
        int attempts = 0;
        ScenarioMergeViewModel vm = ResolvedSampleMerge(json =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new IOException("The disk is full.");
            }

            _written.Add(json);
            return Task.CompletedTask;
        });
        Assert.Null(await vm.ApplyAsync(Checks([])));
        Assert.NotNull(vm.ApplyError);

        ScenarioMergeResult? result = await vm.ApplyAsync(Checks([]));

        Assert.NotNull(result);
        Assert.Null(vm.ApplyError);
        Assert.Equal(result.MergedJson, Assert.Single(_written));
        Assert.Same(result, await vm.Completion);
    }

    private Task Recorded(string json)
    {
        _events.Add("write");
        _written.Add(json);
        return Task.CompletedTask;
    }

    private ScenarioMergeChecks Checks(IReadOnlyList<ScenarioEditIssue> warnings) =>
        new(
            _ =>
            {
                _events.Add("checks");
                return Task.FromResult(warnings);
            },
            shown =>
            {
                _events.Add("show " + shown.Count);
                return Task.CompletedTask;
            }
        );

    // Auto-delete taken from theirs and the preset command from mine; the name merges from theirs by itself.
    private static ScenarioMergeViewModel Resolved(ScenarioMergeViewModel vm)
    {
        vm.Choose(Assert.Single(vm.Rows, r => r.Path == "/autoDeleteMode"), MergeChoice.KeepTheirs);
        vm.Choose(Assert.Single(vm.Rows, r => r.IsClash && (r.FieldKey == "presetCommands[]/command")), MergeChoice.KeepMine);
        Assert.True(vm.CanApply);
        return vm;
    }

    private static ScenarioMergeViewModel ResolvedSampleMerge(Func<string, Task> write) =>
        Resolved(Merge(Text(SampleTexts.Base()), SampleTexts.Mine(), SampleTexts.Theirs(), write));

    private static ScenarioMergeViewModel Merge(string? baseText, JsonObject mine, JsonObject theirs, Func<string, Task> write) =>
        ScenarioMergeViewModel.Create(
            new ScenarioMergeRequest("ZOA", "01H06NVK7VN8BS7MCDXHKJZ7MQ", new(baseText, Text(mine), Text(theirs)), Captions, write)
        );

    private static JsonObject Sample() => JsonNode.Parse(File.ReadAllText(SamplePath))!.AsObject();

    private static string Text(JsonObject scenario) => scenario.ToJsonString();

    // Name changed in theirs only, auto-delete and one preset command changed on both sides.
    private static class SampleTexts
    {
        public static JsonObject Base() => WithPreset(Sample(), "FH 010");

        public static JsonObject Mine()
        {
            JsonObject mine = WithPreset(Sample(), "FH 020");
            mine["autoDeleteMode"] = "OnLanding";
            return mine;
        }

        public static JsonObject Theirs()
        {
            JsonObject theirs = WithPreset(Sample(), "FH 030");
            theirs["autoDeleteMode"] = "Parked";
            theirs["name"] = "Theirs";
            return theirs;
        }

        private static JsonObject WithPreset(JsonObject scenario, string command)
        {
            scenario["aircraft"]![1]!["presetCommands"] = new JsonArray(new JsonObject { ["id"] = PresetId, ["command"] = command });
            return scenario;
        }
    }
}
