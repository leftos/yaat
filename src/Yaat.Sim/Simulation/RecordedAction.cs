using System.Text.Json.Serialization;

namespace Yaat.Sim.Simulation;

[JsonDerivedType(typeof(RecordedCommand), "Command")]
[JsonDerivedType(typeof(RecordedAmendFlightPlan), "AmendFlightPlan")]
[JsonDerivedType(typeof(RecordedWeatherChange), "WeatherChange")]
[JsonDerivedType(typeof(RecordedSettingChange), "SettingChange")]
[JsonDerivedType(typeof(RecordedAsdexMutation), "AsdexMutation")]
[JsonDerivedType(typeof(RecordedArrivalGeneratorsChange), "ArrivalGeneratorsChange")]
public abstract record RecordedAction(double ElapsedSeconds);

public sealed record RecordedCommand(double ElapsedSeconds, string Callsign, string Command, string Initials, string ConnectionId)
    : RecordedAction(ElapsedSeconds);

public sealed record RecordedAmendFlightPlan(double ElapsedSeconds, string Callsign, FlightPlanAmendment Amendment) : RecordedAction(ElapsedSeconds);

public sealed record RecordedWeatherChange(double ElapsedSeconds, string? WeatherJson) : RecordedAction(ElapsedSeconds);

public sealed record RecordedSettingChange(double ElapsedSeconds, string Setting, string? Value) : RecordedAction(ElapsedSeconds);

public sealed record RecordedArrivalGeneratorsChange(double ElapsedSeconds, string GeneratorsJson) : RecordedAction(ElapsedSeconds);

/// <summary>
/// CRC-sourced ASDE-X mutation. <see cref="Kind"/> is one of <c>EditDbFields</c>, <c>Tag</c>,
/// <c>Terminate</c>, <c>Suspend</c>, <c>Unsuspend</c>, <c>InhibitAlerts</c>, <c>EnableAllAlerts</c>.
/// All mutations target server-side <c>AsdexRoomState</c>; the sim ignores them during replay.
/// </summary>
public sealed record RecordedAsdexMutation(
    double ElapsedSeconds,
    string Kind,
    string? AircraftId,
    string? Callsign,
    string? BeaconCode,
    string? Category,
    string? AircraftType,
    string? Fix,
    string? Scratchpad1,
    string? Scratchpad2
) : RecordedAction(ElapsedSeconds);

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
