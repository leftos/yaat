using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Filed flight plan and equipment fields. <see cref="HasFlightPlan"/> distinguishes
/// flighted aircraft from unsupported/ghost tracks. <see cref="RevisionNumber"/>
/// increments on every CRC amend.
/// </summary>
public class AircraftFlightPlan
{
    public bool HasFlightPlan { get; set; }

    /// <summary>
    /// Filed aircraft type — the type as recorded in the flight plan, displayed by STARS,
    /// ASDE-X, the EuroScope tag, the Flight Plan Editor, and flight strips. Distinct from
    /// <see cref="AircraftState.AircraftType"/>, which is the actual physical type that drives
    /// physics/performance and the Tower Cab "out the window" datablock. May differ from the
    /// physical type (instructor amendments, scenario fidelity), and may be empty when an
    /// instructor blanks the field.
    /// </summary>
    public string AircraftType { get; set; } = "";

    /// <summary>
    /// Filed aircraft type with the wake-turbulence prefix stripped (e.g., "H/B763/L" → "B763").
    /// Mirrors <see cref="AircraftState.BaseAircraftType"/> for the filed string so STARS,
    /// ASDE-X, and FP-driven displays can show the bare ICAO designator.
    /// </summary>
    public string BaseAircraftType => AircraftState.StripTypePrefix(AircraftType);
    public string Departure { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Route { get; set; } = "";
    public string Remarks { get; set; } = "";

    /// <summary>
    /// Monotonically increasing count of flight-plan amendments applied to this
    /// aircraft. Starts at 0 for a freshly-spawned aircraft; incremented by
    /// <c>SimulationEngine.AmendFlightPlan</c> on every amendment (empty-amendment
    /// calls still tick to match CRC's behavior of showing a revision bump any time
    /// the controller presses "amend").
    /// </summary>
    public int RevisionNumber { get; set; }

    public string EquipmentSuffix { get; set; } = "A";
    public string FlightRules { get; set; } = "IFR";
    public bool IsVfr => FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);
    public int CruiseAltitude { get; set; }

    /// <summary>
    /// Filed cruise speed, parsed from the flight plan and round-tripped through DTOs/snapshots,
    /// but NOT consumed by physics. Controllers don't act on filed cruise speed in real ops, so
    /// the simulation drives target speed off <see cref="AircraftPerformance.DefaultSpeed"/>
    /// (profile-derived) instead. Kept as a flight plan field for display/scenario fidelity.
    /// </summary>
    public int CruiseSpeed { get; set; }

    /// <summary>
    /// TCP that originally created this flight plan via a CRC STARS command (DA / VP / implied
    /// forms). Populated by <c>RoomEngine.RecordAndDispatchFlightPlanAsync</c>; null for plans
    /// created any other way (scenario-spawned, scenario JSON, recordings predating this field).
    /// Used by <c>TickProcessor.ProcessFlightPlanCreatorAutoTrack</c> to auto-acquire the track
    /// to the creating TCP once the pilot is squawking the assigned beacon code.
    /// </summary>
    public TrackOwner? CreatedByOwner { get; set; }

    public AircraftFlightPlanDto ToSnapshot() =>
        new()
        {
            HasFlightPlan = HasFlightPlan,
            AircraftType = AircraftType,
            Departure = Departure,
            Destination = Destination,
            Route = Route,
            Remarks = Remarks,
            RevisionNumber = RevisionNumber,
            EquipmentSuffix = EquipmentSuffix,
            FlightRules = FlightRules,
            CruiseAltitude = CruiseAltitude,
            CruiseSpeed = CruiseSpeed,
            CreatedByOwner = CreatedByOwner?.ToSnapshot(),
        };

    public static AircraftFlightPlan FromSnapshot(AircraftFlightPlanDto dto) =>
        new()
        {
            HasFlightPlan = dto.HasFlightPlan,
            AircraftType = dto.AircraftType,
            Departure = dto.Departure,
            Destination = dto.Destination,
            Route = dto.Route,
            Remarks = dto.Remarks,
            RevisionNumber = dto.RevisionNumber,
            EquipmentSuffix = dto.EquipmentSuffix,
            FlightRules = dto.FlightRules,
            CruiseAltitude = dto.CruiseAltitude,
            CruiseSpeed = dto.CruiseSpeed,
            CreatedByOwner = dto.CreatedByOwner is not null ? TrackOwner.FromSnapshot(dto.CreatedByOwner) : null,
        };
}
