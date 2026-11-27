using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The CRR range data block through the Scope's own wiring: the room's groups (<c>MainViewModel.CrrGroups</c>) reach the
/// canvas through the view's binding, and a change of them rebuilds the frame its tracks are laid out in.
/// </summary>
public class ScopeCrrTests
{
    private static async Task<(MainViewModel Main, ScopeView View, Window Window)> ShowEramScope()
    {
        MainViewModel main = await MainAsync();
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        Helpers.ScopeTestFixture.PickLook(main, ScopeTemplates.EramId);
        main.OpenScope = scope;
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return (main, view, window);
    }

    private static ScopeCrrReadoutView? ReadoutOf(ScopeCanvas canvas, string callsign)
    {
        Dispatcher.UIThread.RunJobs();
        return canvas.CurrentFrame.Tracks.Single(t => t.Aircraft.Callsign == callsign).CrrReadout;
    }

    [AvaloniaFact]
    public async Task TheRoomsGroups_ReachTheFrame_AndAChangeRebuildsIt()
    {
        (MainViewModel main, ScopeView view, Window window) = await ShowEramScope();
        try
        {
            AircraftModel ac = AddAircraft(main, "UAL1549", 9000);
            ac.EramCrrGroup = "A";
            Assert.Null(ReadoutOf(view.Canvas, "UAL1549"));

            main.ApplyCrrGroups([new EramCrrGroupHubDto("A", "Coral", 37.5, -122.2)]);

            ScopeCrrReadoutView crr = Assert.IsType<ScopeCrrReadoutView>(ReadoutOf(view.Canvas, "UAL1549"));
            Assert.Equal("12.0", crr.Text);

            main.ApplyCrrGroups([]);

            Assert.Null(ReadoutOf(view.Canvas, "UAL1549"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
