using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Sim.Simulation.Spine;

/// <summary>
/// Where the drained state changes go — the strip and TDLS change sets, and the coordination dirty flag. Declared on
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
    /// The coordination lists changed. Payload-less: the host re-pushes the whole
    /// <c>StarsCoordination</c> topic, which is what the wire carries anyway. Same suppression rule as
    /// <see cref="OnStripsChanged"/> — a reconstruction stays silent and the room re-syncs when it lands.
    /// </summary>
    void OnCoordinationChanged();
}
