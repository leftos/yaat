namespace Yaat.Sim.Simulation;

public sealed class SessionRecording
{
    public required string ScenarioJson { get; init; }
    public required int RngSeed { get; init; }
    public string? WeatherJson { get; init; }
    public required List<RecordedAction> Actions { get; init; }
    public required double TotalElapsedSeconds { get; init; }

    public string? ScenarioName { get; init; }
    public string? ScenarioId { get; init; }
    public string? ArtccId { get; init; }
    public DateTime? RecordedAtUtc { get; init; }
    public string? RecordedBy { get; init; }
}
