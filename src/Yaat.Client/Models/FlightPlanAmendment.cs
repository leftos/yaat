namespace Yaat.Client.Models;

public record FlightPlanAmendment(
    string? AircraftType,
    string? EquipmentSuffix,
    string? Departure,
    string? Destination,
    int? CruiseSpeed,
    int? CruiseAltitude,
    string? FlightRules,
    string? Route,
    string? Remarks,
    string? Scratchpad1,
    string? Scratchpad2,
    uint? BeaconCode
);
