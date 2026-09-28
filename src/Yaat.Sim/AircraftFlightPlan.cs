using Yaat.Sim.Data;
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

    /// <summary>
    /// ICAO flight-plan field-10a equipment codes (e.g. <c>SDE2E3FGHIJ5M1RWXY</c>). Empty when never
    /// entered — scenarios file FAA <c>TYPE/SUFFIX</c> only, so this is populated by controller
    /// amendment (YAAT Flight Plan Editor). CRC derives the ERAM SATCOMM <c>*</c> indicator from the
    /// satcom codes (J5-J7, M1-M3).
    /// </summary>
    public string IcaoEquipmentCodes { get; set; } = "";
    public string FlightRules { get; set; } = "IFR";
    public bool IsVfr => FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Filed altitude — the notation axis of the plan (single / block / VFR / OTP / above), in feet.
    /// Distinct from <see cref="FlightRules"/> (the IFR/VFR rules axis) and from
    /// <see cref="ControlTargets.AssignedAltitude"/> (the current ATC clearance). Defaults to
    /// <see cref="PlannedAltitude.None"/> (no filed altitude). Setting a different altitude, by any path, clears
    /// <see cref="AltitudeFixPassed"/> and the resolved fix position.
    /// </summary>
    public PlannedAltitude Altitude
    {
        get => _altitude;
        set
        {
            if (value == _altitude)
            {
                return;
            }
            _altitude = value;
            AltitudeFixPassed = false;
            _altitudeFixResolved = false;
            _altitudeFixPosition = null;
        }
    }

    private PlannedAltitude _altitude = PlannedAltitude.None;

    /// <summary>
    /// Whether the aircraft has passed the fix of a fix-qualified <see cref="Altitude"/> (<c>170/SJC/110</c>), so that the
    /// altitude after it holds. Latched by <c>SimulationEngine.TickAltitudeFixPassage</c>; cleared when the altitude is
    /// replaced. Display data only: only the ERAM data block and QF read it (<see cref="EramAltitudeFeet"/>).
    /// </summary>
    public bool AltitudeFixPassed { get; set; }

    private bool _altitudeFixResolved;
    private LatLon? _altitudeFixPosition;

    /// <summary>
    /// The altitude in feet the ERAM data block (Field B) and QF show: the altitude after the fix once
    /// <see cref="AltitudeFixPassed"/> is set, otherwise <see cref="PlannedAltitude.CruiseFeet"/>. Every other reader of the
    /// flight-plan altitude reads <see cref="PlannedAltitude.CruiseFeet"/>.
    /// </summary>
    public int? EramAltitudeFeet => AltitudeFixPassed && (Altitude.AfterFixFeet is { } afterFix) ? afterFix : Altitude.CruiseFeet;

    /// <summary>
    /// The position of the fix of a fix-qualified <see cref="Altitude"/>, resolved through <see cref="EramFixResolver"/> on
    /// first use and kept until the altitude is replaced. Null when the altitude is not fix-qualified or the fix does not resolve.
    /// </summary>
    public LatLon? AltitudeFixPosition(NavigationDatabase navDb)
    {
        if (Altitude.AltitudeFix is not { } fix)
        {
            return null;
        }
        if (!_altitudeFixResolved)
        {
            _altitudeFixPosition = EramFixResolver.Resolve(fix, navDb);
            _altitudeFixResolved = true;
        }
        return _altitudeFixPosition;
    }

    /// <summary>
    /// Filed cruise speed, parsed from the flight plan and round-tripped through DTOs/snapshots,
    /// but NOT consumed by physics. Controllers don't act on filed cruise speed in real ops, so
    /// the simulation drives target speed off <see cref="AircraftPerformance.DefaultSpeed"/>
    /// (profile-derived) instead. Kept as a flight plan field for display/scenario fidelity.
    /// </summary>
    public int CruiseSpeed { get; set; }

    /// <summary>
    /// ERAM field 09, the requested altitude (the altitude the pilot asked for, distinct from the filed
    /// <see cref="Altitude"/>), entered with <c>AM &lt;FLID&gt; RAL &lt;alt&gt;</c>. Null when never entered.
    /// Display data only: nothing in the simulation reads it.
    /// </summary>
    public PlannedAltitude? RequestedAltitude { get; set; }

    /// <summary>
    /// ERAM field 22, the special aircraft indicator <c>H</c> (field 03 element a), entered with
    /// <c>AM &lt;FLID&gt; SAI H</c>. Independent of any <c>H/</c> prefix on <see cref="AircraftType"/>.
    /// Display data only.
    /// </summary>
    public bool HasSpecialAircraftIndicator { get; set; }

    /// <summary>
    /// ERAM field 21, the number of aircraft in the flight (ICAO 909a), entered with <c>AM &lt;FLID&gt; NUM &lt;n&gt;</c>.
    /// Null when never entered. Display data only: a count here moves no aircraft.
    /// </summary>
    public int? NumberOfAircraft { get; set; }

    /// <summary>
    /// TCP that originally created this flight plan via a CRC STARS command (DA / VP / implied
    /// forms). Populated by <c>RoomEngine.RecordAndDispatchFlightPlanAsync</c>; null for plans
    /// created any other way (scenario-spawned, scenario JSON, recordings predating this field).
    /// Used by <c>SimulationEngine.TickFlightPlanCreatorAutoTrack</c> to auto-acquire the track
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
            IcaoEquipmentCodes = IcaoEquipmentCodes,
            FlightRules = FlightRules,
            AltitudeCruiseFeet = Altitude.CruiseFeet,
            AltitudeBlockFloorFeet = Altitude.BlockFloorFeet,
            AltitudeIsVfr = Altitude.IsVfr,
            AltitudeIsVfrOnTop = Altitude.IsVfrOnTop,
            AltitudeIsAbove = Altitude.IsAbove,
            AltitudeFix = Altitude.AltitudeFix,
            AltitudeAfterFixFeet = Altitude.AfterFixFeet,
            AltitudeFixPassed = AltitudeFixPassed,
            CruiseSpeed = CruiseSpeed,
            RequestedAltitude = RequestedAltitude,
            HasSpecialAircraftIndicator = HasSpecialAircraftIndicator,
            NumberOfAircraft = NumberOfAircraft,
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
            IcaoEquipmentCodes = dto.IcaoEquipmentCodes,
            FlightRules = dto.FlightRules,
            Altitude = new PlannedAltitude(
                dto.AltitudeCruiseFeet,
                dto.AltitudeBlockFloorFeet,
                dto.AltitudeIsVfr,
                dto.AltitudeIsVfrOnTop,
                dto.AltitudeIsAbove
            )
            {
                AltitudeFix = dto.AltitudeFix,
                AfterFixFeet = dto.AltitudeAfterFixFeet,
            },
            // After Altitude, whose setter clears the latch.
            AltitudeFixPassed = dto.AltitudeFixPassed,
            CruiseSpeed = dto.CruiseSpeed,
            RequestedAltitude = dto.RequestedAltitude,
            HasSpecialAircraftIndicator = dto.HasSpecialAircraftIndicator,
            NumberOfAircraft = dto.NumberOfAircraft,
            CreatedByOwner = dto.CreatedByOwner is not null ? TrackOwner.FromSnapshot(dto.CreatedByOwner) : null,
        };
}
