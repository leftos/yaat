using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's datablock deconfliction: off by default, seeded from the saved default mode and never written back to it;
/// <c>.scope dcnf off|snap|free</c> and DISPLAY's DCNF (reading DCNF, DCNF S, DCNF F) set it; overlapping blocks on the canvas
/// spread apart and hit-test where they are drawn; the mode is kept in a pref set, and a set without it takes the seed.
/// </summary>
public class ScopeDeconflictTests
{
    private static ScopeButton Dcnf => ScopeToolbarButtons.Find(ScopeButtonId.DisplayDcnf)!;

    [AvaloniaFact]
    public void ByDefault_DeconflictionIsOff()
    {
        (_, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            Assert.Equal(DatablockDeconflictMode.Off, scope.DeconflictMode);
            Assert.Null(scope.SpinnerValue(Dcnf));
            Assert.False(scope.IsActive(Dcnf));
        }
    }

    [AvaloniaFact]
    public void TheModeIsSeededFromTheSavedDefault_AndNeverWrittenBack()
    {
        WriteSavedDeconflictMode(DatablockDeconflictMode.FreeForm);
        var main = new MainViewModel(new FakeFilePickerService());
        ScopeMapToolsCommandTests.LoadSource(main);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;

        Assert.Equal(DatablockDeconflictMode.FreeForm, scope.DeconflictMode);

        main.RunScopeCommand(".scope dcnf snap");

        Assert.Equal(DatablockDeconflictMode.CompassSnap, scope.DeconflictMode);
        Assert.Equal(DatablockDeconflictMode.FreeForm, main.Preferences.RadarDeconflictMode);
    }

    [AvaloniaFact]
    public async Task DcnfVerb_SetsTheMode_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope dcnf snap");
            Assert.Equal(DatablockDeconflictMode.CompassSnap, scope.DeconflictMode);
            Assert.Equal("Radar DCNF SNAP", main.StatusText);

            main.RunScopeCommand(".scope dcnf FREE");
            Assert.Equal(DatablockDeconflictMode.FreeForm, scope.DeconflictMode);
            Assert.Equal("Radar DCNF FREE", main.StatusText);

            main.RunScopeCommand(".scope dcnf off");
            Assert.Equal(DatablockDeconflictMode.Off, scope.DeconflictMode);
            Assert.Equal("Radar DCNF OFF", main.StatusText);

            foreach (string bad in new[] { ".scope dcnf", ".scope dcnf on", ".scope dcnf snap now" })
            {
                main.RunScopeCommand(bad);
                Assert.Equal("Usage: .scope dcnf <off|snap|free>", main.StatusText);
            }

            Assert.Equal(DatablockDeconflictMode.Off, scope.DeconflictMode);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task DcnfVerb_WithNoOtherScope_ActsOnThePrimaryScope()
    {
        MainViewModel main = await MainAsync();

        main.RunScopeCommand(".scope dcnf snap");

        Assert.Equal(DatablockDeconflictMode.CompassSnap, main.PrimaryScope.DeconflictMode);
        Assert.Equal("Radar DCNF SNAP", main.StatusText);
    }

    [AvaloniaFact]
    public void DcnfTile_CyclesOffSnapFree_ReadingDcnfDcnfSDcnfF_AndKeepsTheTrayOpen()
    {
        (_, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.Press(ScopeToolbarButtons.Find(ScopeButtonId.Display)!);
            Assert.Equal(ScopeButtonId.Display, scope.OpenMenu);

            scope.Press(Dcnf);
            Assert.Equal(DatablockDeconflictMode.CompassSnap, scope.DeconflictMode);
            Assert.Equal("S", scope.SpinnerValue(Dcnf));
            Assert.True(scope.IsActive(Dcnf));
            Assert.Equal(ScopeButtonId.Display, scope.OpenMenu);

            scope.Press(Dcnf);
            Assert.Equal(DatablockDeconflictMode.FreeForm, scope.DeconflictMode);
            Assert.Equal("F", scope.SpinnerValue(Dcnf));

            scope.Press(Dcnf);
            Assert.Equal(DatablockDeconflictMode.Off, scope.DeconflictMode);
            Assert.Null(scope.SpinnerValue(Dcnf));
            Assert.False(scope.IsActive(Dcnf));
        }
    }

    [AvaloniaFact]
    public void TheMode_IsKeptInAPrefSet_ASetWithoutItTakingTheSeed()
    {
        WriteSavedDeconflictMode(DatablockDeconflictMode.FreeForm);
        (_, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.DeconflictMode = DatablockDeconflictMode.CompassSnap;
            ScopePrefSet set = scope.CapturePrefSet("MINE");
            Assert.Equal(DatablockDeconflictMode.CompassSnap, set.MapTools.Deconflict);

            scope.DeconflictMode = DatablockDeconflictMode.Off;
            scope.ApplyPrefSet(set);
            Assert.Equal(DatablockDeconflictMode.CompassSnap, scope.DeconflictMode);

            scope.ApplyPrefSet(set with { MapTools = new ScopePrefMapTools(false, false, null, null, null) });
            Assert.Equal(DatablockDeconflictMode.FreeForm, scope.DeconflictMode);

            scope.DeconflictMode = DatablockDeconflictMode.Off;
            scope.ApplyPrefSet(set with { MapTools = new ScopePrefMapTools(false, false, null, null, null) });
            Assert.Equal(DatablockDeconflictMode.FreeForm, scope.DeconflictMode);
        }
    }

    [AvaloniaFact]
    public void OnTheCanvas_OverlappingBlocksSpreadApart_AndHitTestWhereDrawn()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(scope.Main, view, "UAL238", new Point(300, 300));
            AddAircraft(scope.Main, view, "SWA1234", new Point(304, 300));
            Assert.True(BlocksOverlap(view.Canvas.CurrentFrame));

            scope.Main.RunScopeCommand(".scope dcnf snap");
            Dispatcher.UIThread.RunJobs();

            ScopeFrame frame = view.Canvas.CurrentFrame;
            Assert.False(BlocksOverlap(frame));
            foreach (ScopeTrack track in frame.Tracks)
            {
                SKRect callsign = track.Block.Fields.First(f => f.Slot == ScopeFieldSlot.Callsign).Rect;
                Assert.Same(track.Aircraft, frame.BlockAt(callsign.MidX, callsign.MidY));
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void OnTheCanvas_FreeForm_AfterHoverMoves_ABlockHitTestsWhereTheLastRenderDrewIt()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(scope.Main, view, "UAL238", new Point(300, 300));
            AddAircraft(scope.Main, view, "SWA1234", new Point(304, 300));
            scope.Main.RunScopeCommand(".scope dcnf free");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            SKRect onBlock = Assert.IsType<ScopeFrame>(view.Canvas.LastRenderedFrame).Tracks.First(t => t.Aircraft.Callsign == "UAL238").Block.Bounds;

            // Onto a block, off it, and off once more: each hover change drops the cached frame, and the pointer handler
            // builds a fresh one for its hit-test; renders come between them as they would.
            foreach (Point at in new[] { new Point(onBlock.MidX, onBlock.MidY), new Point(650, 100), new Point(660, 110) })
            {
                window.MouseMove(InWindow(view, window, at), RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
            }

            // The last render's frame, then a hit-test frame built after it, as the next hover change would build one.
            ScopeFrame drawn = Assert.IsType<ScopeFrame>(view.Canvas.LastRenderedFrame);
            view.Canvas.DropCachedFrame();
            ScopeFrame hitTested = view.Canvas.CurrentFrame;
            Assert.Same(drawn, view.Canvas.LastRenderedFrame);
            Assert.NotSame(drawn, hitTested);
            foreach (ScopeTrack track in hitTested.Tracks)
            {
                Assert.Equal(drawn.Tracks.Single(t => t.Aircraft == track.Aircraft).Block.Bounds, track.Block.Bounds);
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Point InWindow(ScopeView view, Window window, Point onCanvas)
    {
        Point? inWindow = view.Canvas.TranslatePoint(onCanvas, window);
        Assert.NotNull(inWindow);
        return inWindow.Value;
    }

    private static bool BlocksOverlap(ScopeFrame frame)
    {
        Assert.Equal(2, frame.Tracks.Count);
        SKRect a = frame.Tracks[0].Block.Bounds;
        SKRect b = frame.Tracks[1].Block.Bounds;
        return (Math.Min(a.Right, b.Right) > Math.Max(a.Left, b.Left)) && (Math.Min(a.Bottom, b.Bottom) > Math.Max(a.Top, b.Top));
    }

    /// <summary>Writes a preferences file whose only setting is the saved datablock deconfliction mode, before any is read.</summary>
    private static void WriteSavedDeconflictMode(DatablockDeconflictMode mode)
    {
        var file = new JsonObject { ["preferencesVersion"] = 1, ["radarDeconflictMode"] = (int)mode };
        File.WriteAllText(UserPreferences.FilePath, file.ToJsonString());
    }

    /// <summary>A scope seeded from one STARS area, with no window.</summary>
    private static (MainViewModel Main, ScopeViewModel Scope) SeededScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        ScopeMapToolsCommandTests.LoadSource(main);
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        return (main, scope);
    }

    private static (ScopeViewModel Scope, ScopeView View, Window Window) ShowScope()
    {
        (_, ScopeViewModel scope) = SeededScope();
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

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (scope, view, window);
    }

    private static void AddAircraft(MainViewModel main, ScopeView view, string callsign, Point symbol)
    {
        ScopeCanvas canvas = view.Canvas;
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)symbol.X, (float)symbol.Y);
        main.Aircraft.Add(
            new AircraftModel
            {
                Callsign = callsign,
                AircraftType = "B738",
                FiledAircraftType = "B738",
                FlightRules = "IFR",
                TransponderMode = "C",
                Altitude = 5000,
                GroundSpeed = 250,
                Position = new LatLon(lat, lon),
            }
        );
        canvas.MarkDirty();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
