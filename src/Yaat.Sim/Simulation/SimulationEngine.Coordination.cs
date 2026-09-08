using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation;

// The STARS coordination half of the engine: the release-rundown timers spine step, the removal a radar acquisition
// owes, the ARTCC initialisation that builds the scenario's channels, and the dirty flag the drain turns into the
// host's one broadcast. The verb bodies live in Simulation/Coordination/CoordinationCommandHandler.cs. Everything here
// decides from engine state alone — the scenario's channels and its elapsed clock — so every run kind reaches the same
// list; what changed is drained to the host from DrainStateChangesInto.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// True when a coordination body has changed a channel since the last drain. Payload-less on purpose: the wire
    /// carries the whole <c>StarsCoordination</c> topic anyway, so what a host needs to know is only "re-push it".
    /// </summary>
    internal bool CoordinationChanged { get; private set; }

    /// <summary>Marks the coordination lists dirty; the next drain hands the host one <c>OnCoordinationChanged</c>.</summary>
    internal void MarkCoordinationChanged() => CoordinationChanged = true;

    /// <summary>
    /// Takes the flag and clears it, for the one path that mutates the engine outside both the action router's drain
    /// and the post-physics one: the server's scenario load, which builds the channels before either has run.
    /// </summary>
    internal bool DrainCoordinationChanged()
    {
        var changed = CoordinationChanged;
        CoordinationChanged = false;
        return changed;
    }

    /// <summary>
    /// The release-rundown clocks, run once per second on every run kind. An acknowledged message shows the departure
    /// expiration warning once its remaining life falls to
    /// <see cref="SimScenarioState.CoordinationExpiryWarningSeconds"/> and voids when it runs out; a recalled one
    /// leaves the list when its linger expires. Nothing here reads the wall clock, so a replay reaches each transition
    /// at the same elapsed second the live room did.
    /// </summary>
    public void TickCoordinationTimers()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        var now = scenario.ElapsedSeconds;
        bool changed = false;

        foreach (var channel in scenario.CoordinationChannels.Values)
        {
            for (int i = channel.Items.Count - 1; i >= 0; i--)
            {
                var item = channel.Items[i];
                changed |= TickAcknowledgedItem(item, now);

                if ((item.Status == StarsCoordinationStatus.Recalled) && item.ExpireTime.HasValue && (item.ExpireTime.Value <= now))
                {
                    channel.Items.RemoveAt(i);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            MarkCoordinationChanged();
        }
    }

    /// <summary>
    /// One acknowledged release's clock: it shows the departure-expiration warning once its remaining life falls to
    /// <see cref="SimScenarioState.CoordinationExpiryWarningSeconds"/>, and voids when the life runs out. True when the
    /// item's status changed. The recall linger is not here — that one removes the item, which only the loop that owns
    /// the list can do.
    /// </summary>
    private static bool TickAcknowledgedItem(CoordinationItem item, double now)
    {
        if (
            (item.Status is not StarsCoordinationStatus.Acknowledged and not StarsCoordinationStatus.DepartureExpirationWarning)
            || !item.ExpireTime.HasValue
        )
        {
            return false;
        }

        var remaining = item.ExpireTime.Value - now;
        if (remaining <= 0)
        {
            item.Status = StarsCoordinationStatus.VoidUnacknowledged;
            item.ExpireTime = null;
            return true;
        }

        if ((remaining <= SimScenarioState.CoordinationExpiryWarningSeconds) && (item.Status == StarsCoordinationStatus.Acknowledged))
        {
            item.Status = StarsCoordinationStatus.DepartureExpirationWarning;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Drops every coordination item for <paramref name="callsign"/>: once a controller owns the track, the release
    /// rundown that got it there is moot. Run by the <c>TRACK</c> arm on every run kind.
    /// </summary>
    public void RemoveCoordinationOnRadarAcquisition(string callsign)
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        foreach (var channel in scenario.CoordinationChannels.Values)
        {
            var removed = channel.Items.RemoveAll(i => i.AircraftId.Equals(callsign, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                MarkCoordinationChanged();
                _logger.LogDebug("Removed {Count} coordination items for {Callsign} on radar acquisition", removed, callsign);
            }
        }
    }

    /// <summary>
    /// Builds the scenario's coordination channels from every STARS list in the loaded ARTCC's facility tree, so the
    /// verbs can assume the lists exist. Run by every path that resolves a scenario's ARTCC configuration — the
    /// server's scenario load and the replay driver — and a no-op without one. Idempotent: a second call replaces each
    /// channel with a fresh one, and a snapshot restore replaces them all anyway.
    /// </summary>
    public void InitializeCoordinationChannelsFromArtcc()
    {
        if (Scenario is not { ArtccConfig: { } config } scenario)
        {
            return;
        }

        var channels = config.GetCoordinationChannels(config.Facility.Id);
        foreach (var ch in channels)
        {
            scenario.CoordinationChannels[ch.ListId] = ch;
        }

        if (channels.Count > 0)
        {
            MarkCoordinationChanged();
            _logger.LogInformation("Loaded {Count} coordination channels for scenario '{Name}'", channels.Count, scenario.ScenarioName);
        }
    }
}
