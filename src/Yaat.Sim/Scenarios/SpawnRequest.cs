namespace Yaat.Sim.Scenarios;

public enum FlightRulesKind { Ifr, Vfr }

public enum WeightClass { Small, Large, Heavy }

public enum EngineKind { Piston, Turboprop, Jet }

public enum SpawnPositionType { Bearing, Runway, OnFinal }

public sealed class SpawnRequest
{
    public required FlightRulesKind Rules { get; init; }
    public required WeightClass Weight { get; init; }
    public required EngineKind Engine { get; init; }
    public required SpawnPositionType PositionType { get; init; }

    // Bearing variant
    public double Bearing { get; init; }
    public double DistanceNm { get; init; }
    public double Altitude { get; init; }

    // Runway variant
    public string RunwayId { get; init; } = "";

    // OnFinal variant
    public double FinalDistanceNm { get; init; }

    // Optional overrides
    public string? ExplicitType { get; init; }
    public string? ExplicitAirline { get; init; }
}
