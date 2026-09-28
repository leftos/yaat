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

    /// <summary>
    /// ERAM field 11's interfacility remarks (entered after the clear-weather symbol): the remarks a filed plan, a scenario
    /// and CRC's flight-plan editor carry, and where the VATSIM voice marker (<c>/v/</c>, <c>/r/</c>, <c>/t/</c>) is written.
    /// </summary>
    public string InterfacilityRemarks { get; set; } = "";

    /// <summary>ERAM field 11's intrafacility remarks (entered after the overcast symbol), kept within the facility.</summary>
    public string IntrafacilityRemarks { get; set; } = "";

    /// <summary>
    /// The remarks as one string, the form CRC, strips, TDLS and the voice-type parse read: the intrafacility remarks, then
    /// the interfacility remarks, joined by one space when both are non-empty.
    /// </summary>
    public string Remarks =>
        ((IntrafacilityRemarks.Length > 0) && (InterfacilityRemarks.Length > 0))
            ? $"{IntrafacilityRemarks} {InterfacilityRemarks}"
            : IntrafacilityRemarks + InterfacilityRemarks;

    /// <summary>
    /// Replaces the remarks with one composed string, as CRC's flight-plan editor sends them back: the same string as
    /// <see cref="Remarks"/>, compared trimmed, changes nothing; any other becomes the interfacility remarks and clears the
    /// intrafacility ones.
    /// </summary>
    public void ReplaceRemarks(string remarks)
    {
        if (remarks.Trim() == Remarks.Trim())
        {
            return;
        }
        InterfacilityRemarks = remarks;
        IntrafacilityRemarks = "";
    }

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
    /// <see cref="AltitudeFixApproached"/>, <see cref="AltitudeFixPassed"/> and the resolved fix position.
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
            AltitudeFixApproached = false;
            AltitudeFixPassed = false;
            _altitudeFixResolved = false;
            _altitudeFixPosition = null;
        }
    }

    private PlannedAltitude _altitude = PlannedAltitude.None;

    /// <summary>
    /// Whether the aircraft, airborne, has been within 10 nm of the fix of a fix-qualified <see cref="Altitude"/> with the fix
    /// no more than 90° off its track, closing on it. Set by <c>SimulationEngine.TickAltitudeFixPassage</c>, which latches
    /// <see cref="AltitudeFixPassed"/> only after it; cleared when the altitude is replaced.
    /// </summary>
    public bool AltitudeFixApproached { get; set; }

    /// <summary>
    /// Whether the aircraft has passed the fix of a fix-qualified <see cref="Altitude"/> (<c>170/SJC/110</c>), so that the
    /// altitude after it holds: having approached it (<see cref="AltitudeFixApproached"/>), the fix went more than 90° off
    /// its track. Latched by <c>SimulationEngine.TickAltitudeFixPassage</c>; cleared when the altitude is replaced. Display
    /// data only: only the ERAM data block and QF read it (<see cref="EramAltitudeFeet"/>).
    /// </summary>
    public bool AltitudeFixPassed { get; set; }

    private bool _altitudeFixResolved;
    private LatLon? _altitudeFixPosition;

    /// <summary>
    /// The altitude in feet the ERAM data block (Field B) and QF show: the altitude after the fix once
    /// <see cref="AltitudeFixPassed"/> is set, otherwise <see cref="PlannedAltitude.CruiseFeet"/>. Every other reader of the
    /// flight-plan altitude reads <see cref="PlannedAltitude.CruiseFeet"/>.
    /// </summary>
    public int? EramAltitudeFeet => (AltitudeFixPassed && (Altitude.AfterFixFeet is { } afterFix)) ? afterFix : Altitude.CruiseFeet;

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
    /// (profile-derived) instead. Kept as a flight plan field for display/scenario fidelity. A true airspeed in knots;
    /// 0 when none is filed, and always 0 while a <see cref="CruiseMach"/> or a classified speed is filed. Amendments go
    /// through <see cref="SetTrueAirspeed"/>, <see cref="SetMach"/> and <see cref="SetClassifiedSpeed"/>.
    /// </summary>
    public int CruiseSpeed
    {
        get => _cruiseSpeed;
        init => _cruiseSpeed = value;
    }

    private int _cruiseSpeed;

    /// <summary>
    /// ERAM field 05 as a Mach number in hundredths (<c>M078</c> is 78), entered with <c>AM &lt;FLID&gt; SPD M078</c>.
    /// Null when the filed speed is not a Mach number.
    /// </summary>
    public int? CruiseMach { get; private set; }

    /// <summary>ERAM field 05 as a classified speed (<c>SC</c>). The filed speed is withheld, so <see cref="CruiseSpeed"/> is 0.</summary>
    public bool IsSpeedClassified { get; private set; }

    /// <summary>
    /// Files a true airspeed, clearing a Mach or classified speed. A 0 while a Mach or classified speed is filed changes
    /// nothing: YAAT's flight-plan editor shows such a plan's speed empty and sends 0 back on every amend (CRC's editor
    /// sends 0 too, which yaat-server maps to no speed edit before it gets here).
    /// </summary>
    public void SetTrueAirspeed(int knots)
    {
        if ((knots == 0) && ((CruiseMach is not null) || IsSpeedClassified))
        {
            return;
        }
        _cruiseSpeed = knots;
        CruiseMach = null;
        IsSpeedClassified = false;
    }

    /// <summary>Files a Mach number in hundredths, clearing a true airspeed or classified speed.</summary>
    public void SetMach(int hundredths)
    {
        CruiseMach = hundredths;
        _cruiseSpeed = 0;
        IsSpeedClassified = false;
    }

    /// <summary>Files a classified speed, clearing a true airspeed or Mach number.</summary>
    public void SetClassifiedSpeed()
    {
        IsSpeedClassified = true;
        _cruiseSpeed = 0;
        CruiseMach = null;
    }

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
            InterfacilityRemarks = InterfacilityRemarks,
            IntrafacilityRemarks = IntrafacilityRemarks,
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
            AltitudeFixApproached = AltitudeFixApproached,
            AltitudeFixPassed = AltitudeFixPassed,
            CruiseSpeed = CruiseSpeed,
            CruiseMach = CruiseMach,
            IsSpeedClassified = IsSpeedClassified,
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
            InterfacilityRemarks = dto.InterfacilityRemarks,
            IntrafacilityRemarks = dto.IntrafacilityRemarks,
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
            // After Altitude, whose setter clears both flags.
            AltitudeFixApproached = dto.AltitudeFixApproached,
            AltitudeFixPassed = dto.AltitudeFixPassed,
            CruiseSpeed = dto.CruiseSpeed,
            CruiseMach = dto.CruiseMach,
            IsSpeedClassified = dto.IsSpeedClassified,
            RequestedAltitude = dto.RequestedAltitude,
            HasSpecialAircraftIndicator = dto.HasSpecialAircraftIndicator,
            NumberOfAircraft = dto.NumberOfAircraft,
            CreatedByOwner = dto.CreatedByOwner is not null ? TrackOwner.FromSnapshot(dto.CreatedByOwner) : null,
        };
}
