using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The scope's dwell-lock toggle is the instructor's own: it holds the block at Dwell on this scope only, needs no viewer
/// ERAM sector, and sends nothing to the server, so the student's CRC display state is never changed.
/// </summary>
public class ScopeViewModelDwellLockTests
{
    [AvaloniaFact]
    public void ToggleDwellLock_AddsThenRemovesTheCallsign_WithNoViewerEramSector()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
        Assert.Equal(ScopeViewer.None, scope.Viewer);
        string? status = main.StatusText;

        scope.ToggleDwellLock("UAL238");

        Assert.Equal(["UAL238"], scope.DataBlockState.DwellLockedCallsigns);

        scope.ToggleDwellLock("UAL238");

        Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
        Assert.Equal(status, main.StatusText);
    }

    [AvaloniaFact]
    public void ToggleDwellLock_NotConnected_SendsNothing_AndSetsNoStatus()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
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
        string? status = main.StatusText;

        // The connection is down, so a send would have thrown or shown its error.
        scope.ToggleDwellLock("UAL238");

        Assert.Equal(status, main.StatusText);
        Assert.Contains("UAL238", scope.DataBlockState.DwellLockedCallsigns);
    }

    [AvaloniaFact]
    public void ToggleDwellLock_OnACrcLockedBlock_AddsNoLocalLock()
    {
        (_, ScopeViewModel scope) = ScopeWithCrcLockedAircraft();
        using (scope)
        {
            scope.ToggleDwellLock("UAL238");

            Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
        }
    }

    [AvaloniaFact]
    public void ToggleDwellLock_LocalLockOnACrcLockedBlock_IsReleased()
    {
        (_, ScopeViewModel scope) = ScopeWithCrcLockedAircraft();
        using (scope)
        {
            scope.DataBlockState.DwellLockedCallsigns.Add("UAL238");

            scope.ToggleDwellLock("UAL238");

            Assert.Empty(scope.DataBlockState.DwellLockedCallsigns);
        }
    }

    // A scope viewed from ZOA sector 44, with UAL238 in the traffic, its block dwell-locked by the student's CRC for that sector.
    private static (MainViewModel Main, ScopeViewModel Scope) ScopeWithCrcLockedAircraft()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
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
        main.Aircraft.Add(new AircraftModel { Callsign = "UAL238", EramDwellLockedSectors = [new EramSectorKey("ZOA", "44")] });
        Assert.Equal("44", scope.Viewer.EramSectorId);
        return (main, scope);
    }
}
