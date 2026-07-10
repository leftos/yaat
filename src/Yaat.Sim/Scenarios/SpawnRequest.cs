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
    Helicopter,
}

public enum SpawnPositionType
{
    Bearing,
    Runway,
    OnFinal,
    AtFix,
    Parking,
    OnStar,
}

public sealed class SpawnRequest
{
    public required FlightRulesKind Rules { get; init; }
    public required WeightClass Weight { get; init; }
    public required EngineKind Engine { get; init; }
    public required SpawnPositionType PositionType { get; init; }

    // Bearing variant. Bearing is TRUE degrees from the airport; callers holding a magnetic bearing
    // convert with MagneticDeclination.MagneticToTrue first.
    public double Bearing { get; init; }
    public double DistanceNm { get; init; }
    public double Altitude { get; init; }

    // VFR-generator variant. Null (the ADD-command default) spawns a VFR aircraft as a cold call:
    // no filed plan, no assigned code, squawking 1200. Non-null files a VFR plan to this destination and
    // draws a discrete code from the facility's VFR beacon bank, matching how scenarios hand-author VFR
    // arrivals — and letting the server's auto-delete-on-landing recognise the aircraft as an arrival.
    public string? VfrFiledDestination { get; init; }

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

    // OnStar variant — spawn an IFR arrival established on a STAR.
    // Entry waypoint (spawn position) and STAR id as typed; resolved against NavigationDatabase
    // in the generator. StarRunway is the optional runway-transition designator.
    public string StarEntryFix { get; init; } = "";
    public string StarId { get; init; } = "";
    public string? StarRunway { get; init; }

    // True (default) = descend via the STAR from spawn; false (LVL keyword) = hold level.
    public bool DescendVia { get; init; }

    // Null = compute a default establishment altitude from the STAR constraints / flying miles.
    public double? StarAltitude { get; init; }

    // Null = use the STAR-published speed at the join fix, else the category default.
    public double? StarSpeedKts { get; init; }

    // Destination airport for multi-airport STARs. Null = primary scenario airport.
    public string? DestinationAirportId { get; init; }

    // Optional overrides
    public string? ExplicitType { get; init; }
    public string? ExplicitAirline { get; init; }

    // Airport whose recent/route airline cache should constrain generated IFR callsigns.
    public string? PreferredAirlineAirportId { get; init; }
}
