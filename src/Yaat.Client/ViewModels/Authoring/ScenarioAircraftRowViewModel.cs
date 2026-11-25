using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// One row of the Aircraft tab's grid: the display texts of one scenario aircraft. A field edit, or its undo or redo,
/// that leaves the aircraft list's ids and order unchanged refreshes the row in place through <see cref="Update"/>;
/// any other change rebuilds the rows. Read-only to the grid; the aircraft is found again by <see cref="Id"/>.
/// </summary>
public sealed class ScenarioAircraftRowViewModel : ObservableObject
{
    private int _index;
    private string _callsign = "";
    private string _type = "";
    private string _transponder = "";
    private string _spawnDelay = "";
    private int _spawnDelaySeconds;
    private string _airport = "";
    private string _start = "";
    private string _route = "";
    private string _presets = "";
    private int _presetCount;
    private string _autoTrack = "";
    private ScenarioFieldIssue? _issue;

    /// <summary>Initializes a new instance of the <see cref="ScenarioAircraftRowViewModel"/> class from an aircraft.</summary>
    /// <param name="aircraft">The aircraft, as the document holds it now.</param>
    /// <param name="index">The aircraft's index into the scenario's aircraft list.</param>
    public ScenarioAircraftRowViewModel(ScenarioAircraft aircraft, int index)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        Id = aircraft.Id;
        Update(aircraft, index);
    }

    /// <summary>Gets the aircraft's id, the key the selection is held by.</summary>
    public string Id { get; }

    /// <summary>Gets the aircraft's index into the scenario's aircraft list when the row was last built or updated.</summary>
    public int Index
    {
        get => _index;
        private set => SetProperty(ref _index, value);
    }

    /// <summary>Gets the callsign (the aircraft's <c>aircraftId</c>).</summary>
    public string Callsign
    {
        get => _callsign;
        private set => SetProperty(ref _callsign, value);
    }

    /// <summary>Gets the aircraft type.</summary>
    public string Type
    {
        get => _type;
        private set => SetProperty(ref _type, value);
    }

    /// <summary>Gets the transponder mode.</summary>
    public string Transponder
    {
        get => _transponder;
        private set => SetProperty(ref _transponder, value);
    }

    /// <summary>Gets the spawn delay as the editor's time fields show it (<c>m:ss</c>, <c>h:mm:ss</c>).</summary>
    public string SpawnDelay
    {
        get => _spawnDelay;
        private set => SetProperty(ref _spawnDelay, value);
    }

    /// <summary>Gets the spawn delay in seconds, the Spawn delay column's sort key.</summary>
    public int SpawnDelaySeconds
    {
        get => _spawnDelaySeconds;
        private set => SetProperty(ref _spawnDelaySeconds, value);
    }

    /// <summary>Gets the aircraft's own airport id; empty when it has none.</summary>
    public string Airport
    {
        get => _airport;
        private set => SetProperty(ref _airport, value);
    }

    /// <summary>Gets a one-line summary of the starting conditions: the kind and its place.</summary>
    public string Start
    {
        get => _start;
        private set => SetProperty(ref _start, value);
    }

    /// <summary>Gets the flight plan's route; empty without a flight plan.</summary>
    public string Route
    {
        get => _route;
        private set => SetProperty(ref _route, value);
    }

    /// <summary>Gets the number of preset commands, shown; empty when there are none.</summary>
    public string Presets
    {
        get => _presets;
        private set => SetProperty(ref _presets, value);
    }

    /// <summary>Gets the number of preset commands, the Presets column's sort key.</summary>
    public int PresetCount
    {
        get => _presetCount;
        private set => SetProperty(ref _presetCount, value);
    }

    /// <summary>Gets the auto-track position id; empty without auto-track.</summary>
    public string AutoTrack
    {
        get => _autoTrack;
        private set => SetProperty(ref _autoTrack, value);
    }

    /// <summary>
    /// Gets or sets the aircraft's live-validation errors and warnings, the grid's row marker; null when it has none. Set by the
    /// Aircraft tab from the latest findings, never from the document.
    /// </summary>
    public ScenarioFieldIssue? Issue
    {
        get => _issue;
        set => SetProperty(ref _issue, value);
    }

    /// <summary>Refreshes the row's texts from its aircraft as the document holds it now, raising a change for each that differs.</summary>
    /// <param name="aircraft">The aircraft, the one with the row's <see cref="Id"/>.</param>
    /// <param name="index">The aircraft's index into the scenario's aircraft list.</param>
    /// <exception cref="ArgumentException">The aircraft is not the row's.</exception>
    public void Update(ScenarioAircraft aircraft, int index)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        if (!string.Equals(aircraft.Id, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The aircraft {aircraft.Id} is not the row's {Id}.", nameof(aircraft));
        }

        Index = index;
        Callsign = aircraft.AircraftId;
        Type = aircraft.AircraftType;
        Transponder = aircraft.TransponderMode;
        SpawnDelaySeconds = aircraft.SpawnDelay;
        SpawnDelay = ScenarioFieldText.FormatTime(aircraft.SpawnDelay);
        Airport = aircraft.AirportId ?? "";
        Start = StartText(aircraft.StartingConditions);
        Route = aircraft.FlightPlan?.Route ?? "";
        PresetCount = aircraft.PresetCommands?.Count ?? 0;
        Presets = (PresetCount == 0) ? "" : PresetCount.ToString(CultureInfo.InvariantCulture);
        AutoTrack = aircraft.AutoTrackConditions?.PositionId ?? "";
    }

    // The Start column: the kind with its place, or the raw type text of a kind the loader does not know.
    private static string StartText(StartingConditions? start)
    {
        if (start is null)
        {
            return "";
        }

        return start.Type switch
        {
            StartingConditionKinds.Parking => $"Parking {start.Parking}".TrimEnd(),
            StartingConditionKinds.OnRunway => $"Runway {start.Runway}".TrimEnd(),
            StartingConditionKinds.Coordinates => "Coordinates",
            StartingConditionKinds.FixOrFrd => start.Fix ?? "",
            StartingConditionKinds.OnFinal => FinalText(start),
            _ => start.Type,
        };
    }

    private static string FinalText(StartingConditions start)
    {
        string runway = $"Final {start.Runway}".TrimEnd();
        return (start.DistanceFromRunway is { } distance) ? string.Create(CultureInfo.InvariantCulture, $"{runway}, {distance} nm") : runway;
    }
}
