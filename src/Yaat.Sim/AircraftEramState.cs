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

    /// <summary>Active ERAM pointouts.</summary>
    public List<EramPointoutState> Pointouts { get; set; } = [];

    public List<Tcp> ForcedPointoutsTo { get; set; } = [];

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
