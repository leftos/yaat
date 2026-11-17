using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope.Prefs;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Which map surface the measuring tool's typed <c>.rbl A B</c> places on: the last surface that took the focus or a press,
/// the Radar before any, and the Radar again once the Scope closes. The public placement the app tools call stays on the Radar.
/// </summary>
public class MainViewModelActiveMeasureViewTests
{
    [AvaloniaFact]
    public async Task NothingFocusedYet_IsTheRadar()
    {
        MainViewModel main = await MainAsync();

        Assert.Equal(RblView.Radar, main.ActiveMeasureView);
    }

    [AvaloniaFact]
    public async Task TheScopeReportingFocus_MakesItTheScope()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ActivateMeasureSurface();

            Assert.Equal(RblView.Scope, main.ActiveMeasureView);
        }
    }

    [AvaloniaFact]
    public async Task ARadarFocus_AfterTheScope_MakesItTheRadar()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ActivateMeasureSurface();
            main.SetActiveMeasureView(RblView.Radar);

            Assert.Equal(RblView.Radar, main.ActiveMeasureView);
        }
    }

    [AvaloniaFact]
    public async Task AGroundFocus_MakesItTheGround()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.ActivateMeasureSurface();
            main.SetActiveMeasureView(RblView.Ground);

            Assert.Equal(RblView.Ground, main.ActiveMeasureView);
        }
    }

    [AvaloniaFact]
    public async Task TheScopeClosing_FallsBackToTheRadar_AndAReopenedScopeDoesNotTakeItBack()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        scope.ActivateMeasureSurface();

        main.OpenScope = null;
        scope.Dispose();

        Assert.Equal(RblView.Radar, main.ActiveMeasureView);

        using var reopened = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = reopened;

        Assert.Equal(RblView.Radar, main.ActiveMeasureView);
    }

    [AvaloniaFact]
    public async Task TypedRbl_FromTheCommandLine_PlacesOnTheActiveSurface()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            await main.CrcAliasLoad;
            AddAircraft(main, "UAL1", 5000);
            AddAircraft(main, "UAL2", 6000);
            scope.ActivateMeasureSurface();

            var sink = new StatusSink();
            await main.SendTextAsync(".rbl UAL1 UAL2", sink);
            Dispatcher.UIThread.RunJobs();

            RangeBearingLine line = Assert.Single(main.Measure.Lines);
            Assert.Equal(RblView.Scope, line.View);
            Assert.Null(sink.Error);
            // The reading resolves through the Scope's lookup ("Measurement <brg>/<dist>-1"), not the unresolved "placed" text.
            Assert.StartsWith("Measurement ", sink.Status, StringComparison.Ordinal);
            Assert.EndsWith("-1", sink.Status, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public async Task ThePublicPlacement_PlacesOnTheRadar_WhateverIsActive()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            AddAircraft(main, "UAL1", 5000);
            AddAircraft(main, "UAL2", 6000);
            scope.ActivateMeasureSurface();

            MeasurePlacement placed = main.PlaceMeasurementFromText("UAL1", "UAL2");

            Assert.Equal(1, placed.Slot);
            Assert.Equal(RblView.Radar, Assert.Single(main.Measure.Lines).View);
            Assert.Equal(RblView.Scope, main.ActiveMeasureView);
        }
    }

    private sealed class StatusSink : ICommandTextSink
    {
        public string Status { get; private set; } = "";

        public string? Error { get; private set; }

        public void ReportStatus(string status) => Status = status;

        public void ReportError(string message) => Error = message;

        public void ClearInput() { }

        public void AddHistory(string callsign, string command) { }

        public void AddWarning(string message) { }
    }
}
