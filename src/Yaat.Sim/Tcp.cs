namespace Yaat.Sim;

public record Tcp(int Subset, string SectorId, string Id, string? ParentTcpId)
{
    public virtual bool Equals(Tcp? other) =>
        other is not null && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString() => $"{Subset}{SectorId}";
}
