using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Per-track STARS display state: scratchpads (CRC + ASDEX), assigned/temporary/pilot
/// altitudes, display inhibitions (Mode-C, MSAW, duplicate beacon, conflict alert),
/// TPA type, and the per-TCP shared display dictionary.
/// </summary>
public class AircraftStarsState
{
    public string? Scratchpad1 { get; set; }
    public bool WasScratchpad1Cleared { get; set; }
    public string? PreviousScratchpad1 { get; set; }
    public string? Scratchpad2 { get; set; }
    public string? PreviousScratchpad2 { get; set; }
    public string? AsdexScratchpad1 { get; set; }
    public string? AsdexScratchpad2 { get; set; }
    public int? TemporaryAltitude { get; set; }
    public int? PilotReportedAltitude { get; set; }
    public bool IsAnnotated { get; set; }
    public int? AssignedAltitude { get; set; }
    public bool IsCaInhibited { get; set; }
    public bool IsModeCInhibited { get; set; }
    public bool IsMsawInhibited { get; set; }
    public bool IsDuplicateBeaconInhibited { get; set; }
    public int? TpaType { get; set; }
    public int? GlobalLeaderDirection { get; set; }
    public Dictionary<string, StarsTrackSharedState> SharedState { get; set; } = [];

    public AircraftStarsStateDto ToSnapshot() =>
        new()
        {
            Scratchpad1 = Scratchpad1,
            WasScratchpad1Cleared = WasScratchpad1Cleared,
            PreviousScratchpad1 = PreviousScratchpad1,
            Scratchpad2 = Scratchpad2,
            PreviousScratchpad2 = PreviousScratchpad2,
            AsdexScratchpad1 = AsdexScratchpad1,
            AsdexScratchpad2 = AsdexScratchpad2,
            TemporaryAltitude = TemporaryAltitude,
            PilotReportedAltitude = PilotReportedAltitude,
            IsAnnotated = IsAnnotated,
            AssignedAltitude = AssignedAltitude,
            IsCaInhibited = IsCaInhibited,
            IsModeCInhibited = IsModeCInhibited,
            IsMsawInhibited = IsMsawInhibited,
            IsDuplicateBeaconInhibited = IsDuplicateBeaconInhibited,
            TpaType = TpaType,
            GlobalLeaderDirection = GlobalLeaderDirection,
            SharedState = SharedState.Count > 0 ? SharedState.ToDictionary(kv => kv.Key, kv => kv.Value.ToSnapshot()) : null,
        };

    public static AircraftStarsState FromSnapshot(AircraftStarsStateDto dto) =>
        new()
        {
            Scratchpad1 = dto.Scratchpad1,
            WasScratchpad1Cleared = dto.WasScratchpad1Cleared,
            PreviousScratchpad1 = dto.PreviousScratchpad1,
            Scratchpad2 = dto.Scratchpad2,
            PreviousScratchpad2 = dto.PreviousScratchpad2,
            AsdexScratchpad1 = dto.AsdexScratchpad1,
            AsdexScratchpad2 = dto.AsdexScratchpad2,
            TemporaryAltitude = dto.TemporaryAltitude,
            PilotReportedAltitude = dto.PilotReportedAltitude,
            IsAnnotated = dto.IsAnnotated,
            AssignedAltitude = dto.AssignedAltitude,
            IsCaInhibited = dto.IsCaInhibited,
            IsModeCInhibited = dto.IsModeCInhibited,
            IsMsawInhibited = dto.IsMsawInhibited,
            IsDuplicateBeaconInhibited = dto.IsDuplicateBeaconInhibited,
            TpaType = dto.TpaType,
            GlobalLeaderDirection = dto.GlobalLeaderDirection,
            SharedState = dto.SharedState is not null
                ? dto.SharedState.ToDictionary(kv => kv.Key, kv => StarsTrackSharedState.FromSnapshot(kv.Value))
                : [],
        };
}
