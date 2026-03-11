namespace Yaat.Sim;

public sealed class ConflictAlertState
{
    public Dictionary<string, ActiveConflict> Conflicts { get; } = [];
}

public sealed class ActiveConflict
{
    public required string Id { get; init; }
    public required string CallsignA { get; init; }
    public required string CallsignB { get; init; }
    public bool IsAcknowledged { get; set; }
}
