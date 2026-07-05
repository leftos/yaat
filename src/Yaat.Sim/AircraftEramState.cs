using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// ERAM-side per-track display state mirrored to CRC. Includes leader/dwell
/// overrides, the ERAM-tier interim/procedure altitude pile, and pending pointouts.
/// </summary>
public class AircraftEramState
{
    public bool IsDwellLocked { get; set; }
    public bool IsVci { get; set; }

    /// <summary>Leader direction override (1=SW .. 9=NE per CRC enum; 5=Default). Null = sector default.</summary>
    public int? LeaderDirection { get; set; }

    /// <summary>Leader length override (0-8 lines). Null = sector default.</summary>
    public int? LeaderLength { get; set; }

    /// <summary>Interim altitude issued via ERAM QQ, in hundreds of feet (the unit CRC renders directly).</summary>
    public int? InterimAltitude { get; set; }

    /// <summary>Local interim altitude (QQ L&lt;alt&gt;), in hundreds of feet.</summary>
    public int? LocalInterimAltitude { get; set; }

    /// <summary>Procedure altitude from QQ P&lt;alt&gt;, in hundreds of feet.</summary>
    public int? ProcedureAltitude { get; set; }

    /// <summary>Controller-entered altitude (QQ R&lt;alt&gt; / auto-track cleared), in hundreds of feet.</summary>
    public int? ControllerEnteredAltitude { get; set; }

    /// <summary>
    /// ERAM FDB line-4 (HSF) assigned heading, set via the <c>QS &lt;heading&gt;</c> command (docs/crc/eram.md
    /// §QS Command). A manual controller annotation — NOT the aircraft's actual assigned heading. Null = unset.
    /// </summary>
    public string? AssignedHeading { get; set; }

    /// <summary>ERAM FDB line-4 (HSF) assigned speed, set via <c>QS /&lt;speed&gt;</c>. A manual annotation. Null = unset.</summary>
    public string? AssignedSpeed { get; set; }

    /// <summary>ERAM FDB line-4 (HSF) free text, set via the <c>QS `&lt;text&gt;</c> backtick form. Distinct from the
    /// STARS scratchpad. Null = unset.</summary>
    public string? FreeText { get; set; }

    /// <summary>Active ERAM pointouts.</summary>
    public List<EramPointoutState> Pointouts { get; set; } = [];

    public List<Tcp> ForcedPointoutsTo { get; set; } = [];

    /// <summary>
    /// QH-frozen track (CRC ERAM QH display function, <c>docs/crc/eram.md</c> §Freezing a Track): the data
    /// block is parked at <see cref="FrozenLat"/>/<see cref="FrozenLon"/> and unpaired from the target. A
    /// frozen track shows FRZN, holds its snapshot altitude, and is exempt from coast and every auto-removal
    /// path until re-started (TRACK), which revalidates it per 7110.65 §5-2-15 ("track start from … frozen status").
    /// </summary>
    public bool IsFrozen { get; set; }

    /// <summary>Frozen location (from the QH command). Null unless <see cref="IsFrozen"/>.</summary>
    public double? FrozenLat { get; set; }
    public double? FrozenLon { get; set; }

    /// <summary>Frozen (snapshot) altitude in hundreds of feet, captured at freeze time.</summary>
    public int? FrozenAltitude { get; set; }

    /// <summary>
    /// The sector that owned the Track immediately before a handoff was accepted (or the Track was
    /// force-taken). While the accept window is open the previous owner's FDB shows the Field-E accepted
    /// indicator <c>Oxxx</c>/<c>Kxxx</c>/<c>OUNK</c> (docs/crc/eram.md §Data Blocks; CRC
    /// <c>FdbRenderObject</c> renders <c>RecentHandoffPeer</c> as the same-facility abbreviation context and
    /// the current <see cref="AircraftTrack.Owner"/> — the acceptor — as the sector shown). Null when no
    /// accept is being confirmed. Cleared on drop; overwritten by the next accept; the 30 s window is
    /// enforced by the broadcast against <see cref="RecentHandoffAcceptedAtSeconds"/>.
    /// </summary>
    public TrackOwner? RecentHandoffPreviousOwner { get; set; }

    /// <summary><c>Kxxx</c> (accepted with <c>/OK</c>, i.e. force-taken) when true; <c>Oxxx</c> when false.</summary>
    public bool RecentHandoffWasForced { get; set; }

    /// <summary>Sim-elapsed seconds at which the handoff was accepted; the accepted indicator expires 30 s later.</summary>
    public double? RecentHandoffAcceptedAtSeconds { get; set; }

    public AircraftEramStateDto ToSnapshot() =>
        new()
        {
            IsDwellLocked = IsDwellLocked,
            IsVci = IsVci,
            LeaderDirection = LeaderDirection,
            LeaderLength = LeaderLength,
            InterimAltitude = InterimAltitude,
            LocalInterimAltitude = LocalInterimAltitude,
            ProcedureAltitude = ProcedureAltitude,
            ControllerEnteredAltitude = ControllerEnteredAltitude,
            AssignedHeading = AssignedHeading,
            AssignedSpeed = AssignedSpeed,
            FreeText = FreeText,
            IsFrozen = IsFrozen,
            FrozenLat = FrozenLat,
            FrozenLon = FrozenLon,
            FrozenAltitude = FrozenAltitude,
            RecentHandoffPreviousOwner = RecentHandoffPreviousOwner?.ToSnapshot(),
            RecentHandoffWasForced = RecentHandoffWasForced,
            RecentHandoffAcceptedAtSeconds = RecentHandoffAcceptedAtSeconds,
            Pointouts =
                Pointouts.Count > 0
                    ? Pointouts
                        .Select(p => new EramPointoutStateDto
                        {
                            OriginatingFacility = p.OriginatingFacility,
                            OriginatingSector = p.OriginatingSector,
                            ReceivingFacility = p.ReceivingFacility,
                            ReceivingSector = p.ReceivingSector,
                            IsAcknowledged = p.IsAcknowledged,
                            IsRecipientSuppressed = p.IsRecipientSuppressed,
                            IsRSideCleared = p.IsRSideCleared,
                            IsDSideCleared = p.IsDSideCleared,
                        })
                        .ToList()
                    : null,
            ForcedPointoutsTo = ForcedPointoutsTo.Count > 0 ? ForcedPointoutsTo.Select(t => t.ToSnapshot()).ToList() : null,
        };

    public static AircraftEramState FromSnapshot(AircraftEramStateDto dto) =>
        new()
        {
            IsDwellLocked = dto.IsDwellLocked,
            IsVci = dto.IsVci,
            LeaderDirection = dto.LeaderDirection,
            LeaderLength = dto.LeaderLength,
            InterimAltitude = dto.InterimAltitude,
            LocalInterimAltitude = dto.LocalInterimAltitude,
            ProcedureAltitude = dto.ProcedureAltitude,
            ControllerEnteredAltitude = dto.ControllerEnteredAltitude,
            AssignedHeading = dto.AssignedHeading,
            AssignedSpeed = dto.AssignedSpeed,
            FreeText = dto.FreeText,
            IsFrozen = dto.IsFrozen,
            FrozenLat = dto.FrozenLat,
            FrozenLon = dto.FrozenLon,
            FrozenAltitude = dto.FrozenAltitude,
            RecentHandoffPreviousOwner = dto.RecentHandoffPreviousOwner is null ? null : TrackOwner.FromSnapshot(dto.RecentHandoffPreviousOwner),
            RecentHandoffWasForced = dto.RecentHandoffWasForced,
            RecentHandoffAcceptedAtSeconds = dto.RecentHandoffAcceptedAtSeconds,
            Pointouts = dto.Pointouts is null
                ? []
                : dto
                    .Pointouts.Select(p => new EramPointoutState
                    {
                        OriginatingFacility = p.OriginatingFacility,
                        OriginatingSector = p.OriginatingSector,
                        ReceivingFacility = p.ReceivingFacility,
                        ReceivingSector = p.ReceivingSector,
                        IsAcknowledged = p.IsAcknowledged,
                        IsRecipientSuppressed = p.IsRecipientSuppressed,
                        IsRSideCleared = p.IsRSideCleared,
                        IsDSideCleared = p.IsDSideCleared,
                    })
                    .ToList(),
            ForcedPointoutsTo = dto.ForcedPointoutsTo is not null ? dto.ForcedPointoutsTo.Select(Tcp.FromSnapshot).ToList() : [],
        };
}
