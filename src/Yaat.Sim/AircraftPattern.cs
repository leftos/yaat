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
/// A pattern modifier (EXT leg / SA / MNA) issued before its target circuit exists.
/// <see cref="TargetLeg"/> is the leg the modifier applies to (the extended leg for ExtendLeg; Downwind
/// for Short/NormalApproach) and gates which queued entries it can attach to.
/// <see cref="ForNextCircuit"/> partitions the one slot between its two consumers: true when the modifier
/// targets the circuit the cycle terminator in progress will append (consumed by PhaseRunner), false when
/// it targets the circuit a still-queued pattern entry builds (consumed by TryEnterPattern).
/// </summary>
public sealed record PendingEntryModifier(PendingEntryModifierKind Kind, PatternEntryLeg TargetLeg, bool ForNextCircuit);

/// <summary>
/// A landing/option clearance (CLAND/TG/SG/LA/COPT) issued before the pattern-entry command that
/// will build its approach has fired — e.g. CLAND while ERD 28R sits queued behind DCT VPCOL.
/// <see cref="RunwayId"/> is the runway the clearance names, adopted from the queued entry when the
/// controller gave a bare verb. It is never empty: a clearance states its runway (7110.65 §3-10-5.a),
/// the pilot reads it back (AIM §4-4-7.b.4), and a circuit built for a different runway voids the
/// clearance — none of which works without one, so arming is refused when neither side names a runway.
///
/// <para><see cref="PatternRunwayId"/> and <see cref="PatternAltitudeFt"/> carry the option clearance's
/// MLT/MRT modifier (<c>COPT MLT 28L</c>): the pattern the aircraft climbs out into after the clearance,
/// applied to the circuit the queued entry builds. Both null when the clearance named no pattern runway
/// or altitude. The modifier's <em>direction</em> is not here — it is stamped on
/// <see cref="AircraftPattern.TrafficDirection"/> when the clearance is armed, like every other
/// side effect that lives outside the PhaseList.</para>
///
/// <para><see cref="AtOrAboveFt"/> is a restricted low approach's altitude (<c>LA 600</c>, 7110.65 3-10-10), installed on the
/// low approach the queued entry builds; null for every other clearance and for an unrestricted low approach.</para>
/// </summary>
public sealed record PendingLandingClearance(
    ClearanceType Clearance,
    string RunwayId,
    string? PatternRunwayId,
    int? PatternAltitudeFt,
    int? AtOrAboveFt
);

/// <summary>
/// A landing-family clearance as issued, before it is armed or installed: its type, and for a restricted low approach
/// (<c>LA 600</c>, 7110.65 3-10-10) the at-or-above altitude (feet MSL) and the altitude token as typed, which a refusal
/// quotes. <see cref="AtOrAboveFt"/> and <see cref="AtOrAboveText"/> are null for every other clearance and for an
/// unrestricted low approach.
/// </summary>
public sealed record ArmedClearance(ClearanceType Clearance, int? AtOrAboveFt, string? AtOrAboveText);

/// <summary>
/// Per-aircraft pattern overrides. Null fields fall back to category defaults
/// (downwind offset, pattern altitude). Set by CM/DM during pattern mode and
/// by MLT/MRT/CTO/GA when the controller specifies an explicit altitude.
/// </summary>
public class AircraftPattern
{
    /// <summary>Override for pattern downwind offset distance (NM). Null uses category default.</summary>
    public double? SizeOverrideNm { get; set; }

    /// <summary>
    /// Override for pattern altitude (feet MSL). Null uses category-based default. Written only through
    /// <see cref="SetAltitudeOverride"/>.
    /// </summary>
    public double? AltitudeOverrideFt { get; private set; }

    /// <summary>
    /// Sets <see cref="AltitudeOverrideFt"/> and brings a queued, not yet started field crossing in
    /// <paramref name="phases"/> into line with it (<see cref="PatternBuilder.ReconcileFieldCrossingPrefix"/>): whether that
    /// crossing needs a teardrop re-entry depends on the pattern altitude, so every write goes through here.
    /// </summary>
    /// <param name="altitudeFt">The new pattern altitude (feet MSL), or null to fall back to the runway's own rules.</param>
    /// <param name="phases">The aircraft's phase list, or null when it has none.</param>
    /// <param name="category">The aircraft's category, used to resolve the crossing altitude.</param>
    public void SetAltitudeOverride(double? altitudeFt, PhaseList? phases, AircraftCategory category)
    {
        AltitudeOverrideFt = altitudeFt;
        if (phases is not null)
        {
            PatternBuilder.ReconcileFieldCrossingPrefix(phases, category, altitudeFt);
        }
    }

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

    /// <summary>
    /// The warning owed for a circuit that crosses another runway, set when the circuit is first built and voiced on the next
    /// tick, where the solo and RPO routing is known. Laps and waypoint rebuilds never set it.
    /// </summary>
    public PatternRunwayCrossing? PendingRunwayCrossing { get; set; }

    /// <summary>The pattern runways and sides already warned about, as "28L Left", so each is said once.</summary>
    private readonly SortedSet<string> _warnedRunwayCrossings = new(StringComparer.Ordinal);

    /// <summary>
    /// Records the crossing of a newly built <paramref name="circuit"/> as owed, if it has one and its runway and side have not
    /// been warned about yet; a lap of the same circuit never warns again, and a circuit noted again before its warning is voiced
    /// replaces the one owed.
    /// </summary>
    public void NoteRunwayCrossing(IEnumerable<Phase> circuit)
    {
        PatternWaypoints? crossingCircuit = circuit
            .Select(AirborneFollowHelper.PatternWaypointsOf)
            .FirstOrDefault(w => w?.RunwayCrossing is not null);
        if (crossingCircuit?.RunwayCrossing is not { } crossing)
        {
            return;
        }

        if (!_warnedRunwayCrossings.Contains(WarnedKey(crossing, crossingCircuit.Direction)))
        {
            PendingRunwayCrossing = crossing;
        }
    }

    /// <summary>Marks the runway and side of <paramref name="crossing"/> warned about, once its warning is voiced.</summary>
    public void MarkRunwayCrossingWarned(PatternRunwayCrossing crossing, PatternDirection direction) =>
        _warnedRunwayCrossings.Add(WarnedKey(crossing, direction));

    private static string WarnedKey(PatternRunwayCrossing crossing, PatternDirection direction) => $"{crossing.PatternRunway} {direction}";

    public AircraftPatternDto ToSnapshot() =>
        new()
        {
            SizeOverrideNm = SizeOverrideNm,
            AltitudeOverrideFt = AltitudeOverrideFt,
            TrafficDirection = TrafficDirection.HasValue ? (byte)TrafficDirection.Value : null,
            ExtendNextUpwind = ExtendNextUpwind ? true : null,
            PendingEntryModifierKind = PendingEntryModifier is not null ? (byte)PendingEntryModifier.Kind : null,
            PendingEntryModifierLeg = PendingEntryModifier is not null ? (byte)PendingEntryModifier.TargetLeg : null,
            PendingEntryModifierForNextCircuit = PendingEntryModifier is { ForNextCircuit: true } ? true : null,
            PendingLandingClearanceType = PendingLandingClearance is not null ? (byte)PendingLandingClearance.Clearance : null,
            PendingLandingClearanceRunwayId = PendingLandingClearance?.RunwayId,
            PendingLandingClearancePatternRunwayId = PendingLandingClearance?.PatternRunwayId,
            PendingLandingClearancePatternAltitudeFt = PendingLandingClearance?.PatternAltitudeFt,
            PendingLandingClearanceAtOrAboveFt = PendingLandingClearance?.AtOrAboveFt,
            PendingCrossingPatternRunway = PendingRunwayCrossing?.PatternRunway,
            PendingCrossingCrossedRunway = PendingRunwayCrossing?.CrossedRunway,
            PendingCrossingTightestFlyableNm = PendingRunwayCrossing?.TightestFlyableNm,
            WarnedRunwayCrossings = _warnedRunwayCrossings.Count > 0 ? [.. _warnedRunwayCrossings] : null,
        };

    public static AircraftPattern FromSnapshot(AircraftPatternDto dto)
    {
        var pattern = new AircraftPattern
        {
            SizeOverrideNm = dto.SizeOverrideNm,
            AltitudeOverrideFt = dto.AltitudeOverrideFt,
            TrafficDirection = dto.TrafficDirection.HasValue ? (PatternDirection)dto.TrafficDirection.Value : null,
            ExtendNextUpwind = dto.ExtendNextUpwind ?? false,
            PendingEntryModifier =
                dto.PendingEntryModifierKind is { } kind && dto.PendingEntryModifierLeg is { } leg
                    ? new PendingEntryModifier((PendingEntryModifierKind)kind, (PatternEntryLeg)leg, dto.PendingEntryModifierForNextCircuit ?? false)
                    : null,
            PendingLandingClearance =
                dto.PendingLandingClearanceType is { } clearance && !string.IsNullOrEmpty(dto.PendingLandingClearanceRunwayId)
                    ? new PendingLandingClearance(
                        (ClearanceType)clearance,
                        dto.PendingLandingClearanceRunwayId,
                        dto.PendingLandingClearancePatternRunwayId,
                        dto.PendingLandingClearancePatternAltitudeFt,
                        dto.PendingLandingClearanceAtOrAboveFt
                    )
                    : null,
            PendingRunwayCrossing =
                dto.PendingCrossingPatternRunway is { } patternRunway
                && dto.PendingCrossingCrossedRunway is { } crossedRunway
                && dto.PendingCrossingTightestFlyableNm is { } tightestNm
                    ? new PatternRunwayCrossing(patternRunway, crossedRunway, tightestNm)
                    : null,
        };

        pattern._warnedRunwayCrossings.UnionWith(dto.WarnedRunwayCrossings ?? []);
        return pattern;
    }
}
