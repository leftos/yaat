using System.Text.Json.Serialization;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>A <see cref="TugRowAnchor"/>: where a tow began, with the nose it had there, and its first move's kind.</summary>
public sealed class TugRowAnchorDto
{
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required double NoseTrueDeg { get; init; }
    public required PushbackLegKind FirstKind { get; init; }
}

/// <summary>A <see cref="PresetTaxiStop"/>: the stop a spawn's timed TAXI preset ends at.</summary>
public sealed class PresetTaxiStopDto
{
    public required PresetTaxiStopKind Kind { get; init; }
    public required string Name { get; init; }
    public string? OnTaxiway { get; init; }
}

/// <summary>A <see cref="TaxiTrailEdge"/>: one straight taxi edge an aircraft drove, by its end node ids, with its length.</summary>
public sealed class TaxiTrailEdgeDto
{
    public required int NodeA { get; init; }
    public required int NodeB { get; init; }
    public required double LengthFt { get; init; }
}

public sealed class AircraftGroundOpsDto
{
    public string? LayoutAirportId { get; init; }
    public TaxiRouteDto? AssignedTaxiRoute { get; init; }
    public string? ParkingSpot { get; init; }
    public string? CurrentTaxiway { get; init; }
    public required bool IsHeld { get; init; }
    public string? GiveWayTarget { get; init; }
    public required bool AutoDeleteExempt { get; init; }
    public bool PendingAutoDelete { get; init; }

    /// <summary>A controller's explicit <c>NODEL</c>; optional — earlier snapshots restore false.</summary>
    public bool NoDeleteRequested { get; init; }

    /// <summary>A forced tow (<c>PUSHF</c>/<c>PUSHMF</c>) under way ignores parked aircraft; optional — earlier snapshots restore false.</summary>
    public bool ForcedTowIgnoresParked { get; init; }

    /// <summary>
    /// Where the tow under way began and its first move's kind, the row anchor of its outline floor
    /// (<see cref="TugRowAnchor"/>); null when no tow is under way. Optional — earlier snapshots restore null, which
    /// anchors the floor to each move's start alone.
    /// </summary>
    public TugRowAnchorDto? TowRowAnchor { get; init; }
    public required double ConflictBreakRemainingSeconds { get; init; }
    public double? SpeedLimit { get; init; }

    /// <summary>Callsign this aircraft is auto-yielding to (drives the "→{target} (auto)" badge); null when not auto-yielding.</summary>
    public string? AutoYieldTarget { get; init; }

    /// <summary>True when the auto-yield is a same-edge in-trail follow ("Following") rather than a converging give-way ("Yielding to").</summary>
    public bool AutoYieldIsFollowing { get; init; }

    /// <summary>1-based position in the departure line at this aircraft's destination-runway hold-short node, or 0 when not in a countable line.</summary>
    public int RunwayQueuePosition { get; init; }

    /// <summary>Display designator of the runway this aircraft is queued for (e.g. "28R"), empty when not in a line.</summary>
    public string RunwayQueueRunway { get; init; } = "";

    /// <summary>Taxiway name of the intersection this aircraft is departing from (e.g. "E"), empty for a full-length departure or when not in a line.</summary>
    public string RunwayQueueIntersection { get; init; } = "";
    public double? PushbackTrueHeadingDeg { get; init; }

    /// <summary>The direction from the nose gear out along the towbar to the tug, degrees true; null when no tug is attached.</summary>
    public double? TowbarTrueHeadingDeg { get; init; }
    public required bool HasAnnouncedReady { get; init; }
    public bool InitialCallupDecisionProcessed { get; init; }

    /// <summary>
    /// The aircraft's initial call-up plan. Settable so <see cref="SnapshotSchemaMigrator"/> can derive it for a snapshot
    /// written before V33 from <see cref="LegacyIsScriptedDeparture"/> and <see cref="InitialCallupDecisionProcessed"/>.
    /// </summary>
    public InitialCallupPlan InitialCallup { get; set; }

    /// <summary>The movement-area taxiway a coordinate ground spawn sits on, or null.</summary>
    public string? SpawnTaxiway { get; init; }

    /// <summary>The stand the aircraft's push started from, or null when it has not been pushed off a stand.</summary>
    public string? PushedBackFrom { get; init; }

    /// <summary>The spot the aircraft's last push ended on, or null when it did not end on a spot.</summary>
    public string? PushEndSpot { get; init; }

    /// <summary>The stop the spawn's timed TAXI preset ends at, for an after-taxi-arrival call; null otherwise.</summary>
    public PresetTaxiStopDto? PresetTaxiStop { get; init; }

    /// <summary>The cardinal a VFR departure names in its request to a delivery student, or null before it asks.</summary>
    public string? VfrDepartureDirection { get; init; }

    /// <summary>
    /// Before V33: the aircraft had a TAXI preset and so never made the stand call. Read only by
    /// <see cref="SnapshotSchemaMigrator"/> to derive <see cref="InitialCallup"/>, which then nulls it so a rewritten
    /// snapshot drops it; never written.
    /// </summary>
    [JsonPropertyName("IsScriptedDeparture")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyIsScriptedDeparture { get; set; }

    /// <summary>True while a landed aircraft still owes ground its taxi-in call.</summary>
    public bool AwaitingTaxiInCall { get; init; }

    /// <summary>True once the tower has sent the pilot to ground (CT to a ground position, or FCA).</summary>
    public bool ReleasedToGround { get; init; }
    public bool IsExpeditingTaxi { get; init; }

    /// <summary>Controller-commanded taxi-speed cap (kts), or null to use the category default.</summary>
    public double? CommandedTaxiSpeedKts { get; init; }
    public bool IsExpeditingExit { get; init; }

    /// <summary>True when a brisk "immediate"/"without delay" lineup has been requested (CTO IMM / LUAW WD).</summary>
    public bool IsExpeditingLineup { get; init; }

    /// <summary>Seconds the current GIVEWAY hold has been active (drives the safety-timeout auto-release).</summary>
    public double HoldElapsedSeconds { get; init; }

    /// <summary>Seconds this aircraft has been stopped on the ground (drives the GIVEWAY target-stationary fallback).</summary>
    public double StationarySeconds { get; init; }

    /// <summary>True when this IFR departure is held for release (held short of the runway until released).</summary>
    public bool HeldForRelease { get; init; }

    /// <summary>True when a held ground departure has been released and is awaiting its auto-issued takeoff clearance.</summary>
    public bool ReleasedForDeparture { get; init; }

    /// <summary>Scenario-elapsed seconds at which the departure was released (drives the auto-CTO jitter).</summary>
    public double ReleasedAtSeconds { get; init; }

    /// <summary>True when a held runway spawn was released through the hold-for-release spawn gate before it spawned.</summary>
    public bool ReleasedAtSpawnGate { get; init; }

    /// <summary>Absolute-UTC start of the Call-For-Release window, or null. Alert-only (GitHub issue #230).</summary>
    public DateTime? ReleaseWindowStartUtc { get; init; }

    /// <summary>Absolute-UTC end of the Call-For-Release window, or null.</summary>
    public DateTime? ReleaseWindowEndUtc { get; init; }

    /// <summary>
    /// The straight taxi edges the aircraft has driven, oldest first (<see cref="TaxiEdgeTrail"/>). Written only when the
    /// trail is not empty, so a snapshot with no trail is byte-identical to one written before the field existed; absent
    /// on read restores an empty trail.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TaxiTrailEdgeDto>? TaxiEdgeTrail { get; init; }
}
