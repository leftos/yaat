using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Active-position (TCP) indicator/dropdown in the terminal input bar (issue #199 follow-up).
/// The selection mirrors the server's persistent active position: server-originated updates
/// (bootstrap seed / <c>PositionDisplayChanged</c>) must NOT echo an <c>AS</c> command, while a
/// user pick from the dropdown must send one. A unit-constructed VM has no live server connection,
/// so an attempted <c>AS</c> send fails and surfaces as an "AS error" in <see cref="MainViewModel.StatusText"/>;
/// that makes StatusText the distinguishing observable between the two paths.
/// </summary>
public class MainViewModelActivePositionTests
{
    private static OnlineControllerDto Controller(string? tcp) =>
        new("OAK_TWR", "OAK Tower", "120.000", "OAK", "Oakland", "Tower", tcp, null, "Real Name", true, true, tcp);

    [AvaloniaFact]
    public async Task SetActiveTcpFromServer_SeedsIndicator_WithoutSendingAs()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        await vm.NavDataInitialization;
        vm.StatusText = "sentinel";

        vm.SetActiveTcpFromServer("3Y");

        Assert.Equal("3Y", vm.ActiveTcp);
        Assert.True(vm.ShowActiveTcpSelector);
        Assert.Contains("3Y", vm.ActiveTcpOptions);
        Assert.Equal("sentinel", vm.StatusText); // server-originated update must not echo an AS command
    }

    [AvaloniaFact]
    public async Task SelectingTcp_SendsAsCommand()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        await vm.NavDataInitialization;
        vm.SetActiveTcpFromServer("3Y");
        vm.StatusText = "sentinel";

        // Simulate the dropdown writing a new selection back to the bound property.
        vm.ActiveTcp = "4U";

        // No live connection → the AS send fails and surfaces as an AS error, proving the
        // user-pick path attempted to send (unlike the suppressed server path above).
        Assert.StartsWith("AS error", vm.StatusText);
    }

    [AvaloniaFact]
    public void SetActiveTcpFromServer_PopulatesOptionBeforeSelection()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        // The bound ComboBox resolves its SelectedItem against the current ItemsSource: if ActiveTcp
        // is assigned before ActiveTcpOptions contains it, the ComboBox renders blank and re-adding the
        // item later does not re-sync (re-seeding the same value fires no PropertyChanged). Capture the
        // options snapshot at the instant ActiveTcp becomes the selected value.
        bool optionPresentWhenSelected = false;
        vm.PropertyChanged += (_, e) =>
        {
            if ((e.PropertyName == nameof(MainViewModel.ActiveTcp)) && (vm.ActiveTcp is not null))
            {
                optionPresentWhenSelected = vm.ActiveTcpOptions.Contains(vm.ActiveTcp);
            }
        };

        vm.SetActiveTcpFromServer("3O");

        Assert.True(
            optionPresentWhenSelected,
            "ActiveTcp must already be in ActiveTcpOptions when it becomes the selected value, " + "otherwise the bound ComboBox renders blank."
        );
    }

    [AvaloniaFact]
    public void RebuildOptions_KeepsCurrentTcpFirst_AddsOnlineControllers_Deduped()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.OnlineControllers.Add(Controller("4U"));
        vm.OnlineControllers.Add(Controller("3Y")); // same as the current TCP — must dedupe
        vm.OnlineControllers.Add(Controller(null)); // no TCP — excluded

        vm.SetActiveTcpFromServer("3Y");

        Assert.Equal("3Y", vm.ActiveTcpOptions[0]); // current TCP listed first
        Assert.Contains("4U", vm.ActiveTcpOptions);
        Assert.Equal(2, vm.ActiveTcpOptions.Count); // 3Y + 4U; the duplicate and the null-TCP entry are dropped
    }

    private static readonly ActivePositionDto SfoDep = new(
        "SFO_DEP",
        "Area D Sutro",
        "NorCal Approach",
        "135.100",
        "NCT",
        "Northern California TRACON",
        "Tracon",
        "4U"
    );

    private static PositionDisplayConfigDto Config(string tcp, ActivePositionDto? activePosition) => new([], [], [], tcp, activePosition, 3);

    private static ScenarioBootstrap Bootstrap(PositionDisplayConfigDto config) =>
        new()
        {
            ScenarioId = "scenario-1",
            ScenarioName = "SFO Departures",
            PrimaryAirportId = "SFO",
            PositionDisplayConfig = config,
            FlightStripsConfig = null,
            Aircraft = [],
            ElapsedSeconds = 0,
        };

    [AvaloniaFact]
    public void ScenarioBootstrap_SetsActivePositionAndTcp()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyScenarioBootstrap(Bootstrap(Config("4U", SfoDep)));

        Assert.Equal(SfoDep, vm.ActivePosition);
        Assert.Equal("4U", vm.ActiveTcp);
    }

    [AvaloniaFact]
    public void PositionDisplayChanged_SetsActivePositionAndTcp()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyScenarioBootstrap(Bootstrap(Config("4U", SfoDep)));
        ActivePositionDto suuSouth = new("SUU_S_APP", "South", "Travis Approach", "119.900", "SUU", "Travis RAPCON", "Tracon", "SUU_S_APP");

        vm.OnPositionDisplayChanged(Config("1S", suuSouth));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(suuSouth, vm.ActivePosition);
        Assert.Equal("1S", vm.ActiveTcp);
    }

    [AvaloniaFact]
    public void ScenarioUnload_ClearsActivePosition()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyScenarioBootstrap(Bootstrap(Config("4U", SfoDep)));

        vm.ClearScenarioState();

        Assert.Null(vm.ActivePosition);
        Assert.Null(vm.ActiveTcp);
    }

    /// <summary>
    /// A dropdown pick changes the TCP before the server answers (and the server may not answer at all), so the position the server
    /// last described no longer applies: it is dropped until the server's next update names the new one.
    /// </summary>
    [AvaloniaFact]
    public void UserPick_DropsTheActivePosition_UntilTheServerAnswers()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.SetActivePositionFromServer(Config("4U", SfoDep));

        vm.ActiveTcp = "1S";

        Assert.Null(vm.ActivePosition);
    }

    [AvaloniaFact]
    public void ClearingActiveTcp_HidesIndicatorAndEmptiesOptions()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.SetActiveTcpFromServer("3Y");
        Assert.True(vm.ShowActiveTcpSelector);

        vm.SetActiveTcpFromServer(null);

        Assert.Null(vm.ActiveTcp);
        Assert.False(vm.ShowActiveTcpSelector);
        Assert.Empty(vm.ActiveTcpOptions);
    }
}
