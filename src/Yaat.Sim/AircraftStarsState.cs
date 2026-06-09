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
    public string? AsdexCallsignOverride { get; set; }
    public string? AsdexBeaconCodeOverride { get; set; }
    public string? AsdexCategoryOverride { get; set; }
    public string? AsdexAircraftTypeOverride { get; set; }
    public string? AsdexFixOverride { get; set; }
    public bool AsdexSuspended { get; set; }
    public bool AsdexTerminated { get; set; }
    public bool AsdexAlertsInhibited { get; set; }
    public string? SaidScratchpad1 { get; set; }
    public string? SaidScratchpad2 { get; set; }
    public string? SaidCallsignOverride { get; set; }
    public string? SaidBeaconCodeOverride { get; set; }
    public string? SaidCategoryOverride { get; set; }
    public string? SaidAircraftTypeOverride { get; set; }
    public string? SaidFixOverride { get; set; }
    public bool SaidSuspended { get; set; }
    public bool SaidTerminated { get; set; }
    public int? TemporaryAltitude { get; set; }
    public int? PilotReportedAltitude { get; set; }
    public bool IsAnnotated { get; set; }
    public int? AssignedAltitude { get; set; }
    public bool IsCaInhibited { get; set; }
    public bool IsModeCInhibited { get; set; }
    public bool IsMsawInhibited { get; set; }
    public bool IsDuplicateBeaconInhibited { get; set; }
    public int? TpaType { get; set; }
    public double TpaSize { get; set; }
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
            AsdexCallsignOverride = AsdexCallsignOverride,
            AsdexBeaconCodeOverride = AsdexBeaconCodeOverride,
            AsdexCategoryOverride = AsdexCategoryOverride,
            AsdexAircraftTypeOverride = AsdexAircraftTypeOverride,
            AsdexFixOverride = AsdexFixOverride,
            AsdexSuspended = AsdexSuspended,
            AsdexTerminated = AsdexTerminated,
            AsdexAlertsInhibited = AsdexAlertsInhibited,
            SaidScratchpad1 = SaidScratchpad1,
            SaidScratchpad2 = SaidScratchpad2,
            SaidCallsignOverride = SaidCallsignOverride,
            SaidBeaconCodeOverride = SaidBeaconCodeOverride,
            SaidCategoryOverride = SaidCategoryOverride,
            SaidAircraftTypeOverride = SaidAircraftTypeOverride,
            SaidFixOverride = SaidFixOverride,
            SaidSuspended = SaidSuspended,
            SaidTerminated = SaidTerminated,
            TemporaryAltitude = TemporaryAltitude,
            PilotReportedAltitude = PilotReportedAltitude,
            IsAnnotated = IsAnnotated,
            AssignedAltitude = AssignedAltitude,
            IsCaInhibited = IsCaInhibited,
            IsModeCInhibited = IsModeCInhibited,
            IsMsawInhibited = IsMsawInhibited,
            IsDuplicateBeaconInhibited = IsDuplicateBeaconInhibited,
            TpaType = TpaType,
            TpaSize = TpaSize,
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
            AsdexCallsignOverride = dto.AsdexCallsignOverride,
            AsdexBeaconCodeOverride = dto.AsdexBeaconCodeOverride,
            AsdexCategoryOverride = dto.AsdexCategoryOverride,
            AsdexAircraftTypeOverride = dto.AsdexAircraftTypeOverride,
            AsdexFixOverride = dto.AsdexFixOverride,
            AsdexSuspended = dto.AsdexSuspended,
            AsdexTerminated = dto.AsdexTerminated,
            AsdexAlertsInhibited = dto.AsdexAlertsInhibited,
            SaidScratchpad1 = dto.SaidScratchpad1,
            SaidScratchpad2 = dto.SaidScratchpad2,
            SaidCallsignOverride = dto.SaidCallsignOverride,
            SaidBeaconCodeOverride = dto.SaidBeaconCodeOverride,
            SaidCategoryOverride = dto.SaidCategoryOverride,
            SaidAircraftTypeOverride = dto.SaidAircraftTypeOverride,
            SaidFixOverride = dto.SaidFixOverride,
            SaidSuspended = dto.SaidSuspended,
            SaidTerminated = dto.SaidTerminated,
            TemporaryAltitude = dto.TemporaryAltitude,
            PilotReportedAltitude = dto.PilotReportedAltitude,
            IsAnnotated = dto.IsAnnotated,
            AssignedAltitude = dto.AssignedAltitude,
            IsCaInhibited = dto.IsCaInhibited,
            IsModeCInhibited = dto.IsModeCInhibited,
            IsMsawInhibited = dto.IsMsawInhibited,
            IsDuplicateBeaconInhibited = dto.IsDuplicateBeaconInhibited,
            TpaType = dto.TpaType,
            TpaSize = dto.TpaSize,
            GlobalLeaderDirection = dto.GlobalLeaderDirection,
            SharedState = dto.SharedState is not null
                ? dto.SharedState.ToDictionary(kv => kv.Key, kv => StarsTrackSharedState.FromSnapshot(kv.Value))
                : [],
        };
}
