using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases;

/// <summary>
/// The pattern altitude an option clearance's modifier gives the runway it names (<c>COPT MLT 28L 20</c> on
/// the 28R final): on the runway-transition circuit, <see cref="AircraftPattern.AltitudeOverrideFt"/> becomes
/// <see cref="AltitudeFt"/>. A null <see cref="AltitudeFt"/> is the runway change's own clear — different
/// runway, different TPA.
/// </summary>
public sealed record DeferredPatternAltitude(double? AltitudeFt);

/// <summary>How a phase held behind a boundary hold (<see cref="PhaseList.HeldPhase"/>) carries on when the hold ends.</summary>
public enum HoldResumeKind
{
    /// <summary>The held phase becomes current again and goes on ticking from its own state, with no second <c>OnStart</c>.</summary>
    InPlace,

    /// <summary>The held phase ends Completed and a copy of it takes its place, started once.</summary>
    RestartCopy,
}

/// <summary>
/// The end of a boundary hold standing in front of a held phase (<see cref="PhaseList.EndHold"/>): resume the held phase in
/// place, or replace it with <see cref="Copy"/>.
/// </summary>
public sealed record HoldResume
{
    private HoldResume(HoldResumeKind kind, Phase? copy)
    {
        Kind = kind;
        Copy = copy;
    }

    /// <summary>Whether the held phase resumes where it stood or is replaced by <see cref="Copy"/>.</summary>
    public HoldResumeKind Kind { get; }

    /// <summary>The phase that replaces the held one; null for <see cref="HoldResumeKind.InPlace"/>, never null otherwise.</summary>
    public Phase? Copy { get; }

    /// <summary>Resume the held phase where it stood.</summary>
    public static HoldResume InPlace { get; } = new(HoldResumeKind.InPlace, null);

    /// <summary>End the held phase Completed and put <paramref name="copy"/>, not yet started, in its place.</summary>
    public static HoldResume RestartWith(Phase copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        return new(HoldResumeKind.RestartCopy, copy);
    }
}

/// <summary>
/// A boundary hold taken out of <see cref="List"/> by <see cref="PhaseList.LiftHold"/>: the hold, the phase it stood in front
/// of and the index both stood at. Ended once, by <see cref="PhaseList.PutBackHold"/> on the same list or by
/// <see cref="Discard"/>.
/// </summary>
internal sealed class HoldLift
{
    private static readonly ILogger Log = SimLog.CreateLogger("HoldLift");

    internal HoldLift(PhaseList list, AirspaceBoundaryHoldPhase hold, Phase held, int index)
    {
        List = list;
        Hold = hold;
        Held = held;
        Index = index;
    }

    /// <summary>The list the hold was lifted from.</summary>
    public PhaseList List { get; }

    /// <summary>The lifted hold.</summary>
    public AirspaceBoundaryHoldPhase Hold { get; }

    /// <summary>The phase the hold stood in front of.</summary>
    public Phase Held { get; }

    /// <summary>The index the hold and then the held phase stood at.</summary>
    public int Index { get; }

    /// <summary>Whether the lift has ended: the hold was put back or discarded.</summary>
    public bool Ended { get; internal set; }

    /// <summary>
    /// The hold does not go back: it ends Skipped, with one <c>OnEnd</c> when it had started (Active); a hold that never
    /// started (Pending) gets none, as it had no <c>OnStart</c>. Nothing for a lift already ended.
    /// </summary>
    public void Discard(PhaseContext ctx)
    {
        if (Ended)
        {
            return;
        }

        Ended = true;
        bool started = Hold.Status == PhaseStatus.Active;
        Hold.Status = PhaseStatus.Skipped;
        if (started)
        {
            Hold.OnEnd(ctx, PhaseStatus.Skipped);
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "[HoldLift] lifted {Hold} discarded ({Started}): {Held} did not survive the command",
                Hold.Name,
                started ? "started" : "never started",
                Held.Name
            );
        }
    }
}

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
    /// Typed SID runway-transition legs (VD/VM/VA/VI/CA/CF …) resolved from CIFP data, mirroring
    /// <see cref="DepartureRoute"/>. Non-null only when the filed SID has coded heading/intercept
    /// legs. InitialClimbPhase flies these (fly runway heading, await vectors, then course-to-fix)
    /// instead of navigating direct to the first named fix. Carried here so a clearance issued
    /// during taxi (rolling CTO) keeps the legs through TaxiingPhase's InitialClimb construction.
    /// </summary>
    public List<ProcedureLeg>? DepartureProcedureLegs { get; init; }

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
            DepartureProcedureLegs = DepartureProcedureLegs?.Select(l => l.ToSnapshot()).ToList(),
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
            DepartureProcedureLegs = dto.DepartureProcedureLegs?.Select(ProcedureLeg.FromSnapshot).ToList(),
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
    private static readonly ILogger Log = SimLog.CreateLogger("PhaseList");

    public RunwayInfo? AssignedRunway { get; set; }

    /// <summary>
    /// The taxi route a TAXI sent on the landing rollout stores behind the rollout's exit: set when the TAXI is accepted, started
    /// (and cleared) when the runway exit completes (<c>GroundCommandHandler.TryStartStoredTaxiRoute</c>), dropped when the exit is
    /// given up or another exit is instructed. Held on the runway after a LAHSO stop (<see cref="Tower.RunwayHoldingPhase"/>), it
    /// is a TAXI whose route enters the runway held short of: started (and cleared) by the CROSS that releases the hold
    /// (<c>GroundCommandHandler.StartTaxiStoredAtLahsoHold</c>), replaced by a later TAXI or EXIT. Null otherwise; the active route
    /// is <see cref="AircraftGroundOps.AssignedTaxiRoute"/>.
    /// </summary>
    public TaxiRoute? TaxiRoute { get; set; }

    /// <summary>
    /// The destination runway the TAXI that stored <see cref="TaxiRoute"/> named, assigned when the route starts in preference to the
    /// runway its last bar detects; set and cleared with <see cref="TaxiRoute"/>, null for a TAXI naming none.
    /// </summary>
    public string? StoredTaxiDestinationRunway { get; set; }

    /// <summary>
    /// Drops the stored taxi route and its destination runway (<see cref="TaxiRoute"/>, <see cref="StoredTaxiDestinationRunway"/>).
    /// </summary>
    public void ClearStoredTaxiRoute()
    {
        TaxiRoute = null;
        StoredTaxiDestinationRunway = null;
    }

    /// <summary>
    /// Pre-issued departure clearance (LUAW or CTO) during taxi.
    /// Set by LUAW/CTO commands while taxiing. Consumed by TaxiingPhase
    /// when the taxi route completes (at departure runway threshold).
    /// </summary>
    public DepartureClearanceInfo? DepartureClearance { get; set; }

    /// <summary>
    /// The taxiway a line-up and wait given at its hold short sent the aircraft onto, to line up on the runway at that intersection
    /// (7110.65 3-9-4.m). A takeoff clearance read back afterwards names it ("cleared for takeoff runway 28R at E"). Cleared when the
    /// takeoff roll starts, when the stored clearance is cancelled, and when the aircraft leaves line-up and wait other than by its
    /// takeoff. Null for none.
    /// </summary>
    public string? LineUpIntersectionTaxiway { get; set; }

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
    /// Transient one-shot signal: a CLAND just changed the landing runway to a diverging runway during
    /// a low approach (#292), so the controller/pilot phraseology uses "change to runway N" (7110.65
    /// §3-10-5). Set by the retarget, consumed and cleared by the pilot readback. Not snapshotted — a
    /// same-tick readback cue only.
    /// </summary>
    public bool LandingRunwayChangedFromLowApproach { get; set; }

    /// <summary>
    /// Instructor/RPO forced-landing override. Set by CLANDF (which also grants landing
    /// clearance). When true, FinalApproachPhase and LandingPhase suppress every automatic
    /// go-around and actively drive the aircraft to a touchdown regardless of energy state.
    /// Persists across the FinalApproach→Landing boundary; cleared by GA, CTOC, or touchdown.
    /// </summary>
    public bool ForceLanding { get; set; }

    /// <summary>
    /// The runway exit is finishing a CLANDF rollout. Set by <c>LandingPhase</c> when a forced landing completes on the
    /// ground (it stopped on the runway, or handed off to the exit); read by <c>RunwayExitPhase</c>, whose backstop
    /// backtracks to an exit behind a stopped aircraft only when this is set — any other aircraft stopped on the runway
    /// asks for a back-taxi instead (AIM 4-3-21.a). Cleared when the runway exit ends or any tower or ground command
    /// reaches the aircraft.
    /// </summary>
    public bool ForcedRollout { get; set; }

    /// <summary>
    /// When set, the aircraft is doing pattern work (MLT/MRT/CTOMLT/CTOMRT).
    /// Each approach defaults to touch-and-go and re-enters the pattern.
    /// Null means the aircraft will full-stop on landing.
    /// </summary>
    public PatternDirection? TrafficDirection { get; set; }

    /// <summary>
    /// The runway the <em>next</em> circuit is built for — what <c>PhaseRunner</c>'s auto-cycle reads
    /// (<c>PatternRunway ?? AssignedRunway</c>). Its relationship to <see cref="AssignedRunway"/> depends
    /// on how it was set:
    ///
    /// <list type="bullet">
    /// <item>Cross-runway closed traffic (takeoff 33, pattern for 28R): equal to
    /// <see cref="AssignedRunway"/>, which the clearance moved to the pattern runway so the
    /// circuit/final/landing phases use it, while <see cref="DepartureRunway"/> holds the takeoff
    /// runway.</item>
    /// <item>An option clearance's pattern modifier (<c>COPT MLT 28L</c> on the 28R final): the runway
    /// the aircraft will transition to. <see cref="AssignedRunway"/> stays the runway the clearance is
    /// being flown on until the auto-cycle builds the transition circuit and moves it.</item>
    /// </list>
    ///
    /// Every writer that changes the runway an aircraft is patterning on writes both fields (or leaves
    /// the transition to the auto-cycle); a stale one builds the next circuit on the runway the aircraft
    /// was told to leave.
    /// </summary>
    public RunwayInfo? PatternRunway { get; set; }

    /// <summary>
    /// The altitude change an option clearance's modifier made for a <see cref="PatternRunway"/> other than the
    /// runway being flown. The modifier describes the pattern after this approach, so this circuit keeps its
    /// altitude; <c>PhaseRunner</c> applies this to <see cref="AircraftPattern.AltitudeOverrideFt"/> when it
    /// builds the runway-transition circuit, and clears it. An entry to the armed runway itself uses it up the
    /// same way (<c>PatternCommandHandler.TryEnterPattern</c>). Null when none is armed. It is voided by a
    /// go-around, a closed-traffic takeoff, a pattern runway change, a plain altitude command, a runway-less
    /// modifier's altitude and a runway-less <c>MLT</c>/<c>MRT</c> with an altitude.
    /// </summary>
    public DeferredPatternAltitude? TransitionPatternAltitude { get; set; }

    /// <summary>
    /// When set, the lineup/takeoff phases use this runway instead of
    /// <see cref="AssignedRunway"/>. Populated for cross-runway closed traffic where the
    /// takeoff runway differs from the pattern runway (e.g., takeoff 33, pattern for 28R):
    /// <see cref="AssignedRunway"/> is the pattern runway (28R) while this is the departure
    /// runway (33). Null for same-runway operations, where lineup/takeoff fall back to
    /// <see cref="AssignedRunway"/>. Read by the four takeoff-family phases (LineUpPhase, LinedUpAndWaitingPhase,
    /// TakeoffPhase, RejectedTakeoffPhase); occupancy readers use <see cref="CurrentPhaseRunway"/> or <see cref="RunsOn"/>
    /// instead, since this field outlives the takeoff.
    /// </summary>
    public RunwayInfo? DepartureRunway { get; set; }

    /// <summary>
    /// The runway the current phase runs on: <see cref="DepartureRunway"/> (else <see cref="AssignedRunway"/>) while the
    /// aircraft is lining up, lined up and waiting, taking off or rejecting a takeoff, <see cref="AssignedRunway"/> otherwise.
    /// <see cref="DepartureRunway"/> outlives the takeoff (follow and training readers need it afterwards), so an aircraft
    /// cleared for cross-runway closed traffic is judged on its departure runway only while it is still on it, and on its pattern
    /// runway for every circuit after.
    /// </summary>
    public RunwayInfo? CurrentPhaseRunway =>
        (CurrentPhase is LineUpPhase or LinedUpAndWaitingPhase or TakeoffPhase or RejectedTakeoffPhase)
            ? DepartureRunway ?? AssignedRunway
            : AssignedRunway;

    /// <summary>
    /// The current phase runs on the same pavement as <paramref name="runway"/>: <see cref="CurrentPhaseRunway"/> names it, or
    /// the current phase is a <see cref="HoldingInPositionPhase"/>, which runs on no runway of its own, and either
    /// <see cref="DepartureRunway"/> or <see cref="AssignedRunway"/> names it. A caller judging a holding aircraft adds its own
    /// pavement test.
    /// </summary>
    public bool RunsOn(RunwayInfo runway) =>
        (CurrentPhase is HoldingInPositionPhase)
            ? NamesSamePavement(DepartureRunway, runway) || NamesSamePavement(AssignedRunway, runway)
            : NamesSamePavement(CurrentPhaseRunway, runway);

    private static bool NamesSamePavement(RunwayInfo? own, RunwayInfo runway) =>
        (own is not null) && Data.NavigationDatabase.AirportIdsMatch(own.AirportId, runway.AirportId) && own.Id.Overlaps(runway.Id);

    /// <summary>
    /// Controller-requested exit preference. Set by EL/ER/EXIT commands,
    /// consumed by RunwayExitPhase. Persists across phase transitions so
    /// the controller can issue the command during approach or rollout.
    /// </summary>
    public ExitPreference? RequestedExit { get; set; }

    /// <summary>
    /// The exit a TAXI on the landing rollout implied and instructed: while <see cref="RequestedExit"/> is this very instance, a
    /// go-around drops it with the stored route, and an exit instruction the controller gave on its own (another instance) stays.
    /// </summary>
    public ExitPreference? TaxiImpliedExit { get; set; }

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

    /// <summary>
    /// Taxiways the crew has told the controller it is unable to exit at on this landing (P/CG UNABLE: the controller may already
    /// be acting on it; 7110.65 3-10-9.a, AIM 4-3-21.a). Shared by <c>LandingPhase</c> and <c>RunwayExitPhase</c>, so every exit
    /// search on the landing skips them while the aircraft is rolling — the rollout's and the runway
    /// exit's own re-searches after the hand-off — until a new instruction names one again. Once stopped on the runway the aircraft
    /// may taxi forward to a given-up taxiway ahead like any other exit (the "unable" was about rollout speed). Cleared when a landing
    /// starts and when the runway exit completes (<c>RunwayExitPhase.CompleteExit</c>). Compared without regard to case.
    /// </summary>
    public HashSet<string> GivenUpExitTaxiways { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The crew has made its one "unable to exit" call on this landing (AIM 4-3-21.a): no exit it can take ahead, so it stops on the
    /// runway. Shared by <c>LandingPhase</c>, which calls as the final stop on the remaining runway begins, and <c>RunwayExitPhase</c>,
    /// which calls when it stops with no exit or brakes to such a stop itself, so the call is made once across both. Cleared when a
    /// landing starts and when the runway exit completes (<c>RunwayExitPhase.CompleteExit</c>).
    /// </summary>
    public bool ReportedNoExitAhead { get; set; }

    /// <summary>
    /// The no-exit stop on the remaining runway has engaged on this landing, in <c>LandingPhase</c> or <c>RunwayExitPhase</c>: from
    /// then on every exit search of both phases judges its pick by the reach test, so an exit that frees mid-stop is taken only when
    /// it can be made, across a disengage, the hand-off and a restore. Set the first time the stop engages; cleared with
    /// <see cref="ReportedNoExitAhead"/> (when a landing starts and when the runway exit completes).
    /// </summary>
    public bool NoExitReachTestActive { get; set; }

    /// <summary>
    /// The runway a <c>CROSS</c> given on the landing rollout or during the runway exit pre-clears (as typed, e.g. <c>19R</c>):
    /// the runway the exit's taxiway leads across. Consumed by <c>RunwayExitPhase</c> as it builds the route to that runway, its
    /// clearance moved onto the route's bar, so only one of the two holds it at a time; dropped when the exit leads elsewhere,
    /// when a TAXI is stored behind the exit, and when a landing starts.
    /// </summary>
    public string? PreClearedCrossing { get; set; }

    /// <summary>
    /// The exit side preference a rollout <c>CROSS</c> set toward the runway it pre-cleared (<see cref="PreClearedCrossing"/>): while
    /// <see cref="RequestedExit"/> is this very instance, a TAXI stored behind the exit drops it with the crossing. Null otherwise.
    /// </summary>
    public ExitPreference? CrossExitPreference { get; set; }

    public int CurrentIndex { get; private set; }

    /// <summary>
    /// The full phase list. Mutable — commands and clearances may insert,
    /// remove, or replace future phases at any time.
    /// </summary>
    public List<Phase> Phases { get; } = [];

    public Phase? CurrentPhase => CurrentIndex >= 0 && CurrentIndex < Phases.Count ? Phases[CurrentIndex] : null;

    public bool IsComplete => CurrentIndex >= Phases.Count;

    /// <summary>
    /// The phase a boundary hold stands in front of: the phase directly behind the current one when the current phase is an
    /// <see cref="AirspaceBoundaryHoldPhase"/> and that phase is still Active (<see cref="InsertHoldInFront"/>). It is not
    /// ticked while held. Null when no hold stands or the phase behind it has not started. Derived from the list alone, so a
    /// snapshot round-trip keeps it.
    /// </summary>
    public Phase? HeldPhase =>
        (CurrentPhase is AirspaceBoundaryHoldPhase)
        && (CurrentIndex + 1 < Phases.Count)
        && (Phases[CurrentIndex + 1] is { Status: PhaseStatus.Active } held)
            ? held
            : null;

    /// <summary>
    /// The phase the aircraft is flying for anyone asking what it is doing: <see cref="HeldPhase"/> while a boundary hold stands
    /// in front of one, else <see cref="CurrentPhase"/>. Read-only; the phase that ticks is always <see cref="CurrentPhase"/>.
    /// </summary>
    public Phase? EffectivePhase => HeldPhase ?? CurrentPhase;

    /// <summary>
    /// The index of <see cref="EffectivePhase"/>: one past <see cref="CurrentIndex"/> while a boundary hold stands in front of
    /// <see cref="HeldPhase"/>, else <see cref="CurrentIndex"/> (a lifted hold is out of the list, so a lift reads the current
    /// index). Read-only, for a reader that scans the phases queued after the one the aircraft is flying.
    /// </summary>
    public int EffectiveIndex => (HeldPhase is null) ? CurrentIndex : CurrentIndex + 1;

    /// <summary>
    /// Takes the boundary hold standing in front of <see cref="HeldPhase"/> out of the list, so the held phase is
    /// <see cref="CurrentPhase"/> while a controller command is judged, until <see cref="PutBackHold"/> or
    /// <see cref="HoldLift.Discard"/> ends the lift. No hook runs, and the hold keeps its status (Pending when it has not
    /// started yet). Null, and nothing changes, when no held pair stands; the dispatcher keeps the lifted hold on the
    /// aircraft (<see cref="AircraftState.LiftedBoundaryHold"/>) and lifts nothing in a nested dispatch.
    /// </summary>
    internal HoldLift? LiftHold()
    {
        if ((HeldPhase is not { } held) || (CurrentPhase is not AirspaceBoundaryHoldPhase hold))
        {
            return null;
        }

        Phases.RemoveAt(CurrentIndex);
        return new HoldLift(this, hold, held, CurrentIndex);
    }

    /// <summary>
    /// Puts the hold <paramref name="lift"/> took out of this list back in front of the held phase, with no <c>OnStart</c>
    /// and its state kept, when the held phase is still current at the same index, the same instance and Active, and answers
    /// true. Otherwise the command ended, advanced or replaced the held phase: nothing changes and the answer is false, for the
    /// caller to <see cref="HoldLift.Discard"/> the hold. False too for a lift already ended.
    /// </summary>
    internal bool PutBackHold(HoldLift lift)
    {
        bool heldSurvived =
            !lift.Ended && (CurrentIndex == lift.Index) && ReferenceEquals(CurrentPhase, lift.Held) && (lift.Held.Status == PhaseStatus.Active);
        if (!heldSurvived)
        {
            return false;
        }

        Phases.Insert(CurrentIndex, lift.Hold);
        lift.Ended = true;
        return true;
    }

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
        // A phase held behind a boundary hold is still the one the aircraft is established on (or climbing out in).
        Phase? phase = EffectivePhase;
        if (phase is null or GoAroundPhase)
        {
            return false;
        }

        // A lateral-only localizer join (JFAC/JLOC) holds assigned altitude — MVA still applies.
        if (ActiveApproach is { LateralInterceptOnly: true })
        {
            return false;
        }

        return (phase is FinalApproachPhase) || ((ActiveApproach is not null) && ApproachCommandHandler.IsArrivalApproachPhase(phase));
    }

    public void Add(Phase phase) => Phases.Add(phase);

    /// <summary>
    /// Starts the chain at its first phase. A chain installed while a tug is still attached — a command cleared a tow
    /// that was moving, and <see cref="PushbackPhase.OnEnd"/> left the tug on — first gets a
    /// <see cref="TugReleasePhase"/>, which brakes the aircraft to rest before the chain's own first phase starts.
    /// </summary>
    public void Start(PhaseContext ctx)
    {
        if ((ctx.Aircraft.Ground.TowbarTrueHeading is not null) && ctx.Aircraft.IsOnGround)
        {
            Phases.Insert(0, TugReleasePhase.For(ctx.Aircraft, Phases.Count > 0 ? Phases[0] : null));
        }

        CurrentIndex = 0;
        if (CurrentPhase is { } phase)
        {
            phase.Status = PhaseStatus.Active;
            phase.OnStart(ctx);
        }
    }

    /// <summary>
    /// Ends the current phase Completed and starts the next. Throws <see cref="InvalidOperationException"/> while a boundary
    /// hold stands in front of <see cref="HeldPhase"/>: that pair ends through <see cref="EndHold"/>, and a command acts on the
    /// held phase only with the hold lifted (<see cref="LiftHold"/>).
    /// </summary>
    public void AdvanceToNext(PhaseContext ctx)
    {
        ThrowIfHoldStands(nameof(AdvanceToNext));
        Phase? current = CurrentPhase;
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

    /// <summary>
    /// Puts an Orbit-mode <paramref name="hold"/> in front of the running phase: the hold goes in at <see cref="CurrentIndex"/>,
    /// Pending, and is started on the next tick by the pending-start path; the running phase moves one place back and stays
    /// Active as <see cref="HeldPhase"/>, neither ticked nor ended. Throws <see cref="InvalidOperationException"/> unless the
    /// hold is in Orbit mode and Pending, the current phase is Active and no hold stands.
    /// </summary>
    public void InsertHoldInFront(AirspaceBoundaryHoldPhase hold)
    {
        if (hold.Mode != AirspaceHoldMode.Orbit)
        {
            throw new InvalidOperationException("Only an Orbit-mode boundary hold goes in front of a running phase");
        }

        if (hold.Status != PhaseStatus.Pending)
        {
            throw new InvalidOperationException($"A boundary hold goes in front of a running phase Pending, not {hold.Status}");
        }

        if (CurrentPhase is not { Status: PhaseStatus.Active } running)
        {
            throw new InvalidOperationException("A boundary hold goes in front of an Active current phase only");
        }

        if (running is AirspaceBoundaryHoldPhase)
        {
            throw new InvalidOperationException("A boundary hold already stands: a hold never holds a hold");
        }

        Phases.Insert(CurrentIndex, hold);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("[PhaseList] {Hold} inserted in front of {Held} at index {Index}", hold.Name, running.Name, CurrentIndex);
        }
    }

    /// <summary>
    /// Ends the boundary hold standing in front of <see cref="HeldPhase"/>: a started hold ends Completed, a hold that never
    /// started (still Pending) is taken out of the list with no <c>OnStart</c> and no <c>OnEnd</c>; either way the held phase
    /// becomes current. <see cref="HoldResumeKind.InPlace"/> leaves it Active with no second <c>OnStart</c>;
    /// <see cref="HoldResumeKind.RestartCopy"/> ends it Completed and puts <see cref="HoldResume.Copy"/> in its place, Pending,
    /// for the pending-start path to start. Never <see cref="AdvanceToNext"/>, which would start the held phase again. Throws
    /// <see cref="InvalidOperationException"/> when no phase is held, or when the copy is the held phase itself or not Pending;
    /// nothing changes then.
    /// </summary>
    public void EndHold(PhaseContext ctx, HoldResume resume)
    {
        if ((HeldPhase is not { } held) || (CurrentPhase is not { } hold))
        {
            throw new InvalidOperationException("No boundary hold stands in front of a held phase");
        }

        Phase? copy = (resume.Kind == HoldResumeKind.RestartCopy) ? ValidRestartCopy(resume, held) : null;
        EndOrDropHold(ctx, hold);

        if (copy is not null)
        {
            held.Status = PhaseStatus.Completed;
            held.OnEnd(ctx, PhaseStatus.Completed);
            Phases[CurrentIndex] = copy;
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("[PhaseList] {Hold} ended, {Held} resumes {Kind}", hold.Name, held.Name, resume.Kind);
        }
    }

    private static Phase ValidRestartCopy(HoldResume resume, Phase held)
    {
        // HoldResume.RestartWith refuses a null copy.
        Phase copy = resume.Copy!;
        if (ReferenceEquals(copy, held))
        {
            throw new InvalidOperationException("A restart copy must be a new phase, not the held phase itself");
        }

        if (copy.Status != PhaseStatus.Pending)
        {
            throw new InvalidOperationException($"A restart copy goes in Pending, not {copy.Status}");
        }

        return copy;
    }

    /// <summary>
    /// A started hold ends Completed and the index moves onto the held phase; a hold that never started leaves the list, which
    /// brings the held phase to the current index, and gets no <c>OnEnd</c> (it had no <c>OnStart</c>).
    /// </summary>
    private void EndOrDropHold(PhaseContext ctx, Phase hold)
    {
        if (hold.Status == PhaseStatus.Pending)
        {
            Phases.RemoveAt(CurrentIndex);
            return;
        }

        hold.Status = PhaseStatus.Completed;
        hold.OnEnd(ctx, PhaseStatus.Completed);
        CurrentIndex++;
    }

    /// <summary>
    /// Inserts <paramref name="phase"/> right after the current phase. Throws <see cref="InvalidOperationException"/> while a
    /// boundary hold stands in front of <see cref="HeldPhase"/> (lift it first, <see cref="LiftHold"/>).
    /// </summary>
    public void InsertAfterCurrent(Phase phase)
    {
        ThrowIfHoldStands(nameof(InsertAfterCurrent));
        int insertAt = CurrentIndex + 1;
        if (insertAt > Phases.Count)
        {
            insertAt = Phases.Count;
        }
        Phases.Insert(insertAt, phase);
    }

    /// <summary>
    /// Insert a sequence of phases immediately after the current phase,
    /// preserving the order of the provided list. Throws <see cref="InvalidOperationException"/> while a boundary hold stands
    /// in front of <see cref="HeldPhase"/> (lift it first, <see cref="LiftHold"/>).
    /// </summary>
    public void InsertAfterCurrent(IEnumerable<Phase> phases)
    {
        ThrowIfHoldStands(nameof(InsertAfterCurrent));
        int insertAt = CurrentIndex + 1;
        if (insertAt > Phases.Count)
        {
            insertAt = Phases.Count;
        }
        Phases.InsertRange(insertAt, phases);
    }

    /// <summary>
    /// Remove all pending phases after the current one and replace them.
    /// The current (active) phase is not affected. Throws <see cref="InvalidOperationException"/> while a boundary hold stands
    /// in front of <see cref="HeldPhase"/>, which would remove the held phase with no <c>OnEnd</c> (lift it first,
    /// <see cref="LiftHold"/>).
    /// </summary>
    public void ReplaceUpcoming(IEnumerable<Phase> phases)
    {
        ThrowIfHoldStands(nameof(ReplaceUpcoming));
        int removeFrom = CurrentIndex + 1;
        if (removeFrom < Phases.Count)
        {
            Phases.RemoveRange(removeFrom, Phases.Count - removeFrom);
        }
        Phases.AddRange(phases);
    }

    /// <summary>
    /// Refuses <paramref name="operation"/> on a list with a boundary hold standing in front of <see cref="HeldPhase"/>: the
    /// operation is legal only on the lifted list, where the held phase is current.
    /// </summary>
    private void ThrowIfHoldStands(string operation)
    {
        if (HeldPhase is { } held)
        {
            throw new InvalidOperationException(
                $"{operation} with a boundary hold standing in front of {held.Name}: lift the hold first, so the held phase is current"
            );
        }
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
            Phase skipped = CurrentPhase;
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
    /// End the active phase with Skipped status and mark list complete. A phase held behind a boundary hold
    /// (<see cref="HeldPhase"/>) is Active too, so it also ends Skipped, after the hold; the pending phases are marked Skipped
    /// without an <c>OnEnd</c>, as they never started.
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
            Phase phase = Phases[i];
            bool started = phase.Status == PhaseStatus.Active;
            phase.Status = PhaseStatus.Skipped;
            if (started)
            {
                phase.OnEnd(ctx, PhaseStatus.Skipped);
            }
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
            LineUpIntersectionTaxiway = dto.LineUpIntersectionTaxiway,
            LandingClearance = dto.LandingClearance.HasValue ? (ClearanceType)dto.LandingClearance.Value : null,
            ClearedRunwayId = dto.ClearedRunwayId,
            ForceLanding = dto.ForceLanding,
            ForcedRollout = dto.ForcedRollout,
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
            TransitionPatternAltitude = dto.TransitionPatternAltitude is { } deferred ? new DeferredPatternAltitude(deferred.AltitudeFt) : null,
            DepartureRunway = dto.DepartureRunway is not null ? RunwayInfo.FromSnapshot(dto.DepartureRunway) : null,
            ActiveApproach = dto.ActiveApproach is not null ? ApproachClearance.FromSnapshot(dto.ActiveApproach) : null,
            LahsoHoldShort = dto.LahsoHoldShort is not null ? LahsoTarget.FromSnapshot(dto.LahsoHoldShort) : null,
        };
        if (dto.GivenUpExitTaxiways is not null)
        {
            list.GivenUpExitTaxiways.UnionWith(dto.GivenUpExitTaxiways);
        }

        list.ReportedNoExitAhead = dto.ReportedNoExitAhead;
        list.NoExitReachTestActive = dto.NoExitReachTestActive;
        list.PreClearedCrossing = dto.PreClearedCrossing;
        list.CrossExitPreference = (dto.RequestedExitIsCrossSide == true) ? list.RequestedExit : null;

        list.StoredTaxiDestinationRunway = list.TaxiRoute is not null ? dto.StoredTaxiDestinationRunway : null;
        list.TaxiImpliedExit = (dto.RequestedExitIsTaxiImplied == true) ? list.RequestedExit : null;
        if ((dto.TaxiRoute is not null) && (list.TaxiRoute is null))
        {
            if (Log.IsEnabled(LogLevel.Information))
            {
                Log.LogInformation(
                    "[PhaseList] stored taxi route dropped on restore: {Reason}",
                    groundLayout is null ? "no ground layout" : "its nodes are not in the layout"
                );
            }
        }

        foreach (PhaseDto phaseDto in dto.Phases)
        {
            Phase phase = RestorePhase(phaseDto, groundLayout);
            if (phase is LandingPhase landing)
            {
                landing.ShareRequestedExit(list.RequestedExit);
            }

            list.Add(phase);
        }

        // A release judges the commands it does not answer by the phase behind it (TugReleasePhase.Next).
        for (int i = 0; i < list.Phases.Count - 1; i++)
        {
            if (list.Phases[i] is TugReleasePhase release)
            {
                release.Next = list.Phases[i + 1];
            }
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
            CrossingRunwayPhaseDto d => CrossingRunwayPhase.FromSnapshot(d, groundLayout),
            ClearRunwayPhaseDto d => ClearRunwayPhase.FromSnapshot(d),
            AirTaxiPhaseDto d => AirTaxiPhase.FromSnapshot(d),
            HoldingInPositionPhaseDto d => HoldingInPositionPhase.FromSnapshot(d),
            HoldingAfterPushbackPhaseDto d => HoldingAfterPushbackPhase.FromSnapshot(d),
            HoldingAfterExitPhaseDto d => HoldingAfterExitPhase.FromSnapshot(d),
            AtParkingPhaseDto d => AtParkingPhase.FromSnapshot(d),
            TaxiingPhaseDto d => TaxiingPhase.FromSnapshot(d),
            FollowingPhaseDto d => FollowingPhase.FromSnapshot(d, groundLayout),
            PushbackPhaseDto d => PushbackPhase.FromSnapshot(d),
            PushbackToSpotPhaseDto d => PushbackPhase.FromSnapshot(SpotPushbackAsMove(d, groundLayout)),
            TugReleasePhaseDto d => TugReleasePhase.FromSnapshot(d),
            RunwayExitPhaseDto d => RunwayExitPhase.FromSnapshot(d, groundLayout),
            HelicopterLandingPhaseDto d => HelicopterLandingPhase.FromSnapshot(d),
            HelicopterApproachPhaseDto d => HelicopterApproachPhase.FromSnapshot(d),
            GoAroundPhaseDto d => GoAroundPhase.FromSnapshot(d),
            HelicopterTakeoffPhaseDto d => HelicopterTakeoffPhase.FromSnapshot(d),
            LowApproachPhaseDto d => LowApproachPhase.FromSnapshot(d),
            RunwayHoldingPhaseDto d => RunwayHoldingPhase.FromSnapshot(d),
            MakeTurnPhaseDto d => MakeTurnPhase.FromSnapshot(d),
            VfrHoldPhaseDto d => VfrHoldPhase.FromSnapshot(d),
            AirspaceBoundaryHoldPhaseDto d => AirspaceBoundaryHoldPhase.FromSnapshot(d),
            MilitaryRoutePhaseDto d => MilitaryRoutePhase.FromSnapshot(d),
            AerialRefuelingAnchorPhaseDto d => AerialRefuelingAnchorPhase.FromSnapshot(d),
            STurnPhaseDto d => STurnPhase.FromSnapshot(d),
            StopAndGoPhaseDto d => StopAndGoPhase.FromSnapshot(d),
            TouchAndGoPhaseDto d => TouchAndGoPhase.FromSnapshot(d),
            TakeoffPhaseDto d => TakeoffPhase.FromSnapshot(d),
            RejectedTakeoffPhaseDto d => RejectedTakeoffPhase.FromSnapshot(d),
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

    /// <summary>
    /// Converts a pre-#233 spot pushback — a multi-segment reverse along a taxi route, pivoting in place at the
    /// corners — into the pushback it would be issued as today: one tug move onto the spot, expressed in the legacy
    /// fields <see cref="PushbackPhase.FromSnapshot"/> already reads for a pre-tug-move snapshot, which finish the
    /// move from the live pose on the first tick. The route's last node is the spot, so the whole route collapses to
    /// a single move onto it; the intermediate nodes and the pivots are dropped. One that had arrived restores
    /// completed. Without a ground layout, or when the node is not in one, the current segment's end stands in and
    /// the aircraft stops short of the spot, there.
    /// </summary>
    private static PushbackPhaseDto SpotPushbackAsMove(PushbackToSpotPhaseDto dto, AirportGroundLayout? groundLayout)
    {
        var target = new LatLon(dto.TargetLat, dto.TargetLon);
        int? spotNodeId = dto.Route?.Segments is { Count: > 0 } segments ? segments[^1].ToNodeId : null;
        if ((groundLayout is { } layout) && (spotNodeId is { } nodeId) && layout.Nodes.TryGetValue(nodeId, out GroundNode? spotNode))
        {
            target = spotNode.Position;
        }
        else
        {
            Log.LogWarning(
                "[Restore] A pre-#233 spot pushback names destination node {NodeId}, which this ground layout does not have; the "
                    + "restored push stops at its current segment's end ({Lat:F6},{Lon:F6}) instead of the spot",
                spotNodeId?.ToString() ?? "none",
                target.Lat,
                target.Lon
            );
        }

        return new PushbackPhaseDto
        {
            Status = dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            Requirements = dto.Requirements,
            Kind = PushbackLegKind.Push,
            LegacyTargetLatitude = target.Lat,
            LegacyTargetLongitude = target.Lon,
            LegacyTargetHeading = dto.TargetHeading,
            LegacyReachedTarget = dto.ReachedFinalNode,
            TimeSinceLastLog = dto.TimeSinceLastLog,
        };
    }

    public PhaseListDto ToSnapshot() =>
        new()
        {
            AssignedRunway = AssignedRunway?.ToSnapshot(),
            TaxiRoute = TaxiRoute?.ToSnapshot(),
            DepartureClearance = DepartureClearance?.ToSnapshot(),
            LineUpIntersectionTaxiway = LineUpIntersectionTaxiway,
            LandingClearance = LandingClearance.HasValue ? (int)LandingClearance.Value : null,
            ClearedRunwayId = ClearedRunwayId,
            ForceLanding = ForceLanding,
            ForcedRollout = ForcedRollout,
            TrafficDirection = TrafficDirection.HasValue ? (int)TrafficDirection.Value : null,
            PatternRunway = PatternRunway?.ToSnapshot(),
            TransitionPatternAltitude = TransitionPatternAltitude is { } deferred
                ? new DeferredPatternAltitudeDto { AltitudeFt = deferred.AltitudeFt }
                : null,
            DepartureRunway = DepartureRunway?.ToSnapshot(),
            RequestedExit = RequestedExit?.Side is not null ? (int)RequestedExit.Side.Value : null,
            RequestedExitTaxiway = RequestedExit?.Taxiway,
            ActiveApproach = ActiveApproach?.ToSnapshot(),
            LahsoHoldShort = LahsoHoldShort?.ToSnapshot(),
            GivenUpExitTaxiways = (GivenUpExitTaxiways.Count > 0) ? [.. GivenUpExitTaxiways.Order(StringComparer.OrdinalIgnoreCase)] : null,
            ReportedNoExitAhead = ReportedNoExitAhead,
            NoExitReachTestActive = NoExitReachTestActive,
            PreClearedCrossing = PreClearedCrossing,
            RequestedExitIsCrossSide = ((CrossExitPreference is not null) && ReferenceEquals(CrossExitPreference, RequestedExit)) ? true : null,
            StoredTaxiDestinationRunway = StoredTaxiDestinationRunway,
            RequestedExitIsTaxiImplied = ((TaxiImpliedExit is not null) && ReferenceEquals(TaxiImpliedExit, RequestedExit)) ? true : null,
            CurrentIndex = CurrentIndex,
            Phases = [.. Phases.Select(p => p is LandingPhase landing ? landing.ToSnapshot(RequestedExit) : p.ToSnapshot())],
        };
}
