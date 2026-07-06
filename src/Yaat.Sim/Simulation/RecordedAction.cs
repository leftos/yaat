using System.Text.Json.Serialization;
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
}

/// <summary>
/// A controller/RPO chat message sent to the training room. Chat has no simulation-state effect,
/// so replay/reconstruction ignores it; it is recorded so exported bundles carry the chat log and
/// forward tape-playback can re-surface it in the terminal.
/// </summary>
public sealed record RecordedChat(double ElapsedSeconds, string Initials, string Message) : RecordedAction(ElapsedSeconds);

public sealed record RecordedAmendFlightPlan(double ElapsedSeconds, string Callsign, FlightPlanAmendment Amendment) : RecordedAction(ElapsedSeconds);

/// <summary>
/// A controller "recycle beacon code" request (CRC Flight Plan Editor button or the YAAT
/// training-hub <c>RequestNewBeaconCode</c>). Replay re-runs the pool release+draw so the
/// recycled code is reproduced deterministically on rewind.
/// </summary>
public sealed record RecordedRequestNewBeaconCode(double ElapsedSeconds, string Callsign) : RecordedAction(ElapsedSeconds);

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
    uint? BeaconCode = null
);
