namespace Yaat.Sim;

public class AircraftState
{
    public required string Callsign { get; set; }
    public required string AircraftType { get; set; }
    public string? ScenarioId { get; set; }
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
    public ControlTargets Targets { get; } = new();
    public CommandQueue Queue { get; set; } = new();
}
