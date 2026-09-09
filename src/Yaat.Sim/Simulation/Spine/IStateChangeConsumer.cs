using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Sim.Simulation.Spine;

/// <summary>
/// Where the drained state changes go — the strip and TDLS change sets, the coordination dirty flag the
/// channels and the tower lists share, the bookmark one, the session clock's and the ERAM CRR groups'. Declared on
/// its own because both halves of a host reach it: the action router holds the action view (<c>IActionHost</c>) and
/// the post-physics drain step the consumer view (<see cref="IHostConsumers"/>), and one implementation on a host
/// answers both.
/// </summary>
public interface IStateChangeConsumer
{
    /// <summary>
    /// What the strip bodies touched since the last drain: the items whose records changed and whether a full state is
    /// owed. The host broadcasts them unless it is suppressed; a reconstruction drops them and the room re-syncs
    /// afterwards.
    /// </summary>
    void OnStripsChanged(StripChangeSet changes);

    /// <summary>
    /// What the TDLS bodies touched since the last drain: the items whose records changed, the items removed, and
    /// whether a full state is owed. Same suppression rule as <see cref="OnStripsChanged"/>.
    /// </summary>
    void OnTdlsChanged(TdlsChangeSet changes);

    /// <summary>
    /// The coordination lists changed — a channel's release rundown, or a tower list the proximity step touched.
    /// Payload-less: the host re-pushes the whole <c>StarsCoordination</c> topic, which is what the wire carries
    /// anyway (every channel and every tower list in one payload). Same suppression rule as
    /// <see cref="OnStripsChanged"/> — a reconstruction stays silent and the room re-syncs when it lands.
    /// </summary>
    void OnCoordinationChanged();

    /// <summary>
    /// The shared timeline bookmarks changed — a <c>BM</c> verb, or one of the room's bookmark RPCs. Payload-less:
    /// the host re-sends the whole list, which is what the wire carries. Same suppression rule as
    /// <see cref="OnStripsChanged"/>.
    /// </summary>
    void OnBookmarksChanged();

    /// <summary>
    /// The session clock changed — <c>PAUSE</c>, <c>UNPAUSE</c> or <c>SIMRATE</c>, or the host's own pause and
    /// resume, which run the same bodies. Payload-less: the host re-sends the whole sim state. A host answering this
    /// needs no suppression check of its own, because the sim-state broadcast applies it. These are not the only
    /// writers of the clock: a host that pauses a room on its own — an unattended room, or a rewind — does so outside
    /// these bodies and answers for the broadcast itself.
    /// </summary>
    void OnSimStateChanged();

    /// <summary>
    /// The ERAM Continuous Range Readout groups changed — one was created, replaced, recolored or deleted.
    /// Payload-less: the host re-pushes the whole <c>EramCrrGroups</c> topic, which is what the wire carries anyway.
    /// Same suppression rule as <see cref="OnStripsChanged"/>. A deletion is the exception the topic's additive shape
    /// forces: re-pushing the remaining groups cannot unsay one, so the CRC handler that deletes it still sends the
    /// explicit <c>DeleteEramCrrGroups</c> itself.
    /// </summary>
    void OnEramCrrGroupsChanged();
}
