namespace Yaat.Sim.Phases;

/// <summary>
/// Captures approach quality metrics at localizer establishment and landing.
/// Created in FinalApproachPhase when the aircraft is established on the localizer;
/// stamped with landing time in PhaseRunner when the landing phase completes.
/// </summary>
public sealed class ApproachScore
{
    // Identity
    public required string Callsign { get; init; }
    public required string AircraftType { get; init; }
    public required string ApproachId { get; init; }
    public required string RunwayId { get; init; }
    public required string AirportCode { get; init; }

    // Intercept metrics
    public double InterceptAngleDeg { get; set; }
    public double InterceptDistanceNm { get; set; }
    public double MinInterceptDistanceNm { get; set; }
    public double GlideSlopeDeviationFt { get; set; }
    public double SpeedAtInterceptKts { get; set; }
    public bool WasForced { get; set; }
    public bool IsPatternTraffic { get; set; }

    // TBL 5-9-1 legality
    public double MaxAllowedAngleDeg { get; set; }
    public bool IsInterceptAngleLegal { get; set; }
    public bool IsInterceptDistanceLegal { get; set; }

    // Timestamps (scenario elapsed seconds)
    public double EstablishedAtSeconds { get; set; }
    public double? LandedAtSeconds { get; set; }

    // Position at establishment (for separation computation)
    public double EstablishedLat { get; set; }
    public double EstablishedLon { get; set; }
}
