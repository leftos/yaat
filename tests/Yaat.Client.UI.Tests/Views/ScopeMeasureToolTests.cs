using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;
using Yaat.Sim.Data;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The measuring tool on the Scope window: Ctrl+M (Cmd+M on macOS) arms it and says so in the Scope's response, a full store
/// is an error response; Esc clears the entry, then cancels the measurement, then disarms the Rings tool, and gives way to a
/// view-card move; a typed <c>.rbl A B</c> places on the surface that last took the focus; a Scope line follows its aircraft,
/// goes with it, and outlives the Scope's track-state clear.
/// </summary>
public class ScopeMeasureToolTests
{
    private const string ArmedStatus = "Measure: click the first point or aircraft (Esc to cancel)";

    private static RawInputModifiers ActionModifier => PlatformHelper.IsMacOS ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScopeAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        await main.CrcAliasLoad;
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

    private static void Settle(Window window)
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    // A press on an empty corner of the canvas: it takes the focus and hits no track.
    private static void FocusCanvas(Window window, ScopeView view)
    {
        Point? inWindow = view.Canvas.TranslatePoint(new Point(5, view.Canvas.Bounds.Height - 5), window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task TypeAndEnterAsync(Window window, ScopeViewModel scope, string text)
    {
        window.KeyTextInput(text);
        Settle(window);
        window.DispatchKey(Key.Enter);
        await scope.LastEntryRun;
        Settle(window);
    }

    private static RblEndpoint AtPoint(double lat, double lon) => RblEndpoint.AtPoint(new LatLon(lat, lon), "");

    private static void PlaceFixedLine(MainViewModel main, RblView view) =>
        main.Measure.Place(
            AtPoint(37.6, -122.4),
            AtPoint(37.8, -122.0),
            new MeasureTarget(view, main.MeasureSurface(view, null).Lookup, RblUnits.NauticalMiles, null)
        );

    private static async Task<(MainViewModel Main, ScopeViewModel Scope)> StarsScopeAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ProfileKind = ScopeProfileKind.Stars;
        return (main, scope);
    }

    private static async Task RunEntryAsync(ScopeViewModel scope, string entry)
    {
        Assert.True(scope.TypeEntryText(entry));
        Assert.True(scope.ExecuteEntry());
        await scope.LastEntryRun;
    }

    private static void PlaceScopeLine(MainViewModel main, ScopeViewModel scope) =>
        main.Measure.Place(AtPoint(37.6, -122.4), AtPoint(37.8, -122.0), scope.MeasureTarget);

    private static ScopeEntryResponse Refusal(string message, string entry) => new(ScopeResponseKind.Error, message, entry, []);

    [AvaloniaFact]
    public async Task StarT_WithNothingSlewed_ClearsThisScopesLines_AndAnswersNothing()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        ScopeViewInstance other = main.OpenExtraScopeView();
        try
        {
            PlaceScopeLine(main, scope);
            PlaceScopeLine(main, scope);
            PlaceScopeLine(main, other.Vm);

            await RunEntryAsync(scope, "*T");

            Assert.Same(other.Vm, Assert.Single(main.Measure.Lines).Surface);
            Assert.Null(scope.EntryResponse);
        }
        finally
        {
            main.CloseExtraScopeView(other);
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StarTN_RemovesLineN()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            PlaceScopeLine(main, scope);
            PlaceScopeLine(main, scope);

            await RunEntryAsync(scope, "*T1");

            Assert.Equal(2, Assert.Single(main.Measure.Lines).Slot);
            Assert.Null(scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("*T0")]
    [InlineData("*T16")]
    [InlineData("*T3")]
    public async Task StarTN_OutsideOneToFifteenOrOnAnEmptySlot_IsRblId(string entry)
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            PlaceScopeLine(main, scope);
            PlaceScopeLine(main, scope);

            await RunEntryAsync(scope, entry);

            Assert.Equal(2, main.Measure.Lines.Count);
            Assert.Equal(Refusal("RBL ID", entry), scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StarT_ThenASlewOnATrack_AnchorsThere_AndTheNextSlewCompletesTheLine()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            Assert.True(scope.TypeEntryText("*T"));
            Assert.True(scope.FillsOnLeftClick("UAL1"));

            scope.FillTrack("UAL1");

            Assert.Equal("", scope.EntryText);
            Assert.Equal(new RblPendingAnchor(RblEndpoint.OnAircraft("UAL1"), RblView.Scope, scope), main.Measure.Anchor);

            scope.PickMeasurePoint(AtPoint(37.8, -122.0));

            RangeBearingLine line = Assert.Single(main.Measure.Lines);
            Assert.Equal("UAL1", line.A.Callsign);
            Assert.Same(scope, line.Surface);
            // CRC shows nothing when a slew completes the line, as when a typed entry does.
            Assert.Null(scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StarT_ThenASlewOnTheMap_AnchorsAtThePoint_AndATypedFixCompletesTheLine()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            Assert.True(scope.TypeEntryText("*T"));
            scope.FillPoint(new LatLon(37.6, -122.4), null);
            Assert.Equal(new LatLon(37.6, -122.4), main.Measure.Anchor?.Endpoint.FixedPosition);

            await RunEntryAsync(scope, "OAK");

            RangeBearingLine line = Assert.Single(main.Measure.Lines);
            Assert.Equal("OAK", line.B.Label);
            Assert.False(line.B.IsLatched);
            Assert.Null(main.Measure.Anchor);
            Assert.Null(scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StarTBeacon_AnchorsOnThatTrack_AndATypedAcidCompletesTheLine()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000).BeaconCode = 1234;
            AddAircraft(main, "UAL2", 6000).BeaconCode = 4521;

            await RunEntryAsync(scope, "*T1234");

            Assert.Equal(new RblPendingAnchor(RblEndpoint.OnAircraft("UAL1"), RblView.Scope, scope), main.Measure.Anchor);
            Assert.Null(scope.EntryResponse);

            await RunEntryAsync(scope, "UAL2");

            RangeBearingLine line = Assert.Single(main.Measure.Lines);
            Assert.Equal(("UAL1", "UAL2"), (line.A.Callsign, line.B.Callsign));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("*T7777")]
    [InlineData("*TZZ")]
    public async Task StarTBeaconOrAcid_MatchingNoTrack_IsNoFlight(string entry)
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000).BeaconCode = 1234;

            await RunEntryAsync(scope, entry);

            Assert.Null(main.Measure.Anchor);
            Assert.Equal(Refusal("NO FLIGHT", entry), scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StarTFix_WithEveryLineInUse_IsCapacity()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            for (int i = 0; i < RangeBearingLineStore.MaxLines; i++)
            {
                PlaceFixedLine(main, RblView.Ground);
            }

            await RunEntryAsync(scope, "*TOAK");

            Assert.Null(main.Measure.Anchor);
            Assert.Equal(Refusal("CAPACITY", "*TOAK"), scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("*T 1")]
    [InlineData("*T UAL1 UAL2")]
    [InlineData("*T OAK")]
    public async Task SpacedStarTForms_AreFormat(string entry)
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            AddAircraft(main, "UAL2", 6000);
            PlaceScopeLine(main, scope);

            await RunEntryAsync(scope, entry);

            Assert.Single(main.Measure.Lines);
            Assert.Null(main.Measure.Anchor);
            Assert.Equal(Refusal("FORMAT", entry), scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AGluedStarTEntry_WithASlew_IsFormat()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            PlaceScopeLine(main, scope);
            Assert.True(scope.TypeEntryText("*T1"));

            scope.FillTrack("UAL1");

            Assert.Single(main.Measure.Lines);
            Assert.Null(main.Measure.Anchor);
            Assert.Equal(Refusal("FORMAT", "*T1"), scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    // Two-character idents, the first the pinned navigation data knows serves.
    private static readonly string[] TwoCharacterIdents = ["OA", "SF", "LV", "HU", "PY", "LG", "MO", "AU"];

    private static void FillEveryFreeSlot(MainViewModel main)
    {
        for (int i = main.Measure.Lines.Count; i < RangeBearingLineStore.MaxLines; i++)
        {
            PlaceFixedLine(main, RblView.Ground);
        }
    }

    // A *T anchor at OAK, typed on the Scope; returns it.
    private static async Task<RblPendingAnchor> AnchorAtOakAsync(MainViewModel main, ScopeViewModel scope)
    {
        await RunEntryAsync(scope, "*TOAK");
        return Assert.IsType<RblPendingAnchor>(main.Measure.Anchor);
    }

    [AvaloniaFact]
    public async Task StarTN_OnAnotherScopesLine_IsRblId()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        ScopeViewInstance other = main.OpenExtraScopeView();
        try
        {
            PlaceScopeLine(main, other.Vm);

            await RunEntryAsync(scope, "*T1");

            Assert.Single(main.Measure.Lines);
            Assert.Equal(Refusal("RBL ID", "*T1"), scope.EntryResponse);
        }
        finally
        {
            main.CloseExtraScopeView(other);
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StarTBeacon_SquawkedByTwoTracks_IsDupTrk()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000).BeaconCode = 1200;
            AddAircraft(main, "UAL2", 6000).BeaconCode = 1200;

            await RunEntryAsync(scope, "*T1200");

            Assert.Null(main.Measure.Anchor);
            Assert.Equal(Refusal("DUP TRK", "*T1200"), scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StarTBeacon_MatchesATracksAssignedCode_WhileItSquawksAnother()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AircraftModel ual1 = AddAircraft(main, "UAL1", 5000);
            ual1.BeaconCode = 1200;
            ual1.AssignedBeaconCode = 4521;
            AddAircraft(main, "UAL2", 6000).BeaconCode = 1200;

            await RunEntryAsync(scope, "*T4521");

            Assert.Equal(new RblPendingAnchor(RblEndpoint.OnAircraft("UAL1"), RblView.Scope, scope), main.Measure.Anchor);
            Assert.Null(scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StarTBeaconZero_MatchesNoTrackWithoutACode_IsNoFlight()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AircraftModel ual1 = AddAircraft(main, "UAL1", 5000);
            ual1.BeaconCode = 0;
            ual1.AssignedBeaconCode = 0;

            await RunEntryAsync(scope, "*T0000");

            Assert.Null(main.Measure.Anchor);
            Assert.Equal(Refusal("NO FLIGHT", "*T0000"), scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    // Under three characters the token is an ACID only (CRC's IM:1672-1701), even when the navigation data has that ident.
    [AvaloniaFact]
    public async Task StarTTwoCharacters_IsAnAcidOnly_NotANavdataIdent()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            string ident = TwoCharacterIdents.First(c => NavigationDatabase.Instance.GetFixPosition(c) is not null);

            await RunEntryAsync(scope, "*T" + ident);

            Assert.Null(main.Measure.Anchor);
            Assert.Equal(Refusal("NO FLIGHT", "*T" + ident), scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StarTThreeCharacters_ThatAreAFixAndACallsign_AnchorsOnTheFix()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AddAircraft(main, "OAK", 5000);

            RblPendingAnchor anchor = await AnchorAtOakAsync(main, scope);

            Assert.False(anchor.Endpoint.IsLatched);
            Assert.Equal("OAK", anchor.Endpoint.Label);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData("OAK MOD")]
    [InlineData("*T")]
    public async Task TheEntryAfterAStarTAnchor_ThatIsNoCompletion_IsFormat_AndTheAnchorStays(string entry)
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            RblPendingAnchor anchor = await AnchorAtOakAsync(main, scope);

            await RunEntryAsync(scope, entry);

            Assert.Equal(anchor, main.Measure.Anchor);
            Assert.Empty(main.Measure.Lines);
            Assert.Equal(Refusal("FORMAT", entry), scope.EntryResponse);

            await RunEntryAsync(scope, "MOD");
            Assert.Single(main.Measure.Lines);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task EnterOnAnEmptyPreview_WhileAStarTAnchorIsPending_IsFormat_AndTheAnchorStays()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            RblPendingAnchor anchor = await AnchorAtOakAsync(main, scope);

            Assert.True(scope.ExecuteEntry());

            Assert.Equal(anchor, main.Measure.Anchor);
            Assert.Equal(Refusal("FORMAT", ""), scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ATypedCompletion_WithEveryLineInUse_IsCapacity_AndTheAnchorStaysPending()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            RblPendingAnchor anchor = await AnchorAtOakAsync(main, scope);
            FillEveryFreeSlot(main);

            await RunEntryAsync(scope, "MOD");

            Assert.Equal(anchor, main.Measure.Anchor);
            Assert.Equal(Refusal("CAPACITY", "MOD"), scope.EntryResponse);

            main.Measure.Remove(1);
            await RunEntryAsync(scope, "MOD");

            Assert.Null(main.Measure.Anchor);
            Assert.Equal(RangeBearingLineStore.MaxLines, main.Measure.Lines.Count);
            Assert.Null(scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AClickCompletion_WithEveryLineInUse_IsCapacity_AndTheAnchorStaysPending()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            RblPendingAnchor anchor = await AnchorAtOakAsync(main, scope);
            FillEveryFreeSlot(main);

            scope.PickMeasurePoint(AtPoint(37.8, -122.0));

            Assert.Equal(anchor, main.Measure.Anchor);
            Assert.Equal(Refusal("CAPACITY", ""), scope.EntryResponse);

            main.Measure.Remove(1);
            scope.PickMeasurePoint(AtPoint(37.8, -122.0));

            Assert.Null(main.Measure.Anchor);
            Assert.Equal(RangeBearingLineStore.MaxLines, main.Measure.Lines.Count);
            Assert.Null(scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    // The review's scenario: a *T line completed by a click, then a Ctrl+M anchor at the same track; the next entry is the
    // Scope's own again, not a completion of the old *T anchor.
    [AvaloniaFact]
    public async Task AfterAStarTLineIsCompletedByAClick_ACtrlMAnchorAtTheSameTrack_DoesNotCaptureTheNextEntry()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            main.SelectedAircraft = AddAircraft(main, "UAL2", 6000);
            List<string> sent = [];
            scope.YaatEntrySender = (text, _, _) =>
            {
                sent.Add(text);
                return Task.CompletedTask;
            };

            await RunEntryAsync(scope, "*TUAL1");
            scope.PickMeasurePoint(RblEndpoint.OnAircraft("UAL2"));
            scope.ArmMeasure();
            scope.PickMeasurePoint(RblEndpoint.OnAircraft("UAL1"));
            RblPendingAnchor? anchor = main.Measure.Anchor;

            await RunEntryAsync(scope, "+250");

            Assert.Contains(sent, text => text.Contains("250", StringComparison.Ordinal));
            Assert.Equal(anchor, main.Measure.Anchor);
            Assert.Single(main.Measure.Lines);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AClickWithTextInThePreview_WhileAStarTAnchorIsPending_IsFormat_AndTheAnchorStays()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            RblPendingAnchor anchor = await AnchorAtOakAsync(main, scope);
            Assert.True(scope.TypeEntryText("MOD"));
            Assert.True(scope.FillsOnLeftClick("UAL1"));

            scope.FillTrack("UAL1");

            Assert.Equal(anchor, main.Measure.Anchor);
            Assert.Empty(main.Measure.Lines);
            Assert.Equal("", scope.EntryText);
            Assert.Equal(Refusal("FORMAT", "MOD"), scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ADollarStarTEntry_IsFormat()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            PlaceScopeLine(main, scope);
            List<string> sent = [];
            scope.YaatEntrySender = (text, _, _) =>
            {
                sent.Add(text);
                return Task.CompletedTask;
            };

            await RunEntryAsync(scope, "$*T1");

            Assert.Empty(sent);
            Assert.Single(main.Measure.Lines);
            Assert.Equal(Refusal("FORMAT", "$*T1"), scope.EntryResponse);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AFunctionKey_WhileAStarTAnchorIsPending_ResetsTheAnchor()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            await AnchorAtOakAsync(main, scope);

            Assert.True(scope.StartStarsFunctionEntry(Key.F3));

            Assert.Null(main.Measure.Anchor);
            Assert.Equal("INIT ", scope.EntryText);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task OneEsc_WhileAStarTAnchorIsPending_ClearsTheText_TheResponse_AndTheAnchor()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            scope.ProfileKind = ScopeProfileKind.Stars;
            FocusCanvas(window, view);
            await TypeAndEnterAsync(window, scope, "*TOAK");
            await TypeAndEnterAsync(window, scope, "A");
            Assert.NotNull(main.Measure.Anchor);
            Assert.NotNull(scope.EntryResponse);
            window.KeyTextInput("MO");
            Settle(window);

            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.Equal("", scope.EntryText);
            Assert.Null(scope.EntryResponse);
            Assert.Null(main.Measure.Anchor);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task LeavingTheRoom_DropsAPendingStarTAnchor()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            await AnchorAtOakAsync(main, scope);

            main.ClearRoomState();

            Assert.Null(main.Measure.Anchor);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AProfileChange_DropsAPendingStarTAnchor()
    {
        (MainViewModel main, ScopeViewModel scope) = await StarsScopeAsync();
        try
        {
            await AnchorAtOakAsync(main, scope);

            scope.ProfileKind = ScopeProfileKind.Eram;

            Assert.Null(main.Measure.Anchor);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task CtrlM_InTheScopeWindow_ArmsTheTool_AndTheScopeResponseSaysSo()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            window.DispatchKey(Key.M, ActionModifier);

            Assert.True(main.Measure.IsMeasuring);
            Assert.True(view.Canvas.IsMeasuring);
            Assert.Equal(RblView.Scope, main.ActiveMeasureView);
            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Text, ArmedStatus, "", []), scope.EntryResponse);
            Assert.Equal(ArmedStatus, main.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlM_WithEveryMeasurementInUse_ReportsAnErrorResponse_AndDoesNotArm()
    {
        (MainViewModel main, ScopeViewModel scope, _, Window window) = await ShowScopeAsync();
        try
        {
            for (int i = 0; i < RangeBearingLineStore.MaxLines; i++)
            {
                PlaceFixedLine(main, RblView.Ground);
            }

            window.DispatchKey(Key.M, ActionModifier);

            Assert.False(main.Measure.IsMeasuring);
            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Error, RangeBearingViewState.FullStatus, "", []), scope.EntryResponse);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_ClearsTheEntry_ThenCancelsTheMeasurement_ThenDisarmsTheRings()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            scope.Pins = scope.Pins with { Rings = ScopeProfile.RingsJRing };
            scope.Press(ScopeToolbarButtons.Find(ScopeButtonId.Tools)!);
            FocusCanvas(window, view);
            window.DispatchKey(Key.M, ActionModifier);
            window.KeyTextInput("AB");
            Settle(window);

            window.DispatchKey(Key.Escape);
            Assert.Equal("", scope.EntryText);
            Assert.Null(scope.EntryResponse);
            Assert.True(main.Measure.IsMeasuring);
            Assert.True(scope.IsRingToolArmed);

            window.DispatchKey(Key.Escape);
            Assert.False(main.Measure.IsMeasuring);
            Assert.Equal("Measure cancelled", main.StatusText);
            Assert.Null(scope.EntryResponse);
            Assert.True(scope.IsRingToolArmed);

            window.DispatchKey(Key.Escape);
            Assert.False(scope.IsRingToolArmed);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_LeavesAMeasurementArmedOnAnotherSurface()
    {
        (MainViewModel main, ScopeViewModel scope, _, Window window) = await ShowScopeAsync();
        try
        {
            main.Measure.Arm();
            main.SetGroundMeasureActive();

            window.DispatchKey(Key.Escape);

            Assert.True(main.Measure.IsMeasuring);
            Assert.False(scope.CancelMeasure());
        }
        finally
        {
            window.Close();
        }
    }

    // Esc's measure step on a Scope, case by case (anchor holder x this Scope the active surface x tool armed): it cancels only
    // this Scope's own anchor, or the armed tool with no anchor while this Scope is the active surface; otherwise the key
    // passes on to the next Esc step and the measurement stays.

    [AvaloniaFact]
    public async Task Esc_OnTheActiveScope_CancelsItsOwnAnchor()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.PickMeasurePoint(AtPoint(37.7, -122.2));
        Assert.True(scope.IsActiveMeasureSurface);

        Assert.True(scope.CancelMeasure());

        Assert.Null(main.Measure.Anchor);
        Assert.False(main.Measure.IsMeasuring);
    }

    [AvaloniaFact]
    public async Task Esc_OnTheScopeThatPlacedTheAnchor_CancelsIt_WhateverIsActive()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        ScopeViewInstance other = main.OpenExtraScopeView();
        try
        {
            scope.PickMeasurePoint(AtPoint(37.7, -122.2));
            other.Vm.ActivateMeasureSurface();

            Assert.True(scope.CancelMeasure());

            Assert.Null(main.Measure.Anchor);
            Assert.False(main.Measure.IsMeasuring);
        }
        finally
        {
            main.CloseExtraScopeView(other);
        }
    }

    [AvaloniaFact]
    public async Task Esc_OnTheActiveScope_LeavesAnotherScopesAnchor_AndPassesOnToTheNextEscStep()
    {
        (MainViewModel main, ScopeViewModel scope, _, Window window) = await ShowScopeAsync();
        ScopeViewInstance other = main.OpenExtraScopeView();
        try
        {
            scope.Pins = scope.Pins with { Rings = ScopeProfile.RingsJRing };
            scope.Press(ScopeToolbarButtons.Find(ScopeButtonId.Tools)!);
            Assert.True(scope.IsRingToolArmed);
            other.Vm.PickMeasurePoint(AtPoint(37.7, -122.2));
            scope.ActivateMeasureSurface();

            window.DispatchKey(Key.Escape);

            Assert.True(main.Measure.IsMeasuring);
            Assert.Same(other.Vm, Assert.NotNull(main.Measure.Anchor).Surface);
            Assert.False(scope.IsRingToolArmed);
            Assert.False(scope.CancelMeasure());
            Assert.Same(other.Vm, Assert.NotNull(main.Measure.Anchor).Surface);
        }
        finally
        {
            main.CloseExtraScopeView(other);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_OnAnInactiveScope_LeavesAnotherScopesAnchor()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        ScopeViewInstance other = main.OpenExtraScopeView();
        try
        {
            other.Vm.PickMeasurePoint(AtPoint(37.7, -122.2));
            Assert.False(scope.IsActiveMeasureSurface);

            Assert.False(scope.CancelMeasure());

            Assert.True(main.Measure.IsMeasuring);
            Assert.Same(other.Vm, Assert.NotNull(main.Measure.Anchor).Surface);
            Assert.True(other.Vm.CancelMeasure());
            Assert.Null(main.Measure.Anchor);
        }
        finally
        {
            main.CloseExtraScopeView(other);
        }
    }

    [AvaloniaFact]
    public async Task Esc_OnTheActiveScope_LeavesAGroundAnchor()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        main.Measure.Pick(AtPoint(37.7, -122.2), main.MeasureSurface(RblView.Ground, null));
        scope.ActivateMeasureSurface();

        Assert.False(scope.CancelMeasure());

        Assert.True(main.Measure.IsMeasuring);
        Assert.Equal(RblView.Ground, Assert.NotNull(main.Measure.Anchor).View);
    }

    [AvaloniaFact]
    public async Task Esc_OnTheActiveScope_WithTheToolArmedAndNoAnchor_StillCancelsIt()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        main.Measure.Arm();
        scope.ActivateMeasureSurface();

        Assert.True(scope.CancelMeasure());

        Assert.False(main.Measure.IsMeasuring);
    }

    [AvaloniaFact]
    public async Task Esc_OnAnInactiveScope_WithTheToolArmedAndNoAnchor_LeavesIt()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        ScopeViewInstance other = main.OpenExtraScopeView();
        try
        {
            main.Measure.Arm();
            other.Vm.ActivateMeasureSurface();

            Assert.False(scope.CancelMeasure());

            Assert.True(main.Measure.IsMeasuring);
        }
        finally
        {
            main.CloseExtraScopeView(other);
        }
    }

    [AvaloniaFact]
    public async Task Esc_WithTheToolDisarmed_HasNothingToCancel_ActiveOrNot()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        ScopeViewInstance other = main.OpenExtraScopeView();
        try
        {
            scope.ActivateMeasureSurface();
            Assert.False(scope.CancelMeasure());

            other.Vm.ActivateMeasureSurface();
            Assert.False(scope.CancelMeasure());

            Assert.False(main.Measure.IsMeasuring);
            Assert.Null(main.Measure.Anchor);
        }
        finally
        {
            main.CloseExtraScopeView(other);
        }
    }

    [AvaloniaFact]
    public async Task Esc_GivesWayToAViewMove_BeforeTheMeasurement()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            window.DispatchKey(Key.M, ActionModifier);
            ScopeViewCard ssa = Assert.Single(view.TearoffLayer.GetVisualDescendants().OfType<ScopeViewCard>());
            Border title = ssa.FindControl<Border>("PickArea")!;
            Point grab = title.TranslatePoint(new Point(title.Bounds.Width / 2, title.Bounds.Height / 2), window)!.Value;
            window.MouseMove(grab);
            window.MouseDown(grab, MouseButton.Left);
            window.MouseUp(grab, MouseButton.Left);
            Settle(window);
            Assert.True(view.TearoffLayer.IsMoving);

            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.False(view.TearoffLayer.IsMoving);
            Assert.True(main.Measure.IsMeasuring);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TypedRbl_AfterACanvasFocus_PlacesOnTheScope()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            AddAircraft(main, "UAL2", 6000);
            main.SetGroundMeasureActive();
            FocusCanvas(window, view);
            Assert.Equal(RblView.Scope, main.ActiveMeasureView);

            await TypeAndEnterAsync(window, scope, ".rbl UAL1 UAL2");

            Assert.Equal(RblView.Scope, Assert.Single(main.Measure.Lines).View);
            Assert.Equal(1, Assert.Single(view.Canvas.RenderSnapshotForTesting().RangeBearingLines).Slot);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TypedRbl_AfterAGroundFocus_PlacesOnTheGround()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            AddAircraft(main, "UAL2", 6000);
            FocusCanvas(window, view);
            main.SetGroundMeasureActive();

            await TypeAndEnterAsync(window, scope, ".rbl UAL1 UAL2");

            Assert.Equal(RblView.Ground, Assert.Single(main.Measure.Lines).View);
            Assert.Empty(view.Canvas.RenderSnapshotForTesting().RangeBearingLines);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AScopeLine_FollowsItsAircraft_AndGoesWhenTheAircraftIsDeleted()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AircraftModel ual1 = AddAircraft(main, "UAL1", 5000);
            AircraftModel ual2 = AddAircraft(main, "UAL2", 6000);
            ual2.Position = new LatLon(37.9, -122.1);
            FocusCanvas(window, view);
            await TypeAndEnterAsync(window, scope, ".rbl UAL1 UAL2");

            ual1.Position = new LatLon(37.5, -122.3);
            Assert.Equal(new LatLon(37.5, -122.3), Assert.Single(view.Canvas.RenderSnapshotForTesting().RangeBearingLines).A);

            main.OnAircraftDeleted("UAL1");
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(main.Measure.Lines);
            Assert.Empty(view.Canvas.RenderSnapshotForTesting().RangeBearingLines);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ScopeLines_OutliveTheScopesUnloadAndRewindClear()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            main.Measure.PlaceSilently(AtPoint(37.6, -122.4), AtPoint(37.8, -122.0), scope.MeasureTarget);

            main.ClearScenarioState();
            main.ApplyScenarioRewind([]);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(RblView.Scope, Assert.Single(main.Measure.Lines).View);
            Assert.Single(view.Canvas.RenderSnapshotForTesting().RangeBearingLines);
        }
        finally
        {
            window.Close();
        }
    }

    // Esc case table, first Esc (entry empty, the measure prompt showing, armed on the Scope, Rings armed): the tool disarms
    // and the prompt goes in one press; the Rings stay armed for the next Esc.
    [AvaloniaFact]
    public async Task CtrlM_ThenOneEsc_DisarmsAndClearsThePrompt_AndTheNextEscDisarmsTheRings()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            scope.Pins = scope.Pins with { Rings = ScopeProfile.RingsJRing };
            scope.Press(ScopeToolbarButtons.Find(ScopeButtonId.Tools)!);
            FocusCanvas(window, view);
            window.DispatchKey(Key.M, ActionModifier);
            Assert.NotNull(scope.EntryResponse);

            window.DispatchKey(Key.Escape);
            Assert.False(main.Measure.IsMeasuring);
            Assert.Null(scope.EntryResponse);
            Assert.Equal("Measure cancelled", main.StatusText);
            Assert.True(scope.IsRingToolArmed);

            window.DispatchKey(Key.Escape);
            Assert.False(scope.IsRingToolArmed);
        }
        finally
        {
            window.Close();
        }
    }

    // Esc case table, first Esc (entry empty, the measure prompt showing, another Scope holding the first point): the prompt
    // goes, and the tool and the other Scope's anchor stay.
    [AvaloniaFact]
    public async Task CtrlM_ThenAnotherScopesAnchor_EscTakesThePromptOnly_AndTheAnchorStays()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        ScopeViewInstance other = main.OpenExtraScopeView();
        try
        {
            FocusCanvas(window, view);
            window.DispatchKey(Key.M, ActionModifier);
            other.Vm.PickMeasurePoint(AtPoint(37.7, -122.2));
            Assert.NotNull(scope.EntryResponse);

            window.DispatchKey(Key.Escape);

            Assert.Null(scope.EntryResponse);
            Assert.True(main.Measure.IsMeasuring);
            Assert.Same(other.Vm, Assert.NotNull(main.Measure.Anchor).Surface);
        }
        finally
        {
            main.CloseExtraScopeView(other);
            window.Close();
        }
    }

    // Esc case table, first Esc (entry empty, another response showing, armed on the Scope): the response goes, the tool stays.
    [AvaloniaFact]
    public async Task Esc_WithAnotherResponseShowing_ClearsItFirst_AndKeepsTheToolArmed()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            FocusCanvas(window, view);
            window.DispatchKey(Key.M, ActionModifier);
            scope.EntryResponse = new ScopeEntryResponse(ScopeResponseKind.Accept, "ACCEPT", ".X", []);

            window.DispatchKey(Key.Escape);

            Assert.Null(scope.EntryResponse);
            Assert.True(main.Measure.IsMeasuring);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheArmPrompt_Goes_WhenTheToolIsDisarmedElsewhere()
    {
        (MainViewModel main, ScopeViewModel scope, _, Window window) = await ShowScopeAsync();
        try
        {
            window.DispatchKey(Key.M, ActionModifier);
            Assert.NotNull(scope.EntryResponse);

            main.Measure.Cancel();

            Assert.Null(scope.EntryResponse);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnotherResponse_StaysWhenTheToolIsDisarmed()
    {
        (MainViewModel main, ScopeViewModel scope, _, Window window) = await ShowScopeAsync();
        try
        {
            window.DispatchKey(Key.M, ActionModifier);
            var accept = new ScopeEntryResponse(ScopeResponseKind.Accept, "ACCEPT", ".X", []);
            scope.EntryResponse = accept;

            main.Measure.Cancel();

            Assert.Same(accept, scope.EntryResponse);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnArmFromAnotherSurface_WritesNothingToTheScopeResponse_EvenWithTheScopeActive()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            FocusCanvas(window, view);
            Assert.Equal(RblView.Scope, main.ActiveMeasureView);

            main.Ground.StartMeasureCommand.Execute(null);
            Assert.Null(scope.EntryResponse);
            Assert.Equal(ArmedStatus, main.StatusText);

            main.Measure.Cancel();
            main.Measure.Arm();
            Assert.Null(scope.EntryResponse);

            window.DispatchKey(Key.M, ActionModifier);
            Assert.Equal(new ScopeEntryResponse(ScopeResponseKind.Text, ArmedStatus, "", []), scope.EntryResponse);
        }
        finally
        {
            window.Close();
        }
    }
}
