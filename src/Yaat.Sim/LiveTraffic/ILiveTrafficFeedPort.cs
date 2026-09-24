using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.LiveTraffic;

/// <summary>
/// The external live-traffic feed as <see cref="SimulationEngine.TickLiveTrafficSync"/> sees it: what is in the room's
/// scope this second, already placed on sim time, and where each existing shadow stands in the feed. The host supplies
/// it (the live server reads its SWIM store or a DVR replay of it; every other host supplies
/// <see cref="EmptyLiveTrafficFeedPort"/>). The engine owns every decision the answers feed — spawn vs refresh, the
/// collision with a simulated aircraft, the filter gate and the removal tiers — so the port answers facts only: which
/// tracks, which samples, whether the feed ended a track, whether it is outside the scope. Wall-clock time never
/// crosses it: the anchor that turns a feed timestamp into a sim second is the host's.
/// </summary>
public interface ILiveTrafficFeedPort
{
    /// <summary>
    /// Opens the second: the tracks delivered in scope, in the order the step applies them, and whether every shadow is
    /// cleared first. Called once per second, before any other member.
    /// </summary>
    LiveTrafficFeedSecond BeginSecond();

    /// <summary>
    /// Where the shadow <paramref name="callsign"/> stands in the feed this second. Asked only after
    /// <see cref="BeginSecond"/> answered <see cref="LiveTrafficFeedSecond.Syncs"/>, and only for a shadow silent for
    /// at least two seconds.
    /// </summary>
    LiveTrafficShadowStatus ShadowStatus(string callsign);

    /// <summary>
    /// Closes a second <see cref="BeginSecond"/> opened with <see cref="LiveTrafficFeedSecond.Syncs"/> (the DVR replay
    /// advances here).
    /// </summary>
    void EndSecond();
}

/// <summary>
/// One second of the feed. <paramref name="Syncs"/> false is an inert second — nothing is applied or aged out, and
/// <see cref="ILiveTrafficFeedPort.EndSecond"/> is not called. <paramref name="ClearShadows"/>, when set, removes every
/// shadow with that reason before anything else: <see cref="LiveTrafficRemovalReason.Disabled"/> when live traffic was
/// turned off, <see cref="LiveTrafficRemovalReason.Reanchored"/> when the room rejoins the feed after a gap (the gap is a
/// wall-clock length, so the host keeps it for its own notice).
/// </summary>
public sealed record LiveTrafficFeedSecond(bool Syncs, LiveTrafficRemovalReason? ClearShadows, IReadOnlyList<LiveTrafficFeedTrack> Tracks)
{
    /// <summary>Nothing to do this second.</summary>
    public static LiveTrafficFeedSecond Inert { get; } = new(false, null, []);
}

/// <summary>
/// A track the feed delivered in scope this second. <paramref name="Sample"/> is already on sim time.
/// <paramref name="MatchesFilter"/> is the room's live-traffic filter's verdict on it. <paramref name="SpawnState"/> builds
/// the shadow's initial state, and is called only for a track that spawns one.
/// </summary>
public sealed record LiveTrafficFeedTrack(string Callsign, LiveTrafficSample Sample, bool MatchesFilter, Func<AircraftSnapshotDto> SpawnState);

/// <summary>Where an existing shadow stands in the feed, in the order the removal tiers read it.</summary>
public enum LiveTrafficShadowStatus
{
    /// <summary>The feed holds no row for the callsign (a fresh DVR store, or a reaped row): only the silence backstop applies.</summary>
    Absent,

    /// <summary>The feed ended the track (TAIS terminated/drop, SFDPS dropped/completed, a correlator reap): removed promptly.</summary>
    Ended,

    /// <summary>The room's filter excludes the track: removed promptly.</summary>
    FilteredOut,

    /// <summary>Still delivered, but outside the room's scope: removed once the shadow is silent past the out-of-scope window.</summary>
    OutOfScope,

    /// <summary>In scope, or no longer delivered: only the silence backstop applies.</summary>
    Present,
}

/// <summary>The feed of a host with none: every second is inert.</summary>
public sealed class EmptyLiveTrafficFeedPort : ILiveTrafficFeedPort
{
    public static EmptyLiveTrafficFeedPort Instance { get; } = new();

    private EmptyLiveTrafficFeedPort() { }

    public LiveTrafficFeedSecond BeginSecond() => LiveTrafficFeedSecond.Inert;

    public LiveTrafficShadowStatus ShadowStatus(string callsign) =>
        throw new InvalidOperationException($"The empty live-traffic feed never syncs, so no shadow status is asked of it (asked for {callsign})");

    public void EndSecond() => throw new InvalidOperationException("The empty live-traffic feed never syncs, so no second is ended on it");
}
