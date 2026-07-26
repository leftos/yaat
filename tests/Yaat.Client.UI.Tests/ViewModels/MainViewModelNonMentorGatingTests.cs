using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// A non-mentor (a signed-in controller who holds no VATUSA mentor role and is rated below Instructor)
/// cannot create rooms or load/unload scenarios — the server rejects those with a <c>HubException</c>.
/// Before this gating existed the Load/Unload Scenario menu items stayed enabled for them and the
/// rejection surfaced only as a line of small gray status-bar text, so students reported the commands
/// "did nothing".
///
/// The gate is a rating tier, not the RPO position — a mentor working an RPO position keeps full
/// powers. (The flag was called <c>IsLimitedRpo</c> until it was renamed for exactly that confusion.)
///
/// The controls must be disabled up front. Everything else in a room (pause, sim rate, weather, spawn,
/// commands) stays open to non-mentors by the confirmed design decision in
/// docs/plans/rpo-limited-access-and-vatusa-artcc.md — these tests pin that boundary in both directions.
/// </summary>
public class MainViewModelNonMentorGatingTests
{
    private static MainViewModel InRoom(bool nonMentor) =>
        new(new FakeFilePickerService())
        {
            IsConnected = true,
            ActiveRoomId = "room-1",
            IsNonMentor = nonMentor,
            ActiveScenarioId = "scenario-1",
        };

    [AvaloniaFact]
    public void NonMentor_CannotLoadScenario()
    {
        Assert.False(InRoom(nonMentor: true).CanLoadScenario);
    }

    [AvaloniaFact]
    public void MentorInRoom_CanLoadScenario()
    {
        Assert.True(InRoom(nonMentor: false).CanLoadScenario);
    }

    [AvaloniaFact]
    public void NonMentor_CannotUnloadScenario()
    {
        Assert.False(InRoom(nonMentor: true).UnloadScenarioCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void MentorInRoom_CanUnloadScenario()
    {
        Assert.True(InRoom(nonMentor: false).UnloadScenarioCommand.CanExecute(null));
    }

    /// <summary>
    /// The gate has to re-evaluate when the non-mentor flag arrives — it is set at connect time, after the
    /// commands have already been created, so a missing change notification would leave a stale enabled
    /// menu item. This is the exact wiring that was absent for the scenario commands.
    /// </summary>
    [AvaloniaFact]
    public void FlippingIsNonMentor_RaisesCanExecuteChanged_ForScenarioCommands()
    {
        var vm = InRoom(nonMentor: false);
        bool unloadChanged = false;
        bool loadChanged = false;
        vm.UnloadScenarioCommand.CanExecuteChanged += (_, _) => unloadChanged = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CanLoadScenario))
            {
                loadChanged = true;
            }
        };

        vm.IsNonMentor = true;

        Assert.True(unloadChanged, "UnloadScenarioCommand must re-evaluate when IsNonMentor changes.");
        Assert.True(loadChanged, "CanLoadScenario must notify when IsNonMentor changes.");
    }

    /// <summary>
    /// Guards against over-gating. The design doc deliberately leaves in-room powers open to non-mentors;
    /// only create/load/unload/kick are mentor-only. Restart is open too — unlike Unload it re-runs the
    /// same scenario, so it can neither strand the room nor switch scenarios.
    /// </summary>
    [AvaloniaFact]
    public void NonMentor_RetainsInRoomPowers()
    {
        var vm = InRoom(nonMentor: true);

        Assert.True(vm.CanExecuteInRoom, "In-room powers (pause, sim rate, weather, spawn) stay open to non-mentors.");
        Assert.True(vm.RestartScenarioCommand.CanExecute(null), "Restart is available to any room member.");
    }

    [AvaloniaFact]
    public void RestartScenario_RequiresAnActiveScenario()
    {
        var vm = new MainViewModel(new FakeFilePickerService())
        {
            IsConnected = true,
            ActiveRoomId = "room-1",
            IsNonMentor = true,
        };

        Assert.False(vm.RestartScenarioCommand.CanExecute(null));
    }
}
