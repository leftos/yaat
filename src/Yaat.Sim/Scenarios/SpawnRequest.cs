namespace Yaat.Sim.Scenarios;

public enum FlightRulesKind
{
    Ifr,
    Vfr,
}

public enum WeightClass
{
    Small,
    SmallPlus,
    Large,
    Heavy,
}

public enum EngineKind
{
    Piston,
    Turboprop,
    Jet,
}

public enum SpawnPositionType
{
    Bearing,
    Runway,
    OnFinal,
    AtFix,
    Parking,
}

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

    // Optional filed route for a departure lined up on a runway (e.g. "NIMI6 OAK SAU").
    // Empty for a bare runway spawn; set so a subsequent CTO flies the filed SID.
    public string Route { get; init; } = "";

    // OnFinal variant
    public double FinalDistanceNm { get; init; }

    // AtFix variant
    public string FixId { get; init; } = "";

    // Parking variant
    public string ParkingName { get; init; } = "";

    // Optional overrides
    public string? ExplicitType { get; init; }
    public string? ExplicitAirline { get; init; }

    // Airport whose recent/route airline cache should constrain generated IFR callsigns.
    public string? PreferredAirlineAirportId { get; init; }
}
