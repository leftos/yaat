using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The scope's dwell-lock toggle: with no viewer ERAM sector it refuses with a status message and sends nothing, as CRC's
/// <c>ToggleEramDwellLock</c> ignores a client with no ERAM sector.
/// </summary>
public class ScopeViewModelDwellLockTests
{
    [AvaloniaFact]
    public async Task ToggleDwellLock_NoViewerEramSector_SetsTheStatus_AndSendsNothing()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main);
        Assert.Equal(ScopeViewer.None, scope.Viewer);

        await scope.ToggleDwellLockAsync("UAL238");

        // The connection is down, so a send would have thrown and shown its error in place of the refusal.
        Assert.Equal("No ERAM sector: select an ERAM position with AS to lock a data block", main.StatusText);
    }

    [AvaloniaFact]
    public async Task ToggleDwellLock_WithAnEramSector_NotConnected_ShowsTheError()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main);
        var sector44 = new ActivePositionDto(
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
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "C44", sector44, 3, NoStarsDisplay: true));
        Assert.Equal("44", scope.Viewer.EramSectorId);

        await scope.ToggleDwellLockAsync("UAL238");

        Assert.Equal("Dwell lock error: Not connected.", main.StatusText);
    }
}
