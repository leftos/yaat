using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>
/// One controller action for the <see cref="ActionRouter"/>: who issued what to whom, and — when the action comes from
/// a recording — the values the live run drew from a clock or a live-only random stream (<see cref="Baked"/>). A fresh
/// action carries <c>Baked = null</c> and the arm that draws bakes its draw into the record it returns; an action
/// applied from a record uses the baked value and draws nothing, so every run kind reproduces the live outcome.
/// </summary>
public sealed record ActionInput(string Callsign, string Command, string ConnectionId, string Initials, BakedDraws? Baked)
{
    /// <summary>
    /// Whether the host decided, when the command was issued, that its issuer is the solo-training student; null when
    /// no host decided it (the standalone engine, an AI or scripted dispatch, a CRC wire command). Only the not-heard
    /// rule reads it, and a fresh action's value is recorded so a replay reads the same verdict from
    /// <see cref="BakedDraws.IssuedByStudent"/> instead of deciding again.
    /// </summary>
    public bool? IssuedByStudent { get; init; }
}

/// <summary>
/// The values a live run decided while applying a command, stored on the <see cref="RecordedCommand"/> so no other run
/// has to decide them: the pilot-reaction delay, the airborne spawn jitter of an immediate <c>REL</c>, the aircraft an
/// <c>ADD</c> generated, the wall clock a <c>CFR</c> window was anchored to, the strip id a creating strip verb minted,
/// and whether the issuer was the student. Each is null when the command did not draw it.
/// </summary>
/// <param name="StripId">
/// The id a strip verb that mints one — <c>SEP</c>, <c>HSC</c>, <c>SCAN</c>, <c>BLANK</c> — drew live; reused on replay so a later
/// command addressing it resolves.
/// </param>
/// <param name="IssuedByStudent">
/// The host's issue-time verdict that the issuer was the solo-training student (<see cref="ActionInput.IssuedByStudent"/>);
/// null on a recording written before the verdict was recorded, which replays as heard.
/// </param>
public sealed record BakedDraws(
    double? ReactionDelaySeconds,
    int? SpawnJitterSeconds,
    AircraftSnapshotDto? SpawnedAircraft,
    DateTime? IssuedAtUtc,
    string? StripId,
    bool? IssuedByStudent
)
{
    public static BakedDraws Of(RecordedCommand record) =>
        new(
            record.ReactionDelaySeconds,
            record.SpawnJitterSeconds,
            record.SpawnedAircraft,
            record.IssuedAtUtc,
            record.StripId,
            record.IssuedByStudent
        );
}
