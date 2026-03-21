using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

public record Tcp(int Subset, string SectorId, string Id, string? ParentTcpId)
{
    public virtual bool Equals(Tcp? other) => other is not null && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString() => $"{Subset}{SectorId}";

    public TcpDto ToSnapshot() =>
        new()
        {
            Subset = Subset,
            SectorId = SectorId,
            Id = Id,
            ParentTcpId = ParentTcpId,
        };

    public static Tcp FromSnapshot(TcpDto dto) => new(dto.Subset, dto.SectorId, dto.Id, dto.ParentTcpId);
}
