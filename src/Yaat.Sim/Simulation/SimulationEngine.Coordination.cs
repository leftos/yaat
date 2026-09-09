using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation;

// The STARS coordination half of the engine: the release-rundown timers spine step, the tower lists' proximity step,
// the removal a radar acquisition owes, the ARTCC initialisation that builds the scenario's channels and the tower
// list airports, and the dirty flag the drain turns into the host's one broadcast. The verb bodies live in
// Simulation/Coordination/CoordinationCommandHandler.cs. Everything here decides from engine state alone — the
// scenario's channels, the world's positions and its elapsed clock — so every run kind reaches the same lists; what
// changed is drained to the host from DrainStateChangesInto. The tower lists share that one flag because they share
// the wire topic: CRC's StarsCoordination payload carries every channel and every tower list together.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// True when a coordination body has changed a channel — or the proximity step a tower list — since the last
    /// drain. Payload-less on purpose: the wire carries the whole <c>StarsCoordination</c> topic anyway, channels and
    /// tower lists together, so what a host needs to know is only "re-push it".
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
    /// reverts to Unsent when its linger expires, which takes it off every receiver's display and leaves it on the
    /// sender's. Nothing here reads the wall clock, so a replay reaches each transition at the same elapsed second the
    /// live room did.
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
                    // The recall linger expiring reverts the item to Unsent rather than deleting it: CRC draws one
                    // shared item per viewer and hides an Unsent one everywhere but its origin TCP, so the receiver
                    // loses its copy while the sender gets its text back, with no per-viewer split here. The sender
                    // clears it with a second RDR, which the Unsent branch of the recall body removes outright.
                    // The automatic-release flag goes with the send it described: re-sending the reverted item and
                    // acknowledging it by hand reaches Acknowledged, and CRC chimes only for an acknowledged release
                    // that was not automatic.
                    item.Status = StarsCoordinationStatus.Unsent;
                    item.ExpireTime = null;
                    item.WasAutomaticRelease = false;
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
    /// item's status changed. The recall linger is not here — that one reverts the item to Unsent, which the loop that
    /// owns the list does after this.
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
    /// The tower lists' proximity pass, run once per second on every run kind: every aircraft within a list
    /// airport's range is on that list, stamped with the elapsed second it arrived at, and one that left or was
    /// deleted comes off. The entry second is the P-list's <c>DropZoneEntryTime</c> sort key, which is why it is
    /// snapshotted rather than re-derived — see <see cref="Snapshots.TowerListSnapshotMapper"/>.
    /// </summary>
    public void TickTowerLists()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        if (TowerListTracker.Update(World.GetSnapshot(), scenario.ElapsedSeconds))
        {
            MarkCoordinationChanged();
        }
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

    /// <summary>
    /// Builds the tower list airports from every STARS area in the loaded ARTCC's facility tree, so the proximity
    /// step has lists to fill. Run by every path that resolves a scenario's ARTCC configuration, alongside the
    /// coordination channels, and a no-op without one. The dwell entries it clears are the session's, not the
    /// configuration's: a snapshot restore later replaces them, and it is only ever the airports that come from here.
    /// </summary>
    public void InitializeTowerListsFromArtcc()
    {
        if (Scenario is not { ArtccConfig: { } config } scenario)
        {
            return;
        }

        TowerListTracker.Initialize(config);

        var listCount = TowerListTracker.GetLists().Count;
        if (listCount > 0)
        {
            _logger.LogInformation("Initialized {Count} tower list(s) for scenario '{Name}'", listCount, scenario.ScenarioName);
        }
    }
}
