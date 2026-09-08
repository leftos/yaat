using System.Collections.Concurrent;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation.Strips;

/// <summary>
/// The flight strips of one run: the strips themselves, the bay/rack layout they sit in, and the two printer
/// queues. Engine-owned (<see cref="SimulationEngine.Strips"/>) — a fresh engine starts empty, the snapshot's
/// server section carries it, and every run kind therefore has the same strips at the same second.
/// <para>
/// Shared between the host's command pipeline, its CRC translation layer, and the auto-print hook, so all
/// mutations funnel through the host's <c>StripMutations</c>, which takes the single <see cref="Gate"/> lock —
/// Items/Bays/PrinterQueues are updated atomically under it. Concurrent collections are retained for read-only
/// enumeration but callers must hold the gate for any coherent multi-slice update (moves touch Items + source
/// rack + dest rack together).
/// </para>
/// </summary>
public sealed class FlightStripState
{
    public object Gate { get; } = new();

    public ConcurrentDictionary<string, StripItemRecord> Items { get; } = new();
    public ConcurrentDictionary<string, Dictionary<string, List<string>[]>> Bays { get; } = new();

    public List<string> DeparturePrinterQueue { get; } = new();
    public List<string> ArrivalPrinterQueue { get; } = new();

    public int NextBlankId { get; set; } = 1;

    /// <summary>
    /// What the mutations have touched since the last drain — the broadcast seam, not simulation state: transient,
    /// never snapshotted, cleared with the session.
    /// </summary>
    public StripChangeTracker Changes { get; } = new();

    /// <summary>
    /// Pre-creates one empty rack-list per rack index for every bay in the given facility's
    /// flight-strips configuration. Safe to call multiple times (existing bay contents are
    /// preserved; only missing rack slots are created). Called from the scenario-load path
    /// once the student's position and ARTCC config resolve.
    /// </summary>
    public void InitializeFromArtcc(IEnumerable<StripBayConfig> bays)
    {
        lock (Gate)
        {
            foreach (var bay in bays)
            {
                if (!Bays.TryGetValue(bay.Id, out var racks))
                {
                    racks = new Dictionary<string, List<string>[]>();
                    Bays[bay.Id] = racks;
                }

                var rackCount = bay.NumberOfRacks > 0 ? bay.NumberOfRacks : 3;
                for (var i = 0; i < rackCount; i++)
                {
                    var key = i.ToString();
                    if (!racks.ContainsKey(key))
                    {
                        racks[key] = [new List<string>()];
                    }
                }
            }
        }
    }

    /// <summary>
    /// Clears the session state — the strips, both printer queues and the blank id counter — and leaves
    /// <see cref="Bays"/> alone. This is what a snapshot restore with no strip section replaces: the rack skeleton
    /// belongs to the ARTCC's bay configuration, which the load that ran just before it re-derived, and dropping it
    /// would leave the room with no racks to print into. <see cref="Reset"/> (scenario unload) is the one that goes further.
    /// </summary>
    public void ClearSession()
    {
        lock (Gate)
        {
            Items.Clear();
            DeparturePrinterQueue.Clear();
            ArrivalPrinterQueue.Clear();
            NextBlankId = 1;
            Changes.Clear();
        }
    }

    /// <summary>
    /// Clears all strips, bays, printer queues, and the blank id counter. Called when a
    /// scenario unloads so a fresh scenario starts with no residual state.
    /// </summary>
    public void Reset()
    {
        lock (Gate)
        {
            ClearSession();
            Bays.Clear();
        }
    }
}

public record StripItemRecord(
    string Id,
    string? AircraftId,
    int Type,
    bool IsOffset,
    string[] FieldValues,
    string FacilityId,
    string BayId,
    int Rack,
    int Index
);
