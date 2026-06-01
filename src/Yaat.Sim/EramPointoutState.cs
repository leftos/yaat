namespace Yaat.Sim;

/// <summary>
/// Per-aircraft ERAM pointout record. Mirrors vatsim-server-rs
/// <c>radar_state::PointoutState</c> (crates/radar_state/src/lib.rs:231).
/// Round-tripped through <see cref="Simulation.Snapshots.AircraftSnapshotDto"/> as part of the
/// <see cref="AircraftEramState"/> satellite (serialized via <c>AircraftEramStateDto.Pointouts</c>).
/// </summary>
public sealed class EramPointoutState
{
    public string OriginatingFacility { get; set; } = "";
    public string OriginatingSector { get; set; } = "";
    public string ReceivingFacility { get; set; } = "";
    public string ReceivingSector { get; set; } = "";

    public bool IsAcknowledged { get; set; }
    public bool IsRecipientSuppressed { get; set; }
    public bool IsRSideCleared { get; set; }
    public bool IsDSideCleared { get; set; }
}
