using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;

namespace Yaat.Sim.Commands;

/// <summary>
/// The one TCP→owner chain and the one identity resolver, shared by the live server, server-side reconstruction
/// and the in-engine replay applier. AS-prefix extraction, scenario-first TCP→Owner resolution with the ARTCC-config
/// fallback chain read from <see cref="SimScenarioState.ArtccConfig"/>, owner→TCP lookup, and connection→identity
/// resolution over a <see cref="PositionSelections"/>. No I/O, no broadcast, no state of its own.
/// </summary>
public static class TrackResolver
{
    /// <summary>
    /// Splits a command string into its AS-prefix override (if any) and the remainder.
    /// "AS 3Y ACCEPT" → ("ACCEPT", "3Y"). Standalone "AS 3Y" returns the original
    /// command and a null override so callers parse it as a normal SetActivePositionCommand.
    /// </summary>
    public static (string Remainder, string? AsOverrideTcp) ExtractAsPrefix(string command)
    {
        var trimmed = command.TrimStart();
        var upper = trimmed.ToUpperInvariant();
        if (!upper.StartsWith("AS ", StringComparison.Ordinal))
        {
            return (command, null);
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            // Standalone "AS" or "AS 3Y" — handled by SetActivePositionCommand path
            return (command, null);
        }

        var tcpCode = parts[1].ToUpperInvariant();
        var remainder = string.Join(' ', parts.Skip(2));
        return (remainder, tcpCode);
    }

    /// <summary>
    /// Resolves a TCP code (e.g. "3Y") to a TrackOwner. Checks the scenario's student TCP first (CRC registers as
    /// the student position; multiple positions can share a TCP, e.g. OAK_TWR and OAK_GND both use 3O), then the
    /// scenario's ATC positions, then — when the scenario carries an ARTCC config — the student facility's TCP
    /// table, ERAM codes (<c>C44</c>), STARS interfacility handoff codes entered from the student facility
    /// (<c>`31H</c>), and finally ERAM→STARS prefixed codes (<c>Q2B</c>), which name their receiving facility and so
    /// resolve without a student facility. A code no table knows is tried as a position callsign (<c>OAK_GND</c>) — the
    /// form a CRC position outside the student facility, or one sharing its TCP with another position, is selected by.
    /// </summary>
    public static TrackOwner? ResolveTcpToOwner(SimScenarioState scenario, string tcpCode)
    {
        if (
            scenario.StudentPosition is not null
            && scenario.StudentTcp is not null
            && string.Equals(scenario.StudentTcp.ToString(), tcpCode, StringComparison.OrdinalIgnoreCase)
        )
        {
            return scenario.StudentPosition;
        }

        foreach (var atc in scenario.AtcPositions)
        {
            if (atc.Tcp is not null && string.Equals(atc.Tcp.ToString(), tcpCode, StringComparison.OrdinalIgnoreCase))
            {
                return atc.Owner;
            }
        }

        var artccConfig = scenario.ArtccConfig;
        if (artccConfig is null)
        {
            return null;
        }

        var facilityId = scenario.StudentPosition?.FacilityId;
        if (!string.IsNullOrEmpty(facilityId))
        {
            var resolved =
                artccConfig.ResolveTcpCode(facilityId, tcpCode)
                ?? artccConfig.ResolveEramCode(tcpCode)
                ?? artccConfig.ResolveStarsHandoffCode(facilityId, tcpCode);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return artccConfig.ResolveEramToStarsHandoffCode(tcpCode) ?? ResolvePositionName(artccConfig, tcpCode);
    }

    /// <summary>
    /// The position named by a callsign (<c>OAK_GND</c>) or, where a config carries several positions of one callsign
    /// on different TCPs, by <c>{callsign}@{code}</c> (<c>NCT_APP@1M</c>) with the code <see cref="AsPrefixCode"/> gives
    /// the position. Null when no position matches.
    /// </summary>
    private static TrackOwner? ResolvePositionName(ArtccConfigRoot artccConfig, string name)
    {
        var at = name.IndexOf('@');
        if (at < 0)
        {
            var position = artccConfig.FindPositionByCallsign(name);
            return position is null ? null : artccConfig.ResolvePosition(position.Id);
        }

        var callsign = name[..at];
        var code = name[(at + 1)..];
        foreach (var candidate in artccConfig.FindPositionsByCallsign(callsign))
        {
            var owner = artccConfig.ResolvePosition(candidate.Id);
            if ((owner is not null) && AsPrefixCode(owner).Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                return owner;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a TCP code to a <see cref="Tcp"/> by searching the scenario's ATC positions, then the student
    /// facility's TCP table in the scenario's ARTCC config.
    /// </summary>
    public static Tcp? FindTcpByCode(SimScenarioState scenario, string tcpCode)
    {
        foreach (var atc in scenario.AtcPositions)
        {
            if (atc.Tcp is not null && string.Equals(atc.Tcp.ToString(), tcpCode, StringComparison.OrdinalIgnoreCase))
            {
                return atc.Tcp;
            }
        }

        var artccConfig = scenario.ArtccConfig;
        if (artccConfig is null)
        {
            return null;
        }

        var facilityId = scenario.StudentPosition?.FacilityId;
        if (string.IsNullOrEmpty(facilityId))
        {
            return null;
        }

        return artccConfig.FindTcpByCode(facilityId, tcpCode);
    }

    /// <summary>
    /// The code an <c>AS {code}</c> prefix names <paramref name="owner"/> by, so a command issued as that owner
    /// round-trips through <see cref="ResolveTcpToOwner"/> on replay: <c>C{sector}</c> for an ERAM sector,
    /// <c>{subset}{sector}</c> for a STARS TCP, else the owner's callsign.
    /// </summary>
    public static string AsPrefixCode(TrackOwner owner)
    {
        if ((owner.OwnerType == TrackOwnerType.Eram) && (owner.SectorId is not null))
        {
            return $"C{owner.SectorId}";
        }

        if ((owner.Subset is not null) && (owner.SectorId is not null))
        {
            return $"{owner.Subset}{owner.SectorId}";
        }

        return owner.Callsign;
    }

    /// <summary>
    /// Returns the TCP corresponding to a given owner by searching the scenario's
    /// ATC positions, then falling back to the student TCP if the callsign matches.
    /// </summary>
    public static Tcp? FindTcpForOwner(TrackOwner owner, SimScenarioState scenario)
    {
        foreach (var atc in scenario.AtcPositions)
        {
            if (atc.Owner.Callsign == owner.Callsign)
            {
                return atc.Tcp;
            }
        }

        if (scenario.StudentPosition is not null && scenario.StudentTcp is not null && owner.Callsign == scenario.StudentPosition.Callsign)
        {
            return scenario.StudentTcp;
        }

        return null;
    }

    /// <summary>
    /// The identity a command from <paramref name="connectionId"/> acts as: the AS override when given; else, for an
    /// AI-controller connection, the position its connection id names (resolved from the ARTCC config, so it needs
    /// no student facility and no AS prefix); else the position the connection selected earlier; else the student.
    ///
    /// The AI branch is checked before the selected-position map on purpose. An AI position works the position its
    /// connection id names and cannot select another, while the map is shared with replay and keyed only by
    /// connection id — so a recorded active-position selection carrying an AI connection id would otherwise
    /// displace the live AI's identity.
    /// </summary>
    public static TrackOwner? ResolveIdentity(SimScenarioState scenario, PositionSelections selections, string connectionId, string? asOverrideTcp)
    {
        if (asOverrideTcp is not null)
        {
            return ResolveTcpToOwner(scenario, asOverrideTcp);
        }

        if (AiConnectionId.TryParse(connectionId, out var positionId) && scenario.ArtccConfig?.ResolvePosition(positionId) is { } aiPosition)
        {
            return aiPosition;
        }

        if (selections.TryGet(connectionId, out var selected))
        {
            return selected;
        }

        return scenario.StudentPosition;
    }
}
