using Avalonia.Controls;
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
        var procHeader = this.FindControl<TextBlock>("ProcedureIssuesHeader");
        var procGrid = this.FindControl<DataGrid>("ProcedureIssuesGrid");

        int totalScenarios = results.Count;
        int totalPresets = results.Sum(r => r.TotalPresets);
        int totalFailures = results.Sum(r => r.Failures.Count);
        int totalProcIssues = results.Sum(r => r.ProcedureIssues.Count);

        if (summaryText is not null)
        {
            summaryText.Text = $"{artccId} Scenario Validation";
        }

        if (statsText is not null)
        {
            statsText.Text =
                $"{totalScenarios} scenarios, {totalPresets} presets, {totalFailures} failure{(totalFailures != 1 ? "s" : "")}, {totalProcIssues} procedure issue{(totalProcIssues != 1 ? "s" : "")}";
        }

        var rows = results.SelectMany(r => r.Failures.Select(f => new ValidationRow(r.ScenarioName, f.AircraftId, f.Command))).ToList();

        if (failuresHeader is not null)
        {
            failuresHeader.Text =
                rows.Count > 0
                    ? $"{rows.Count} Parse Failure{(rows.Count != 1 ? "s" : "")} across {results.Count(r => r.Failures.Count > 0)} scenario{(results.Count(r => r.Failures.Count > 0) != 1 ? "s" : "")}"
                    : "No parse failures";
        }

        if (failuresGrid is not null)
        {
            failuresGrid.ItemsSource = rows;
        }

        var procRows = results
            .SelectMany(r =>
                r.ProcedureIssues.Select(i => new ProcedureIssueRow(
                    r.ScenarioName,
                    i.AircraftId,
                    i.ProcedureId,
                    i.Kind == ProcedureIssueKind.VersionChanged ? $"Version changed → {i.ResolvedId}" : "Not found in NavData"
                ))
            )
            .ToList();

        if (procHeader is not null)
        {
            procHeader.Text =
                procRows.Count > 0
                    ? $"{procRows.Count} Procedure Issue{(procRows.Count != 1 ? "s" : "")} across {results.Count(r => r.ProcedureIssues.Count > 0)} scenario{(results.Count(r => r.ProcedureIssues.Count > 0) != 1 ? "s" : "")}"
                    : "No procedure issues";
        }

        if (procGrid is not null)
        {
            procGrid.ItemsSource = procRows;
        }

        _reportText = BuildReportText(artccId, results);
    }

    private static string BuildReportText(string artccId, List<ScenarioValidationResult> results)
    {
        int totalPresets = results.Sum(r => r.TotalPresets);
        int totalFailures = results.Sum(r => r.Failures.Count);
        int totalProcIssues = results.Sum(r => r.ProcedureIssues.Count);

        var lines = new List<string>
        {
            $"{artccId} Scenario Validation Report",
            $"{results.Count} scenarios, {totalPresets} presets, {totalFailures} failure{(totalFailures != 1 ? "s" : "")}, {totalProcIssues} procedure issue{(totalProcIssues != 1 ? "s" : "")}",
            "",
        };

        var failedScenarios = results.Where(r => r.Failures.Count > 0).ToList();
        if (failedScenarios.Count > 0)
        {
            lines.Add("Parse Failures:");
            foreach (var scenario in failedScenarios)
            {
                lines.Add($"  {scenario.ScenarioName}");
                var byAircraft = scenario.Failures.GroupBy(f => f.AircraftId);
                foreach (var group in byAircraft)
                {
                    lines.Add($"    {group.Key}:");
                    foreach (var f in group)
                    {
                        lines.Add($"      \"{f.Command}\"");
                    }
                }
                lines.Add("");
            }
        }

        var procScenarios = results.Where(r => r.ProcedureIssues.Count > 0).ToList();
        if (procScenarios.Count > 0)
        {
            lines.Add("Procedure Issues:");
            foreach (var scenario in procScenarios)
            {
                lines.Add($"  {scenario.ScenarioName}");
                foreach (var issue in scenario.ProcedureIssues)
                {
                    string detail =
                        issue.Kind == ProcedureIssueKind.VersionChanged
                            ? $"    {issue.AircraftId}: {issue.ProcedureId} → {issue.ResolvedId}"
                            : $"    {issue.AircraftId}: {issue.ProcedureId} not found";
                    lines.Add(detail);
                }
                lines.Add("");
            }
        }

        if (failedScenarios.Count == 0 && procScenarios.Count == 0)
        {
            lines.Add("All preset commands parsed successfully. No procedure issues found.");
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

public record ValidationRow(string ScenarioName, string AircraftId, string Command);

public record ProcedureIssueRow(string ScenarioName, string AircraftId, string ProcedureId, string IssueDescription);
