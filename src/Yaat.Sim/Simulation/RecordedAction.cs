using System.Text.Json.Serialization;

namespace Yaat.Sim.Simulation;

[JsonDerivedType(typeof(RecordedCommand), "Command")]
[JsonDerivedType(typeof(RecordedSpawn), "Spawn")]
[JsonDerivedType(typeof(RecordedDelete), "Delete")]
[JsonDerivedType(typeof(RecordedWarp), "Warp")]
[JsonDerivedType(typeof(RecordedAmendFlightPlan), "AmendFlightPlan")]
[JsonDerivedType(typeof(RecordedWeatherChange), "WeatherChange")]
[JsonDerivedType(typeof(RecordedSettingChange), "SettingChange")]
public abstract record RecordedAction(double ElapsedSeconds);

public sealed record RecordedCommand(double ElapsedSeconds, string Callsign, string Command, string Initials, string ConnectionId)
    : RecordedAction(ElapsedSeconds);

public sealed record RecordedSpawn(double ElapsedSeconds, string Args) : RecordedAction(ElapsedSeconds);

public sealed record RecordedDelete(double ElapsedSeconds, string Callsign) : RecordedAction(ElapsedSeconds);

public sealed record RecordedWarp(double ElapsedSeconds, string Callsign, double Latitude, double Longitude, double Heading)
    : RecordedAction(ElapsedSeconds);

public sealed record RecordedAmendFlightPlan(double ElapsedSeconds, string Callsign, FlightPlanAmendment Amendment) : RecordedAction(ElapsedSeconds);

public sealed record RecordedWeatherChange(double ElapsedSeconds, string? WeatherJson) : RecordedAction(ElapsedSeconds);

public sealed record RecordedSettingChange(double ElapsedSeconds, string Setting, string? Value) : RecordedAction(ElapsedSeconds);

public record FlightPlanAmendment(
    string? AircraftType = null,
    string? EquipmentSuffix = null,
    string? Departure = null,
    string? Destination = null,
    int? CruiseSpeed = null,
    int? CruiseAltitude = null,
    string? FlightRules = null,
    string? Route = null,
    string? Remarks = null,
    string? Scratchpad1 = null,
    string? Scratchpad2 = null,
    uint? BeaconCode = null
);
