namespace Yaat.Sim;

public sealed class CoordinationItem
{
    public required string Id { get; init; }
    public required string AircraftId { get; init; }
    public StarsCoordinationStatus Status { get; set; }
    public string Message { get; set; } = "";
    public DateTime? ExpireTime { get; set; }
    public required Tcp OriginTcp { get; init; }
    public string ExitFix { get; set; } = "";
    public bool WasAutomaticRelease { get; set; }
    public int SequenceNumber { get; set; }
}
