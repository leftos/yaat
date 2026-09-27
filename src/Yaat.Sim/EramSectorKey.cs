using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// One ERAM sector, named by its facility and sector id: the key CRC's per-sector display state (a subscription, a
/// data-block format choice) is addressed by. Carried in <see cref="AircraftEramState.PointoutMinimizedSectors"/> and
/// <see cref="AircraftEramState.FdbOpenSectors"/>, and round-tripped through <see cref="EramSectorKeyDto"/>.
/// </summary>
public sealed record EramSectorKey(string Facility, string Sector)
{
    public bool Is(string facility, string sector) =>
        string.Equals(Facility, facility, StringComparison.Ordinal) && string.Equals(Sector, sector, StringComparison.Ordinal);

    public EramSectorKeyDto ToSnapshot() => new() { Facility = Facility, Sector = Sector };

    public static EramSectorKey FromSnapshot(EramSectorKeyDto dto) => new(dto.Facility, dto.Sector);
}
