using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim;

public class AircraftState
{
    public required string Callsign { get; set; }
    public required string AircraftType { get; set; }
    public string BaseAircraftType => AircraftType.Contains('/') ? AircraftType.Split('/')[0] : AircraftType;
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

    // Visual approach state
    public bool HasReportedFieldInSight { get; set; }
    public bool HasReportedTrafficInSight { get; set; }
    public string? FollowingCallsign { get; set; }

    // Sequence state
    public int? SequenceNumber { get; set; }
    public string? FollowTarget { get; set; }

    public HashSet<string> GetProgrammedFixes(IApproachLookup? approachLookup)
    {
        var fixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Route fixes: split on spaces, strip airway suffixes (e.g., ".V25")
        if (!string.IsNullOrEmpty(Route))
        {
            foreach (var token in Route.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var dotIndex = token.IndexOf('.');
                var fixName = dotIndex >= 0 ? token[..dotIndex] : token;
                if (!string.IsNullOrEmpty(fixName))
                {
                    fixes.Add(fixName);
                }
            }
        }

        // Expected approach fix names
        if (!string.IsNullOrEmpty(ExpectedApproach) && approachLookup is not null)
        {
            string airport = !string.IsNullOrEmpty(Destination) ? Destination : Departure;
            if (!string.IsNullOrEmpty(airport))
            {
                var procedure = approachLookup.GetApproach(airport, ExpectedApproach);
                if (procedure is not null)
                {
                    foreach (var name in ApproachCommandHandler.GetApproachFixNames(procedure))
                    {
                        fixes.Add(name);
                    }
                }
            }
        }

        // Active approach fix names
        if (Phases?.ActiveApproach?.Procedure is { } activeProc)
        {
            foreach (var name in ApproachCommandHandler.GetApproachFixNames(activeProc))
            {
                fixes.Add(name);
            }
        }

        return fixes;
    }
}
