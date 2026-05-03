namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftStarsStateDto
{
    public string? Scratchpad1 { get; init; }
    public required bool WasScratchpad1Cleared { get; init; }
    public string? PreviousScratchpad1 { get; init; }
    public string? Scratchpad2 { get; init; }
    public string? PreviousScratchpad2 { get; init; }
    public string? AsdexScratchpad1 { get; init; }
    public string? AsdexScratchpad2 { get; init; }
    public string? AsdexCallsignOverride { get; init; }
    public string? AsdexBeaconCodeOverride { get; init; }
    public string? AsdexCategoryOverride { get; init; }
    public string? AsdexAircraftTypeOverride { get; init; }
    public string? AsdexFixOverride { get; init; }
    public required bool AsdexSuspended { get; init; }
    public required bool AsdexTerminated { get; init; }
    public required bool AsdexAlertsInhibited { get; init; }
    public int? TemporaryAltitude { get; init; }
    public int? PilotReportedAltitude { get; init; }
    public required bool IsAnnotated { get; init; }
    public int? AssignedAltitude { get; init; }
    public required bool IsCaInhibited { get; init; }
    public required bool IsModeCInhibited { get; init; }
    public required bool IsMsawInhibited { get; init; }
    public required bool IsDuplicateBeaconInhibited { get; init; }
    public int? TpaType { get; init; }
    public int? GlobalLeaderDirection { get; init; }
    public Dictionary<string, SharedStateDto>? SharedState { get; init; }
}
