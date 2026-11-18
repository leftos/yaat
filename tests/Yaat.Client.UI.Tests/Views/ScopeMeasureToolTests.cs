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
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The measuring tool on the Scope window: Ctrl+M (Cmd+M on macOS) arms it and says so in the Scope's response, a full store
/// is an error response; Esc clears the entry, then cancels the measurement, then disarms the Rings tool, and gives way to a
/// tear-off move; a typed <c>.rbl A B</c> places on the surface that last took the focus; a Scope line follows its aircraft,
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
                PlaceFixedLine(main, RblView.Radar);
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
            main.SetActiveMeasureView(RblView.Radar);

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
    public async Task Esc_OnTheActiveScope_LeavesARadarAnchor()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        main.Measure.Pick(AtPoint(37.7, -122.2), main.MeasureSurface(RblView.Radar, null));
        scope.ActivateMeasureSurface();

        Assert.False(scope.CancelMeasure());

        Assert.True(main.Measure.IsMeasuring);
        Assert.Equal(RblView.Radar, Assert.NotNull(main.Measure.Anchor).View);
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
    public async Task Esc_GivesWayToATearoffMove_BeforeTheMeasurement()
    {
        (MainViewModel main, _, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            main.RunScopeCommand(".scope tearoff range tl 10 10");
            Settle(window);
            window.DispatchKey(Key.M, ActionModifier);
            ScopeButtonControl copy = view
                .TearoffLayer.GetVisualDescendants()
                .OfType<ScopeButtonControl>()
                .Single(c => c.IsTearoff && c.IsEffectivelyVisible && (c.Key == new ScopeButtonKey(ScopeButtonId.Range, null)));
            Border strip = Assert.IsType<Border>(copy.Strip);
            Point grab = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)!.Value;
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
            main.SetActiveMeasureView(RblView.Radar);
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
    public async Task TypedRbl_AfterARadarFocus_PlacesOnTheRadar()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            AddAircraft(main, "UAL1", 5000);
            AddAircraft(main, "UAL2", 6000);
            FocusCanvas(window, view);
            main.SetActiveMeasureView(RblView.Radar);

            await TypeAndEnterAsync(window, scope, ".rbl UAL1 UAL2");

            Assert.Equal(RblView.Radar, Assert.Single(main.Measure.Lines).View);
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

            main.Radar.StartMeasureCommand.Execute(null);
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
