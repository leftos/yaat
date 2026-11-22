using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

public sealed partial class ScenarioGeneralViewModel
{
    private const string NoneText = "None";
    private const string SummarySeparator = " · ";

    private static readonly string[] SummaryProperties =
    [
        nameof(SettingsSummary),
        nameof(AtcSummary),
        nameof(TriggersSummary),
        nameof(GeneratorsSummary),
        nameof(YaatExtensionsSummary),
        nameof(StripsSummary),
    ];

    /// <summary>Gets the scenario's id, shown read-only: it names the working copy's file.</summary>
    public string ScenarioId => _session.ScenarioId;

    /// <summary>
    /// Gets or sets the student position picker's selection. Null (a picker clearing as its options are swapped) and the
    /// value already held write nothing; any other value is the user's pick and is committed as one undo step.
    /// </summary>
    public string? StudentPositionPick
    {
        get => StudentPositionId;
        set => ScenarioFieldText.Pick(value, StudentPositionId, v => StudentPositionId = v, CommitStudentPosition);
    }

    /// <summary>Gets or sets the minimum rating picker's selection; see <see cref="StudentPositionPick"/>.</summary>
    public string? MinimumRatingPick
    {
        get => MinimumRating;
        set => ScenarioFieldText.Pick(value, MinimumRating, v => MinimumRating = v, CommitMinimumRating);
    }

    /// <summary>Gets or sets the auto-delete picker's selection; see <see cref="StudentPositionPick"/>.</summary>
    public string? AutoDeletePick
    {
        get => AutoDeleteMode;
        set => ScenarioFieldText.Pick(value, AutoDeleteMode, v => AutoDeleteMode = v, CommitAutoDelete);
    }

    /// <summary>Gets or sets the primary airport picker's selection; see <see cref="StudentPositionPick"/>.</summary>
    public string? PrimaryAirportPick
    {
        get => PrimaryAirportId;
        set => ScenarioFieldText.Pick(value, PrimaryAirportId, v => PrimaryAirportId = v, CommitPrimaryAirport);
    }

    /// <summary>
    /// Gets or sets the picker's selected option, the one of <see cref="StudentPositionOptions"/> whose value the field holds. The
    /// picker binds its SelectedItem here: a picker's SelectedValue binding does not survive its options being swapped.
    /// Setting it goes through <see cref="StudentPositionPick"/>, so only the user's pick is committed.
    /// </summary>
    public ScenarioPickOption? StudentPositionSelection
    {
        get => ScenarioFieldText.Selected(StudentPositionOptions, StudentPositionId);
        set => StudentPositionPick = value?.Value;
    }

    /// <summary>
    /// Gets or sets the picker's selected option, the one of <see cref="RatingOptions"/> whose value the field holds. The
    /// picker binds its SelectedItem here: a picker's SelectedValue binding does not survive its options being swapped.
    /// Setting it goes through <see cref="MinimumRatingPick"/>, so only the user's pick is committed.
    /// </summary>
    public ScenarioPickOption? MinimumRatingSelection
    {
        get => ScenarioFieldText.Selected(RatingOptions, MinimumRating);
        set => MinimumRatingPick = value?.Value;
    }

    /// <summary>
    /// Gets or sets the picker's selected option, the one of <see cref="AutoDeleteOptions"/> whose value the field holds. The
    /// picker binds its SelectedItem here: a picker's SelectedValue binding does not survive its options being swapped.
    /// Setting it goes through <see cref="AutoDeletePick"/>, so only the user's pick is committed.
    /// </summary>
    public ScenarioPickOption? AutoDeleteSelection
    {
        get => ScenarioFieldText.Selected(AutoDeleteOptions, AutoDeleteMode);
        set => AutoDeletePick = value?.Value;
    }

    /// <summary>Gets the Settings section's one-line summary, shown while it is collapsed: the name.</summary>
    public string SettingsSummary => _session.Document.Scenario.Name;

    /// <summary>Gets the ATC positions section's summary: each position's callsign (its id when the config does not list it).</summary>
    public string AtcSummary => Join((_session.Document.Scenario.Atc ?? []).Select(a => FindPosition(a.PositionId)?.Callsign ?? a.PositionId));

    /// <summary>Gets the initialization triggers section's summary: each trigger's time and command, as "0:00 SQALL".</summary>
    public string TriggersSummary =>
        Join((_session.Document.Scenario.InitializationTriggers ?? []).Select(t => $"{ScenarioFieldText.FormatTime(t.TimeOffset)} {t.Command}"));

    /// <summary>Gets the generators section's summary: each arrival generator as "Arrival 28L, every 3:00".</summary>
    public string GeneratorsSummary =>
        Join(
            (_session.Document.Scenario.AircraftGenerators ?? []).Select(g =>
                $"Arrival {g.Runway}, every {ScenarioFieldText.FormatTime(g.IntervalTime)}"
            )
        );

    /// <summary>Gets the YAAT extensions section's summary: each VFR arrival and overflight generator and its interval.</summary>
    public string YaatExtensionsSummary
    {
        get
        {
            Scenario scenario = _session.Document.Scenario;
            IEnumerable<string> vfr = (scenario.VfrArrivalGenerators ?? []).Select(g =>
                $"VFR arrival, every {ScenarioFieldText.FormatTime(g.IntervalTime)}"
            );
            IEnumerable<string> overflights = (scenario.OverflightGenerators ?? []).Select(g =>
                $"Overflight, every {ScenarioFieldText.FormatTime(g.IntervalTime)}"
            );
            return Join(vfr.Concat(overflights));
        }
    }

    /// <summary>Gets the strip bays section's summary: each placement as "Ground (SFO), rack 1, 3 aircraft".</summary>
    public string StripsSummary => Join((_session.Document.Scenario.FlightStripConfigurations ?? []).Select(StripSummary));

    private static string Join(IEnumerable<string> parts)
    {
        string joined = string.Join(SummarySeparator, parts);
        return (joined.Length == 0) ? NoneText : joined;
    }

    private string StripSummary(FlightStripConfiguration strip)
    {
        string value = StripPlacementRowViewModel.BayValueOf(strip.FacilityId, strip.BayId);
        string bay = _bayOptions.FirstOrDefault(b => string.Equals(b.Value, value, StringComparison.Ordinal))?.Label ?? value;
        int aircraft = (strip.AircraftIds ?? []).Count;
        return $"{((bay.Length == 0) ? "No bay" : bay)}, rack {strip.Rack}, {aircraft} aircraft";
    }

    private void RefreshSummaries()
    {
        foreach (string property in SummaryProperties)
        {
            OnPropertyChanged(property);
        }
    }
}
