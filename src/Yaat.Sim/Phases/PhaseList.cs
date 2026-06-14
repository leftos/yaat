using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases;

/// <summary>
/// LAHSO hold-short point data: the location where the aircraft must stop
/// before the intersecting runway, and the ID of that runway.
/// </summary>
public sealed class LahsoTarget
{
    public required double Lat { get; init; }
    public required double Lon { get; init; }
    public required double DistFromThresholdNm { get; init; }
    public required string CrossingRunwayId { get; init; }

    public LahsoTargetDto ToSnapshot() =>
        new()
        {
            Lat = Lat,
            Lon = Lon,
            DistFromThresholdNm = DistFromThresholdNm,
            CrossingRunwayId = CrossingRunwayId,
        };

    public static LahsoTarget FromSnapshot(LahsoTargetDto dto) =>
        new()
        {
            Lat = dto.Lat,
            Lon = dto.Lon,
            DistFromThresholdNm = dto.DistFromThresholdNm,
            CrossingRunwayId = dto.CrossingRunwayId,
        };
}

/// <summary>
/// Pre-issued departure clearance (LUAW or CTO) stored during taxi.
/// Consumed when aircraft reaches the departure runway hold-short.
/// </summary>
public sealed class DepartureClearanceInfo
{
    public required ClearanceType Type { get; init; }
    public required DepartureInstruction Departure { get; init; }
    public int? AssignedAltitude { get; init; }

    /// <summary>
    /// Pre-resolved navigation targets for route-based departures.
    /// Set by the dispatcher so phases don't need NavigationDatabase.
    /// </summary>
    public List<NavigationTarget>? DepartureRoute { get; init; }

    /// <summary>
    /// SID procedure ID if departure route was resolved from CIFP data.
    /// Used to activate SID via mode during initial climb.
    /// </summary>
    public string? DepartureSidId { get; init; }

    /// <summary>Magnetic heading from a radar vectors SID (VM/VA leg).</summary>
    public double? SidDepartureHeadingMagnetic { get; init; }

    /// <summary>
    /// When true, initial climb holds runway heading until DER + IFR min turn altitude before
    /// applying <see cref="SidDepartureHeadingMagnetic"/>.
    /// </summary>
    public bool RvSidDeferHeadingUntilMinAlt { get; init; }

    /// <summary>
    /// When true, the filed SID is a radar-vectors SID but its published vectors heading could not be
    /// resolved (CIFP unavailable — e.g. the procedure was retired from the current FAA cycle). Initial
    /// climb holds runway heading and awaits vectors instead of navigating direct to the first enroute fix.
    /// </summary>
    public bool RvSidHoldRunwayHeading { get; init; }

    /// <summary>
    /// Pre-resolved pattern runway for cross-runway closed traffic departures.
    /// Set by the dispatcher so TaxiingPhase doesn't need NavigationDatabase.
    /// </summary>
    public RunwayInfo? PatternRunway { get; init; }

    /// <summary>
    /// Node IDs of taxi-route hold-shorts that were flipped from
    /// IsCleared=false to true when this clearance was stored. Used by CTOC
    /// to revert only the hold-shorts this clearance pre-cleared, leaving
    /// crossings independently cleared by LV/RC/CROSS untouched.
    /// </summary>
    public IReadOnlyList<int>? PreClearedHoldShortNodeIds { get; init; }

    public DepartureClearanceDto ToSnapshot() =>
        new()
        {
            Type = (int)Type,
            Departure = Departure.ToSnapshot(),
            AssignedAltitude = AssignedAltitude,
            DepartureRoute = DepartureRoute?.Select(t => t.ToSnapshot()).ToList(),
            DepartureSidId = DepartureSidId,
            SidDepartureHeadingMagnetic = SidDepartureHeadingMagnetic,
            RvSidDeferHeadingUntilMinAlt = RvSidDeferHeadingUntilMinAlt,
            RvSidHoldRunwayHeading = RvSidHoldRunwayHeading,
            PatternRunway = PatternRunway?.ToSnapshot(),
            PreClearedHoldShortNodeIds = PreClearedHoldShortNodeIds?.ToList(),
        };

    public static DepartureClearanceInfo FromSnapshot(DepartureClearanceDto dto) =>
        new()
        {
            Type = (ClearanceType)dto.Type,
            Departure = DepartureInstruction.FromSnapshot(dto.Departure),
            AssignedAltitude = dto.AssignedAltitude,
            DepartureRoute = dto.DepartureRoute?.Select(NavigationTarget.FromSnapshot).ToList(),
            DepartureSidId = dto.DepartureSidId,
            SidDepartureHeadingMagnetic = dto.SidDepartureHeadingMagnetic,
            RvSidDeferHeadingUntilMinAlt = dto.RvSidDeferHeadingUntilMinAlt,
            RvSidHoldRunwayHeading = dto.RvSidHoldRunwayHeading,
            PatternRunway = dto.PatternRunway is not null ? RunwayInfo.FromSnapshot(dto.PatternRunway) : null,
            PreClearedHoldShortNodeIds = dto.PreClearedHoldShortNodeIds,
        };
}

public sealed class PhaseList
{
    public RunwayInfo? AssignedRunway { get; set; }

    /// <summary>
    /// Taxi route persisted across phase transitions (set once, consumed by ground phases).
    /// </summary>
    public TaxiRoute? TaxiRoute { get; set; }

    /// <summary>
    /// Pre-issued departure clearance (LUAW or CTO) during taxi.
    /// Set by LUAW/CTO commands while taxiing. Consumed by TaxiingPhase
    /// when the taxi route completes (at departure runway threshold).
    /// </summary>
    public DepartureClearanceInfo? DepartureClearance { get; set; }

    /// <summary>
    /// Landing clearance that persists across phases.
    /// Set by CLAND command (possibly on downwind/base), consumed by FinalApproachPhase.
    /// Cleared by CTOC or supplanted by a new clearance for a different runway.
    /// </summary>
    public ClearanceType? LandingClearance { get; set; }

    /// <summary>
    /// Runway ID the landing clearance applies to (null if no clearance active).
    /// </summary>
    public string? ClearedRunwayId { get; set; }

    /// <summary>
    /// Instructor/RPO forced-landing override. Set by CLANDF (which also grants landing
    /// clearance). When true, FinalApproachPhase and LandingPhase suppress every automatic
    /// go-around and actively drive the aircraft to a touchdown regardless of energy state.
    /// Persists across the FinalApproach→Landing boundary; cleared by GA, CTOC, or touchdown.
    /// </summary>
    public bool ForceLanding { get; set; }

    /// <summary>
    /// When set, the aircraft is doing pattern work (MLT/MRT/CTOMLT/CTOMRT).
    /// Each approach defaults to touch-and-go and re-enters the pattern.
    /// Null means the aircraft will full-stop on landing.
    /// </summary>
    public PatternDirection? TrafficDirection { get; set; }

    /// <summary>
    /// When set, the pattern circuit uses a different runway than the takeoff runway.
    /// Used for cross-runway closed traffic (e.g., takeoff 33, pattern for 28R).
    /// <see cref="AssignedRunway"/> holds this pattern runway (so the circuit/final/landing
    /// phases use it); <see cref="DepartureRunway"/> holds the takeoff runway. PhaseRunner
    /// uses this for auto-cycle.
    /// </summary>
    public RunwayInfo? PatternRunway { get; set; }

    /// <summary>
    /// When set, the lineup/takeoff phases use this runway instead of
    /// <see cref="AssignedRunway"/>. Populated for cross-runway closed traffic where the
    /// takeoff runway differs from the pattern runway (e.g., takeoff 33, pattern for 28R):
    /// <see cref="AssignedRunway"/> is the pattern runway (28R) while this is the departure
    /// runway (33). Null for same-runway operations, where lineup/takeoff fall back to
    /// <see cref="AssignedRunway"/>. Read by LineUpPhase/LinedUpAndWaitingPhase/TakeoffPhase.
    /// </summary>
    public RunwayInfo? DepartureRunway { get; set; }

    /// <summary>
    /// Controller-requested exit preference. Set by EL/ER/EXIT commands,
    /// consumed by RunwayExitPhase. Persists across phase transitions so
    /// the controller can issue the command during approach or rollout.
    /// </summary>
    public ExitPreference? RequestedExit { get; set; }

    /// <summary>
    /// Active approach clearance. Set by JFAC/CAPP/JAPP/PTAC commands.
    /// Used by FinalApproachPhase and approach navigation phases.
    /// </summary>
    public ApproachClearance? ActiveApproach { get; set; }

    /// <summary>
    /// LAHSO hold-short target. Set by LAHSO command, consumed by LandingPhase
    /// to stop before the intersecting runway. Cleared after aircraft is released.
    /// </summary>
    public LahsoTarget? LahsoHoldShort { get; set; }

    /// <summary>
    /// Resolved exit info from LandingPhase's continuous evaluation.
    /// Set by LandingPhase when it commits to an exit at the branch point.
    /// Consumed by RunwayExitPhase.OnStart and cleared. Transient — not persisted.
    /// </summary>
    public ResolvedExitInfo? ResolvedExit { get; set; }

    public int CurrentIndex { get; private set; }

    /// <summary>
    /// The full phase list. Mutable — commands and clearances may insert,
    /// remove, or replace future phases at any time.
    /// </summary>
    public List<Phase> Phases { get; } = [];

    public Phase? CurrentPhase => CurrentIndex >= 0 && CurrentIndex < Phases.Count ? Phases[CurrentIndex] : null;

    public bool IsComplete => CurrentIndex >= Phases.Count;

    /// <summary>
    /// True when the aircraft is established on an approach procedure that is descending it
    /// (or will descend it) below the MVA — on final/glideslope, or cleared for a full
    /// approach (CAPP/JAPP/PTAC) and flying the procedure. A lateral-only localizer join
    /// (JFAC/JLOC) holds its assigned altitude and is excluded; so are pattern legs and a
    /// go-around (climbing out). Consumed by the client to inhibit the MVA altitude tint: on final
    /// the published procedure owns obstacle clearance (or, on a visual, the pilot has the runway in
    /// sight), so the MVA no longer applies — AIM 4-1-16.a.1; cf. 7110.65 §5-14-7 (MSAW).
    /// </summary>
    public bool IsEstablishedOnApproach()
    {
        if (CurrentPhase is null or GoAroundPhase)
        {
            return false;
        }

        // A lateral-only localizer join (JFAC/JLOC) holds assigned altitude — MVA still applies.
        if (ActiveApproach is { LateralInterceptOnly: true })
        {
            return false;
        }

        return CurrentPhase is FinalApproachPhase || (ActiveApproach is not null && ApproachCommandHandler.IsArrivalApproachPhase(CurrentPhase));
    }

    public void Add(Phase phase)
    {
        Phases.Add(phase);
    }

    public void Start(PhaseContext ctx)
    {
        CurrentIndex = 0;
        if (CurrentPhase is { } phase)
        {
            phase.Status = PhaseStatus.Active;
            phase.OnStart(ctx);
        }
    }

    public void AdvanceToNext(PhaseContext ctx)
    {
        var current = CurrentPhase;
        if (current is not null)
        {
            current.Status = PhaseStatus.Completed;
            current.OnEnd(ctx, PhaseStatus.Completed);
        }

        CurrentIndex++;

        if (CurrentPhase is { } next)
        {
            next.Status = PhaseStatus.Active;
            next.OnStart(ctx);
        }
    }

    public void InsertAfterCurrent(Phase phase)
    {
        int insertAt = CurrentIndex + 1;
        if (insertAt > Phases.Count)
        {
            insertAt = Phases.Count;
        }
        Phases.Insert(insertAt, phase);
    }

    /// <summary>
    /// Insert a sequence of phases immediately after the current phase,
    /// preserving the order of the provided list.
    /// </summary>
    public void InsertAfterCurrent(IEnumerable<Phase> phases)
    {
        int insertAt = CurrentIndex + 1;
        if (insertAt > Phases.Count)
        {
            insertAt = Phases.Count;
        }
        Phases.InsertRange(insertAt, phases);
    }

    /// <summary>
    /// Remove all pending phases after the current one and replace them.
    /// The current (active) phase is not affected.
    /// </summary>
    public void ReplaceUpcoming(IEnumerable<Phase> phases)
    {
        int removeFrom = CurrentIndex + 1;
        if (removeFrom < Phases.Count)
        {
            Phases.RemoveRange(removeFrom, Phases.Count - removeFrom);
        }
        Phases.AddRange(phases);
    }

    /// <summary>
    /// Skip forward to the first pending phase of type T.
    /// All intermediate phases get Skipped status.
    /// </summary>
    public void SkipTo<T>(PhaseContext ctx)
        where T : Phase
    {
        while (CurrentPhase is not null && CurrentPhase is not T)
        {
            var skipped = CurrentPhase;
            skipped.Status = PhaseStatus.Skipped;
            skipped.OnEnd(ctx, PhaseStatus.Skipped);
            CurrentIndex++;
        }

        if (CurrentPhase is { Status: PhaseStatus.Pending } next)
        {
            next.Status = PhaseStatus.Active;
            next.OnStart(ctx);
        }
    }

    /// <summary>
    /// End the active phase with Skipped status and mark list complete.
    /// </summary>
    public void Clear(PhaseContext ctx)
    {
        if (CurrentPhase is { } current && current.Status == PhaseStatus.Active)
        {
            current.Status = PhaseStatus.Skipped;
            current.OnEnd(ctx, PhaseStatus.Skipped);
        }

        // Skip all remaining phases
        for (int i = CurrentIndex + 1; i < Phases.Count; i++)
        {
            Phases[i].Status = PhaseStatus.Skipped;
        }

        CurrentIndex = Phases.Count;
    }

    public static PhaseList FromSnapshot(PhaseListDto dto, AirportGroundLayout? groundLayout)
    {
        var list = new PhaseList
        {
            AssignedRunway = dto.AssignedRunway is not null ? RunwayInfo.FromSnapshot(dto.AssignedRunway) : null,
            TaxiRoute = dto.TaxiRoute is not null ? Data.Airport.TaxiRoute.FromSnapshot(dto.TaxiRoute, groundLayout) : null,
            DepartureClearance = dto.DepartureClearance is not null ? DepartureClearanceInfo.FromSnapshot(dto.DepartureClearance) : null,
            LandingClearance = dto.LandingClearance.HasValue ? (ClearanceType)dto.LandingClearance.Value : null,
            ClearedRunwayId = dto.ClearedRunwayId,
            ForceLanding = dto.ForceLanding,
            RequestedExit =
                (dto.RequestedExit.HasValue || dto.RequestedExitTaxiway is not null)
                    ? new ExitPreference
                    {
                        Side = dto.RequestedExit.HasValue ? (ExitSide)dto.RequestedExit.Value : null,
                        Taxiway = dto.RequestedExitTaxiway,
                    }
                    : null,
            TrafficDirection = dto.TrafficDirection.HasValue ? (PatternDirection)dto.TrafficDirection.Value : null,
            PatternRunway = dto.PatternRunway is not null ? RunwayInfo.FromSnapshot(dto.PatternRunway) : null,
            DepartureRunway = dto.DepartureRunway is not null ? RunwayInfo.FromSnapshot(dto.DepartureRunway) : null,
            ActiveApproach = dto.ActiveApproach is not null ? ApproachClearance.FromSnapshot(dto.ActiveApproach) : null,
            LahsoHoldShort = dto.LahsoHoldShort is not null ? LahsoTarget.FromSnapshot(dto.LahsoHoldShort) : null,
        };

        foreach (var phaseDto in dto.Phases)
        {
            var phase = RestorePhase(phaseDto, groundLayout);
            list.Add(phase);
        }

        // Advance CurrentIndex to match — we can't set it directly (private set),
        // so we use the fact that phases are added sequentially and CurrentIndex starts at 0.
        // We need to set it via reflection-free approach: just track the index.
        while (list.CurrentIndex < dto.CurrentIndex && list.CurrentIndex < list.Phases.Count)
        {
            list.CurrentIndex++;
        }

        return list;
    }

    private static Phase RestorePhase(PhaseDto dto, AirportGroundLayout? groundLayout) =>
        dto switch
        {
            HoldingShortPhaseDto d => HoldingShortPhase.FromSnapshot(d),
            CrossingRunwayPhaseDto d => CrossingRunwayPhase.FromSnapshot(d),
            ClearRunwayPhaseDto d => ClearRunwayPhase.FromSnapshot(d),
            AirTaxiPhaseDto d => AirTaxiPhase.FromSnapshot(d),
            HoldingInPositionPhaseDto d => HoldingInPositionPhase.FromSnapshot(d),
            HoldingAfterPushbackPhaseDto d => HoldingAfterPushbackPhase.FromSnapshot(d),
            HoldingAfterExitPhaseDto d => HoldingAfterExitPhase.FromSnapshot(d),
            AtParkingPhaseDto d => AtParkingPhase.FromSnapshot(d),
            TaxiingPhaseDto d => TaxiingPhase.FromSnapshot(d),
            FollowingPhaseDto d => FollowingPhase.FromSnapshot(d),
            PushbackPhaseDto d => PushbackPhase.FromSnapshot(d),
            PushbackToSpotPhaseDto d => PushbackToSpotPhase.FromSnapshot(d, groundLayout)!,
            RunwayExitPhaseDto d => RunwayExitPhase.FromSnapshot(d, groundLayout),
            HelicopterLandingPhaseDto d => HelicopterLandingPhase.FromSnapshot(d),
            GoAroundPhaseDto d => GoAroundPhase.FromSnapshot(d),
            HelicopterTakeoffPhaseDto d => HelicopterTakeoffPhase.FromSnapshot(d),
            LowApproachPhaseDto d => LowApproachPhase.FromSnapshot(d),
            RunwayHoldingPhaseDto d => RunwayHoldingPhase.FromSnapshot(d),
            MakeTurnPhaseDto d => MakeTurnPhase.FromSnapshot(d),
            VfrHoldPhaseDto d => VfrHoldPhase.FromSnapshot(d),
            AirspaceBoundaryHoldPhaseDto d => AirspaceBoundaryHoldPhase.FromSnapshot(d),
            STurnPhaseDto d => STurnPhase.FromSnapshot(d),
            StopAndGoPhaseDto d => StopAndGoPhase.FromSnapshot(d),
            TouchAndGoPhaseDto d => TouchAndGoPhase.FromSnapshot(d),
            TakeoffPhaseDto d => TakeoffPhase.FromSnapshot(d),
            InitialClimbPhaseDto d => InitialClimbPhase.FromSnapshot(d),
            LineUpPhaseDto d => LineUpPhase.FromSnapshot(d),
            LinedUpAndWaitingPhaseDto d => LinedUpAndWaitingPhase.FromSnapshot(d),
            FinalApproachPhaseDto d => FinalApproachPhase.FromSnapshot(d),
            LandingPhaseDto d => LandingPhase.FromSnapshot(d, groundLayout),
            MidfieldCrossingPhaseDto d => MidfieldCrossingPhase.FromSnapshot(d),
            TeardropReentryPhaseDto d => TeardropReentryPhase.FromSnapshot(d),
            PatternEntryPhaseDto d => PatternEntryPhase.FromSnapshot(d),
            BasePhaseDto d => BasePhase.FromSnapshot(d),
            CrosswindPhaseDto d => CrosswindPhase.FromSnapshot(d),
            DownwindPhaseDto d => DownwindPhase.FromSnapshot(d),
            UpwindPhaseDto d => UpwindPhase.FromSnapshot(d),
            PatternExitPhaseDto d => PatternExitPhase.FromSnapshot(d),
            VfrFollowPhaseDto d => VfrFollowPhase.FromSnapshot(d),
            HoldingPatternPhaseDto d => HoldingPatternPhase.FromSnapshot(d),
            ProcedureTurnPhaseDto d => ProcedureTurnPhase.FromSnapshot(d),
            ApproachNavigationPhaseDto d => ApproachNavigationPhase.FromSnapshot(d),
            InterceptCoursePhaseDto d => InterceptCoursePhase.FromSnapshot(d),
            DepartureProcedurePhaseDto d => DepartureProcedurePhase.FromSnapshot(d),
            _ => throw new InvalidOperationException($"Unknown phase DTO type: {dto.GetType().Name}"),
        };

    public PhaseListDto ToSnapshot() =>
        new()
        {
            AssignedRunway = AssignedRunway?.ToSnapshot(),
            TaxiRoute = TaxiRoute?.ToSnapshot(),
            DepartureClearance = DepartureClearance?.ToSnapshot(),
            LandingClearance = LandingClearance.HasValue ? (int)LandingClearance.Value : null,
            ClearedRunwayId = ClearedRunwayId,
            ForceLanding = ForceLanding,
            TrafficDirection = TrafficDirection.HasValue ? (int)TrafficDirection.Value : null,
            PatternRunway = PatternRunway?.ToSnapshot(),
            DepartureRunway = DepartureRunway?.ToSnapshot(),
            RequestedExit = RequestedExit?.Side is not null ? (int)RequestedExit.Side.Value : null,
            RequestedExitTaxiway = RequestedExit?.Taxiway,
            ActiveApproach = ActiveApproach?.ToSnapshot(),
            LahsoHoldShort = LahsoHoldShort?.ToSnapshot(),
            CurrentIndex = CurrentIndex,
            Phases = Phases.Select(p => p.ToSnapshot()).ToList(),
        };
}
