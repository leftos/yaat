using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The scope's viewer: the instructor's active position (the scenario's student position, or the one a persistent <c>AS</c>
/// picked), rebuilt from the main view model whenever the server or the dropdown changes it, with the ERAM sector it names.
/// An ERAM position's display config carries no STARS maps, so it sets the active position and leaves the Scope's maps alone.
/// </summary>
public class ScopeViewModelViewerTests
{
    private static readonly ActivePositionDto Sector44 = new(
        Callsign: "OAK_44_CTR",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    private static PositionDisplayConfigDto EramConfig(string tcp, ActivePositionDto? position) =>
        new([], [], [], tcp, position, 3, NoStarsDisplay: true);

    [AvaloniaFact]
    public void ServerActivePosition_SetsTheViewer_AndRaisesItsChange()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        Assert.Equal(ScopeViewer.None, scope.Viewer);
        var changed = new List<string?>();
        scope.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        main.SetActivePositionFromServer(EramConfig("C44", Sector44));

        Assert.Contains(nameof(ScopeViewModel.Viewer), changed);
        Assert.Equal("ZOA", scope.Viewer.EramFacilityId);
        Assert.Equal("44", scope.Viewer.EramSectorId);
        Assert.Equal("C44", scope.Viewer.Acting?.TcpCode);
        Assert.Equal("OAK_44_CTR", scope.Viewer.Acting?.Callsign);
    }

    [AvaloniaFact]
    public void ActiveTcpChange_RebuildsTheViewer()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.SetActivePositionFromServer(EramConfig("C44", Sector44));

        main.SetActiveTcpFromServer("C14");

        Assert.Equal("C14", scope.Viewer.Acting?.TcpCode);
    }

    [AvaloniaFact]
    public void DropdownPick_ThenTheServersAnswerForTheSameTcp_SetsTheSector()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.SetActivePositionFromServer(EramConfig("C14", Sector44 with { Callsign = "OAK_14_CTR", EramSectorId = "14" }));

        // The dropdown pick sets the TCP and drops the old position until the server answers.
        main.ActiveTcp = "C44";
        Dispatcher.UIThread.RunJobs();
        Assert.Null(main.ActivePosition);
        Assert.Null(scope.Viewer.EramSectorId);

        main.OnPositionDisplayChanged(EramConfig("C44", Sector44));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("44", scope.Viewer.EramSectorId);
        Assert.Equal("OAK_44_CTR", scope.Viewer.Acting?.Callsign);
    }

    [AvaloniaFact]
    public void NoStarsDisplayConfig_KeepsTheScopeMaps_AndStillSetsTheActivePosition()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        main.ScopeMaps.ApplyPositionDisplayConfig(new([1, 2, 3], ["4U"], ["SFO"], "4U", null, 3));

        main.OnPositionDisplayChanged(EramConfig("C44", Sector44));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([1, 2, 3], main.ScopeMaps.MapGroupMapIds);
        Assert.Equal(["SFO"], main.ScopeMaps.WeatherAirports);
        Assert.Equal("C44", main.ActiveTcp);
        Assert.Same(Sector44, main.ActivePosition);
    }

    [Fact]
    public void ConfigJsonWithoutTheFlag_ReadsAsAStarsDisplay()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ServerConnection.UseHubJsonContexts(options);
        const string json = """{"mapGroupMapIds":[1],"mapGroupTcpCodes":["4U"],"underlyingAirports":[],"tcpCode":"4U","scratchpadMaxLength":3}""";

        PositionDisplayConfigDto? config = JsonSerializer.Deserialize<PositionDisplayConfigDto>(json, options);

        Assert.NotNull(config);
        Assert.False(config.NoStarsDisplay);
        Assert.Equal("4U", config.TcpCode);
    }
}
