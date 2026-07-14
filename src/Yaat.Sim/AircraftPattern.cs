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

    public AircraftPatternDto ToSnapshot() =>
        new()
        {
            SizeOverrideNm = SizeOverrideNm,
            AltitudeOverrideFt = AltitudeOverrideFt,
            TrafficDirection = TrafficDirection.HasValue ? (byte)TrafficDirection.Value : null,
            ExtendNextUpwind = ExtendNextUpwind ? true : null,
            PendingEntryModifierKind = PendingEntryModifier is not null ? (byte)PendingEntryModifier.Kind : null,
            PendingEntryModifierLeg = PendingEntryModifier is not null ? (byte)PendingEntryModifier.TargetLeg : null,
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
        };
}
