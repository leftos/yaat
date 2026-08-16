using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Regression tests for GitHub issue #360: re-triggering a reuse-and-activate window (Ctrl+Click
/// opening the Flight Plan Editor, or opening the Favorites Panel) while that window is minimized
/// left it minimized — Avalonia's <c>Window.Activate()</c> brings a window to the front but does
/// not un-minimize it. The reuse path must restore <c>WindowState.Normal</c> before activating.
/// </summary>
public class Issue360MinimizedWindowRestoreTests
{
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    [AvaloniaFact]
    public void FlightPlanEditor_ReopenWhileMinimized_RestoresAndLoadsNewAircraft()
    {
        var vm = NewVm();
        try
        {
            var first = FlightPlanEditorManager.Open(new AircraftModel { Callsign = "UAL1" }, vm);
            first.WindowState = WindowState.Minimized;

            var reused = FlightPlanEditorManager.Open(new AircraftModel { Callsign = "DAL2" }, vm);

            Assert.Same(first, reused);
            Assert.Equal(WindowState.Normal, reused.WindowState);
            Assert.Equal("DAL2 - Flight Plan", reused.Title);
        }
        finally
        {
            FlightPlanEditorManager.Close();
        }
    }

    [AvaloniaFact]
    public void FavoritesPanel_ReopenWhileMinimized_Restores()
    {
        var vm = NewVm();
        var first = FavoritesPanelWindow.ShowOrActivate(vm);
        try
        {
            first.WindowState = WindowState.Minimized;

            var reused = FavoritesPanelWindow.ShowOrActivate(vm);

            Assert.Same(first, reused);
            Assert.Equal(WindowState.Normal, reused.WindowState);
        }
        finally
        {
            first.Close();
        }
    }
}
