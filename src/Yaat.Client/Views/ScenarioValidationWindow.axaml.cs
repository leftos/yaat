using Avalonia.Controls;
using Avalonia.Input;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.Views;

public partial class ScenarioValidationWindow : Window
{
    private string _reportText = "";

    public ScenarioValidationWindow()
    {
        InitializeComponent();

        var closeBtn = this.FindControl<Button>("CloseButton");
        if (closeBtn is not null)
        {
            closeBtn.Click += (_, _) => Close();
        }

        var copyBtn = this.FindControl<Button>("CopyReportButton");
        if (copyBtn is not null)
        {
            copyBtn.Click += OnCopyReport;
        }
    }

    public void LoadReport(string artccId, List<ScenarioValidationResult> results)
    {
        var summaryText = this.FindControl<TextBlock>("SummaryText");
        var statsText = this.FindControl<TextBlock>("StatsText");
        var failuresHeader = this.FindControl<TextBlock>("FailuresHeader");
        var failuresGrid = this.FindControl<DataGrid>("FailuresGrid");

        int totalScenarios = results.Count;
        int totalPresets = results.Sum(r => r.TotalPresets);
        int totalFailures = results.Sum(r => r.Failures.Count);
        int knownTypos = results.Sum(r => r.Failures.Count(f => f.IsKnownTypo));
        int newFailures = totalFailures - knownTypos;

        if (summaryText is not null)
        {
            summaryText.Text = $"{artccId} Scenario Validation";
        }

        if (statsText is not null)
        {
            statsText.Text = $"{totalScenarios} scenarios, {totalPresets} presets, {totalFailures} failures ({knownTypos} known typos)";
        }

        var rows = results
            .SelectMany(r =>
                r.Failures.Select(f => new ValidationRow(r.ScenarioName, f.AircraftId, f.Command, f.IsKnownTypo ? "Known Typo" : "Parse Failed"))
            )
            .ToList();

        if (failuresHeader is not null)
        {
            failuresHeader.Text = rows.Count > 0 ? $"{rows.Count} Failures ({newFailures} new, {knownTypos} known typos)" : "No failures found";
        }

        if (failuresGrid is not null)
        {
            failuresGrid.ItemsSource = rows;
        }

        _reportText = BuildReportText(artccId, results, rows);
    }

    private static string BuildReportText(string artccId, List<ScenarioValidationResult> results, List<ValidationRow> rows)
    {
        var lines = new List<string>
        {
            $"{artccId} Scenario Validation Report",
            $"{results.Count} scenarios, {results.Sum(r => r.TotalPresets)} presets",
            "",
        };

        var newFailures = rows.Where(r => r.Status == "Parse Failed").ToList();
        if (newFailures.Count > 0)
        {
            lines.Add($"NEW FAILURES ({newFailures.Count}):");
            foreach (var f in newFailures)
            {
                lines.Add($"  [{f.ScenarioName}] {f.AircraftId}: \"{f.Command}\"");
            }
            lines.Add("");
        }

        var typos = rows.Where(r => r.Status == "Known Typo").ToList();
        if (typos.Count > 0)
        {
            lines.Add($"KNOWN TYPOS ({typos.Count}):");
            foreach (var f in typos)
            {
                lines.Add($"  [{f.ScenarioName}] {f.AircraftId}: \"{f.Command}\"");
            }
        }

        if (newFailures.Count == 0 && typos.Count == 0)
        {
            lines.Add("All preset commands parsed successfully.");
        }

        return string.Join("\n", lines);
    }

    private async void OnCopyReport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(_reportText);
        }
    }
}

public record ValidationRow(string ScenarioName, string AircraftId, string Command, string Status);
