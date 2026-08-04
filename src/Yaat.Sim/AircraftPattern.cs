using Yaat.Sim.Phases;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>Which modifier a queued EXT/SA/MNA pre-arm applies to a pattern entry once it builds.</summary>
public enum PendingEntryModifierKind
{
    ExtendLeg,
    ShortApproach,
    NormalApproach,
}

/// <summary>
/// A pattern modifier (EXT leg / SA / MNA) issued before its target pattern-entry command
/// (ERD/ELD/ERC/ELC/…) has built its circuit. <see cref="TargetLeg"/> is the leg the modifier
/// applies to (the extended leg for ExtendLeg; Downwind for Short/NormalApproach) and gates which
/// queued entries it can attach to.
/// </summary>
public sealed record PendingEntryModifier(PendingEntryModifierKind Kind, PatternEntryLeg TargetLeg);

/// <summary>
/// A landing/option clearance (CLAND/TG/SG/LA/COPT) issued before the pattern-entry command that
/// will build its approach has fired — e.g. CLAND while ERD 28R sits queued behind DCT VPCOL.
/// <see cref="RunwayId"/> is the runway the clearance names, adopted from the queued entry when the
/// controller gave a bare verb. It is never empty: a clearance states its runway (7110.65 §3-10-5.a),
/// the pilot reads it back (AIM §4-4-7.b.4), and a circuit built for a different runway voids the
/// clearance — none of which works without one, so arming is refused when neither side names a runway.
/// </summary>
public sealed record PendingLandingClearance(ClearanceType Clearance, string RunwayId);

/// <summary>
/// Per-aircraft pattern overrides. Null fields fall back to category defaults
/// (downwind offset, pattern altitude). Set by CM/DM during pattern mode and
/// by MLT/MRT/CTO/GA when the controller specifies an explicit altitude.
/// </summary>
public class AircraftPattern
{
    /// <summary>Override for pattern downwind offset distance (NM). Null uses category default.</summary>
    public double? SizeOverrideNm { get; set; }

    /// <summary>Override for pattern altitude (feet MSL). Null uses category-based default.</summary>
    public double? AltitudeOverrideFt { get; set; }

    /// <summary>
    /// Persistent pattern direction set by MLT/MRT/CTO MLT/CTO MRT/CTOMLT/CTOMRT/GA MLT/GA MRT.
    /// Survives phase-list clearing by FH/TR/TL vectors so that a subsequent re-entry
    /// (auto-cycle after T&G, GoAround re-enter, etc.) honors the controller's last
    /// explicit pattern-direction intent. Cleared by CLAND/LAHSO (full-stop intent).
    /// Null = no persistent direction set; PhaseRunner falls back to PhaseList.TrafficDirection.
    /// </summary>
    public PatternDirection? TrafficDirection { get; set; }

    /// <summary>
    /// Set by EXT (bare or EXT UPWIND) when issued during a non-pattern-leg phase
    /// (FinalApproach/TouchAndGo/etc.) for an aircraft cycling in the pattern. Consumed
    /// by PhaseRunner the next time it appends a circuit: the first UpwindPhase of the
    /// new circuit gets IsExtended=true and this flag is cleared. Single-shot.
    /// </summary>
    public bool ExtendNextUpwind { get; set; }

    /// <summary>
    /// Pending EXT/SA/MNA pre-arm for a pattern entry (ERD/ELD/…) that is queued but has not built
    /// its circuit yet — e.g. EXT DOWNWIND issued while ERD 28R sits queued behind DCT VPCOL.
    /// Consumed by <see cref="Yaat.Sim.Commands.PatternCommandHandler"/> when TryEnterPattern builds
    /// the circuit: the matching newly-built leg gets the modifier and this flag is cleared. Single-shot.
    /// Null = no pending modifier.
    /// </summary>
    public PendingEntryModifier? PendingEntryModifier { get; set; }

    /// <summary>
    /// Pending landing/option clearance pre-issued for a pattern entry (ERD/ELD/…) that is queued but
    /// has not built its circuit yet — e.g. CLAND issued while ERD 28R sits queued behind DCT VPCOL.
    /// Consumed by <see cref="Yaat.Sim.Commands.PatternCommandHandler"/> when TryEnterPattern builds the
    /// circuit: it becomes the circuit's standing clearance and this slot is cleared. Single-shot.
    /// Null = no pre-issued clearance.
    /// </summary>
    public PendingLandingClearance? PendingLandingClearance { get; set; }

    public AircraftPatternDto ToSnapshot() =>
        new()
        {
            SizeOverrideNm = SizeOverrideNm,
            AltitudeOverrideFt = AltitudeOverrideFt,
            TrafficDirection = TrafficDirection.HasValue ? (byte)TrafficDirection.Value : null,
            ExtendNextUpwind = ExtendNextUpwind ? true : null,
            PendingEntryModifierKind = PendingEntryModifier is not null ? (byte)PendingEntryModifier.Kind : null,
            PendingEntryModifierLeg = PendingEntryModifier is not null ? (byte)PendingEntryModifier.TargetLeg : null,
            PendingLandingClearanceType = PendingLandingClearance is not null ? (byte)PendingLandingClearance.Clearance : null,
            PendingLandingClearanceRunwayId = PendingLandingClearance?.RunwayId,
        };

    public static AircraftPattern FromSnapshot(AircraftPatternDto dto) =>
        new()
        {
            SizeOverrideNm = dto.SizeOverrideNm,
            AltitudeOverrideFt = dto.AltitudeOverrideFt,
            TrafficDirection = dto.TrafficDirection.HasValue ? (PatternDirection)dto.TrafficDirection.Value : null,
            ExtendNextUpwind = dto.ExtendNextUpwind ?? false,
            PendingEntryModifier =
                dto.PendingEntryModifierKind is { } kind && dto.PendingEntryModifierLeg is { } leg
                    ? new PendingEntryModifier((PendingEntryModifierKind)kind, (PatternEntryLeg)leg)
                    : null,
            PendingLandingClearance =
                dto.PendingLandingClearanceType is { } clearance && !string.IsNullOrEmpty(dto.PendingLandingClearanceRunwayId)
                    ? new PendingLandingClearance((ClearanceType)clearance, dto.PendingLandingClearanceRunwayId)
                    : null,
        };
}
