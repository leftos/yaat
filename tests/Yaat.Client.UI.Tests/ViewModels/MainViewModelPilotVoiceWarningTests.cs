using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The warning a solo-training student gets when pilot voice (TTS) is not working: a banner while the condition
/// holds, and a dialog on the first resume of each session. Solo pilots speak only through TTS, so a student
/// without it hears nothing.
///
/// The room, scenario and solo flag arrive through <see cref="MainViewModel.ApplyRoomState"/>, and every resume goes
/// through a real path: the Pause button (<c>TogglePauseCommand</c>), the timeline play button
/// (<c>TogglePlaybackCommand</c>) or a typed <c>UNPAUSE</c>. The dialog is the view-supplied
/// <see cref="MainViewModel.PilotVoiceWarningPrompt"/>, stubbed here. A unit-constructed VM has no server connection,
/// so a resume that gets past the gate fails with "Not connected.": the buttons catch it into
/// <see cref="MainViewModel.StatusText"/>, and the typed path lets it escape. That failure is how a test tells a sent
/// resume from one the gate stopped.
/// </summary>
public class MainViewModelPilotVoiceWarningTests
{
    private const string Sentinel = "sentinel";

    /// <summary>Counts how often the dialog is shown and answers every time with <see cref="Answer"/>.</summary>
    private sealed class PromptStub(PilotVoiceWarningChoice answer)
    {
        public int Shown { get; private set; }

        public PilotVoiceWarningChoice Answer { get; set; } = answer;

        public Task<PilotVoiceWarningChoice> Show()
        {
            Shown++;
            return Task.FromResult(Answer);
        }
    }

    /// <summary>A paused room with a scenario loaded; <paramref name="solo"/> is the room's solo-training flag.</summary>
    private static RoomStateDto PausedRoom(bool solo) =>
        new(
            RoomId: "ROOM-A",
            CreatorInitials: "CX",
            CreatorArtccId: "ZOA",
            Members: [],
            ScenarioName: "OAK Ground 7",
            ScenarioId: "scenario-7",
            IsPaused: true,
            SimRate: 1.0,
            PrimaryAirportId: null,
            AllAircraft: [],
            AircraftGenerators: [],
            VfrArrivalGenerators: [],
            OverflightGenerators: [],
            Positions: [],
            SoloTrainingMode: solo
        );

    /// <summary>
    /// A VM joined to a paused room, with pilot voice switched off in the preferences (the default, set explicitly
    /// because the test process shares one preferences file).
    /// </summary>
    private static MainViewModel JoinedViewModel(bool solo, PromptStub prompt)
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        Dispatcher.UIThread.RunJobs();
        vm.Preferences.SetPilotVoiceSettings(false, 80, true);
        vm.ApplyRoomState(PausedRoom(solo));
        Dispatcher.UIThread.RunJobs();
        vm.PilotVoiceWarningPrompt = prompt.Show;
        vm.StatusText = Sentinel;
        return vm;
    }

    /// <summary>Types <c>UNPAUSE</c> and sends it; true when the resume was sent (and failed on the missing connection).</summary>
    private static async Task<bool> TypeUnpauseAsync(MainViewModel vm)
    {
        vm.CommandText = "UNPAUSE";
        try
        {
            await vm.SendCommandCommand.ExecuteAsync(null);
            return false;
        }
        catch (InvalidOperationException ex) when (ex.Message == "Not connected.")
        {
            return true;
        }
    }

    [AvaloniaFact]
    public async Task Resume_SoloWithoutVoice_FirstTime_Intercepts()
    {
        var prompt = new PromptStub(PilotVoiceWarningChoice.Cancel);
        MainViewModel vm = JoinedViewModel(solo: true, prompt);

        await vm.TogglePauseCommand.ExecuteAsync(null);

        Assert.Equal(1, prompt.Shown);
        Assert.Equal(Sentinel, vm.StatusText); // the resume never reached the connection
    }

    [AvaloniaFact]
    public async Task Resume_AfterCancel_InterceptsAgain()
    {
        var prompt = new PromptStub(PilotVoiceWarningChoice.Cancel);
        MainViewModel vm = JoinedViewModel(solo: true, prompt);

        await vm.TogglePauseCommand.ExecuteAsync(null);
        await vm.TogglePlaybackCommand.ExecuteAsync(null);
        bool typedSent = await TypeUnpauseAsync(vm);

        Assert.Equal(3, prompt.Shown);
        Assert.Equal(Sentinel, vm.StatusText);
        Assert.False(typedSent);
        Assert.Equal("UNPAUSE", vm.CommandText); // a cancelled typed resume stays in the box
    }

    [AvaloniaFact]
    public async Task Resume_AfterStartAnyway_DoesNotIntercept()
    {
        var prompt = new PromptStub(PilotVoiceWarningChoice.StartAnyway);
        MainViewModel vm = JoinedViewModel(solo: true, prompt);

        await vm.TogglePauseCommand.ExecuteAsync(null);
        Assert.Equal(1, prompt.Shown);
        Assert.NotEqual(Sentinel, vm.StatusText); // the resume was sent

        vm.StatusText = Sentinel;
        await vm.TogglePlaybackCommand.ExecuteAsync(null);
        Assert.NotEqual(Sentinel, vm.StatusText);

        Assert.True(await TypeUnpauseAsync(vm));
        Assert.Equal(1, prompt.Shown);
    }

    /// <summary>
    /// Pilot voice availability is the voice pack on disk plus a default audio device, which a test cannot control, so
    /// this one case goes through <see cref="MainViewModel.EvaluatePilotVoiceWarning"/> rather than the room state.
    /// </summary>
    [AvaloniaFact]
    public void Resume_SoloWithVoice_DoesNotIntercept()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        Dispatcher.UIThread.RunJobs();

        vm.EvaluatePilotVoiceWarning(soloSession: true, pilotVoiceUsable: true);

        Assert.False(vm.ShouldInterceptResume());
        Assert.False(vm.NoPilotVoiceInSolo);
    }

    [AvaloniaFact]
    public async Task Resume_NotSolo_DoesNotIntercept()
    {
        var prompt = new PromptStub(PilotVoiceWarningChoice.Cancel);
        MainViewModel vm = JoinedViewModel(solo: false, prompt);

        await vm.TogglePauseCommand.ExecuteAsync(null);
        await vm.TogglePlaybackCommand.ExecuteAsync(null);
        bool typedSent = await TypeUnpauseAsync(vm);

        Assert.Equal(0, prompt.Shown);
        Assert.True(typedSent);
    }

    [AvaloniaFact]
    public async Task ScenarioReload_ResetsTheOncePerSessionFlag()
    {
        var prompt = new PromptStub(PilotVoiceWarningChoice.StartAnyway);
        MainViewModel vm = JoinedViewModel(solo: true, prompt);
        await vm.TogglePauseCommand.ExecuteAsync(null);
        Assert.Equal(1, prompt.Shown);

        vm.OnScenarioLoaded(
            new ScenarioLoadedDto("scenario-8", "OAK Ground 8", null, IsPaused: true, SimRate: 1, AllAircraft: [], SoloTrainingMode: true)
        );
        Dispatcher.UIThread.RunJobs();
        await vm.TogglePauseCommand.ExecuteAsync(null);

        Assert.Equal(2, prompt.Shown);
    }

    /// <summary>A reconnect or a server-restart restore re-applies the same room; the student's "Start anyway" stands.</summary>
    [AvaloniaFact]
    public async Task Reconnect_ApplyRoomStateSameRoom_KeepsTheOncePerSessionFlag()
    {
        var prompt = new PromptStub(PilotVoiceWarningChoice.StartAnyway);
        MainViewModel vm = JoinedViewModel(solo: true, prompt);
        await vm.TogglePauseCommand.ExecuteAsync(null);
        Assert.Equal(1, prompt.Shown);

        vm.ApplyRoomState(PausedRoom(solo: true));
        Dispatcher.UIThread.RunJobs();
        await vm.TogglePauseCommand.ExecuteAsync(null);

        Assert.Equal(1, prompt.Shown);
    }

    [AvaloniaFact]
    public async Task Banner_VisibleOnlyWhenSoloWithoutVoice()
    {
        var prompt = new PromptStub(PilotVoiceWarningChoice.StartAnyway);
        var vm = new MainViewModel(new FakeFilePickerService());
        Dispatcher.UIThread.RunJobs();
        vm.Preferences.SetPilotVoiceSettings(false, 80, true);
        vm.PilotVoiceWarningPrompt = prompt.Show;
        Assert.False(vm.NoPilotVoiceInSolo); // not in a room

        vm.ApplyRoomState(PausedRoom(solo: true));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.NoPilotVoiceInSolo);

        // Choosing "Start anyway" silences the dialog, not the banner.
        await vm.TogglePauseCommand.ExecuteAsync(null);
        Assert.Equal(1, prompt.Shown);
        Assert.True(vm.NoPilotVoiceInSolo);

        vm.ApplyRoomState(PausedRoom(solo: false));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.NoPilotVoiceInSolo);
    }

    [AvaloniaFact]
    public async Task Resume_ChoosingVoiceSettings_RaisesSettingsRequestOnce_AndDoesNotResume()
    {
        var prompt = new PromptStub(PilotVoiceWarningChoice.OpenVoiceSettings);
        MainViewModel vm = JoinedViewModel(solo: true, prompt);
        int settingsRequests = 0;
        vm.PilotVoiceSettingsRequested += () => settingsRequests++;

        await vm.TogglePauseCommand.ExecuteAsync(null);

        Assert.Equal(1, prompt.Shown);
        Assert.Equal(1, settingsRequests);
        Assert.Equal(Sentinel, vm.StatusText);
    }

    [AvaloniaFact]
    public void OpenPilotVoiceSettingsCommand_RaisesSettingsRequestOnce_AndDoesNotResume()
    {
        var prompt = new PromptStub(PilotVoiceWarningChoice.StartAnyway);
        MainViewModel vm = JoinedViewModel(solo: true, prompt);
        int settingsRequests = 0;
        vm.PilotVoiceSettingsRequested += () => settingsRequests++;

        vm.OpenPilotVoiceSettingsCommand.Execute(null);

        Assert.Equal(1, settingsRequests);
        Assert.Equal(0, prompt.Shown);
        Assert.Equal(Sentinel, vm.StatusText);
        Assert.True(vm.IsPaused);
    }
}
