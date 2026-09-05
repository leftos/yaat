using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>
/// One controller action for the <see cref="ActionRouter"/>: who issued what to whom, and — when the action comes from
/// a recording — the values the live run drew from a clock or a live-only random stream (<see cref="Baked"/>). A fresh
/// action carries <c>Baked = null</c> and the arm that draws bakes its draw into the record it returns; an action
/// applied from a record uses the baked value and draws nothing, so every run kind reproduces the live outcome.
/// </summary>
public sealed record ActionInput(string Callsign, string Command, string ConnectionId, string Initials, BakedDraws? Baked);

/// <summary>
/// The draws a live run made while applying a command, stored on the <see cref="RecordedCommand"/> so no other run has
/// to make them: the pilot-reaction delay, the airborne spawn jitter of an immediate <c>REL</c>, the aircraft an
/// <c>ADD</c> generated, and the wall clock a <c>CFR</c> window was anchored to. Each is null when the command did not draw it.
/// </summary>
public sealed record BakedDraws(double? ReactionDelaySeconds, int? SpawnJitterSeconds, AircraftSnapshotDto? SpawnedAircraft, DateTime? IssuedAtUtc)
{
    public static BakedDraws Of(RecordedCommand record) =>
        new(record.ReactionDelaySeconds, record.SpawnJitterSeconds, record.SpawnedAircraft, record.IssuedAtUtc);
}
