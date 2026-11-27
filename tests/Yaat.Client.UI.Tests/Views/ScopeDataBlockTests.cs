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
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's datablock gestures and its display state: a drag on a block moves it from where it is drawn, a Shift+click on a
/// block or symbol with no action hides it and shows it again, and the view model's verbs hide, show and reset what the menus
/// and automation call.
/// </summary>
public class ScopeDataBlockTests
{
    private const string Callsign = "UAL238";

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

    // ── Drag ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void ABlockDrag_MovesTheBlock_FromWhereItIsDrawn()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            ScopeTrack before = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            SKPoint seed = before.BlockOffset;
            Point grab = CentreOf(view.Canvas, ScopeFieldSlot.Callsign);

            Drag(window, view.Canvas, grab, new Point(grab.X + 40, grab.Y - 30));

            Assert.True(scope.DataBlockState.ManualOffsets.TryGetValue(Callsign, out SKPoint offset));
            Assert.Equal(seed.X + 40, offset.X, 0.5);
            Assert.Equal(seed.Y - 30, offset.Y, 0.5);
            ScopeTrack after = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            Assert.Equal(before.Block.Bounds.Left + 40, after.Block.Bounds.Left, 0.5);
            Assert.Equal(before.Block.Bounds.Top - 30, after.Block.Bounds.Top, 0.5);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void AClickOnABlock_ThatDoesNotMovePastTheThreshold_WritesNoOffset()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            Point grab = CentreOf(view.Canvas, ScopeFieldSlot.Callsign);

            Drag(window, view.Canvas, grab, new Point(grab.X + 2, grab.Y));

            Assert.Empty(scope.DataBlockState.ManualOffsets);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ABlockDrag_StartsWithTheBlockWhereTheStudentsLeaderDirectionDrawsIt()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            scope.Preferences.SetSyncStudentLeaderDirection(true);
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300), ac => ac.StudentLeaderDirection = 8);
            ScopeTrack before = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            Assert.NotEqual(DatablockText.DefaultOffset, before.BlockOffset);
            Point grab = CentreOf(view.Canvas, ScopeFieldSlot.Callsign);

            Drag(window, view.Canvas, grab, new Point(grab.X + 30, grab.Y + 30));

            Assert.True(scope.DataBlockState.ManualOffsets.TryGetValue(Callsign, out SKPoint offset));
            Assert.Equal(before.BlockOffset.X + 30, offset.X, 0.5);
            Assert.Equal(before.BlockOffset.Y + 30, offset.Y, 0.5);
        }
        finally
        {
            Close(window);
        }
    }

    // ERAM Field A's press toggles the instructor's dwell lock and still starts the drag: CRC drags the block from it.
    [AvaloniaFact]
    public void ABlockDrag_FromEramFieldA_MovesTheBlock_AndThePressStillTogglesTheLock()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope(ScopeTemplates.EramId);
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            SKPoint seed = Assert.Single(view.Canvas.CurrentFrame.Tracks).BlockOffset;
            Point grab = CentreOf(view.Canvas, ScopeFieldSlot.Callsign);

            Drag(window, view.Canvas, grab, new Point(grab.X + 40, grab.Y + 20));

            Assert.Contains(Callsign, scope.DataBlockState.DwellLockedCallsigns);
            Assert.True(scope.DataBlockState.ManualOffsets.TryGetValue(Callsign, out SKPoint offset));
            Assert.Equal(seed.X + 40, offset.X, 0.5);
            Assert.Equal(seed.Y + 20, offset.Y, 0.5);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ADragFromAnActionField_OpensItsMenu_AndMovesNoBlock()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope(ScopeTemplates.EramId);
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            var actions = new List<ScopeFieldAction>();
            view.Canvas.FieldClicked += (_, action, _, _) => actions.Add(action);
            Point grab = CentreOf(view.Canvas, ScopeFieldSlot.EramAltitude);

            Drag(window, view.Canvas, grab, new Point(grab.X + 40, grab.Y + 30));

            Assert.Equal([ScopeFieldAction.OpenEramAltitudeMenu], actions);
            Assert.Empty(scope.DataBlockState.ManualOffsets);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ADragFromTheSymbolOnly_DoesNotMoveTheBlock()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));

            Drag(window, view.Canvas, new Point(300, 300), new Point(360, 340));

            Assert.Empty(scope.DataBlockState.ManualOffsets);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void AnAltLeftDrag_Measures_AndMovesNoBlock()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            Point grab = CentreOf(view.Canvas, ScopeFieldSlot.Callsign);

            Drag(window, view.Canvas, grab, new Point(grab.X + 60, grab.Y + 60), RawInputModifiers.Alt);

            Assert.Empty(scope.DataBlockState.ManualOffsets);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ABlockDrag_EndsWhenTheLeftButtonIsUp_WithoutARelease()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            SKPoint seed = Assert.Single(view.Canvas.CurrentFrame.Tracks).BlockOffset;
            Point grab = CentreOf(view.Canvas, ScopeFieldSlot.Callsign);
            Point inWindow = InWindow(view.Canvas, window, grab);

            window.MouseDown(inWindow, MouseButton.Left);
            window.MouseMove(new Point(inWindow.X + 20, inWindow.Y + 20), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            // No release ever arrives (the pointer left over an overlay): the first move without the button ends the drag.
            window.MouseMove(new Point(inWindow.X + 25, inWindow.Y + 25));
            Dispatcher.UIThread.RunJobs();
            window.MouseMove(new Point(inWindow.X + 120, inWindow.Y + 120));
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(new Point(inWindow.X + 120, inWindow.Y + 120), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.True(scope.DataBlockState.ManualOffsets.TryGetValue(Callsign, out SKPoint offset));
            Assert.Equal(seed.X + 20, offset.X, 0.5);
            Assert.Equal(seed.Y + 20, offset.Y, 0.5);
        }
        finally
        {
            Close(window);
        }
    }

    // ── Shift+click hide ────────────────────────────────────────

    [AvaloniaFact]
    public void ShiftClickOnTheSymbol_HidesTheBlock_AndAgainShowsIt()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            var clicked = new List<string>();
            view.Canvas.AircraftLeftClicked += clicked.Add;

            ShiftClick(window, view.Canvas, new Point(300, 300));

            Assert.Contains(Callsign, scope.DataBlockState.HiddenCallsigns);
            Assert.Empty(clicked);

            ShiftClick(window, view.Canvas, new Point(300, 300));

            Assert.Empty(scope.DataBlockState.HiddenCallsigns);
            Assert.Empty(clicked);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ShiftClickOnAStarsCallsignLine_HidesIt_WithoutSelectingOrDragging()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            Point onCallsign = CentreOf(view.Canvas, ScopeFieldSlot.Callsign);
            Point inWindow = InWindow(view.Canvas, window, onCallsign);

            window.MouseDown(inWindow, MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(new Point(inWindow.X + 40, inWindow.Y + 40), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(inWindow.X + 40, inWindow.Y + 40), MouseButton.Left, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(Callsign, scope.DataBlockState.HiddenCallsigns);
            Assert.Null(main.SelectedAircraft);
            Assert.Empty(scope.DataBlockState.ManualOffsets);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ShiftClickOnABlockAreaWithNoAction_HidesIt()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            Assert.Equal(ScopeFieldAction.None, ActionOf(view.Canvas, ScopeFieldSlot.CwtType));

            ShiftClick(window, view.Canvas, CentreOf(view.Canvas, ScopeFieldSlot.CwtType));

            Assert.Contains(Callsign, scope.DataBlockState.HiddenCallsigns);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ShiftClickOnEramFieldA_TogglesTheDwellLock_AndDoesNotHide()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope(ScopeTemplates.EramId);
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));

            ShiftClick(window, view.Canvas, CentreOf(view.Canvas, ScopeFieldSlot.Callsign));

            Assert.DoesNotContain(Callsign, scope.DataBlockState.HiddenCallsigns);
            Assert.Contains(Callsign, scope.DataBlockState.DwellLockedCallsigns);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ShiftClickOnAnEramHsfIndicatorOrVciCell_LeavesItsToggle_AndDoesNotHide()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope(ScopeTemplates.EramId);
        try
        {
            main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "C44", Sector44, 3, NoStarsDisplay: true));
            // The track is not the viewer's, so the ERAM band would draw it as an LDB, which has no VCI cell: pin others Full.
            scope.PinOthersLevel(ScopeProfile.OthersFull);
            Dispatcher.UIThread.RunJobs();
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300), ac => ac.EramAssignedHeading = "H270");
            main.SelectedAircraft = null;
            Dispatcher.UIThread.RunJobs();
            SKRect cell = Assert.NotNull(Assert.Single(view.Canvas.CurrentFrame.Tracks).Block.VciCell);

            ShiftClick(window, view.Canvas, CentreOf(view.Canvas, ScopeFieldSlot.HsfIndicator));

            Assert.DoesNotContain(Callsign, scope.DataBlockState.HiddenCallsigns);
            Assert.Equal("QS UAL238", scope.EntryText);

            scope.ClearEntry();
            Dispatcher.UIThread.RunJobs();
            ShiftClick(window, view.Canvas, new Point(cell.Left - 4f, cell.Top - 2f));

            Assert.DoesNotContain(Callsign, scope.DataBlockState.HiddenCallsigns);
            Assert.Equal("QN // UAL238", scope.EntryText);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ActionShiftClickOnTheSymbol_DoesNotHide()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            RawInputModifiers action = PlatformHelper.IsMacOS ? RawInputModifiers.Meta : RawInputModifiers.Control;
            Point inWindow = InWindow(view.Canvas, window, new Point(300, 300));

            window.MouseDown(inWindow, MouseButton.Left, RawInputModifiers.Shift | action);
            window.MouseUp(inWindow, MouseButton.Left, RawInputModifiers.Shift | action);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(scope.DataBlockState.HiddenCallsigns);
        }
        finally
        {
            Close(window);
        }
    }

    // ── The Rings tool ──────────────────────────────────────────

    [AvaloniaFact]
    public void RingsArmed_AClickOnABlock_PlacesTheRing_AndWritesNoOffset()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            ArmRings(scope);
            Point onCallsign = CentreOf(view.Canvas, ScopeFieldSlot.Callsign);

            Click(window, view.Canvas, onCallsign);

            Assert.True(scope.LocalRings.ContainsKey(Callsign));
            Assert.Empty(scope.DataBlockState.ManualOffsets);
            Assert.Empty(scope.DataBlockState.HiddenCallsigns);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void RingsArmed_ADragOnABlock_PlacesTheRing_AndMovesNoBlock()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            ArmRings(scope);
            Point grab = CentreOf(view.Canvas, ScopeFieldSlot.Callsign);

            Drag(window, view.Canvas, grab, new Point(grab.X + 40, grab.Y - 30));

            Assert.True(scope.LocalRings.ContainsKey(Callsign));
            Assert.Empty(scope.DataBlockState.ManualOffsets);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void RingsArmed_AShiftClickOnTheSymbol_PlacesTheRing_AndHidesNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            ArmRings(scope);

            ShiftClick(window, view.Canvas, new Point(300, 300));

            Assert.True(scope.LocalRings.ContainsKey(Callsign));
            Assert.Empty(scope.DataBlockState.HiddenCallsigns);
        }
        finally
        {
            Close(window);
        }
    }

    // ── The frame a hidden block leaves ─────────────────────────

    [AvaloniaFact]
    public void AHiddenBlock_LaysOutNothing_InTheNextFrame()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            // A frame built and kept before the hide, so the hide alone has to refresh it: no render tick and no new traffic.
            Assert.NotEmpty(Assert.Single(view.Canvas.CurrentFrame.Tracks).Block.Fields);

            scope.HideDataBlock(Callsign);

            ScopeTrack hidden = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            Assert.Empty(hidden.Block.Fields);
            Assert.False(hidden.ShowsLeader);
            Assert.Same(ac, view.Canvas.CurrentFrame.TargetAt(300, 300));
            Assert.Null(view.Canvas.CurrentFrame.BlockAt(hidden.Symbol.X + 40f, hidden.Symbol.Y - 30f));

            main.SelectedAircraft = ac;
            RenderFrame(view.Canvas);

            ScopeTrack selected = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            Assert.NotEmpty(selected.Block.Fields);
        }
        finally
        {
            Close(window);
        }
    }

    // ── View-model verbs ────────────────────────────────────────

    [AvaloniaFact]
    public void HideShowAndShowAll_MoveTheHiddenSet()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            AddAircraft(main, view.Canvas, "SWA12", new Point(500, 450));

            Assert.False(scope.AnyDataBlockHidden);
            scope.HideDataBlock(Callsign);
            Assert.True(scope.IsDataBlockHidden(Callsign));
            scope.HideDataBlock("SWA12");
            Assert.True(scope.AnyDataBlockHidden);

            scope.ShowDataBlock(Callsign);

            Assert.False(scope.IsDataBlockHidden(Callsign));
            Assert.True(scope.AnyDataBlockHidden);

            scope.ToggleDataBlockHidden("SWA12");

            Assert.False(scope.AnyDataBlockHidden);

            scope.ToggleDataBlockHidden(Callsign);
            scope.ShowAllDataBlocks();

            Assert.False(scope.AnyDataBlockHidden);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ResetDataBlockPosition_RemovesTheOffset_Once_AndTheBlockReturnsToItsDefaultPlacement()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            scope.SetDataBlockOffset(Callsign, new SKPoint(-60, 12));
            RenderFrame(view.Canvas);
            Assert.True(scope.HasManualDataBlockOffset(Callsign));
            Assert.Equal(new SKPoint(-60, 12), Assert.Single(view.Canvas.CurrentFrame.Tracks).BlockOffset);

            Assert.True(scope.ResetDataBlockPosition(Callsign));
            RenderFrame(view.Canvas);

            Assert.False(scope.HasManualDataBlockOffset(Callsign));
            Assert.Equal(DatablockText.DefaultOffset, Assert.Single(view.Canvas.CurrentFrame.Tracks).BlockOffset);
            Assert.False(scope.ResetDataBlockPosition(Callsign));
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ClearTrackState_ClearsTheHiddenSet()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, Callsign, new Point(300, 300));
            scope.HideDataBlock(Callsign);
            scope.SetDataBlockOffset(Callsign, new SKPoint(10, 20));

            scope.ClearTrackState();

            Assert.Empty(scope.DataBlockState.HiddenCallsigns);
            Assert.Empty(scope.DataBlockState.ManualOffsets);
        }
        finally
        {
            Close(window);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static (MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window) ShowScope(string templateId = ScopeTemplates.StarsId)
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        Helpers.ScopeTestFixture.PickLook(main, templateId);
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

        RenderFrame(view.Canvas);
        return (main, scope, view, window);
    }

    private static AircraftModel AddAircraft(
        MainViewModel main,
        ScopeCanvas canvas,
        string callsign,
        Point symbol,
        Action<AircraftModel>? setUp = null
    )
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)symbol.X, (float)symbol.Y);
        var ac = new AircraftModel
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
        setUp?.Invoke(ac);
        main.Aircraft.Add(ac);
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static Point CentreOf(ScopeCanvas canvas, ScopeFieldSlot slot)
    {
        FieldRect field = Assert.Single(canvas.CurrentFrame.Tracks).Block.Fields.First(f => f.Slot == slot);
        return new Point(field.Rect.MidX, field.Rect.MidY);
    }

    private static ScopeFieldAction ActionOf(ScopeCanvas canvas, ScopeFieldSlot slot) =>
        Assert.Single(Assert.Single(canvas.CurrentFrame.Tracks).Block.Fields, f => f.Slot == slot).Action;

    private static void Drag(
        Window window,
        ScopeCanvas canvas,
        Point fromCanvas,
        Point toCanvas,
        RawInputModifiers modifiers = RawInputModifiers.None
    )
    {
        Point from = InWindow(canvas, window, fromCanvas);
        Point to = InWindow(canvas, window, toCanvas);
        window.MouseDown(from, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(to, modifiers | RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(to, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point inWindow = InWindow(canvas, window, canvasPoint);
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // Arms the Rings tool as its toolbar button does.
    private static void ArmRings(ScopeViewModel scope)
    {
        scope.Pins = scope.Pins with { Rings = ScopeProfile.RingsJRing };
        scope.Press(ScopeToolbarButtons.Find(ScopeButtonId.Tools)!);
        Assert.Equal(ScopeRingTool.JRing, scope.ArmedRingTool);
    }

    private static void ShiftClick(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point inWindow = InWindow(canvas, window, canvasPoint);
        window.MouseDown(inWindow, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(inWindow, MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point InWindow(Control canvas, Window window, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        return inWindow.Value;
    }

    // Headless frames are drawn only on a render-timer tick.
    private static void RenderFrame(ScopeCanvas canvas)
    {
        canvas.MarkDirty();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Close(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
