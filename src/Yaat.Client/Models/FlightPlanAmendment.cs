using Yaat.Sim;

namespace Yaat.Client.Models;

public record FlightPlanAmendment(
    string? AircraftType,
    string? EquipmentSuffix,
    string? Departure,
    string? Destination,
    int? CruiseSpeed,
    PlannedAltitude? Altitude,
    string? FlightRules,
    string? Route,
    string? Remarks,
    string? Scratchpad1,
    string? Scratchpad2,
    uint? BeaconCode
);
