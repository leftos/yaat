using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Commands;

/// <summary>
/// Pure helpers shared between the server's live track-command pipeline and Sim's
/// in-engine replay applier. AS-prefix extraction, scenario-first TCP→Owner
/// resolution with optional ARTCC-config fallback, owner→TCP lookup. No I/O,
/// no broadcast, no per-connection state.
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
    /// Resolves a TCP code (e.g. "3Y") to a TrackOwner. Checks the scenario's student
    /// TCP first (CRC registers as the student position; multiple positions can share
    /// a TCP, e.g. OAK_TWR and OAK_GND both use 3O), then falls through to ATC positions
    /// in the scenario. When <paramref name="artccConfig"/> is supplied, falls back to
    /// ARTCC-wide TCP/ERAM resolution.
    /// </summary>
    public static TrackOwner? ResolveTcpToOwner(SimScenarioState scenario, string tcpCode, ArtccConfigRoot? artccConfig = null)
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

        if (artccConfig is null)
        {
            return null;
        }

        var facilityId = scenario.StudentPosition?.FacilityId;
        if (!string.IsNullOrEmpty(facilityId))
        {
            var resolved = artccConfig.ResolveTcpCode(facilityId, tcpCode) ?? artccConfig.ResolveEramCode(tcpCode);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        // ERAM→STARS prefixed handoff codes (e.g. "Q2B" = NCT Boulder) carry the facility prefix, so they
        // self-identify the receiving facility and resolve even without a student facility.
        return artccConfig.ResolveEramToStarsHandoffCode(tcpCode);
    }

    /// <summary>
    /// Resolves a TCP code to a <see cref="Tcp"/> by searching the scenario's ATC
    /// positions, then the ARTCC-wide TCP table when <paramref name="artccConfig"/>
    /// is supplied.
    /// </summary>
    public static Tcp? FindTcpByCode(SimScenarioState scenario, string tcpCode, ArtccConfigRoot? artccConfig = null)
    {
        foreach (var atc in scenario.AtcPositions)
        {
            if (atc.Tcp is not null && string.Equals(atc.Tcp.ToString(), tcpCode, StringComparison.OrdinalIgnoreCase))
            {
                return atc.Tcp;
            }
        }

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
}
