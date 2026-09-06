using System.Text.Json.Serialization;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation;

[JsonDerivedType(typeof(RecordedCommand), "Command")]
[JsonDerivedType(typeof(RecordedAmendFlightPlan), "AmendFlightPlan")]
[JsonDerivedType(typeof(RecordedRequestNewBeaconCode), "RequestNewBeaconCode")]
[JsonDerivedType(typeof(RecordedWeatherChange), "WeatherChange")]
[JsonDerivedType(typeof(RecordedSettingChange), "SettingChange")]
[JsonDerivedType(typeof(RecordedAsdexMutation), "AsdexMutation")]
[JsonDerivedType(typeof(RecordedSaidMutation), "SaidMutation")]
[JsonDerivedType(typeof(RecordedArrivalGeneratorsChange), "ArrivalGeneratorsChange")]
[JsonDerivedType(typeof(RecordedAircraftSpawn), "AircraftSpawn")]
[JsonDerivedType(typeof(RecordedChat), "Chat")]
[JsonDerivedType(typeof(RecordedLiveTrafficSample), "LiveTrafficSample")]
[JsonDerivedType(typeof(RecordedLiveTrafficRemoval), "LiveTrafficRemoval")]
[JsonDerivedType(typeof(RecordedLiveTrafficStatus), "LiveTrafficStatus")]
[JsonDerivedType(typeof(RecordedStarsSharedStateChange), "StarsSharedStateChange")]
[JsonDerivedType(typeof(RecordedClearanceChange), "ClearanceChange")]
[JsonDerivedType(typeof(RecordedHoldAnnotationChange), "HoldAnnotationChange")]
[JsonDerivedType(typeof(RecordedEramEntry), "EramEntry")]
[JsonDerivedType(typeof(RecordedEramCrrGroup), "EramCrrGroup")]
public abstract record RecordedAction(double ElapsedSeconds);

public sealed record RecordedCommand(double ElapsedSeconds, string Callsign, string Command, string Initials, string ConnectionId)
    : RecordedAction(ElapsedSeconds)
{
    /// <summary>
    /// Final pilot-reaction delay in seconds applied to this command at the original live run, or null
    /// if no command-run delay was active. Baked in so replays reproduce the exact delay rather than
    /// re-sampling — re-sampling on replay would draw from a divergent RNG state and break determinism.
    /// </summary>
    public double? ReactionDelaySeconds { get; init; }

    /// <summary>
    /// Airborne spawn jitter in seconds drawn for an immediate single <c>REL</c> of a held
    /// runway/airborne departure, or null for ground releases, auto-spaced (queued) releases, and
    /// non-<c>REL</c> commands. Baked in so replays reproduce the exact spawn time rather than
    /// re-sampling — re-sampling on replay would draw from a divergent RNG state and break determinism.
    /// </summary>
    public int? SpawnJitterSeconds { get; init; }

    /// <summary>
    /// The aircraft an <c>ADD</c> generated at the live run (the generator draws the shared RNG and the beacon pool),
    /// so a replay puts the same aircraft into the world without drawing. Null for every other command and for
    /// recordings written before the field existed — those replay by drawing at the same point in the RNG sequence.
    /// </summary>
    public AircraftSnapshotDto? SpawnedAircraft { get; init; }

    /// <summary>The wall clock a <c>CFR</c> window was anchored to when issued, so a replay anchors to the same instant. Null otherwise.</summary>
    public DateTime? IssuedAtUtc { get; init; }

    /// <summary>
    /// Whether the command was accepted when issued. A replay that reaches a different verdict logs a replay-fidelity
    /// warning. Null on recordings written before rejections were recorded — every such command was accepted.
    /// </summary>
    public bool? Accepted { get; init; }
}

/// <summary>
/// A controller/RPO chat message sent to the training room. Chat has no simulation-state effect,
/// so replay/reconstruction ignores it; it is recorded so exported bundles carry the chat log and
/// forward tape-playback can re-surface it in the terminal.
/// </summary>
public sealed record RecordedChat(double ElapsedSeconds, string Initials, string Message) : RecordedAction(ElapsedSeconds);

public sealed record RecordedAmendFlightPlan(double ElapsedSeconds, string Callsign, FlightPlanAmendment Amendment) : RecordedAction(ElapsedSeconds);

/// <summary>
/// A controller "recycle beacon code" request (CRC Flight Plan Editor button, the YAAT training-hub
/// <c>RequestNewBeaconCode</c>, or a bare ERAM <c>QB</c>). Replay re-runs the pool release+draw so the
/// recycled code is reproduced deterministically on rewind. The assigner fields carry the acting ERAM
/// sector when the request came from an ERAM position (CRC's CODE view auto-lists codes it assigned);
/// recordings written before these fields deserialize them as null.
/// </summary>
public sealed record RecordedRequestNewBeaconCode(double ElapsedSeconds, string Callsign, string? AssignedByFacilityId, string? AssignedBySectorId)
    : RecordedAction(ElapsedSeconds);

/// <summary>
/// A weather load (<see cref="WeatherJson"/> non-null) or clear (<see cref="WeatherJson"/> null).
/// <see cref="ReconstructMetars"/> records whether dynamic METAR re-issuance was intended for this
/// load (true for file/API weather, false for live-fetched weather); replay uses it to restore the
/// re-issuer after returning to live. Recordings written before this field deserialize it as false.
/// </summary>
public sealed record RecordedWeatherChange(double ElapsedSeconds, string? WeatherJson, bool ReconstructMetars) : RecordedAction(ElapsedSeconds);

public sealed record RecordedSettingChange(double ElapsedSeconds, string Setting, string? Value) : RecordedAction(ElapsedSeconds);

public sealed record RecordedArrivalGeneratorsChange(double ElapsedSeconds, string GeneratorsJson) : RecordedAction(ElapsedSeconds);

public sealed record RecordedAircraftSpawn(double ElapsedSeconds, AircraftSnapshotDto Aircraft) : RecordedAction(ElapsedSeconds)
{
    public bool IsSynthetic { get; init; }
}

/// <summary>
/// One live-traffic sample applied to a shadow aircraft. Applied <b>pre-tick</b> — before the physics of
/// the second it was recorded in, like <see cref="RecordedAircraftSpawn"/> — because live samples land
/// in pre-physics; applying them after the second would put every replayed second one sample behind.
/// <see cref="SpawnState"/> is present only on the sample that created the shadow (it already embeds
/// the sample); later samples carry null.
/// </summary>
public sealed record RecordedLiveTrafficSample(double ElapsedSeconds, string Callsign, LiveTrafficSample Sample, AircraftSnapshotDto? SpawnState)
    : RecordedAction(ElapsedSeconds);

/// <summary>A shadow aircraft leaving the room (feed drop, staleness, scope, delete). Applied after the second like other actions.</summary>
public sealed record RecordedLiveTrafficRemoval(double ElapsedSeconds, string Callsign, LiveTrafficRemovalReason Reason)
    : RecordedAction(ElapsedSeconds);

/// <summary>
/// The room's live-traffic feed status at the moment it was broadcast (connection, message age, in-scope count) plus the
/// wall clock it was taken at. Diagnostic only: replay ignores it; bug bundles use the series to show feed health over the
/// session and to map sim seconds to the real-world window the raw feed logs are sliced by.
/// </summary>
public sealed record RecordedLiveTrafficStatus(
    double ElapsedSeconds,
    DateTimeOffset WallUtc,
    bool FeedConfigured,
    bool Connected,
    double? LastMessageAgeSeconds,
    int TracksInScope
) : RecordedAction(ElapsedSeconds);

/// <summary>
/// CRC-sourced ASDE-X mutation. <see cref="Kind"/> is one of <c>EditDbFields</c>, <c>Tag</c>,
/// <c>Terminate</c>, <c>Suspend</c>, <c>Unsuspend</c>, <c>InhibitAlerts</c>, <c>EnableAllAlerts</c>.
/// All mutations target server-side <c>AsdexRoomState</c>; the sim ignores them during replay.
/// </summary>
public sealed record RecordedAsdexMutation(
    double ElapsedSeconds,
    string Kind,
    string? AircraftId,
    string? Callsign,
    string? BeaconCode,
    string? Category,
    string? AircraftType,
    string? Fix,
    string? Scratchpad1,
    string? Scratchpad2
) : RecordedAction(ElapsedSeconds);

/// <summary>
/// CRC-sourced SAAB SAID mutation. <see cref="Kind"/> is one of <c>EditDbFields</c>, <c>Tag</c>,
/// <c>Terminate</c>, <c>Suspend</c>, <c>Unsuspend</c> (SAID has no alerts, so no Inhibit/EnableAll).
/// All mutations target server-side <c>SaidRoomState</c> + per-aircraft SAID state; the sim ignores
/// them during replay.
/// </summary>
public sealed record RecordedSaidMutation(
    double ElapsedSeconds,
    string Kind,
    string? AircraftId,
    string? Callsign,
    string? BeaconCode,
    string? Category,
    string? AircraftType,
    string? Fix,
    string? Scratchpad1,
    string? Scratchpad2
) : RecordedAction(ElapsedSeconds);

/// <summary>
/// A CRC position's per-TCP shared display state on a track (<c>UpdateStarsSharedTrackState</c>: forced FDB, highlight,
/// leader direction, query, TPA, the recently-accepted-pointout flag), keyed by the position's TCP id. Applied by
/// <see cref="Commands.TrackEngine.ApplySharedState"/>, which also drops the completed point-out the recipient's
/// slew-to-clear dismisses.
/// </summary>
public sealed record RecordedStarsSharedStateChange(double ElapsedSeconds, string Callsign, string TcpId, SharedStateDto State)
    : RecordedAction(ElapsedSeconds);

/// <summary>The departure clearance the CRC flight-plan editor sent (<c>SendClearance</c>); replaces the aircraft's clearance whole.</summary>
public sealed record RecordedClearanceChange(double ElapsedSeconds, string Callsign, AircraftClearanceDto Clearance) : RecordedAction(ElapsedSeconds);

/// <summary>A CRC hold annotation set (<c>SetHoldAnnotations</c>) or, with a null annotation, deleted (<c>DeleteHoldAnnotations</c>).</summary>
public sealed record RecordedHoldAnnotationChange(double ElapsedSeconds, string Callsign, AircraftHoldAnnotationDto? HoldAnnotation)
    : RecordedAction(ElapsedSeconds);

/// <summary>
/// An ERAM keyboard entry that wrote per-track ERAM state, in the grammar of <see cref="Commands.EramEntryEngine"/>
/// (<c>TRACK [/OK]</c>, <c>FREEZE {lat} {lon}</c>, <c>QQ …</c>, <c>QR {alt}</c>, <c>QS …</c>, <c>LF [{label}]</c>).
/// <see cref="IdentityCode"/> is the acting position's <c>AS</c> code for the entries that act as a position
/// (<c>TRACK</c>); the router resolves it through <see cref="Commands.TrackResolver.ResolveTcpToOwner"/> on apply.
/// </summary>
public sealed record RecordedEramEntry(double ElapsedSeconds, string Callsign, string Entry, string? IdentityCode) : RecordedAction(ElapsedSeconds);

/// <summary>
/// A Continuous Range Readout group created, replaced or recolored (<c>LF</c> with a location, <c>SetEramCrrGroupColor</c>),
/// or with a null <see cref="Lat"/> deleted (<c>ClearOrDeleteEramCrrGroup</c>). The group is room state — a host slot
/// applies it — while membership rides each aircraft's <c>LF</c> entries.
/// </summary>
public sealed record RecordedEramCrrGroup(double ElapsedSeconds, string Label, string? Color, double? Lat, double? Lon)
    : RecordedAction(ElapsedSeconds);

public record FlightPlanAmendment(
    string? AircraftType = null,
    string? EquipmentSuffix = null,
    string? Departure = null,
    string? Destination = null,
    int? CruiseSpeed = null,
    PlannedAltitude? Altitude = null,
    string? FlightRules = null,
    string? Route = null,
    string? Remarks = null,
    string? Scratchpad1 = null,
    string? Scratchpad2 = null,
    uint? BeaconCode = null,
    string? BeaconAssignedByFacilityId = null,
    string? BeaconAssignedBySectorId = null,
    string? IcaoEquipmentCodes = null
);
