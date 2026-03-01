using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim;

public class AircraftState
{
    public required string Callsign { get; set; }
    public required string AircraftType { get; set; }
    public string? ScenarioId { get; set; }
    public string Cid { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Heading { get; set; }
    public double Altitude { get; set; }
    public double GroundSpeed { get; set; }
    public double VerticalSpeed { get; set; }
    public uint BeaconCode { get; set; }
    public string Departure { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Route { get; set; } = "";
    public string EquipmentSuffix { get; set; } = "/L";
    public string FlightRules { get; set; } = "IFR";
    public int CruiseAltitude { get; set; }
    public int CruiseSpeed { get; set; }
    public string TransponderMode { get; set; } = "C";
    public bool IsIdenting { get; set; }
    public bool IsOnGround { get; set; }
    public ControlTargets Targets { get; } = new();
    public CommandQueue Queue { get; set; } = new();
    public PhaseList? Phases { get; set; }
    public List<string> PendingWarnings { get; } = [];

    // Ground operations state
    public TaxiRoute? AssignedTaxiRoute { get; set; }
    public string? ParkingSpot { get; set; }
    public string? CurrentTaxiway { get; set; }
    public bool IsHeld { get; set; }

    // Track operations state
    public TrackOwner? Owner { get; set; }
    public TrackOwner? HandoffPeer { get; set; }
    public TrackOwner? HandoffRedirectedBy { get; set; }
    public StarsPointout? Pointout { get; set; }
    public string? Scratchpad1 { get; set; }
    public string? Scratchpad2 { get; set; }
    public int? TemporaryAltitude { get; set; }
    public bool IsAnnotated { get; set; }
    public bool FrequencyChangeApproved { get; set; }
    public string? ContactPosition { get; set; }
    public bool OnHandoff { get; set; }
    public DateTime? HandoffInitiatedAt { get; set; }
    public int? AssignedAltitude { get; set; }
}
