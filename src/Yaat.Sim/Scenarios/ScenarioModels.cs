using System.Text.Json.Serialization;

namespace Yaat.Sim.Scenarios;

public class Scenario
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("artccId")]
    public string ArtccId { get; set; } = "";

    [JsonPropertyName("aircraft")]
    public List<ScenarioAircraft> Aircraft { get; set; } = [];

    [JsonPropertyName("initializationTriggers")]
    public List<InitializationTrigger> InitializationTriggers { get; set; } = [];

    [JsonPropertyName("aircraftGenerators")]
    public List<ScenarioGeneratorConfig> AircraftGenerators { get; set; } = [];

    [JsonPropertyName("atc")]
    public List<ScenarioAtc> Atc { get; set; } = [];

    [JsonPropertyName("primaryAirportId")]
    public string? PrimaryAirportId { get; set; }

    [JsonPropertyName("primaryApproach")]
    public string? PrimaryApproach { get; set; }

    [JsonPropertyName("studentPositionId")]
    public string? StudentPositionId { get; set; }

    [JsonPropertyName("autoDeleteMode")]
    public string? AutoDeleteMode { get; set; }

    [JsonPropertyName("flightStripConfigurations")]
    public List<FlightStripConfiguration> FlightStripConfigurations { get; set; } = [];

    [JsonPropertyName("minimumRating")]
    public string? MinimumRating { get; set; }
}

public class ScenarioAircraft
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("aircraftId")]
    public string AircraftId { get; set; } = "";

    [JsonPropertyName("aircraftType")]
    public string AircraftType { get; set; } = "";

    [JsonPropertyName("transponderMode")]
    public string TransponderMode { get; set; } = "C";

    [JsonPropertyName("startingConditions")]
    public StartingConditions StartingConditions { get; set; } = new();

    [JsonPropertyName("onAltitudeProfile")]
    public bool OnAltitudeProfile { get; set; }

    [JsonPropertyName("flightplan")]
    public ScenarioFlightPlan? FlightPlan { get; set; }

    [JsonPropertyName("presetCommands")]
    public List<PresetCommand> PresetCommands { get; set; } = [];

    [JsonPropertyName("spawnDelay")]
    public int SpawnDelay { get; set; }

    [JsonPropertyName("airportId")]
    public string? AirportId { get; set; }

    [JsonPropertyName("difficulty")]
    public string? Difficulty { get; set; }

    [JsonPropertyName("autoTrackConditions")]
    public AutoTrackConditions? AutoTrackConditions { get; set; }

    [JsonPropertyName("expectedApproach")]
    public string? ExpectedApproach { get; set; }
}

public class StartingConditions
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("fix")]
    public string? Fix { get; set; }

    [JsonPropertyName("coordinates")]
    public ScenarioCoordinates? Coordinates { get; set; }

    [JsonPropertyName("runway")]
    public string? Runway { get; set; }

    [JsonPropertyName("altitude")]
    public double? Altitude { get; set; }

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }

    [JsonPropertyName("navigationPath")]
    public string? NavigationPath { get; set; }

    [JsonPropertyName("heading")]
    public double? Heading { get; set; }

    [JsonPropertyName("parking")]
    public string? Parking { get; set; }

    [JsonPropertyName("distanceFromRunway")]
    public double? DistanceFromRunway { get; set; }
}

public class ScenarioCoordinates
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }
}

public class ScenarioFlightPlan
{
    [JsonPropertyName("rules")]
    public string? Rules { get; set; }

    [JsonPropertyName("departure")]
    public string Departure { get; set; } = "";

    [JsonPropertyName("destination")]
    public string Destination { get; set; } = "";

    [JsonPropertyName("cruiseAltitude")]
    public int CruiseAltitude { get; set; }

    [JsonPropertyName("cruiseSpeed")]
    public int CruiseSpeed { get; set; }

    [JsonPropertyName("route")]
    public string Route { get; set; } = "";

    [JsonPropertyName("remarks")]
    public string Remarks { get; set; } = "";

    [JsonPropertyName("aircraftType")]
    public string AircraftType { get; set; } = "";
}

public class PresetCommand
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("timeOffset")]
    public int TimeOffset { get; set; }
}

public class InitializationTrigger
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("timeOffset")]
    public int TimeOffset { get; set; }
}

public class AutoTrackConditions
{
    [JsonPropertyName("positionId")]
    public string PositionId { get; set; } = "";

    [JsonPropertyName("handoffDelay")]
    public int? HandoffDelay { get; set; }

    [JsonPropertyName("scratchPad")]
    public string? ScratchPad { get; set; }

    [JsonPropertyName("clearedAltitude")]
    public string? ClearedAltitude { get; set; }
}

/// <summary>
/// Scenario JSON model for aircraft generator configuration.
/// Named ScenarioGeneratorConfig to avoid collision with the runtime
/// <see cref="AircraftGenerator"/> static class in the same namespace.
/// </summary>
public class ScenarioGeneratorConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("runway")]
    public string Runway { get; set; } = "";

    [JsonPropertyName("engineType")]
    public string EngineType { get; set; } = "Jet";

    [JsonPropertyName("weightCategory")]
    public string WeightCategory { get; set; } = "Large";

    [JsonPropertyName("initialDistance")]
    public double InitialDistance { get; set; } = 10;

    [JsonPropertyName("maxDistance")]
    public double MaxDistance { get; set; } = 50;

    [JsonPropertyName("intervalDistance")]
    public double IntervalDistance { get; set; } = 5;

    [JsonPropertyName("startTimeOffset")]
    public int StartTimeOffset { get; set; }

    [JsonPropertyName("maxTime")]
    public int MaxTime { get; set; } = 3600;

    [JsonPropertyName("intervalTime")]
    public int IntervalTime { get; set; } = 300;

    [JsonPropertyName("randomizeInterval")]
    public bool RandomizeInterval { get; set; }

    [JsonPropertyName("randomizeWeightCategory")]
    public bool RandomizeWeightCategory { get; set; }

    [JsonPropertyName("autoTrackConfiguration")]
    public AutoTrackConditions? AutoTrackConfiguration { get; set; }
}

public class ScenarioAtc
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("artccId")]
    public string ArtccId { get; set; } = "";

    [JsonPropertyName("facilityId")]
    public string FacilityId { get; set; } = "";

    [JsonPropertyName("positionId")]
    public string PositionId { get; set; } = "";

    [JsonPropertyName("autoConnect")]
    public bool AutoConnect { get; set; }

    [JsonPropertyName("autoTrackAirportIds")]
    public List<string> AutoTrackAirportIds { get; set; } = [];
}

public class FlightStripConfiguration
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
