using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// YAAT-712: Scenario › Session Report failed with "Expected control 'ScoreBucketsItems' to be 'Avalonia.Controls.DataGrid'
/// but it was 'Avalonia.Controls.ItemsControl'." The window must open and fill every list, the non-grid ones included.
/// </summary>
public class Issue712SessionReportWindowTests
{
    private static SessionReportDto MinimalReport() =>
        new(
            SoloTrainingMode: false,
            ScenarioElapsedSeconds: 3317,
            Score: 100,
            Grade: "A",
            ScoreBuckets: [new SoloTrainingScoreBucketDto("Separation", 40, 0)],
            ActiveEvents: [],
            Timeline: [],
            CoachingNotes: ["No findings yet."],
            ApproachReport: new ApproachReportDto([], [], 3317, "N/A"),
            AircraftDebriefs: []
        );

    [AvaloniaFact]
    public async Task StartAsync_FillsScoreBucketsAndCoachingNotes()
    {
        var window = new SessionReportWindow(() => Task.FromResult<SessionReportDto?>(MinimalReport()), showOnTimeline: null);
        try
        {
            await window.StartAsync();

            Assert.Single(window.ScoreBucketsItems.Items);
            Assert.Equal(["No findings yet."], window.CoachingNotesList.Items.Cast<string>());
            Assert.Equal("A", window.GradeText.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
