using Avalonia.Controls;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public partial class ApproachReportWindow : Window
{
    public ApproachReportWindow()
    {
        InitializeComponent();

        var closeBtn = this.FindControl<Button>("CloseButton");
        if (closeBtn is not null)
        {
            closeBtn.Click += (_, _) => Close();
        }
    }

    public void LoadReport(ApproachReportDto report)
    {
        var gradeText = this.FindControl<TextBlock>("OverallGradeText");
        var elapsedText = this.FindControl<TextBlock>("ElapsedTimeText");
        var statusText = this.FindControl<TextBlock>("StatusText");
        var scoresGrid = this.FindControl<DataGrid>("ApproachScoresGrid");
        var statsGrid = this.FindControl<DataGrid>("RunwayStatsGrid");

        if (gradeText is not null)
        {
            gradeText.Text = $"Overall: {report.OverallGrade}";
        }

        if (elapsedText is not null)
        {
            var elapsed = TimeSpan.FromSeconds(report.ScenarioElapsedSeconds);
            elapsedText.Text = $"Elapsed: {elapsed:h\\:mm\\:ss}";
        }

        if (statusText is not null)
        {
            int total = report.Approaches.Count;
            int landed = report.Approaches.Count(a => a.LandedAtSeconds.HasValue);
            statusText.Text = $"{total} approaches, {landed} landed";
        }

        if (scoresGrid is not null)
        {
            scoresGrid.ItemsSource = report.Approaches;
        }

        if (statsGrid is not null)
        {
            statsGrid.ItemsSource = report.RunwayStats;
        }
    }
}
