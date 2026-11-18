using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Sim;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's DB settings on the real canvas: 4TH LINE, DWELL, ATPA DIST and CHAR seed once from the preferences, change only
/// through <c>.scope db</c>, and reach every frame the canvas builds; the preferences never change them afterwards.
/// </summary>
public class ScopeDbSettingsTests
{
    [AvaloniaFact]
    public void Defaults_SeedFromThePreferencesOnce_AndLaterPreferenceChangesLeaveTheScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        UserPreferences prefs = main.Preferences;
        prefs.SetShowAtpa(true);
        prefs.SetFontSizes(Sizes(prefs, 14));
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope(main);
        try
        {
            Assert.Equal(new ScopeDbSettings(Line4: true, HoverDwell: true, Atpa: true, EramCaRing: true, CharSize: 14), scope.DbSettings);
            AddAtpaPair(main, view.Canvas);
            float width = TrailerBlockWidth(view.Canvas);
            Assert.NotEqual("", AtpaText(view.Canvas));

            prefs.SetShowAtpa(false);
            prefs.SetFontSizes(Sizes(prefs, 20));
            main.Aircraft.Add(Aircraft("SKW5", 37.0, -121.0));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new ScopeDbSettings(Line4: true, HoverDwell: true, Atpa: true, EramCaRing: true, CharSize: 14), scope.DbSettings);
            Assert.NotEqual("", AtpaText(view.Canvas));
            Assert.Equal(width, TrailerBlockWidth(view.Canvas));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Line4Off_DropsTheTypeAndDestinationFallback_AndKeepsTheHsfNote()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope(main);
        try
        {
            main.RunScopeCommand(".scope template eram");
            AddAt(main, view.Canvas, "UAL238", new Point(300, 300), _ => { });
            AddAt(main, view.Canvas, "DAL12", new Point(600, 400), ac => ac.EramFreeText = "WX DEV");
            Assert.Equal("B738", Line4Of(view.Canvas, "UAL238"));
            Assert.Equal("WX DEV", Line4Of(view.Canvas, "DAL12"));

            main.RunScopeCommand(".scope db 4thline off");

            Assert.False(scope.DbSettings.Line4);
            Assert.Equal("", Line4Of(view.Canvas, "UAL238"));
            Assert.Equal("WX DEV", Line4Of(view.Canvas, "DAL12"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void AtpaOff_RemovesTheAtpaField()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        (_, ScopeView view, Window window) = ShowScope(main);
        try
        {
            main.RunScopeCommand(".scope db atpa on");
            AddAtpaPair(main, view.Canvas);
            Assert.NotEqual("", AtpaText(view.Canvas));

            main.RunScopeCommand(".scope db atpa off");

            Assert.Equal("", AtpaText(view.Canvas));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Char_ChangesTheMeasuredBlockSize()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        (_, ScopeView view, Window window) = ShowScope(main);
        try
        {
            main.RunScopeCommand(".scope db char 10");
            AddAt(main, view.Canvas, "UAL238", new Point(300, 300), _ => { });
            ScopeTrack small = Assert.Single(view.Canvas.CurrentFrame.Tracks);

            main.RunScopeCommand(".scope db char 16");

            ScopeTrack large = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            Assert.True(large.Block.TextBounds.Width > small.Block.TextBounds.Width, "the block grows wider");
            Assert.True(large.Block.TextBounds.Height > small.Block.TextBounds.Height, "the block grows taller");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DwellOff_ReleasesTheHoveredBlock_AndHoverDwellsNothing_WhileALockedBlockStaysAtDwell()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope(main);
        try
        {
            main.RunScopeCommand(".scope template eram");
            AddAt(main, view.Canvas, "UAL238", new Point(300, 300), _ => { });
            AddAt(main, view.Canvas, "DAL12", new Point(600, 400), _ => { });
            scope.ToggleDwellLock("DAL12");
            FieldRect callsign = TrackOf(view.Canvas, "UAL238").Block.Fields.First(f => f.Slot == ScopeFieldSlot.Callsign);
            var onBlock = new Point(callsign.Rect.MidX, callsign.Rect.MidY);
            Move(window, view.Canvas, onBlock);
            Assert.Equal(ScopeBlockLevel.Dwell, TrackOf(view.Canvas, "UAL238").Level);

            main.RunScopeCommand(".scope db dwell off");

            Assert.Null(view.Canvas.HoveredCallsign);
            Assert.Equal(ScopeBlockLevel.Full, TrackOf(view.Canvas, "UAL238").Level);
            Move(window, view.Canvas, new Point(onBlock.X + 1, onBlock.Y));
            Assert.Null(view.Canvas.HoveredCallsign);
            Assert.Equal(ScopeBlockLevel.Full, TrackOf(view.Canvas, "UAL238").Level);
            Assert.Equal(ScopeBlockLevel.Dwell, TrackOf(view.Canvas, "DAL12").Level);

            main.RunScopeCommand(".scope db dwell on");
            Move(window, view.Canvas, onBlock);
            Assert.Equal(ScopeBlockLevel.Dwell, TrackOf(view.Canvas, "UAL238").Level);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static FontSizePrefs Sizes(UserPreferences prefs, int radarDatablock) =>
        new(
            radarDatablock,
            prefs.RadarFlyoutFontSize,
            prefs.GroundDatablockFontSize,
            prefs.GroundLabelFontSize,
            prefs.DataGridFontSize,
            prefs.TerminalFontSize,
            prefs.InterfaceFontSize
        );

    private static (ScopeViewModel Scope, ScopeView View, Window Window) ShowScope(MainViewModel main)
    {
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
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
        return (scope, view, window);
    }

    private static AircraftModel Aircraft(string callsign, double lat, double lon) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = 5000,
            GroundSpeed = 250,
            Position = new LatLon(lat, lon),
        };

    // An airborne aircraft whose symbol sits at the given canvas point, added to the traffic the scope draws.
    // The aircraft is set up by setUp before it is added, since the kept frame drops on the add only.
    private static void AddAt(MainViewModel main, ScopeCanvas canvas, string callsign, Point at, Action<AircraftModel> setUp)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)at.X, (float)at.Y);
        AircraftModel ac = Aircraft(callsign, lat, lon);
        setUp(ac);
        main.Aircraft.Add(ac);
        Dispatcher.UIThread.RunJobs();
    }

    // A trailer (UAL238) paired behind a lead (SWA1234) for ATPA.
    private static void AddAtpaPair(MainViewModel main, ScopeCanvas canvas)
    {
        AddAt(main, canvas, "SWA1234", new Point(500, 300), _ => { });
        AddAt(
            main,
            canvas,
            "UAL238",
            new Point(300, 300),
            trailer =>
            {
                trailer.AtpaLeadCallsign = "SWA1234";
                trailer.AtpaAllowedSeparationNm = 3.0;
                trailer.AtpaConeState = AtpaConeState.Warning;
            }
        );
    }

    private static ScopeTrack TrackOf(ScopeCanvas canvas, string callsign) => canvas.CurrentFrame.Tracks.Single(t => t.Aircraft.Callsign == callsign);

    private static string AtpaText(ScopeCanvas canvas) => FieldText(canvas, "UAL238", ScopeFieldSlot.Atpa);

    // Line 4's tokens, their texts joined in their order on the line without the space the template puts before the speed:
    // the drawn line for a line of one token.
    private static string Line4Of(ScopeCanvas canvas, string callsign) => string.Concat(Line4Slots.Select(slot => FieldText(canvas, callsign, slot)));

    private static readonly ScopeFieldSlot[] Line4Slots =
    [
        ScopeFieldSlot.Line4FreeText,
        ScopeFieldSlot.Line4Heading,
        ScopeFieldSlot.Line4Pad,
        ScopeFieldSlot.Line4Speed,
        ScopeFieldSlot.Line4Notes,
    ];

    private static float TrailerBlockWidth(ScopeCanvas canvas) => TrackOf(canvas, "UAL238").Block.TextBounds.Width;

    // The field's text in the canvas's current frame, or empty when the block draws none.
    private static string FieldText(ScopeCanvas canvas, string callsign, ScopeFieldSlot slot) =>
        TrackOf(canvas, callsign).Block.Fields.Where(f => f.Slot == slot).Select(f => f.Text).FirstOrDefault() ?? "";

    // Moves the pointer to a canvas point and lets any repaint it asked for run, as the canvas's own would.
    private static void Move(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        window.MouseMove(inWindow.Value);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
