using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The Scenario menu's Active Runways window: one row per airport of the room's list (the primary first, then the rest in
/// ordinal order), untouched rows following every <c>ActiveRunwaysChanged</c> while a row the user typed in keeps its text,
/// one <c>ARWY</c> per changed row on Apply done through the same parser the server uses, and the close rules it shares
/// with the load prompt (unload, leaving the room, another load, a recording load).
/// </summary>
public class ActiveRunwaysWindowViewModelTests
{
    private static NavigationDatabase NavDb()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb ?? throw new InvalidOperationException("Test navdata did not load");
    }

    private static Dictionary<string, List<string>> Runways(params (string Airport, string[] Tokens)[] entries) =>
        entries.ToDictionary(entry => entry.Airport, entry => entry.Tokens.ToList(), StringComparer.Ordinal);

    /// <summary>A client with a scenario of <paramref name="runways"/> loaded at OAK, and the window open over it.</summary>
    private static (MainViewModel Vm, ActiveRunwaysWindowViewModel Window) OpenWindow(params (string Airport, string[] Tokens)[] runways)
    {
        MainViewModel vm = new(new FakeFilePickerService());
        vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-1", "OAK Ground", "OAK", true, 1, [], Runways(runways)));
        Dispatcher.UIThread.RunJobs();
        return (vm, new ActiveRunwaysWindowViewModel(vm));
    }

    private static void Refresh(MainViewModel vm, params (string Airport, string[] Tokens)[] runways)
    {
        vm.OnActiveRunwaysChanged(new ActiveRunwaysChangedDto(Runways(runways)));
        Dispatcher.UIThread.RunJobs();
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

    // --- Rows ---

    [AvaloniaFact]
    public void Rows_AreThePrimaryAirportFirst_ThenTheRestInOrdinalOrder()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel _, ActiveRunwaysWindowViewModel window) = OpenWindow(("SFO", ["28R"]), ("HWD", ["28L"]), ("OAK", ["30"]));

        Assert.Equal([("OAK", "30"), ("HWD", "28L"), ("SFO", "28R")], window.Rows.Select(row => (row.Airport, row.Text)));
        Assert.True(window.IsOpen);
    }

    [AvaloniaFact]
    public void PrimaryAirportWithNoRunways_StillGetsAnEmptyRow_First()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel _, ActiveRunwaysWindowViewModel window) = OpenWindow(("SFO", ["28R"]));

        Assert.Equal([("OAK", ""), ("SFO", "28R")], window.Rows.Select(row => (row.Airport, row.Text)));
    }

    // --- Following the room ---

    [AvaloniaFact]
    public void LiveChange_RefreshesUntouchedRows_AndLeavesTheWindowOpen()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel vm, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]));

        Refresh(vm, ("OAK", ["D28L", "A28R"]));

        Assert.True(window.IsOpen);
        Assert.Equal([("OAK", "D28L A28R")], window.Rows.Select(row => (row.Airport, row.Text)));
    }

    [AvaloniaFact]
    public void LiveChange_KeepsTheTextOfARowTheUserTypedIn()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel vm, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]), ("SFO", ["28R"]));
        window.Rows[0].Text = "33";

        Refresh(vm, ("OAK", ["28R"]), ("SFO", ["28L"]));

        Assert.Equal([("OAK", "33"), ("SFO", "28L")], window.Rows.Select(row => (row.Airport, row.Text)));
    }

    // --- Apply ---

    [AvaloniaFact]
    public async Task Apply_SendsOnlyTheRowsWhoseTextChanged()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel _, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]), ("SFO", ["28R"]));
        window.Rows[0].Text = "28L";
        RecordingSender sender = AcceptAll();

        await window.ApplyAsync(NavDb(), sender.Send);

        Assert.Equal(["ARWY OAK 28L"], sender.Sent);
    }

    [AvaloniaFact]
    public async Task Apply_ARowTheParserRefuses_SendsNothing_AndShowsTheMessageUnderIt()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel _, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]), ("SFO", ["28R"]));
        window.Rows[0].Text = "28L";
        window.Rows[1].Text = "28L 99X";
        RecordingSender sender = AcceptAll();

        await window.ApplyAsync(NavDb(), sender.Send);

        Assert.Empty(sender.Sent);
        Assert.True(window.IsOpen);
        Assert.False(window.Rows[0].HasError);
        Assert.Equal("Not a runway: 99X", window.Rows[1].Error);
    }

    [AvaloniaFact]
    public async Task Apply_TheServerRefusesARow_KeepsItsTextWithTheMessage_AndSendsTheOthers()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel _, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]), ("SFO", ["28R"]));
        window.Rows[0].Text = "28L";
        window.Rows[1].Text = "28L";
        var sender = new RecordingSender(command =>
            command.StartsWith("ARWY SFO", StringComparison.Ordinal)
                ? new CommandResultDto(false, "No scenario loaded")
                : new CommandResultDto(true, null)
        );

        await window.ApplyAsync(NavDb(), sender.Send);

        Assert.Equal(["ARWY OAK 28L", "ARWY SFO 28L"], sender.Sent);
        Assert.True(window.IsOpen);
        Assert.Null(window.Rows[0].Error);
        Assert.Equal(("SFO", "28L", "No scenario loaded"), (window.Rows[1].Airport, window.Rows[1].Text, window.Rows[1].Error));
    }

    [AvaloniaFact]
    public async Task Apply_AnEmptyRow_SendsEveryEnd()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel _, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]));
        window.Rows[0].Text = " ";
        RecordingSender sender = AcceptAll();

        await window.ApplyAsync(NavDb(), sender.Send);

        string command = Assert.Single(sender.Sent);
        string[] words = command.Split(' ');
        Assert.Equal(["ARWY", "OAK"], words[..2]);
        Assert.Equal(["10L", "10R", "12", "15", "28L", "28R", "30", "33"], words[2..].Order(StringComparer.Ordinal));
    }

    [AvaloniaFact]
    public async Task Apply_KeepsTheWindowOpen_AndTheAppliedRowsFollowTheRoomAgain()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel vm, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]));
        window.Rows[0].Text = "28L";

        await window.ApplyAsync(NavDb(), AcceptAll().Send);
        Refresh(vm, ("OAK", ["12"]));

        Assert.True(window.IsOpen);
        Assert.Equal([("OAK", "12")], window.Rows.Select(row => (row.Airport, row.Text)));
    }

    [AvaloniaFact]
    public async Task Apply_StopsSending_OnceTheWindowCloses()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel vm, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]), ("SFO", ["28R"]));
        window.Rows[0].Text = "28L";
        window.Rows[1].Text = "28L";
        var sender = new RecordingSender(_ =>
        {
            vm.ClearScenarioState();
            return new CommandResultDto(true, null);
        });

        await window.ApplyAsync(NavDb(), sender.Send);

        Assert.Equal(["ARWY OAK 28L"], sender.Sent);
        Assert.False(window.IsOpen);
    }

    // --- Closing ---

    [AvaloniaFact]
    public void ScenarioUnloaded_ClosesTheWindow()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel vm, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]));

        vm.ClearScenarioState();

        Assert.False(window.IsOpen);
    }

    [AvaloniaFact]
    public void LeavingTheRoom_ClosesTheWindow()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel vm, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]));

        vm.ClearRoomState();

        Assert.False(window.IsOpen);
    }

    [AvaloniaFact]
    public void AnotherLoad_ClosesTheWindow()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel vm, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]));

        vm.OnScenarioLoaded(new ScenarioLoadedDto("scenario-2", "OAK Ground", "OAK", true, 1, [], Runways(("OAK", ["12"]))));
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsOpen);
    }

    [AvaloniaFact]
    public void RecordingLoad_ClosesTheWindow()
    {
        using var scope = new PreferencesFileScope();

        (MainViewModel vm, ActiveRunwaysWindowViewModel window) = OpenWindow(("OAK", ["30"]));

        vm.ApplyRecordingResult(new RewindResultDto(true, null, Runways(("OAK", ["12"])), [], "scenario-3", "OAK Ground", "OAK"));

        Assert.False(window.IsOpen);
    }
}
