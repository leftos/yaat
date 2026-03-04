using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim;

public class AircraftState
{
    public required string Callsign { get; set; }
    public required string AircraftType { get; set; }
    public string BaseAircraftType => StripTypePrefix(AircraftType);

    /// <summary>
    /// Extract ICAO type designator from FAA flight plan format.
    /// "B738" → "B738", "H/B763/L" → "B763", "B738/L" → "B738".
    /// </summary>
    internal static string StripTypePrefix(string aircraftType)
    {
        var parts = aircraftType.Split('/');
        if (parts.Length >= 2 && parts[0] is "H" or "J" or "S")
        {
            return parts[1];
        }

        return parts[0];
    }

    public string? ScenarioId { get; set; }
    public string Cid { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Heading { get; set; }

    /// <summary>Ground track direction in degrees. Equals Heading when there is no wind.</summary>
    public double Track { get; set; }

    public double Altitude { get; set; }
    public double GroundSpeed { get; set; }

    /// <summary>
    /// Indicated airspeed in knots. What the pilot flies and ATC commands.
    /// Differs from GroundSpeed when wind is present or at altitude (TAS correction).
    /// </summary>
    public double IndicatedAirspeed { get; set; }

    public double VerticalSpeed { get; set; }
    public uint AssignedBeaconCode { get; set; }
    public uint BeaconCode { get; set; }
    public string Departure { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Route { get; set; } = "";
    public string Remarks { get; set; } = "";
    public string EquipmentSuffix { get; set; } = "A";
    public string FlightRules { get; set; } = "IFR";
    public bool IsVfr => FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);
    public int CruiseAltitude { get; set; }
    public int CruiseSpeed { get; set; }
    public string TransponderMode { get; set; } = "C";
    public bool IsIdenting { get; set; }
    public double? IdentStartedAt { get; set; }
    public bool IsOnGround { get; set; }
    public ControlTargets Targets { get; } = new();
    public CommandQueue Queue { get; set; } = new();
    public PhaseList? Phases { get; set; }
    public List<string> PendingWarnings { get; } = [];
    public List<string> PendingNotifications { get; } = [];
    public List<ApproachScore> PendingApproachScores { get; } = [];
    public ApproachScore? ActiveApproachScore { get; set; }

    // Ground operations state
    public TaxiRoute? AssignedTaxiRoute { get; set; }
    public string? ParkingSpot { get; set; }
    public string? CurrentTaxiway { get; set; }
    public bool IsHeld { get; set; }
    public bool AutoDeleteExempt { get; set; }

    /// <summary>
    /// Max ground speed (kts) imposed by GroundConflictDetector.
    /// Null = no limit. Reset each tick before conflict detection runs.
    /// </summary>
    public double? GroundSpeedLimit { get; set; }

    /// <summary>
    /// When set, FlightPhysics uses this heading for ground position updates
    /// instead of <see cref="Heading"/>. Used by pushback (aircraft nose stays
    /// forward while tug pushes it backward along this direction).
    /// </summary>
    public double? PushbackHeading { get; set; }

    // Track operations state
    public TrackOwner? Owner { get; set; }
    public TrackOwner? HandoffPeer { get; set; }
    public TrackOwner? HandoffRedirectedBy { get; set; }
    public StarsPointout? Pointout { get; set; }
    public string? Scratchpad1 { get; set; }
    public string? Scratchpad2 { get; set; }
    public int? TemporaryAltitude { get; set; }
    public int? PilotReportedAltitude { get; set; }
    public bool IsAnnotated { get; set; }
    public bool FrequencyChangeApproved { get; set; }
    public string? ContactPosition { get; set; }
    public bool OnHandoff { get; set; }
    public double? HandoffInitiatedAt { get; set; }
    public int? AssignedAltitude { get; set; }

    // Approach expectation
    public string? ExpectedApproach { get; set; }

    // Procedure state (SID/STAR)
    public string? ActiveSidId { get; set; }
    public string? ActiveStarId { get; set; }
    public bool SidViaMode { get; set; }
    public bool StarViaMode { get; set; }
    public int? SidViaCeiling { get; set; }
    public int? StarViaFloor { get; set; }

    /// <summary>Current bank angle in degrees. Positive = right bank, negative = left bank. Zero when wings level.</summary>
    public double BankAngle { get; set; }

    // Visual approach state
    public bool HasReportedFieldInSight { get; set; }
    public bool HasReportedTrafficInSight { get; set; }
    public string? FollowingCallsign { get; set; }

    // Voice type (CRC display): 0=Unknown, 1=Full, 2=ReceiveOnly, 3=TextOnly
    public int VoiceType { get; set; } = 1;

    // TDLS dump flag (CRC flight plan)
    public bool TdlsDumped { get; set; }

    // Hold annotations (CRC display)
    public string? HoldAnnotationFix { get; set; }
    public int HoldAnnotationDirection { get; set; }
    public int HoldAnnotationTurns { get; set; }
    public int? HoldAnnotationLegLength { get; set; }
    public bool HoldAnnotationLegLengthInNm { get; set; }
    public int HoldAnnotationEfc { get; set; }

    // Clearance (departure clearance from CRC)
    public string? ClearanceExpect { get; set; }
    public string? ClearanceSid { get; set; }
    public string? ClearanceTransition { get; set; }
    public string? ClearanceClimbout { get; set; }
    public string? ClearanceClimbvia { get; set; }
    public string? ClearanceInitialAlt { get; set; }
    public string? ClearanceContactInfo { get; set; }
    public string? ClearanceLocalInfo { get; set; }
    public string? ClearanceDepFreq { get; set; }

    // Conflict alert
    public bool IsCaInhibited { get; set; }

    // Sequence state
    public int? SequenceNumber { get; set; }
    public string? FollowTarget { get; set; }

    public HashSet<string> GetProgrammedFixes(IApproachLookup? approachLookup, FixDatabase? fixDb = null)
    {
        IReadOnlyList<string>? activeApproachFixNames = null;
        if (Phases?.ActiveApproach?.Procedure is { } activeProc)
        {
            activeApproachFixNames = ApproachCommandHandler.GetApproachFixNames(activeProc);
        }

        return ProgrammedFixResolver.Resolve(Route, ExpectedApproach, Destination, Departure, approachLookup, activeApproachFixNames, fixDb);
    }
}
