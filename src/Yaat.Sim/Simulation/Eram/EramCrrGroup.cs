namespace Yaat.Sim.Simulation.Eram;

/// <summary>
/// One ERAM Continuous Range Readout group: a labelled point on the ground that CRC measures its members' range to
/// (docs/crc/eram.md §Continuous Range Readout View). Created, replaced or recolored by the <c>LF</c> command and the
/// CRR View menu; the group carries only its label, colour and location, because membership rides each aircraft
/// (<see cref="AircraftEramState.CrrGroupLabel"/>) and the per-aircraft Range Data Block distance is computed
/// client-side. Room-global (single-ERAM-sector training), so the engine holds one set for the whole session.
/// </summary>
public sealed record EramCrrGroup(string Label, EramCrrColor Color, double Latitude, double Longitude);

/// <summary>
/// The colour a CRR group renders in — the sector's configured CRR colour at creation, changed afterwards from the
/// CRR View menu. One numbering with the server's wire enum (<c>Yaat.Server.Dtos.CrrColor</c>), which the DTO
/// converter casts to without a mapping, <em>and</em> with the snapshot, which stores the integer
/// (<c>EramCrrGroupSnapshotDto.Color</c>): a member added, renamed or renumbered on either side without the other
/// silently recolors every group on the wire and every group an older snapshot restores.
/// </summary>
public enum EramCrrColor
{
    Green = 0,
    Coral = 1,
    White = 2,
    Yellow = 3,
}
