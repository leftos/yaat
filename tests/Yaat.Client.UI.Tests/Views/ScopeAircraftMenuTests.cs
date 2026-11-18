using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Toolbar;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope aircraft menu's own view section: the Display submenu built from the Scope's state (the hidden block and its
/// reset, the pilot lens's intent path, the measure tool, the local J-ring and cone, and the shared send rows), then Draw
/// route, and the menu's "Settings for this view…".
/// </summary>
public class ScopeAircraftMenuTests
{
    private const string Callsign = "SWA104";
    private const string SettingsHeader = "Settings for this view…";

    private sealed record Shown(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window, AircraftModel Aircraft)
    {
        public ContextMenu Menu() => View.BuildAircraftMenu(Scope, Aircraft, null, Callsign);

        public List<(string Callsign, string Command, string Initials)> Sent { get; } = [];
    }

    private static async Task<Shown> ShowScopeAsync(Action<AircraftModel> setUp)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        AircraftModel ac = AddAircraft(main, Callsign, 5000);
        setUp(ac);
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 900,
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

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var shown = new Shown(main, scope, view, window, ac);
        scope.SendPilotCommand = (callsign, command, initials) =>
        {
            shown.Sent.Add((callsign, command, initials));
            return Task.CompletedTask;
        };
        return shown;
    }

    private static void CloseWindow(Shown s)
    {
        s.View.ActiveContextMenu?.Close();
        Dispatcher.UIThread.RunJobs();
        s.Window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static MenuItem AllCommands(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == AircraftMenuBuilder.AllCommandsHeader);

    private static MenuItem Display(ContextMenu menu) =>
        Assert.Single(AllCommands(menu).Items.OfType<MenuItem>(), i => (i.Header as string) == "Display");

    private static List<string> Headers(MenuItem menu) => [.. menu.Items.Select(Describe)];

    private static string Describe(object? item) =>
        item switch
        {
            Separator => "---",
            MenuItem menuItem => menuItem.Header as string ?? "",
            _ => item?.GetType().Name ?? "null",
        };

    private static MenuItem? Find(MenuItem menu, string header) => menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Header, header));

    private static MenuItem Row(ContextMenu menu, string header) =>
        Assert.Single(Display(menu).Items.OfType<MenuItem>(), i => Equals(i.Header, header));

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void ToggleLensPathOn(ScopeViewModel scope)
    {
        scope.SetLensEnabled(true);
        scope.SetLensOverlay(ScopeToolbarButtons.LensPathItem, on: true);
    }

    // A fresh menu's Display: no data-block form row, no reset until the block moves, and the measure row from the idle tool.
    [AvaloniaFact]
    public async Task Display_OnAFreshMenu_PinsTheRowSequence()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            Assert.Equal(
                [
                    "Hide datablock",
                    "Show nav route",
                    $"Measure from {Callsign}",
                    "---",
                    "Leader direction",
                    "J-ring",
                    "Cone",
                    "---",
                    "Blank target",
                    "Unblank target",
                ],
                Headers(Display(s.Menu()))
            );
        }
        finally
        {
            CloseWindow(s);
        }
    }

    // Every state on at once: the hidden block, the moved block, the toggled intent path and an anchored measurement.
    [AvaloniaFact]
    public async Task Display_AllStatesOn_PinsTheRowsAndTheirLabels()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            s.Scope.HideDataBlock(Callsign);
            s.Scope.SetDataBlockOffset(Callsign, new SKPoint(12, 34));
            ToggleLensPathOn(s.Scope);
            s.Scope.ToggleLensPath(Callsign);
            s.Scope.Measure.Pick(RblEndpoint.OnAircraft(Callsign), s.Scope.MeasureTarget);

            Assert.Equal(
                [
                    "Show datablock",
                    "Reset datablock position",
                    "Hide nav route",
                    $"Measure to {Callsign}",
                    "---",
                    "Leader direction",
                    "J-ring",
                    "Cone",
                    "---",
                    "Blank target",
                    "Unblank target",
                ],
                Headers(Display(s.Menu()))
            );
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task HideDatablockRow_OnAVisibleBlock_ReadsHideDatablock()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            Assert.NotNull(Find(Display(s.Menu()), "Hide datablock"));
            Assert.Null(Find(Display(s.Menu()), "Show datablock"));
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task HideDatablockRow_OnAHiddenBlock_ReadsShowDatablock()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            s.Scope.HideDataBlock(Callsign);

            Assert.NotNull(Find(Display(s.Menu()), "Show datablock"));
            Assert.Null(Find(Display(s.Menu()), "Hide datablock"));
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task HideDatablockRow_Click_HidesTheBlockAndTheRowReadsBack()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            Click(Row(s.Menu(), "Hide datablock"));
            Assert.True(s.Scope.IsDataBlockHidden(Callsign));

            Click(Row(s.Menu(), "Show datablock"));
            Assert.False(s.Scope.IsDataBlockHidden(Callsign));
        }
        finally
        {
            CloseWindow(s);
        }
    }

    // The row acts on the state it was built from: a block hidden elsewhere while the menu is open (a Shift+click) stays hidden
    // when the stale "Hide datablock" row is clicked.
    [AvaloniaFact]
    public async Task HideDatablockRow_ClickedAfterTheBlockWasHiddenElsewhere_KeepsItHidden()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            MenuItem row = Row(s.Menu(), "Hide datablock");
            s.Scope.HideDataBlock(Callsign);

            Click(row);

            Assert.True(s.Scope.IsDataBlockHidden(Callsign));
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task ResetDatablockPosition_IsOfferedOnlyAfterAMove_AndRemovesTheOffset()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            Assert.Null(Find(Display(s.Menu()), "Reset datablock position"));

            s.Scope.SetDataBlockOffset(Callsign, new SKPoint(12, 34));
            Click(Row(s.Menu(), "Reset datablock position"));

            Assert.False(s.Scope.HasManualDataBlockOffset(Callsign));
            Assert.Null(Find(Display(s.Menu()), "Reset datablock position"));
        }
        finally
        {
            CloseWindow(s);
        }
    }

    // With the lens off nothing on the track's path draws: the row offers Show, and a click turns the lens and its PATH on.
    [AvaloniaFact]
    public async Task NavRouteRow_WithTheLensOff_ReadsShowNavRoute_AndAClickTurnsTheLensAndPathOn()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            Click(Row(s.Menu(), "Show nav route"));

            Assert.True(s.Scope.Lens.Enabled);
            Assert.True(s.Scope.Lens.Path);
            Assert.Contains(Callsign, s.Scope.LensPathCallsigns);
            Assert.NotNull(Find(Display(s.Menu()), "Hide nav route"));
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task NavRouteRow_LensOnTrackNotShown_ReadsShowNavRoute_AndAClickAddsTheTrack()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            ToggleLensPathOn(s.Scope);

            Click(Row(s.Menu(), "Show nav route"));

            Assert.Contains(Callsign, s.Scope.LensPathCallsigns);
            Assert.True(s.Scope.Lens.DrawsPath);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task NavRouteRow_TrackToggled_ReadsHideNavRoute_AndAClickRemovesItLeavingTheSwitches()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            ToggleLensPathOn(s.Scope);
            s.Scope.ToggleLensPath(Callsign);

            Click(Row(s.Menu(), "Hide nav route"));

            Assert.DoesNotContain(Callsign, s.Scope.LensPathCallsigns);
            Assert.True(s.Scope.Lens.Enabled);
            Assert.True(s.Scope.Lens.Path);
            Assert.NotNull(Find(Display(s.Menu()), "Show nav route"));
        }
        finally
        {
            CloseWindow(s);
        }
    }

    // The row reads the track's membership in the path set, never the selection; a click adds it, so the path outlives the deselect.
    [AvaloniaFact]
    public async Task NavRouteRow_ASelectedTrackNotToggled_ReadsShowNavRoute_AndThePathOutlivesTheDeselect()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            ToggleLensPathOn(s.Scope);
            s.Main.SelectedAircraft = s.Aircraft;

            Assert.NotNull(Find(Display(s.Menu()), "Show nav route"));
            Click(Row(s.Menu(), "Show nav route"));
            s.Main.SelectedAircraft = null;

            Assert.Contains(Callsign, s.Scope.LensPathCallsigns);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task MeasureRow_ReadsFromThenTo_AndAClickLatchesTheAircraft()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            Click(Row(s.Menu(), $"Measure from {Callsign}"));

            Assert.NotNull(s.Scope.Measure.Anchor);
            Assert.NotNull(Find(Display(s.Menu()), $"Measure to {Callsign}"));
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task JRingRow_IsUnchecked_AndAClickPlacesTheRingAtTheRingsSize()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            Assert.False(Row(s.Menu(), "J-ring").IsChecked);
            Assert.Null(s.Scope.RingsOf(Callsign).JRingNm);

            Click(Row(s.Menu(), "J-ring"));

            Assert.Equal(s.Scope.RingSizeNm, s.Scope.RingsOf(Callsign).JRingNm);
            Assert.True(Row(s.Menu(), "J-ring").IsChecked);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task JRingRow_Checked_AClickRemovesTheRingWhateverItsSize()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            s.Scope.SetRings(Callsign, ScopeTrackRings.None with { JRingNm = s.Scope.RingSizeNm + 2 });
            Assert.True(Row(s.Menu(), "J-ring").IsChecked);

            Click(Row(s.Menu(), "J-ring"));

            Assert.Null(s.Scope.RingsOf(Callsign).JRingNm);
            Assert.False(Row(s.Menu(), "J-ring").IsChecked);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task ConeRow_TogglesTheCone()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            Click(Row(s.Menu(), "Cone"));

            Assert.Equal(s.Scope.RingSizeNm, s.Scope.RingsOf(Callsign).ConeNm);
            Assert.True(Row(s.Menu(), "Cone").IsChecked);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    // The rings are the Scope's own overlay: the menu rows write them and send the server nothing.
    [AvaloniaFact]
    public async Task RingRows_SendNothingToTheServer()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            Click(Row(s.Menu(), "J-ring"));
            Click(Row(s.Menu(), "Cone"));

            Assert.Empty(s.Sent);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    [AvaloniaFact]
    public async Task AircraftMenu_EndsWithSettingsForThisView_OpeningTheRadarSection()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            ContextMenu menu = s.Menu();
            MenuItem last = Assert.IsType<MenuItem>(menu.Items[^1]);
            Assert.Equal(SettingsHeader, last.Header);
            Assert.IsType<Separator>(menu.Items[^2]);

            List<SettingsSectionId?> requests = [];
            s.Main.SettingsRequested += requests.Add;
            Click(last);

            Assert.Equal([SettingsSectionId.Radar], requests);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    // The Scope's twin of the radar's whole-menu seam: its view section ends All Commands, Display before Draw route.
    [AvaloniaFact]
    public async Task BuildAircraftMenu_EndsAllCommandsWithDisplayThenDrawRoute()
    {
        Shown s = await ShowScopeAsync(_ => { });
        try
        {
            List<string> all = Headers(AllCommands(s.Menu()));
            Assert.Equal(["Display", "Draw route"], all[^2..]);
        }
        finally
        {
            CloseWindow(s);
        }
    }

    // Display shows for every aircraft; only Draw route is gated, so a surface live-traffic shadow keeps Display alone.
    [AvaloniaFact]
    public async Task BuildAircraftMenu_ASurfaceShadow_GetsDisplayAndNoDrawRoute()
    {
        Shown s = await ShowScopeAsync(ac =>
        {
            ac.CurrentPhase = "ApproachNav";
            ac.IsLiveTraffic = true;
            ac.IsOnGround = true;
        });
        try
        {
            MenuItem display = Display(s.Menu());
            Assert.NotEmpty(display.Items);
            Assert.DoesNotContain("Draw route", Headers(AllCommands(s.Menu())));
            Assert.NotNull(Find(display, "Hide datablock"));
        }
        finally
        {
            CloseWindow(s);
        }
    }
}
