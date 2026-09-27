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
    public required string Remarks { get; init; }
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
    public required int CruiseSpeed { get; init; }

    /// <summary>ERAM requested altitude — see <see cref="AircraftFlightPlan.RequestedAltitude"/>. Null = none entered.</summary>
    public PlannedAltitude? RequestedAltitude { get; init; }

    /// <summary>ERAM special aircraft indicator <c>H</c> — see <see cref="AircraftFlightPlan.HasSpecialAircraftIndicator"/>.</summary>
    public bool HasSpecialAircraftIndicator { get; init; }

    /// <summary>ERAM number of aircraft — see <see cref="AircraftFlightPlan.NumberOfAircraft"/>. Null = none entered.</summary>
    public int? NumberOfAircraft { get; init; }
    public TrackOwnerDto? CreatedByOwner { get; init; }
}
