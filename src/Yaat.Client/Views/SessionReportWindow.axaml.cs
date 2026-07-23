using System.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views;

public partial class SessionReportWindow : Window
{
    private readonly Func<Task<SessionReportDto?>> _reportLoader;
    private readonly Func<string, double, Task>? _showOnTimeline;
    private readonly DispatcherTimer _refreshTimer;
    private bool _refreshing;

    public SessionReportWindow()
        : this(() => Task.FromResult<SessionReportDto?>(null), showOnTimeline: null) { }

    public SessionReportWindow(Func<Task<SessionReportDto?>> reportLoader, Func<string, double, Task>? showOnTimeline)
    {
        _reportLoader = reportLoader;
        _showOnTimeline = showOnTimeline;
        InitializeComponent();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        var closeBtn = this.FindControl<Button>("CloseButton");
        if (closeBtn is not null)
        {
            closeBtn.Click += (_, _) => Close();
        }

        var refreshBtn = this.FindControl<Button>("RefreshButton");
        if (refreshBtn is not null)
        {
            refreshBtn.Click += async (_, _) => await RefreshAsync();
        }

        var aircraftGrid = this.FindControl<DataGrid>("AircraftDebriefsGrid");
        var showOnTimelineBtn = this.FindControl<Button>("ShowOnTimelineButton");
        if (aircraftGrid is not null && showOnTimelineBtn is not null)
        {
            aircraftGrid.SelectionChanged += (_, _) =>
            {
                showOnTimelineBtn.IsEnabled = _showOnTimeline is not null && aircraftGrid.SelectedItem is AircraftDebriefRow;
            };
            showOnTimelineBtn.Click += async (_, _) =>
            {
                if (_showOnTimeline is not null && aircraftGrid.SelectedItem is AircraftDebriefRow row)
                {
                    await _showOnTimeline(row.Callsign, row.SpawnedAtSeconds);
                }
            };
        }

        Closed += (_, _) => _refreshTimer.Stop();
    }

    public async Task StartAsync()
    {
        await RefreshAsync();
        _refreshTimer.Start();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var report = await _reportLoader();
            if (report is not null)
            {
                LoadReport(report);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void LoadReport(SessionReportDto report)
    {
        SetText("GradeText", report.Grade);
        SetText("ScoreText", $"{report.Score}/100");

        var elapsed = TimeSpan.FromSeconds(report.ScenarioElapsedSeconds);
        SetText("ElapsedText", $"Elapsed {elapsed:h\\:mm\\:ss}");
        SetText("UpdatedText", $"Updated {DateTime.Now:T}");
        SetText("ModeText", report.SoloTrainingMode ? "Solo training scoring active" : "Solo training scoring is not active for this session");

        int activeSafety = report.ActiveEvents.Count(e => e.Severity.Equals("Safety", StringComparison.OrdinalIgnoreCase));
        int activeWarning = report.ActiveEvents.Count(e => e.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
        int approaches = report.ApproachReport.Approaches.Count;
        int landed = report.ApproachReport.Approaches.Count(a => a.LandedAtSeconds.HasValue);
        SetText("SummaryText", $"{activeSafety} safety issue(s), {activeWarning} warning(s), {approaches} approach(es), {landed} landing(s).");

        SetItems("ScoreBucketsItems", report.ScoreBuckets.Select(ScoreBucketRow.FromDto).ToList());
        SetItems("CoachingNotesList", report.CoachingNotes);
        SetItems("ActiveEventsGrid", report.ActiveEvents.Select(EventRow.FromDto).ToList());
        SetItems("TimelineGrid", report.Timeline.Select(EventRow.FromDto).ToList());
        SetItems("AircraftDebriefsGrid", report.AircraftDebriefs.Select(AircraftDebriefRow.FromDto).ToList());
        SetItems("ApproachScoresGrid", report.ApproachReport.Approaches);
        SetItems("RunwayStatsGrid", report.ApproachReport.RunwayStats);

        var hint = this.FindControl<TextBlock>("AircraftSelectionHint");
        if (hint is not null)
        {
            hint.Text =
                report.AircraftDebriefs.Count == 0
                    ? "No aircraft yet — debrief rows appear as aircraft enter the world."
                    : $"{report.AircraftDebriefs.Count} aircraft tracked this session.";
        }
    }

    private void SetText(string controlName, string value)
    {
        var text = this.FindControl<TextBlock>(controlName);
        if (text is not null)
        {
            text.Text = value;
        }
    }

    private void SetItems(string controlName, IEnumerable items)
    {
        if (this.FindControl<DataGrid>(controlName) is { } grid)
        {
            grid.ItemsSource = items;
            return;
        }

        if (this.FindControl<ItemsControl>(controlName) is { } control)
        {
            control.ItemsSource = items;
        }
    }
}
