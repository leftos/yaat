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

    [JsonPropertyName("vfrArrivalGenerators")]
    public List<VfrArrivalGeneratorConfig> VfrArrivalGenerators { get; set; } = [];

    [JsonPropertyName("overflightGenerators")]
    public List<OverflightGeneratorConfig> OverflightGenerators { get; set; } = [];

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

    [JsonPropertyName("interimAltitude")]
    public string? InterimAltitude { get; set; }

    [JsonPropertyName("clearedAltitude")]
    public string? ClearedAltitude { get; set; }
}

/// <summary>
/// Every generator array in one object. This is the unit the generator editor applies to a live scenario,
/// the payload recorded for replay, and the shape broadcast back to clients — one round-trip covers all
/// three generator kinds.
/// </summary>
public class GeneratorsPayload
{
    [JsonPropertyName("aircraftGenerators")]
    public List<ScenarioGeneratorConfig> AircraftGenerators { get; set; } = [];

    [JsonPropertyName("vfrArrivalGenerators")]
    public List<VfrArrivalGeneratorConfig> VfrArrivalGenerators { get; set; } = [];

    [JsonPropertyName("overflightGenerators")]
    public List<OverflightGeneratorConfig> OverflightGenerators { get; set; } = [];
}

/// <summary>
/// The cadence and activation fields every traffic generator shares, regardless of what it spawns.
/// <see cref="GeneratorActivation.IsActive"/> reads these to decide whether a generator may spawn
/// on a given tick.
/// </summary>
public interface IGeneratorConfig
{
    string Id { get; }
    int StartTimeOffset { get; }
    int? MaxTime { get; }
    int IntervalTime { get; }
    bool RandomizeInterval { get; }

    /// <summary>
    /// Instructor override of the <see cref="StartTimeOffset"/>/<see cref="MaxTime"/> window.
    /// Null (the authored default) means "follow the window".
    /// </summary>
    bool? Enabled { get; }
}

/// <summary>
/// Scenario JSON model for aircraft generator configuration.
/// Named ScenarioGeneratorConfig to avoid collision with the runtime
/// <see cref="AircraftGenerator"/> static class in the same namespace.
/// </summary>
public class ScenarioGeneratorConfig : IGeneratorConfig
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

    // vNAS leaves intervalDistance null when omitted (no author distance floor). 0 means the spacing
    // gap falls back to the radar/wake minimum; a positive value binds (not adds) as the in-trail floor.
    [JsonPropertyName("intervalDistance")]
    public double IntervalDistance { get; set; }

    [JsonPropertyName("startTimeOffset")]
    public int StartTimeOffset { get; set; }

    // Nullable to mirror the vNAS model: null means "no time-based exhaustion" -- the stream runs for the
    // whole session rather than stopping at a hard-coded default.
    [JsonPropertyName("maxTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTime { get; set; }

    [JsonPropertyName("intervalTime")]
    public int IntervalTime { get; set; } = 300;

    [JsonPropertyName("randomizeInterval")]
    public bool RandomizeInterval { get; set; }

    [JsonPropertyName("randomizeWeightCategory")]
    public bool RandomizeWeightCategory { get; set; }

    // Instructor override of the time window. Omitted (null) in every authored scenario, and written back
    // only once an instructor toggles the generator in the editor, so vNAS files round-trip unchanged.
    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; set; }

    [JsonPropertyName("autoTrackConfiguration")]
    public AutoTrackConditions? AutoTrackConfiguration { get; set; }
}

/// <summary>
/// Spawns VFR GA traffic inbound to the primary airport on a bearing arc, rather than on a runway's final
/// approach corridor. A YAAT extension: the vNAS/ATCTrainer scenario format has no VFR generator, so this
/// lives in its own <c>vfrArrivalGenerators</c> array and <c>aircraftGenerators</c> stays pure vNAS.
/// </summary>
public class VfrArrivalGeneratorConfig : IGeneratorConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    // Magnetic bearings from the airport bounding the spawn arc. The arc runs clockwise from
    // BearingFrom to BearingTo and may wrap through 360 (e.g. 340 -> 020 is a 40-degree arc through north).
    [JsonPropertyName("bearingFrom")]
    public double BearingFrom { get; set; }

    [JsonPropertyName("bearingTo")]
    public double BearingTo { get; set; } = 360;

    [JsonPropertyName("initialDistance")]
    public double InitialDistance { get; set; } = 10;

    [JsonPropertyName("maxDistance")]
    public double MaxDistance { get; set; } = 20;

    [JsonPropertyName("altitudeMin")]
    public double AltitudeMin { get; set; } = 2500;

    [JsonPropertyName("altitudeMax")]
    public double AltitudeMax { get; set; } = 4500;

    /// <summary>Fix name, FRD, or airport id the aircraft proceeds direct to. Empty = the primary airport.</summary>
    [JsonPropertyName("directTo")]
    public string DirectTo { get; set; } = "";

    /// <summary>0 = spawn level. Negative = spawn descending toward <see cref="DescendToAltitude"/>.</summary>
    [JsonPropertyName("initialVsFpm")]
    public int InitialVsFpm { get; set; }

    /// <summary>Only read when <see cref="InitialVsFpm"/> is negative. Null = traffic-pattern altitude.</summary>
    [JsonPropertyName("descendToAltitude")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DescendToAltitude { get; set; }

    [JsonPropertyName("engineType")]
    public string EngineType { get; set; } = "Piston";

    [JsonPropertyName("weightCategory")]
    public string WeightCategory { get; set; } = "Small";

    [JsonPropertyName("startTimeOffset")]
    public int StartTimeOffset { get; set; }

    [JsonPropertyName("maxTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTime { get; set; }

    [JsonPropertyName("intervalTime")]
    public int IntervalTime { get; set; } = 300;

    [JsonPropertyName("randomizeInterval")]
    public bool RandomizeInterval { get; set; }

    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; set; }

    [JsonPropertyName("autoTrackConfiguration")]
    public AutoTrackConditions? AutoTrackConfiguration { get; set; }
}

/// <summary>
/// Spawns VFR GA traffic transiting the airspace: in on a bearing from the <c>From</c> arc, out toward an
/// exit point on the <c>To</c> arc, deleted once it passes <see cref="ExitDistance"/> outbound. A YAAT
/// extension; see <see cref="VfrArrivalGeneratorConfig"/> for why it lives in its own array.
/// </summary>
public class OverflightGeneratorConfig : IGeneratorConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    // Magnetic bearing arc from the airport that the transit enters on.
    [JsonPropertyName("fromBearingFrom")]
    public double FromBearingFrom { get; set; }

    [JsonPropertyName("fromBearingTo")]
    public double FromBearingTo { get; set; } = 360;

    // Magnetic bearing arc from the airport that the transit exits toward.
    [JsonPropertyName("toBearingFrom")]
    public double ToBearingFrom { get; set; }

    [JsonPropertyName("toBearingTo")]
    public double ToBearingTo { get; set; } = 360;

    [JsonPropertyName("initialDistance")]
    public double InitialDistance { get; set; } = 15;

    [JsonPropertyName("maxDistance")]
    public double MaxDistance { get; set; } = 25;

    // Class C tops out at 4000 ft AGL (AIM 3-2-4.a), so the default band puts a 1200 transit over the top.
    [JsonPropertyName("altitudeMin")]
    public double AltitudeMin { get; set; } = 4500;

    [JsonPropertyName("altitudeMax")]
    public double AltitudeMax { get; set; } = 7500;

    /// <summary>
    /// Snap a level transit's altitude to the VFR hemispheric rule (14 CFR 91.159(a), AIM 3-1-5). Default on.
    /// Turn it off to stage a deliberately non-conforming target.
    /// </summary>
    [JsonPropertyName("snapHemisphericAltitude")]
    public bool SnapHemisphericAltitude { get; set; } = true;

    /// <summary>Distance from the primary airport past which an outbound overflight is deleted. Null = <see cref="MaxDistance"/> + 5.</summary>
    [JsonPropertyName("exitDistance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ExitDistance { get; set; }

    [JsonPropertyName("engineType")]
    public string EngineType { get; set; } = "Piston";

    [JsonPropertyName("weightCategory")]
    public string WeightCategory { get; set; } = "Small";

    [JsonPropertyName("startTimeOffset")]
    public int StartTimeOffset { get; set; }

    [JsonPropertyName("maxTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTime { get; set; }

    [JsonPropertyName("intervalTime")]
    public int IntervalTime { get; set; } = 300;

    [JsonPropertyName("randomizeInterval")]
    public bool RandomizeInterval { get; set; }

    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; set; }
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

    [JsonPropertyName("facilityId")]
    public string? FacilityId { get; set; }

    [JsonPropertyName("bayId")]
    public string? BayId { get; set; }

    [JsonPropertyName("rack")]
    public int Rack { get; set; }

    [JsonPropertyName("aircraftIds")]
    public List<string> AircraftIds { get; set; } = [];
}

/// <summary>
/// Resolved per-callsign strip-bay pre-placement derived from
/// <see cref="Scenario.FlightStripConfigurations"/> at load time. Directs the spawn
/// auto-print hook to drop the aircraft's departure strip into a specific bay/rack
/// instead of the departure printer queue.
/// </summary>
public sealed record ScenarioStripBayAssignment(string FacilityId, string BayId, int Rack);
