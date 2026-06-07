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

    private sealed record ScoreBucketRow(string Name, string Summary, double PercentKept)
    {
        public static ScoreBucketRow FromDto(SoloTrainingScoreBucketDto dto)
        {
            int kept = Math.Max(0, dto.PointsAvailable - dto.PointsLost);
            double percent = dto.PointsAvailable > 0 ? kept * 100.0 / dto.PointsAvailable : 0.0;
            return new ScoreBucketRow(dto.Name, $"{kept}/{dto.PointsAvailable}", percent);
        }
    }

    private sealed record EventRow(
        string StartedText,
        string Severity,
        string Category,
        string Title,
        string Description,
        string RuleReference,
        string CallsignsText,
        string ExposureText,
        string RequiredText,
        string ActualText
    )
    {
        public static EventRow FromDto(SoloTrainingEventDto dto)
        {
            var started = TimeSpan.FromSeconds(dto.StartedAtSeconds);
            var exposure = TimeSpan.FromSeconds(dto.ExposureSeconds);
            string required = !string.IsNullOrWhiteSpace(dto.RequiredText)
                ? dto.RequiredText
                : FormatRequirement(dto.RequiredHorizontalNm, dto.RequiredVerticalFt);
            string actual = !string.IsNullOrWhiteSpace(dto.ActualText)
                ? dto.ActualText
                : FormatRequirement(dto.ActualHorizontalNm, dto.ActualVerticalFt);
            return new EventRow(
                started.ToString(@"h\:mm\:ss"),
                dto.Severity,
                dto.Category,
                dto.Title,
                dto.Description,
                dto.RuleReference,
                string.Join("/", dto.Callsigns),
                exposure.ToString(@"m\:ss"),
                required,
                actual
            );
        }

        private static string FormatRequirement(double? horizontalNm, double? verticalFt)
        {
            if (horizontalNm.HasValue && verticalFt.HasValue)
            {
                return $"{horizontalNm.Value:F1} NM / {verticalFt.Value:F0} ft";
            }

            if (horizontalNm.HasValue)
            {
                return $"{horizontalNm.Value:F1} NM";
            }

            if (verticalFt.HasValue)
            {
                return $"{verticalFt.Value:F0} ft";
            }

            return "";
        }
    }

    private sealed record AircraftDebriefRow(
        string Callsign,
        string AircraftType,
        string OperationText,
        string RouteText,
        string SpawnedText,
        string CompletedText,
        string StatusText,
        string FindingsText,
        string CoachingNote,
        double SpawnedAtSeconds
    )
    {
        public static AircraftDebriefRow FromDto(AircraftDebriefDto dto)
        {
            string route = (dto.FiledDeparture, dto.FiledDestination) switch
            {
                (null, null) => "—",
                ({ Length: > 0 } d, null) => $"{d} →",
                (null, { Length: > 0 } a) => $"→ {a}",
                ({ Length: > 0 } d, { Length: > 0 } a) => $"{d} → {a}",
                _ => "—",
            };

            string spawned = TimeSpan.FromSeconds(dto.SpawnedAtSeconds).ToString(@"h\:mm\:ss");
            string completed = dto.CompletedAtSeconds.HasValue ? TimeSpan.FromSeconds(dto.CompletedAtSeconds.Value).ToString(@"h\:mm\:ss") : "—";

            string status = dto.CompletionReason switch
            {
                "Landed" when !string.IsNullOrEmpty(dto.CompletionDetail) =>
                    $"Landed RW {RunwayIdentifier.ToDisplayDesignator(dto.CompletionDetail)}",
                "Landed" => "Landed",
                "HandedOff" when !string.IsNullOrEmpty(dto.CompletionDetail) => $"Handed off {dto.CompletionDetail}",
                "HandedOff" => "Handed off",
                "Dropped" => "Dropped",
                _ => "Active",
            };

            int total = dto.SeparationFindingCount + dto.RunwayWakeFindingCount + dto.AdvisoryFindingCount + dto.ApproachFindingCount;
            string findings = total == 0 ? "0" : $"{total} ({dto.SafetyFindingCount}S / {dto.WarningFindingCount}W / {dto.CoachFindingCount}C)";

            return new AircraftDebriefRow(
                dto.Callsign,
                dto.AircraftType,
                dto.Operation,
                route,
                spawned,
                completed,
                status,
                findings,
                dto.CoachingNote,
                dto.SpawnedAtSeconds
            );
        }
    }
}
