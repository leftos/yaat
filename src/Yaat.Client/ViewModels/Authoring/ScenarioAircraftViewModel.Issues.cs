using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The Aircraft tab's live-validation findings: a marker on each row with an error or a warning, and beside each checked field of
/// the selected aircraft its findings. These are cross-field findings pushed by <see cref="ScenarioValidationViewModel"/>, kept
/// apart from the text-parse <c>*Error</c> properties a field's own commit sets.
/// </summary>
public sealed partial class ScenarioAircraftViewModel
{
    private IReadOnlyList<ScenarioEditIssue> _issues = [];

    [ObservableProperty]
    private ScenarioFieldIssue? _callsignIssue;

    [ObservableProperty]
    private ScenarioFieldIssue? _aircraftTypeIssue;

    [ObservableProperty]
    private ScenarioFieldIssue? _airportIssue;

    [ObservableProperty]
    private ScenarioFieldIssue? _startKindIssue;

    [ObservableProperty]
    private ScenarioFieldIssue? _parkingIssue;

    [ObservableProperty]
    private ScenarioFieldIssue? _runwayIssue;

    [ObservableProperty]
    private ScenarioFieldIssue? _fixIssue;

    [ObservableProperty]
    private ScenarioFieldIssue? _coordinatesIssue;

    [ObservableProperty]
    private ScenarioFieldIssue? _navigationPathIssue;

    [ObservableProperty]
    private ScenarioFieldIssue? _autoTrackPositionIssue;

    /// <summary>Shows the latest findings on the rows and beside the selected aircraft's fields.</summary>
    /// <param name="issues">Every finding of the latest live-validation run.</param>
    public void ApplyIssues(IReadOnlyList<ScenarioEditIssue> issues)
    {
        _issues = issues ?? throw new ArgumentNullException(nameof(issues));
        ShowIssues();
    }

    // The rows' markers and the selected aircraft's field issues from the latest findings.
    private void ShowIssues()
    {
        foreach (ScenarioAircraftRowViewModel row in Rows)
        {
            row.Issue = ScenarioFieldIssue.Of(_issues.Where(i => (i.AircraftId == row.Id) && (i.Severity != ScenarioEditIssueSeverity.NotChecked)));
        }

        ScenarioEditIssue[] mine = (Selected is { } selected) ? [.. _issues.Where(i => i.AircraftId == selected.Id)] : [];
        CallsignIssue = FieldIssue(mine, ScenarioEditFields.Callsign);
        AircraftTypeIssue = FieldIssue(mine, ScenarioEditFields.AircraftType);
        AirportIssue = FieldIssue(mine, ScenarioEditFields.AirportId);
        StartKindIssue = FieldIssue(mine, ScenarioEditFields.StartKind);
        ParkingIssue = FieldIssue(mine, ScenarioEditFields.Parking);
        RunwayIssue = FieldIssue(mine, ScenarioEditFields.Runway);
        FixIssue = FieldIssue(mine, ScenarioEditFields.Fix);
        CoordinatesIssue = FieldIssue(mine, ScenarioEditFields.Coordinates);
        NavigationPathIssue = FieldIssue(mine, ScenarioEditFields.NavigationPath);
        AutoTrackPositionIssue = FieldIssue(mine, ScenarioEditFields.AutoTrackPosition);
        foreach (ScenarioPresetRowViewModel preset in Presets)
        {
            preset.CommandIssue = ScenarioFieldIssue.Of(mine.Where(i => (i.Field == ScenarioEditFields.PresetCommand) && (i.PresetId == preset.Id)));
        }
    }

    private static ScenarioFieldIssue? FieldIssue(ScenarioEditIssue[] issues, string field) =>
        ScenarioFieldIssue.Of(issues.Where(i => (i.Field == field) && (i.PresetId is null)));
}
