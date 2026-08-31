using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.LiveTraffic;

/// <summary>
/// Turns a sample's real-world ownership (facility + position from the feed) into the shadow's
/// <see cref="TrackOwner"/> and pending-handoff display, resolving against the scenario's ARTCC config so a
/// feed sector lines up with the configured vNAS position (and its callsign) when one matches. Runs on every
/// sample in both brains — samples are recorded, so replays reproduce the same ownership. The feed yields
/// silently: a controller's TRACK clears <see cref="AircraftTrack.OwnerFromLiveFeed"/> (any ordinary owner
/// write does) and the feed stays out until a DROP returns the track to unowned. The feed's real-world
/// scratchpads ride the same gate: they land on the shadow's STARS state until a controller takes the track.
/// </summary>
public static class LiveTrafficOwnerResolver
{
    public static void Apply(AircraftState ac, LiveTrafficSample sample, SimScenarioState scenario)
    {
        var track = ac.Track;
        if ((track.Owner is not null) && !track.OwnerFromLiveFeed)
        {
            return;
        }

        var owner = Resolve(scenario, sample.OwnerFacility, sample.OwnerSector);
        track.SetOwnerFromLiveFeed(owner);
        var pending = (owner is null) ? null : Resolve(scenario, sample.PendingOwnerFacility, sample.PendingOwnerSector);
        if (pending is not null)
        {
            track.HandoffPeer = pending;
            track.OnHandoff = true;
            track.HandoffAccepted = false;
            track.HandoffInitiatedAt ??= scenario.ElapsedSeconds;
        }
        else
        {
            track.HandoffPeer = null;
            track.OnHandoff = false;
            track.HandoffAccepted = false;
            track.HandoffInitiatedAt = null;
        }

        // Null = the feed has never said (leave whatever is there); empty = the feed cleared the pad.
        if (sample.Scratchpad1 is not null)
        {
            ac.Stars.Scratchpad1 = (sample.Scratchpad1.Length == 0) ? null : sample.Scratchpad1;
        }

        if (sample.Scratchpad2 is not null)
        {
            ac.Stars.Scratchpad2 = (sample.Scratchpad2.Length == 0) ? null : sample.Scratchpad2;
        }

        if (sample.LeaderLineDirection is not null)
        {
            // The int encoding is CRC's LeaderDirection enum (SW=1 … NE=9, 5=default); empty/unknown = back to default.
            ac.Stars.GlobalLeaderDirection = sample.LeaderLineDirection switch
            {
                "SW" => 1,
                "S" => 2,
                "SE" => 3,
                "W" => 4,
                "E" => 6,
                "NW" => 7,
                "N" => 8,
                "NE" => 9,
                _ => null,
            };
        }

        if (sample.AssignedBeaconCode is { } assignedBeacon)
        {
            ac.Transponder.AssignedCode = assignedBeacon;
        }

        if (sample.Pointouts is { } pointouts)
        {
            ApplyPointouts(ac, pointouts);
        }
    }

    /// <summary>
    /// Mirrors the feed's point-outs onto the ERAM state wholesale — the feed carries no ack/suppress state, and
    /// an unchanged set is left alone so the display objects don't churn. Locked: hub threads mutate the same list.
    /// </summary>
    private static void ApplyPointouts(AircraftState ac, IReadOnlyList<LiveTrafficPointout> pointouts)
    {
        var eram = ac.Eram.Pointouts;
        lock (eram)
        {
            bool unchanged =
                (eram.Count == pointouts.Count)
                && eram.Zip(pointouts)
                    .All(pair =>
                        (pair.First.OriginatingFacility == pair.Second.FromFacility)
                        && (pair.First.OriginatingSector == pair.Second.FromSector)
                        && (pair.First.ReceivingFacility == pair.Second.ToFacility)
                        && (pair.First.ReceivingSector == pair.Second.ToSector)
                    );
            if (unchanged)
            {
                return;
            }

            eram.Clear();
            foreach (var p in pointouts)
            {
                eram.Add(
                    new EramPointoutState
                    {
                        OriginatingFacility = p.FromFacility,
                        OriginatingSector = p.FromSector,
                        ReceivingFacility = p.ToFacility,
                        ReceivingSector = p.ToSector,
                    }
                );
            }
        }
    }

    /// <summary>
    /// A STARS owner is a TRACON facility + cps (subset digit + sector); an ERAM owner is a centre (the sample's
    /// facility is the ARTCC id, or null for TAIS's single-letter centre alias) + sector. A configured position
    /// whose TCP / ERAM sector matches contributes its real callsign; otherwise a synthetic owner still carries
    /// the right facility/subset/sector so the datablock symbol renders.
    /// </summary>
    public static TrackOwner? Resolve(SimScenarioState scenario, string? facility, string? sector)
    {
        if (string.IsNullOrEmpty(sector))
        {
            return null;
        }

        // US centres are Zxx; TRACON ids never are. TAIS's one-letter centre alias arrives with a null facility.
        bool eram =
            (facility is null)
            || string.Equals(facility, scenario.ArtccId, StringComparison.OrdinalIgnoreCase)
            || ((facility.Length == 3) && (facility[0] == 'Z'));
        if (eram)
        {
            var centre = facility ?? scenario.ArtccId ?? "";
            var node = FindFacility(scenario.ArtccConfig?.Facility, centre) ?? scenario.ArtccConfig?.Facility;
            var position = node?.Positions.FirstOrDefault(p =>
                string.Equals(p.EramConfiguration?.SectorId, sector, StringComparison.OrdinalIgnoreCase)
            );
            return new TrackOwner(position?.Callsign ?? $"{centre}_{sector}", centre, null, sector, TrackOwnerType.Eram);
        }

        if ((sector.Length < 2) || !char.IsAsciiDigit(sector[0]))
        {
            return null;
        }

        int subset = sector[0] - '0';
        var sectorId = sector[1..];
        var starsNode = FindFacility(scenario.ArtccConfig?.Facility, facility!);
        var tcp = starsNode?.StarsConfiguration?.Tcps.FirstOrDefault(t =>
            (t.Subset == subset) && string.Equals(t.SectorId, sectorId, StringComparison.OrdinalIgnoreCase)
        );
        // A TCP's position can live below the STARS facility itself (a tower position holds its TRACON's TCP).
        var starsPosition = (tcp is null) ? null : FindPositionByTcp(starsNode!, tcp.Id);
        return new TrackOwner(starsPosition?.Callsign ?? $"{facility}_{sector}", facility, subset, sectorId, TrackOwnerType.Stars);
    }

    private static PositionConfig? FindPositionByTcp(FacilityConfig node, string tcpId)
    {
        if (node.Positions.FirstOrDefault(p => string.Equals(p.StarsConfiguration?.TcpId, tcpId, StringComparison.Ordinal)) is { } own)
        {
            return own;
        }

        foreach (var child in node.ChildFacilities)
        {
            if (FindPositionByTcp(child, tcpId) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static FacilityConfig? FindFacility(FacilityConfig? node, string id)
    {
        if (node is null)
        {
            return null;
        }

        if (string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.ChildFacilities)
        {
            if (FindFacility(child, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
