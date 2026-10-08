using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The active-runways prompt a mentor's own scenario load opens when the server asks: one row per airport pre-filled with
/// the server's guess, notes on the ends the guess leaves out, the answer sent as one <c>ARWY</c> per airport after the
/// same parser check the server makes, and a refusal that keeps only the refused rows open. Never shown to a non-mentor,
/// in a live session or on a restart; closed by Cancel, an unload, leaving the room or another load.
/// </summary>
public class MainViewModelActiveRunwaysPromptTests
{
    private const string NotSetHint = "Active runways not set; use ARWY or Scenario › Active Runways…";

    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static NavigationDatabase NavDb()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb ?? throw new InvalidOperationException("Test navdata did not load");
    }

    private static Dictionary<string, List<string>> Runways(params (string Airport, string[] Tokens)[] entries) =>
        entries.ToDictionary(entry => entry.Airport, entry => entry.Tokens.ToList(), StringComparer.Ordinal);

    private static MainViewModel PromptedVm(params (string Airport, string[] Tokens)[] prefill)
    {
        MainViewModel vm = NewVm();
        vm.ApplyLoadResultActiveRunways(MainViewModelActiveRunwaysTests.LoadResult([], Runways(prefill), promptNeeded: true));
        Assert.True(vm.ShowActiveRunwaysPrompt);
        return vm;
    }

    private sealed class RecordingSender(Func<string, CommandResultDto> reply)
    {
        public List<string> Sent { get; } = [];

        public Task<CommandResultDto> Send(string command)
        {
            Sent.Add(command);
            return Task.FromResult(reply(command));
        }
    }

    private static RecordingSender AcceptAll() => new(_ => new CommandResultDto(true, null));

    // --- Opening ---

    [AvaloniaFact]
    public void LoadResultAskingForThem_OpensOneRowPerAirport_PrefilledAsTheServerSpelledThem()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L", "A28R"]), ("SFO", ["28R"]));

        Assert.Equal([("OAK", "D28L A28R"), ("SFO", "28R")], vm.ActiveRunwaysPromptRows.Select(row => (row.Airport, row.Text)));
        Assert.All(vm.ActiveRunwaysPromptRows, row => Assert.False(row.HasError));
    }

    [AvaloniaFact]
    public void PrimaryAirportWithoutAGuess_StillGetsARow_First()
    {
        MainViewModel vm = PromptedVm(("SFO", ["28R"]));

        Assert.Equal([("OAK", ""), ("SFO", "28R")], vm.ActiveRunwaysPromptRows.Select(row => (row.Airport, row.Text)));
    }

    [AvaloniaFact]
    public void LoadResultNotAsking_SoloOrSidecar_NeverOpens_ButStoresTheList()
    {
        MainViewModel vm = NewVm();

        vm.ApplyLoadResultActiveRunways(MainViewModelActiveRunwaysTests.LoadResult(Runways(("OAK", ["30"])), Runways(("OAK", ["30"])), false));

        Assert.False(vm.ShowActiveRunwaysPrompt);
        Assert.Equal(["30"], vm.RoomActiveRunways["OAK"]);
    }

    [AvaloniaFact]
    public void NonMentorLoader_IsNeverAsked()
    {
        MainViewModel vm = NewVm();
        vm.IsNonMentor = true;

        vm.ApplyLoadResultActiveRunways(MainViewModelActiveRunwaysTests.LoadResult([], Runways(("OAK", ["D28R"])), promptNeeded: true));

        Assert.False(vm.ShowActiveRunwaysPrompt);
    }

    [AvaloniaFact]
    public void LiveSession_IsNeverAsked()
    {
        MainViewModel vm = NewVm();

        vm.ApplyLoadResultActiveRunways(
            MainViewModelActiveRunwaysTests.LoadResult([], Runways(("OAK", ["D28R"])), promptNeeded: true) with
            {
                IsLiveSession = true,
            }
        );

        Assert.False(vm.ShowActiveRunwaysPrompt);
    }

    [AvaloniaFact]
    public void Restart_NeverAsks()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L"]), ("SFO", ["28R"]));

        vm.OnScenarioRestarted(new ScenarioRestartedDto([], Runways(("OAK", ["30"]))));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.ShowActiveRunwaysPrompt);
        Assert.Equal([("OAK", "D28L"), ("SFO", "28R")], vm.ActiveRunwaysPromptRows.Select(row => (row.Airport, row.Text)));
    }

    // --- Notes ---

    [Fact]
    public void Notes_NameTheAirportsWhoseGuessLacksADepartureOrAnArrivalEnd()
    {
        Dictionary<string, List<string>> prefill = Runways(("OAK", ["A28R"]), ("SFO", ["30"]), ("SQL", ["D30"]));

        List<string> notes = ActiveRunwaysEditor.Notes(["OAK", "SFO", "SQL", "HWD"], prefill);

        Assert.Equal(
            ["No departure end implied at OAK", "No arrival end implied at SQL", "No departure end implied at HWD", "No arrival end implied at HWD"],
            notes
        );
    }

    [AvaloniaFact]
    public void Prompt_ShowsTheNotesForItsAirports_PrimaryFirst_EachAirportsRunwayLineBeforeItsGuessNotes()
    {
        MainViewModel vm = NewVm();
        vm.ApplyLoadResultActiveRunways(
            MainViewModelActiveRunwaysTests.LoadResult([], Runways(("SFO", ["28R"]), ("SQL", ["D30"])), promptNeeded: true) with
            {
                ActiveRunwaysAssigned = new Dictionary<string, RunwayUseCountsDto> { ["SQL"] = new(1, 0, []), ["OAK"] = new(0, 2, ["30"]) },
            }
        );

        Assert.Equal(
            [
                "OAK: 2 arrivals in this scenario already have a runway and keep it; the rest are offered these. "
                    + "Arrivals from the scenario's generators land on 30.",
                "No departure end implied at OAK",
                "No arrival end implied at OAK",
                "SFO: no aircraft placed by this scenario has a runway yet; all of them are offered these.",
                "SQL: 1 departure in this scenario already has a runway and keeps it; the rest are offered these.",
                "No arrival end implied at SQL",
            ],
            vm.ActiveRunwaysPromptNotes
        );
    }

    [AvaloniaFact]
    public async Task Prompt_DropsTheRunwayLine_OfAnAirportWhoseRowWasAccepted()
    {
        MainViewModel vm = NewVm();
        vm.ApplyLoadResultActiveRunways(
            MainViewModelActiveRunwaysTests.LoadResult([], Runways(("OAK", ["28R"]), ("SFO", ["28R"])), promptNeeded: true) with
            {
                ActiveRunwaysAssigned = new Dictionary<string, RunwayUseCountsDto> { ["OAK"] = new(3, 0, []) },
            }
        );
        var sender = new RecordingSender(command =>
            command.StartsWith("ARWY SFO", StringComparison.Ordinal) ? new CommandResultDto(false, "No") : new CommandResultDto(true, null)
        );

        await vm.SubmitActiveRunwaysPromptAsync(NavDb(), sender.Send);

        Assert.Equal(["SFO: no aircraft placed by this scenario has a runway yet; all of them are offered these."], vm.ActiveRunwaysPromptNotes);
    }

    [Theory]
    [InlineData(1, 0, "OAK: 1 departure in this scenario already has a runway and keeps it; the rest are offered these.")]
    [InlineData(3, 0, "OAK: 3 departures in this scenario already have a runway and keep it; the rest are offered these.")]
    [InlineData(0, 1, "OAK: 1 arrival in this scenario already has a runway and keeps it; the rest are offered these.")]
    [InlineData(0, 2, "OAK: 2 arrivals in this scenario already have a runway and keep it; the rest are offered these.")]
    [InlineData(1, 1, "OAK: 1 departure and 1 arrival in this scenario already have a runway and keep it; the rest are offered these.")]
    [InlineData(2, 3, "OAK: 2 departures and 3 arrivals in this scenario already have a runway and keep it; the rest are offered these.")]
    [InlineData(0, 0, "OAK: no aircraft placed by this scenario has a runway yet; all of them are offered these.")]
    public void RunwayLine_SpellsTheCounts_DroppingAZeroPart(int departures, int arrivals, string line) =>
        Assert.Equal(line, ActiveRunwaysEditor.AssignedNote("OAK", new RunwayUseCountsDto(departures, arrivals, [])));

    [Fact]
    public void RunwayLine_AnAirportTheServerDidNotCount_HasNoAircraftWithARunway() =>
        Assert.Equal(
            "OAK: no aircraft placed by this scenario has a runway yet; all of them are offered these.",
            ActiveRunwaysEditor.AssignedNote("OAK", null)
        );

    [Theory]
    [InlineData(
        0,
        new[] { "28R" },
        "OAK: no aircraft placed by this scenario has a runway yet; all of them are offered these. Arrivals from the scenario's generators land on 28R."
    )]
    [InlineData(
        0,
        new[] { "28R", "30" },
        "OAK: no aircraft placed by this scenario has a runway yet; all of them are offered these. Arrivals from the scenario's generators land on 28R and 30."
    )]
    [InlineData(
        2,
        new[] { "28L", "28R", "30" },
        "OAK: 2 departures in this scenario already have a runway and keep it; the rest are offered these. "
            + "Arrivals from the scenario's generators land on 28L, 28R and 30."
    )]
    public void RunwayLine_NamesTheGeneratorsRunways(int departures, string[] runways, string line) =>
        Assert.Equal(line, ActiveRunwaysEditor.AssignedNote("OAK", new RunwayUseCountsDto(departures, 0, [.. runways])));

    // --- Reading a row ---

    [Theory]
    [InlineData("28L,28R", "ARWY OAK 28L 28R")]
    [InlineData("28L 28R", "ARWY OAK 28L 28R")]
    [InlineData("28L\n28R", "ARWY OAK 28L 28R")]
    [InlineData(" d28l, A28R\r\n30 ", "ARWY OAK D28L A28R 30")]
    [InlineData("none", "ARWY OAK NONE")]
    public void RowText_ReadsCommasSpacesAndNewLines(string text, string command)
    {
        ActiveRunwaysAnswer answer = ActiveRunwaysEditor.ToCommand("OAK", text, NavDb());

        Assert.Null(answer.Error);
        Assert.Equal(command, answer.Command);
    }

    [Theory]
    [InlineData("NONE 28R", "NONE must be the only runway")]
    [InlineData("28L 99X", "Not a runway: 99X")]
    [InlineData("28L, 28L", "Runway 28L listed twice")]
    [InlineData("36", "Unknown runway 36 at OAK")]
    public void RowText_TheParserRefuses_CarriesItsMessage(string text, string error)
    {
        ActiveRunwaysAnswer answer = ActiveRunwaysEditor.ToCommand("OAK", text, NavDb());

        Assert.Null(answer.Command);
        Assert.Equal(error, answer.Error);
    }

    [Fact]
    public void EmptyRow_SetsEveryEndOfEveryRunway_AsBareTokens()
    {
        ActiveRunwaysAnswer answer = ActiveRunwaysEditor.ToCommand("OAK", "  ", NavDb());

        Assert.Null(answer.Error);
        Assert.NotNull(answer.Command);
        string[] words = answer.Command.Split(' ');
        Assert.Equal(["ARWY", "OAK"], words[..2]);
        Assert.Equal(["10L", "10R", "12", "15", "28L", "28R", "30", "33"], words[2..].Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EmptyRow_AtAnAirportWithNoRunwayData_SendsNothing()
    {
        ActiveRunwaysAnswer answer = ActiveRunwaysEditor.ToCommand("ZZZZ", "", NavDb());

        Assert.Null(answer.Command);
        Assert.Null(answer.Error);
    }

    [Fact]
    public void Ok_IsDisabledOnlyWhenEveryRowIsEmpty_AndAnAirportHasNoRunwayData()
    {
        NavigationDatabase navDb = NavDb();

        Assert.False(ActiveRunwaysEditor.CanConfirm([new ActiveRunwaysRow("OAK", ""), new ActiveRunwaysRow("ZZZZ", " ")], navDb));
        Assert.True(ActiveRunwaysEditor.CanConfirm([new ActiveRunwaysRow("OAK", ""), new ActiveRunwaysRow("SFO", "")], navDb));
        Assert.True(ActiveRunwaysEditor.CanConfirm([new ActiveRunwaysRow("OAK", "28R"), new ActiveRunwaysRow("ZZZZ", "")], navDb));
    }

    // --- Answering ---

    [AvaloniaFact]
    public async Task Ok_SendsOneArwyPerAirport_AndCloses()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L", "A28R"]), ("SFO", ["28R"]));
        RecordingSender sender = AcceptAll();

        await vm.SubmitActiveRunwaysPromptAsync(NavDb(), sender.Send);

        Assert.Equal(["ARWY OAK D28L A28R", "ARWY SFO 28R"], sender.Sent);
        Assert.False(vm.ShowActiveRunwaysPrompt);
        Assert.Empty(vm.ActiveRunwaysPromptRows);
    }

    [AvaloniaFact]
    public async Task Ok_WithARowTheParserRefuses_SendsNothing_AndShowsTheMessageUnderTheRow()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L"]), ("SFO", ["28R"]));
        vm.ActiveRunwaysPromptRows[1].Text = "28R 99X";
        RecordingSender sender = AcceptAll();

        await vm.SubmitActiveRunwaysPromptAsync(NavDb(), sender.Send);

        Assert.Empty(sender.Sent);
        Assert.True(vm.ShowActiveRunwaysPrompt);
        Assert.False(vm.ActiveRunwaysPromptRows[0].HasError);
        Assert.Equal("Not a runway: 99X", vm.ActiveRunwaysPromptRows[1].Error);
    }

    [AvaloniaFact]
    public async Task Ok_TheServerRefusesARow_KeepsOnlyThatRowOpen_WithTheServersMessage()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L"]), ("SFO", ["28R"]));
        var sender = new RecordingSender(command =>
            command.StartsWith("ARWY SFO", StringComparison.Ordinal)
                ? new CommandResultDto(false, "No scenario loaded")
                : new CommandResultDto(true, null)
        );

        await vm.SubmitActiveRunwaysPromptAsync(NavDb(), sender.Send);

        Assert.Equal(["ARWY OAK D28L", "ARWY SFO 28R"], sender.Sent);
        Assert.True(vm.ShowActiveRunwaysPrompt);
        ActiveRunwaysRow refused = Assert.Single(vm.ActiveRunwaysPromptRows);
        Assert.Equal(("SFO", "No scenario loaded"), (refused.Airport, refused.Error));
    }

    [AvaloniaFact]
    public async Task Ok_StopsSending_OnceThePromptCloses()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L"]), ("SFO", ["28R"]));
        var sender = new RecordingSender(_ =>
        {
            vm.ClearScenarioState();
            return new CommandResultDto(true, null);
        });

        await vm.SubmitActiveRunwaysPromptAsync(NavDb(), sender.Send);

        Assert.Equal(["ARWY OAK D28L"], sender.Sent);
        Assert.False(vm.ShowActiveRunwaysPrompt);
    }

    [AvaloniaFact]
    public void Cancel_ClosesPrintsTheHint_AndLeavesTheRoomsList()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L"]));
        vm.ApplyActiveRunways(Runways(("OAK", ["30"])));

        vm.CancelActiveRunwaysPromptCommand.Execute(null);

        Assert.False(vm.ShowActiveRunwaysPrompt);
        Assert.Equal(NotSetHint, vm.TerminalEntries[^1].Message);
        Assert.Equal(["30"], vm.RoomActiveRunways["OAK"]);
    }

    // --- Closing ---

    [AvaloniaFact]
    public void Unload_ClosesThePrompt()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L"]));

        vm.ClearScenarioState();

        Assert.False(vm.ShowActiveRunwaysPrompt);
        Assert.Empty(vm.ActiveRunwaysPromptRows);
    }

    [AvaloniaFact]
    public void LeavingTheRoom_ClosesThePrompt()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L"]));

        vm.ClearRoomState();

        Assert.False(vm.ShowActiveRunwaysPrompt);
    }

    [AvaloniaFact]
    public void AnotherLoad_ClosesThePrompt()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L"]));

        vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-2", "OAK Ground", "OAK", true, 1, [], []));
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.ShowActiveRunwaysPrompt);
    }

    [AvaloniaFact]
    public void RecordingLoad_ClosesThePrompt()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L"]));

        vm.ApplyRecordingResult(new RewindResultDto(true, null, Runways(("OAK", ["12"])), [], "scenario-3", "OAK Ground", "OAK"));

        Assert.False(vm.ShowActiveRunwaysPrompt);
        Assert.Empty(vm.ActiveRunwaysPromptRows);
    }

    [AvaloniaFact]
    public void ActiveRunwaysChanged_UpdatesTheList_AndLeavesThePromptOpen()
    {
        MainViewModel vm = PromptedVm(("OAK", ["D28L"]));

        vm.OnActiveRunwaysChanged(new ActiveRunwaysChangedDto(Runways(("OAK", ["28R"]))));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.ShowActiveRunwaysPrompt);
        Assert.Equal(["28R"], vm.RoomActiveRunways["OAK"]);
    }
}
