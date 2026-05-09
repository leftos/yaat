using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

public enum WakeDirectiveEffect
{
    SuppressWakeInterval,
    RequireWakeAdvisory,
    SuppressWakeAdvisory,
}

public enum WakeDirectiveOperation
{
    Any,
    DepartureBehindDeparture,
    DepartureBehindLanding,
    ArrivalBehindDeparture,
    ArrivalBehindLanding,
    ApproachBehindArrival,
}

public enum WakeDirectiveRelation
{
    Any,
    SameRunway,
    CloseParallel,
    Intersecting,
    ProjectedConverging,
    OppositeDirection,
}

public sealed record WakeDirectiveContext(
    string SourceEventId,
    string? ArtccId,
    string? AirportId,
    string PrecedingRunwayId,
    string SucceedingRunwayId,
    string PrecedingCallsign,
    string SucceedingCallsign,
    WakeDirectiveOperation Operation,
    WakeDirectiveRelation Relation,
    char PrecedingCwt,
    char SucceedingCwt,
    string SourceRuleReference
);

public sealed class WakeDirectiveRule
{
    [JsonIgnore]
    public string ArtccId { get; set; } = "";

    public string Id { get; set; } = "";

    public string? AirportId { get; set; }

    public List<string> Runways { get; set; } = [];

    public WakeDirectiveOperation Operation { get; set; } = WakeDirectiveOperation.Any;

    public WakeDirectiveRelation Relation { get; set; } = WakeDirectiveRelation.Any;

    public List<char> PrecedingCwt { get; set; } = [];

    public List<char> SucceedingCwt { get; set; } = [];

    public List<string> SourceRuleReferences { get; set; } = [];

    public List<WakeDirectiveEffect> Effects { get; set; } = [];

    public string? RuleReference { get; set; }

    public string? Notes { get; set; }
}
