using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Entries;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's STARS min-sep readout: <c>MIN</c> then two clicked tracks (the second click running it; a typed callsign is
/// FORMAT, as CRC's MIN takes no text) shows one pair's projected closest approach, recomputed as either aircraft moves;
/// <c>MIN</c> and Enter clears it, as do either track leaving, invalid data, a restart and a new <c>MIN</c>; CRC's error
/// categories answer the wrong gestures; ERAM has no MIN.
/// </summary>
public class ScopeMinSepTests
{
    private static async Task<(MainViewModel Main, ScopeViewModel Scope)> ScopeAsync(ScopeProfileKind profile)
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = profile;
        return (main, scope);
    }

    // Two airborne tracks 10 NM apart on reciprocal tracks: a converging pair.
    private static (AircraftModel A, AircraftModel B) AddConvergingPair(MainViewModel main, string a, string b)
    {
        AircraftModel west = AddAircraft(main, a, 5000);
        west.Position = new LatLon(37.7, -122.3);
        west.TrueTrack = 90;
        AircraftModel east = AddAircraft(main, b, 6000);
        east.Position = new LatLon(37.7, -122.3 + (10.0 / (60.0 * Math.Cos(37.7 * Math.PI / 180.0))));
        east.TrueTrack = 270;
        return (west, east);
    }

    private static async Task RunEntryAsync(ScopeViewModel scope, string entry)
    {
        Assert.True(scope.TypeEntryText(entry));
        Assert.True(scope.ExecuteEntry());
        await scope.LastEntryRun;
    }

    // MIN, then a click on track a and one on track b, which runs it.
    private static async Task PairByClicksAsync(ScopeViewModel scope, string a, string b)
    {
        Assert.True(scope.TypeEntryText("MIN"));
        scope.FillTrack(a);
        scope.FillTrackFromClick(b);
        await scope.LastEntryRun;
    }

    /// <summary>CRC's MIN takes no text: a typed callsign, alone or with a clicked one, is FORMAT and leaves the pair as it was.</summary>
    [AvaloniaTheory]
    [InlineData("MIN UAL1 DAL2", null)]
    [InlineData("MIN UAL1", null)]
    [InlineData("MIN UAL1", "DAL2")]
    public async Task TypedMinCallsigns_AnswerFormat(string typed, string? clicked)
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");
            Assert.True(scope.TypeEntryText(typed));
            if (clicked is null)
            {
                Assert.True(scope.ExecuteEntry());
            }
            else
            {
                scope.FillTrackFromClick(clicked);
            }

            await scope.LastEntryRun;

            Assert.Null(scope.MinSep);
            Assert.Equal(ScopeResponseKind.Error, scope.EntryResponse?.Kind);
            Assert.Equal(StarsErrorText.Format, scope.EntryResponse?.Message);
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>Backspace takes back a clicked track: the tracks clicked after it make the pair.</summary>
    [AvaloniaFact]
    public async Task ABackspacedClick_IsForgotten_AndTheNextTwoClicksMakeThePair()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");
            AddConvergingPair(main, "SWA3", "AAL4");
            Assert.True(scope.TypeEntryText("MIN"));
            scope.FillTrack("UAL1");
            for (int i = 0; i < 4; i++)
            {
                Assert.True(scope.BackspaceEntry());
            }

            scope.FillTrackFromClick("SWA3");
            scope.FillTrackFromClick("AAL4");
            await scope.LastEntryRun;

            Assert.Equal(("SWA3", "AAL4"), (scope.MinSep?.CallsignA, scope.MinSep?.CallsignB));
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>A click backspaced away leaves bare MIN, which clears the pair.</summary>
    [AvaloniaFact]
    public async Task MinWithItsClickBackspacedAway_ClearsThePair()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");
            await PairByClicksAsync(scope, "UAL1", "DAL2");
            Assert.NotNull(scope.MinSep);
            Assert.True(scope.TypeEntryText("MIN"));
            scope.FillTrack("UAL1");
            while (scope.EntryText.Length > "MIN".Length)
            {
                Assert.True(scope.BackspaceEntry());
            }

            Assert.True(scope.ExecuteEntry());
            await scope.LastEntryRun;

            Assert.Null(scope.MinSep);
            Assert.NotEqual(ScopeResponseKind.Error, scope.EntryResponse?.Kind);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task MinWithTwoClickedTracks_ShowsAConvergingPair()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");

            await PairByClicksAsync(scope, "UAL1", "DAL2");

            ScopeMinSep pair = Assert.IsType<ScopeMinSep>(scope.MinSep);
            Assert.Equal(("UAL1", "DAL2"), (pair.CallsignA, pair.CallsignB));
            Assert.IsType<MinSepConverging>(pair.Result);
            Assert.Matches(@"^\d+\.\d\d NM$", Assert.Single(MinSepRenderer.LabelLines(pair.Result)));
            Assert.NotEqual(ScopeResponseKind.Error, scope.EntryResponse?.Kind);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task MinThenTwoTrackClicks_RunsOnTheSecondClick()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AircraftModel a = AddOnScreen(main, view.Canvas, "UAL1", new Point(300, 200), 90);
            AircraftModel b = AddOnScreen(main, view.Canvas, "DAL2", new Point(500, 260), 270);
            Settle(window);
            FocusCanvas(window, view);
            window.KeyTextInput("MIN");
            Settle(window);

            ClickTrack(window, view, a);
            Assert.Null(scope.MinSep);
            Assert.Equal("MIN UAL1", scope.EntryText);

            ClickTrack(window, view, b);
            await scope.LastEntryRun;

            ScopeMinSep pair = Assert.IsType<ScopeMinSep>(scope.MinSep);
            Assert.Equal(("UAL1", "DAL2"), (pair.CallsignA, pair.CallsignB));
            Assert.Equal("", scope.EntryText);
            Assert.Same(scope.MinSep, view.Canvas.MinSep);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClickingTheFirstTrackAgain_AnswersBothParametersReferToTheSameTrack()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AircraftModel a = AddOnScreen(main, view.Canvas, "UAL1", new Point(300, 200), 90);
            Settle(window);
            FocusCanvas(window, view);
            window.KeyTextInput("MIN");
            Settle(window);

            ClickTrack(window, view, a);
            ClickTrack(window, view, a);
            await scope.LastEntryRun;

            Assert.Null(scope.MinSep);
            Assert.Equal(ScopeResponseKind.Error, scope.EntryResponse?.Kind);
            Assert.Equal(ScopeViewModel.MinSepSameTrackMessage, scope.EntryResponse?.Message);
            Assert.Equal(StarsErrorText.DuplicateTrack, ScopeViewModel.MinSepSameTrackMessage);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MinAndEnter_ClearsThePair()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");
            await PairByClicksAsync(scope, "UAL1", "DAL2");
            Assert.NotNull(scope.MinSep);

            await RunEntryAsync(scope, "MIN");

            Assert.Null(scope.MinSep);
            Assert.NotEqual(ScopeResponseKind.Error, scope.EntryResponse?.Kind);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task MinOneTrackAndEnter_AnswersMustClickTwoTracks()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");
            Assert.True(scope.TypeEntryText("MIN"));
            scope.FillTrack("UAL1");

            Assert.True(scope.ExecuteEntry());
            await scope.LastEntryRun;

            Assert.Null(scope.MinSep);
            Assert.Equal(ScopeResponseKind.Error, scope.EntryResponse?.Kind);
            Assert.Equal(StarsErrorText.Format, scope.EntryResponse?.Message);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ASecondClick_WhenTheFirstTrackIsNoLongerDisplayed_AnswersFirstTrackNotDisplayed()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            (AircraftModel a, _) = AddConvergingPair(main, "UAL1", "DAL2");
            Assert.True(scope.TypeEntryText("MIN"));
            scope.FillTrack("UAL1");
            a.IsOnGround = true;

            Assert.True(scope.FillsOnLeftClick("DAL2"));
            scope.FillTrackFromClick("DAL2");
            await scope.LastEntryRun;

            Assert.Null(scope.MinSep);
            Assert.Equal(StarsErrorText.IllegalTrack, scope.EntryResponse?.Message);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task EitherAircraftMoving_RecomputesThePair_AndInvalidDataClearsIt()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            (_, AircraftModel b) = AddConvergingPair(main, "UAL1", "DAL2");
            await PairByClicksAsync(scope, "UAL1", "DAL2");
            Assert.IsType<MinSepConverging>(scope.MinSep?.Result);

            b.TrueTrack = 90;
            MinSepDiverging diverging = Assert.IsType<MinSepDiverging>(scope.MinSep?.Result);
            Assert.Equal(["NO XING", MinSepRenderer.FormatNm(diverging.DistanceNm)], MinSepRenderer.LabelLines(diverging));

            b.GroundSpeed = 0.5;
            Assert.Null(scope.MinSep);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task DeletingATrack_ClearsThePair()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");
            await PairByClicksAsync(scope, "UAL1", "DAL2");

            main.OnAircraftDeleted("DAL2");
            Dispatcher.UIThread.RunJobs();

            Assert.Null(scope.MinSep);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ANewMin_ReplacesThePair()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");
            AddConvergingPair(main, "SWA3", "AAL4");
            await PairByClicksAsync(scope, "UAL1", "DAL2");

            await PairByClicksAsync(scope, "SWA3", "AAL4");

            Assert.Equal(("SWA3", "AAL4"), (scope.MinSep?.CallsignA, scope.MinSep?.CallsignB));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RecordingLoadOrRestart_ClearsThePair()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");
            await PairByClicksAsync(scope, "UAL1", "DAL2");

            scope.ClearTrackState();
            Assert.Null(scope.MinSep);

            await PairByClicksAsync(scope, "UAL1", "DAL2");
            main.ApplyScenarioRestart([], null);
            Assert.Null(scope.MinSep);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ASecondClick_AfterTheFirstTrackWasDeleted_AnswersFirstTrackNotDisplayed()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");
            Assert.True(scope.TypeEntryText("MIN"));
            scope.FillTrack("UAL1");
            main.OnAircraftDeleted("UAL1");
            Dispatcher.UIThread.RunJobs();

            Assert.True(scope.FillsOnLeftClick("DAL2"));
            scope.FillTrackFromClick("DAL2");
            await scope.LastEntryRun;

            Assert.Null(scope.MinSep);
            Assert.Equal(ScopeViewModel.MinSepFirstNotDisplayedMessage, scope.EntryResponse?.Message);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AMiddleClickOnTheFirstTrackAgain_AnswersBothParametersReferToTheSameTrack()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");
            Assert.True(scope.TypeEntryText("MIN"));
            scope.FillTrack("UAL1");

            scope.FillTrackAndRun("UAL1");
            await scope.LastEntryRun;

            Assert.Null(scope.MinSep);
            Assert.Equal(ScopeViewModel.MinSepSameTrackMessage, scope.EntryResponse?.Message);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AfterDispose_AnAircraftChange_LeavesNoPair()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        (_, AircraftModel b) = AddConvergingPair(main, "UAL1", "DAL2");
        await PairByClicksAsync(scope, "UAL1", "DAL2");
        Assert.NotNull(scope.MinSep);

        scope.Dispose();
        b.TrueTrack = 90;

        Assert.Null(scope.MinSep);
    }

    [AvaloniaFact]
    public async Task OnEram_MinIsNotTheMinSepTool()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Eram);
        try
        {
            AddConvergingPair(main, "UAL1", "DAL2");
            scope.EramEntrySender = _ => Task.FromResult(new ScopeEramEntryResultDto(false, ["REJECT"], null));

            scope.TypeEntryText("MIN UAL1 DAL2");
            scope.ExecuteEntry();
            await scope.LastEntryRun;

            Assert.Null(scope.MinSep);
        }
        finally
        {
            scope.Dispose();
        }
    }

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScopeAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeAsync(ScopeProfileKind.Stars);
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        Settle(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (main, scope, view, window);
    }

    private static AircraftModel AddOnScreen(MainViewModel main, ScopeCanvas canvas, string callsign, Point screen, double track)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screen.X, (float)screen.Y);
        AircraftModel ac = AddAircraft(main, callsign, 5000);
        ac.Position = new LatLon(lat, lon);
        ac.TrueTrack = track;
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static void Settle(Window window)
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static void FocusCanvas(Window window, ScopeView view)
    {
        Point? inWindow = view.Canvas.TranslatePoint(new Point(20, view.Canvas.Bounds.Height / 2), window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void ClickTrack(Window window, ScopeView view, AircraftModel ac)
    {
        (float x, float y) = view.Canvas.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        Point? inWindow = view.Canvas.TranslatePoint(new Point(x, y), window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Settle(window);
    }
}
