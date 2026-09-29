using System.Text.Json.Serialization;

namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftFlightPlanDto
{
    public required bool HasFlightPlan { get; init; }

    /// <summary>
    /// Filed aircraft type — see <see cref="AircraftFlightPlan.AircraftType"/>.
    /// Plain <c>set</c> with default <c>""</c> so older snapshots (pre-schema-v4) deserialize
    /// cleanly with the default and <see cref="SnapshotSchemaMigrator"/> can mutate the field
    /// in place to seed it from the parent <see cref="AircraftSnapshotDto.AircraftType"/>.
    /// </summary>
    public string AircraftType { get; set; } = "";
    public required string Departure { get; init; }
    public required string Destination { get; init; }
    public required string Route { get; init; }

    /// <summary>
    /// The interfacility remarks — see <see cref="AircraftFlightPlan.InterfacilityRemarks"/>. Keyed <c>Remarks</c>, the key a
    /// snapshot written before the remarks split used for the whole remarks, which load here.
    /// </summary>
    [JsonPropertyName("Remarks")]
    public required string InterfacilityRemarks { get; init; }

    /// <summary>The intrafacility remarks — see <see cref="AircraftFlightPlan.IntrafacilityRemarks"/>.</summary>
    public string IntrafacilityRemarks { get; init; } = "";
    public int RevisionNumber { get; init; }
    public required string EquipmentSuffix { get; init; }
    public string IcaoEquipmentCodes { get; init; } = "";
    public required string FlightRules { get; init; }

    /// <summary>Filed altitude, flattened from <see cref="PlannedAltitude"/> (feet). Ceiling/single value; null = none.</summary>
    public int? AltitudeCruiseFeet { get; init; }
    public int? AltitudeBlockFloorFeet { get; init; }
    public bool AltitudeIsVfr { get; init; }
    public bool AltitudeIsVfrOnTop { get; init; }
    public bool AltitudeIsAbove { get; init; }

    /// <summary>The fix of a fix-qualified altitude, as typed — see <see cref="PlannedAltitude.AltitudeFix"/>. Null = not fix-qualified.</summary>
    public string? AltitudeFix { get; init; }

    /// <summary>The altitude after <see cref="AltitudeFix"/> in feet — see <see cref="PlannedAltitude.AfterFixFeet"/>.</summary>
    public int? AltitudeAfterFixFeet { get; init; }

    /// <summary>Whether the aircraft has closed on <see cref="AltitudeFix"/> — see <see cref="AircraftFlightPlan.AltitudeFixApproached"/>.</summary>
    public bool AltitudeFixApproached { get; init; }

    /// <summary>Whether the aircraft has passed <see cref="AltitudeFix"/> — see <see cref="AircraftFlightPlan.AltitudeFixPassed"/>.</summary>
    public bool AltitudeFixPassed { get; init; }

    /// <summary>ERAM assigned altitude in feet — see <see cref="AircraftFlightPlan.EramAssignedAltitudeFeet"/>. Null = none.</summary>
    public int? EramAssignedAltitudeFeet { get; init; }
    public required int CruiseSpeed { get; init; }

    /// <summary>Filed Mach in hundredths — see <see cref="AircraftFlightPlan.CruiseMach"/>. Null = none.</summary>
    public int? CruiseMach { get; init; }

    /// <summary>Classified speed (<c>SC</c>) — see <see cref="AircraftFlightPlan.IsSpeedClassified"/>.</summary>
    public bool IsSpeedClassified { get; init; }

    /// <summary>ERAM requested altitude — see <see cref="AircraftFlightPlan.RequestedAltitude"/>. Null = none entered.</summary>
    public PlannedAltitude? RequestedAltitude { get; init; }

    /// <summary>ERAM special aircraft indicator <c>H</c> — see <see cref="AircraftFlightPlan.HasSpecialAircraftIndicator"/>.</summary>
    public bool HasSpecialAircraftIndicator { get; init; }

    /// <summary>ERAM number of aircraft — see <see cref="AircraftFlightPlan.NumberOfAircraft"/>. Null = none entered.</summary>
    public int? NumberOfAircraft { get; init; }
    public TrackOwnerDto? CreatedByOwner { get; init; }
}
