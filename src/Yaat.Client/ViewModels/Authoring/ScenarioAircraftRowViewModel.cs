using System.Globalization;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// One row of the Aircraft tab's grid: the display texts of one scenario aircraft, read when the row is built (the grid
/// rebuilds its rows after every edit, undo and redo). Read-only; the aircraft is found again by <see cref="Id"/>.
/// </summary>
public sealed class ScenarioAircraftRowViewModel
{
    /// <summary>Initializes a new instance of the <see cref="ScenarioAircraftRowViewModel"/> class from an aircraft.</summary>
    /// <param name="aircraft">The aircraft, as the document holds it now.</param>
    /// <param name="index">The aircraft's index into the scenario's aircraft list.</param>
    public ScenarioAircraftRowViewModel(ScenarioAircraft aircraft, int index)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        Id = aircraft.Id;
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

    /// <summary>Gets the aircraft's id, the key the selection is held by.</summary>
    public string Id { get; }

    /// <summary>Gets the aircraft's index into the scenario's aircraft list when the row was built.</summary>
    public int Index { get; }

    /// <summary>Gets the callsign (the aircraft's <c>aircraftId</c>).</summary>
    public string Callsign { get; }

    /// <summary>Gets the aircraft type.</summary>
    public string Type { get; }

    /// <summary>Gets the transponder mode.</summary>
    public string Transponder { get; }

    /// <summary>Gets the spawn delay as the editor's time fields show it (<c>m:ss</c>, <c>h:mm:ss</c>).</summary>
    public string SpawnDelay { get; }

    /// <summary>Gets the spawn delay in seconds, the Spawn delay column's sort key.</summary>
    public int SpawnDelaySeconds { get; }

    /// <summary>Gets the aircraft's own airport id; empty when it has none.</summary>
    public string Airport { get; }

    /// <summary>Gets a one-line summary of the starting conditions: the kind and its place.</summary>
    public string Start { get; }

    /// <summary>Gets the flight plan's route; empty without a flight plan.</summary>
    public string Route { get; }

    /// <summary>Gets the number of preset commands, shown; empty when there are none.</summary>
    public string Presets { get; }

    /// <summary>Gets the number of preset commands, the Presets column's sort key.</summary>
    public int PresetCount { get; }

    /// <summary>Gets the auto-track position id; empty without auto-track.</summary>
    public string AutoTrack { get; }

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
