using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation;

/// <summary>
/// One attended CRC position: the vNAS position id the client signed on to, plus the <see cref="TrackOwner"/> and
/// <see cref="Tcp"/> the room's ARTCC configuration resolves it to. Both are null for an id the configuration does not
/// carry — the entry then matches by position id alone.
/// </summary>
public sealed record AttendedPosition(string PositionId, TrackOwner? Owner, Tcp? Tcp);

/// <summary>
/// Which CRC positions are being worked, as engine state. The live server derives the set from its connection registry
/// and records it (<see cref="RecordedAttendanceChange"/>); a replay, a rewind and a from-scratch reconstruction get it
/// from the log and the snapshot, so every run kind answers the tick gates and the consolidation bodies the same way.
///
/// <para>
/// Written only under the room's tick gate — the live sync at the head of a second, the action router applying a record,
/// and a snapshot restore all run mutually exclusive with the tick — so it carries no lock. (The server's session restore
/// is the one path that mutates a registered room outside that gate; it predates this type and is a backlog item.)
/// </para>
/// </summary>
public sealed class Attendance
{
    private static readonly ILogger Log = SimLog.CreateLogger("Attendance");

    private readonly List<AttendedPosition> _positions = [];

    /// <summary>The attended position ids, distinct and ordinal-sorted — the form the record and the snapshot carry.</summary>
    public IReadOnlyList<string> PositionIds => _positions.Select(p => p.PositionId).ToList();

    /// <summary>
    /// Replaces the whole set with <paramref name="positionIds"/>, resolving each through the room's ARTCC
    /// configuration. An id the configuration cannot place is kept id-only: the room's ARTCC is the authority on what a
    /// position is, and dropping the id would silently under-report attendance.
    /// </summary>
    public void Replace(IReadOnlyList<string> positionIds, ArtccConfigRoot? config)
    {
        _positions.Clear();
        foreach (var positionId in positionIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
        {
            var owner = config?.ResolvePosition(positionId);
            var tcp = config?.GetTcpForPosition(positionId);
            if (owner is null && tcp is null)
            {
                Log.LogDebug("Attended position {PositionId} is not in the room's ARTCC config; keeping it by id only", positionId);
            }

            _positions.Add(new AttendedPosition(positionId, owner, tcp));
        }
    }

    /// <summary>Drops every entry — a fresh replay starts with nobody attended until the log says otherwise.</summary>
    public void Clear() => _positions.Clear();

    /// <summary>Whether an attended position holds this TCP.</summary>
    public bool IsTcpAttended(Tcp tcp) => _positions.Any(p => p.Tcp is not null && p.Tcp.Id == tcp.Id);

    /// <summary>
    /// The attended TCP that currently owns <paramref name="tcp"/> through the student facility's consolidation
    /// hierarchy and the manual overrides, or null when the scenario carries no ARTCC configuration, no student
    /// facility, or no attended owner can be resolved.
    /// </summary>
    public Tcp? ConsolidationOwnerOf(Tcp tcp, SimScenarioState scenario, ConsolidationState overrides)
    {
        var facilityId = scenario.StudentPosition?.FacilityId ?? "";
        if (scenario.ArtccConfig is not { } config || string.IsNullOrEmpty(facilityId))
        {
            return null;
        }

        return config.GetConsolidationOwner(facilityId, tcp, IsTcpAttended, overrides);
    }

    /// <summary>
    /// Whether a CRC client is working <paramref name="tcp"/>, either directly or because the TCP's airspace has
    /// consolidated under an attended position.
    /// </summary>
    public bool IsTcpControlledByCrc(Tcp tcp, SimScenarioState scenario, ConsolidationState overrides)
    {
        if (IsTcpAttended(tcp))
        {
            return true;
        }

        var owner = ConsolidationOwnerOf(tcp, scenario, overrides);
        return owner is not null && IsTcpAttended(owner);
    }
}
