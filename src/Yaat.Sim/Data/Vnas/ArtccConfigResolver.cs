using Yaat.Sim.Data;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Pure-function resolvers over an <see cref="ArtccConfigRoot"/>. Moved out of
/// yaat-server's <c>ArtccConfigService</c> so Sim owns the ARTCC navigation
/// primitives — the server keeps the loader (HTTP fetch + disk cache) and
/// delegates resolution here.
///
/// All methods are pure: they walk the immutable facility tree and never touch
/// I/O, network, or per-room state. Server-shaped DTO builders
/// (<c>BuildOpenPositionDto</c>, <c>BuildFlightStripsConfigDto</c>, etc.) stay
/// in yaat-server because they emit CRC wire-format types.
/// </summary>
public static class ArtccConfigResolver
{
    // --- Position lookup ---

    /// <summary>
    /// Looks up a position by its vNAS ULID and returns the owning facility
    /// metadata along with the position config. Walks the entire facility tree.
    /// </summary>
    public static (string? FacilityId, string? FacilityName, string? FacilityType, PositionConfig? Position) FindPosition(
        this ArtccConfigRoot config,
        string positionId
    ) => FindPositionInFacility(config.Facility, positionId);

    private static (string?, string?, string?, PositionConfig?) FindPositionInFacility(FacilityConfig facility, string positionId)
    {
        foreach (var pos in facility.Positions)
        {
            if (pos.Id == positionId)
            {
                return (facility.Id, facility.Name, facility.Type, pos);
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            var result = FindPositionInFacility(child, positionId);
            if (result.Item4 is not null)
            {
                return result;
            }
        }

        return (null, null, null, null);
    }

    /// <summary>
    /// Looks up a position by its CRC-style callsign (e.g. "FAT_F_APP", "OAK_TWR")
    /// across the facility tree. Returns the first match in load order, or null
    /// if no position has that callsign.
    /// </summary>
    public static PositionConfig? FindPositionByCallsign(this ArtccConfigRoot config, string callsign)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return null;
        }

        return FindPositionByCallsignRec(config.Facility, callsign);
    }

    private static PositionConfig? FindPositionByCallsignRec(FacilityConfig facility, string callsign)
    {
        foreach (var pos in facility.Positions)
        {
            if (pos.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase))
            {
                return pos;
            }
        }
        foreach (var child in facility.ChildFacilities)
        {
            var found = FindPositionByCallsignRec(child, callsign);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Looks up a position by its assigned radio frequency, in MHz. Match tolerance is 5 kHz so
    /// callers using 25 kHz spacing ("121.9") or 8.33 kHz spacing ("128.525") both resolve cleanly.
    /// Returns the first match in load order — multiple positions with the same frequency
    /// (e.g. towers that share a channel with adjacent fields) yield whichever position the
    /// facility tree lists first.
    /// </summary>
    public static PositionConfig? FindPositionByFrequency(this ArtccConfigRoot config, double frequencyMhz)
    {
        var frequencyHz = (long)Math.Round(frequencyMhz * 1_000_000.0);
        return FindPositionByFrequencyRec(config.Facility, frequencyHz);
    }

    private static PositionConfig? FindPositionByFrequencyRec(FacilityConfig facility, long frequencyHz)
    {
        const long ToleranceHz = 5_000; // ±5 kHz covers 8.33 kHz spacing rounded to 25 kHz typing.
        foreach (var pos in facility.Positions)
        {
            if (Math.Abs(pos.Frequency - frequencyHz) <= ToleranceHz)
            {
                return pos;
            }
        }
        foreach (var child in facility.ChildFacilities)
        {
            var found = FindPositionByFrequencyRec(child, frequencyHz);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Searches every facility for all positions whose linked TCP matches the given
    /// subset+sectorId code (e.g. "3O" → subset=3, sectorId="O"). Returns every match in tree
    /// order. Multiple positions can link to the same TCP (e.g. OAK_TWR + OAK_GND on STARS
    /// scope "3O"); callers should disambiguate via <see cref="FindPositionByCallsign"/> or
    /// <see cref="FindPositionByFrequency"/> when the list contains more than one entry.
    /// </summary>
    public static IReadOnlyList<PositionConfig> FindPositionsByTcpCodeAnyFacility(this ArtccConfigRoot config, string tcpCode)
    {
        if (tcpCode.Length < 2 || !int.TryParse(tcpCode[..^1], out var subset))
        {
            return Array.Empty<PositionConfig>();
        }
        var sectorId = tcpCode[^1..];
        var matches = new List<PositionConfig>();
        CollectPositionsByTcpCode(config.Facility, subset, sectorId, matches);
        return matches;
    }

    private static void CollectPositionsByTcpCode(FacilityConfig facility, int subset, string sectorId, List<PositionConfig> result)
    {
        if (facility.StarsConfiguration is { } stars)
        {
            foreach (var tcp in stars.Tcps)
            {
                if (tcp.Subset == subset && tcp.SectorId.Equals(sectorId, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var pos in facility.Positions)
                    {
                        if (pos.StarsConfiguration?.TcpId == tcp.Id)
                        {
                            result.Add(pos);
                        }
                    }
                }
            }
        }
        foreach (var child in facility.ChildFacilities)
        {
            CollectPositionsByTcpCode(child, subset, sectorId, result);
        }
    }

    /// <summary>
    /// Walks the facility tree to locate the position with a matching
    /// <c>EramConfiguration.SectorId</c>. Returns the owning facility id along
    /// with the position. Used by ERAM-code resolution (e.g. "C44").
    /// </summary>
    public static (string? FacilityId, PositionConfig? Position) FindEramPositionBySectorId(this ArtccConfigRoot config, string sectorId) =>
        FindEramPositionBySectorIdInFacility(config.Facility, sectorId);

    private static (string?, PositionConfig?) FindEramPositionBySectorIdInFacility(FacilityConfig facility, string sectorId)
    {
        foreach (var pos in facility.Positions)
        {
            if (pos.EramConfiguration is { } eram && eram.SectorId.Equals(sectorId, StringComparison.OrdinalIgnoreCase))
            {
                return (facility.Id, pos);
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            var result = FindEramPositionBySectorIdInFacility(child, sectorId);
            if (result.Item2 is not null)
            {
                return result;
            }
        }

        return (null, null);
    }

    // --- TrackOwner resolution ---

    /// <summary>
    /// Resolves a vNAS position ULID to a <see cref="TrackOwner"/> with callsign,
    /// facility, subset, and sector taken from the STARS configuration. ERAM
    /// positions return <see cref="TrackOwner.CreateEram"/> instead.
    /// </summary>
    public static TrackOwner? ResolvePosition(this ArtccConfigRoot config, string positionId)
    {
        var (facilityId, _, _, pos) = config.FindPosition(positionId);
        if (pos is null || facilityId is null)
        {
            return null;
        }

        if (pos.EramConfiguration is { } eram)
        {
            return TrackOwner.CreateEram(pos.Callsign, facilityId, eram.SectorId);
        }

        var (tcp, starsFacilityId) = config.GetTcpWithFacilityForPosition(positionId);
        if (tcp is null)
        {
            return TrackOwner.CreateStars(pos.Callsign, facilityId, 0, "");
        }

        return TrackOwner.CreateStars(pos.Callsign, starsFacilityId ?? facilityId, tcp.Subset, tcp.SectorId);
    }

    /// <summary>
    /// Resolves an ERAM code like "C44" to a TrackOwner by finding the position
    /// with a matching <c>EramConfiguration.SectorId</c>.
    /// </summary>
    public static TrackOwner? ResolveEramCode(this ArtccConfigRoot config, string eramCode)
    {
        if (eramCode.Length < 2 || eramCode[0] != 'C')
        {
            return null;
        }

        if (!int.TryParse(eramCode[1..], out _))
        {
            return null;
        }

        var sectorId = eramCode[1..];
        var (facilityId, pos) = config.FindEramPositionBySectorId(sectorId);
        if (pos is null || facilityId is null)
        {
            return null;
        }

        return TrackOwner.CreateEram(pos.Callsign, facilityId, sectorId);
    }

    /// <summary>
    /// Resolves a TCP code like "2B" (subset=2, sectorId="B") to a TrackOwner by
    /// finding the matching TCP and its owning position within the given facility.
    /// Returns the target's own identity (facility, TCP).
    /// </summary>
    public static TrackOwner? ResolveTcpCode(this ArtccConfigRoot config, string facilityId, string tcpCode)
    {
        if (tcpCode.Length < 2 || !int.TryParse(tcpCode[..^1], out var subset))
        {
            return null;
        }

        var sectorId = tcpCode[^1..];
        var tcp = config.FindTcpByCode(facilityId, subset, sectorId);
        if (tcp is null)
        {
            return null;
        }

        var pos = config.FindPositionByTcpId(tcp.Id);
        if (pos is null)
        {
            return TrackOwner.CreateStars("", facilityId, subset, sectorId);
        }

        return config.ResolvePosition(pos.Id) ?? TrackOwner.CreateStars(pos.Callsign, facilityId, subset, sectorId);
    }

    /// <summary>
    /// Resolves a STARS interfacility handoff entry to the receiving <see cref="TrackOwner"/>.
    /// CRC sends the triangle/delta symbol (entered with the <c>`</c>/tilde key) as a leading
    /// backtick — normalized from the wire's U+0080 at the message boundary. The leading digit
    /// run is the handoff number, matched against the <paramref name="senderFacilityId"/>
    /// facility's <c>starsHandoffIds</c> to find the receiving facility; any remainder is the
    /// receiving TCP code (subset+sector) within that facility, or the facility's default sector
    /// when absent. In ZOA/NCT: <c>`3</c> → FAT default, <c>`31H</c> → FAT sector 1H (Chandler),
    /// <c>`11N</c> → SUU sector 1N (North). Mirrors <see cref="ResolveEramCode"/>, which keys off
    /// the <c>C</c> prefix for Center handoffs. Returns null when the entry is not a configured
    /// interfacility handoff code.
    /// </summary>
    public static TrackOwner? ResolveStarsHandoffCode(this ArtccConfigRoot config, string senderFacilityId, string code)
    {
        if (string.IsNullOrEmpty(code) || code[0] != '`')
        {
            return null;
        }

        var body = code[1..];
        if (body.Length == 0)
        {
            return null;
        }

        var handoffIds = config.FindFacility(senderFacilityId)?.StarsConfiguration?.StarsHandoffIds;
        if (handoffIds is null)
        {
            return null;
        }

        foreach (var handoff in handoffIds)
        {
            var number = handoff.HandoffNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!body.StartsWith(number, StringComparison.Ordinal))
            {
                continue;
            }

            var receivingTcpCode = body[number.Length..];
            var owner =
                receivingTcpCode.Length == 0
                    ? config.ResolveFacilityDefaultStarsOwner(handoff.FacilityId)
                    : config.ResolveTcpCode(handoff.FacilityId, receivingTcpCode);
            if (owner is not null)
            {
                return owner;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the receiving owner for an interfacility handoff that names a facility but no
    /// sector (e.g. <c>`3</c> → FAT default). Picks the facility's primary STARS sector: the
    /// consolidation-root TCP (no parent), else the first configured TCP.
    /// </summary>
    private static TrackOwner? ResolveFacilityDefaultStarsOwner(this ArtccConfigRoot config, string facilityId)
    {
        var tcps = config.FindFacility(facilityId)?.StarsConfiguration?.Tcps;
        if (tcps is null || tcps.Count == 0)
        {
            return null;
        }

        var primary = tcps.FirstOrDefault(t => string.IsNullOrEmpty(t.ParentTcpId)) ?? tcps[0];
        return config.ResolveTcpCode(facilityId, $"{primary.Subset}{primary.SectorId}");
    }

    // --- TCP lookup ---

    /// <summary>
    /// Returns the TCP linked to a position via its <c>StarsConfiguration.TcpId</c>.
    /// </summary>
    public static Tcp? GetTcpForPosition(this ArtccConfigRoot config, string positionId)
    {
        var (tcp, _) = config.GetTcpWithFacilityForPosition(positionId);
        return tcp;
    }

    /// <summary>
    /// Returns the TCP and the STARS facility ID that owns it. For tower
    /// positions, this is the parent TRACON facility.
    /// </summary>
    public static (Tcp?, string?) GetTcpWithFacilityForPosition(this ArtccConfigRoot config, string positionId)
    {
        var (facilityId, _, _, pos) = config.FindPosition(positionId);
        if (pos?.StarsConfiguration is null || facilityId is null)
        {
            return (null, null);
        }

        var tcpId = pos.StarsConfiguration.TcpId;
        if (string.IsNullOrEmpty(tcpId))
        {
            return (null, null);
        }

        return config.FindTcpWithFacility(facilityId, tcpId);
    }

    /// <summary>
    /// Returns the <see cref="Tcp"/> for a given TCP code within a facility.
    /// </summary>
    public static Tcp? FindTcpByCode(this ArtccConfigRoot config, string facilityId, int subset, string sectorId)
    {
        var facility = config.FindFacility(facilityId);
        if (facility?.StarsConfiguration is null)
        {
            return null;
        }

        foreach (var tcp in facility.StarsConfiguration.Tcps)
        {
            if (tcp.Subset == subset && tcp.SectorId.Equals(sectorId, StringComparison.OrdinalIgnoreCase))
            {
                return new Tcp(tcp.Subset, tcp.SectorId, tcp.Id, tcp.ParentTcpId);
            }
        }

        return null;
    }

    /// <summary>
    /// Parses a string TCP code (e.g. "1F") into subset+sectorId and resolves.
    /// </summary>
    public static Tcp? FindTcpByCode(this ArtccConfigRoot config, string facilityId, string tcpCode)
    {
        if (tcpCode.Length < 2 || !int.TryParse(tcpCode[..^1], out var subset))
        {
            return null;
        }

        var sectorId = tcpCode[^1..];
        return config.FindTcpByCode(facilityId, subset, sectorId);
    }

    /// <summary>
    /// Finds a TCP by ULID and returns both the TCP and the facility ID that
    /// owns the StarsConfiguration containing it. Searches the position's own
    /// facility first, then the entire ARTCC tree (tower positions may use TCPs
    /// from the parent TRACON's StarsConfiguration).
    /// </summary>
    public static (Tcp?, string?) FindTcpWithFacility(this ArtccConfigRoot config, string facilityId, string tcpId)
    {
        var facility = config.FindFacility(facilityId);
        var (tcp, foundFacility) = SearchTcpInFacility(facility, tcpId);
        if (tcp is not null)
        {
            return (tcp, foundFacility);
        }

        return SearchTcpInFacility(config.Facility, tcpId);
    }

    private static (Tcp?, string?) SearchTcpInFacility(FacilityConfig? facility, string tcpId)
    {
        if (facility is null)
        {
            return (null, null);
        }

        if (facility.StarsConfiguration is not null)
        {
            foreach (var tcp in facility.StarsConfiguration.Tcps)
            {
                if (tcp.Id == tcpId)
                {
                    return (new Tcp(tcp.Subset, tcp.SectorId, tcp.Id, tcp.ParentTcpId), facility.Id);
                }
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            var result = SearchTcpInFacility(child, tcpId);
            if (result.Item1 is not null)
            {
                return result;
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Finds a position by its STARS-configured TCP id anywhere in the ARTCC.
    /// </summary>
    public static PositionConfig? FindPositionByTcpId(this ArtccConfigRoot config, string tcpId) =>
        FindPositionByTcpIdInFacility(config.Facility, tcpId);

    private static PositionConfig? FindPositionByTcpIdInFacility(FacilityConfig facility, string tcpId)
    {
        foreach (var pos in facility.Positions)
        {
            if (pos.StarsConfiguration?.TcpId == tcpId)
            {
                return pos;
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            var result = FindPositionByTcpIdInFacility(child, tcpId);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    // --- TCP shorthand expansion ---

    /// <summary>
    /// Expands STARS TCP shorthand relative to a sender position:
    ///   "2B" → "2B"  (full code, returned as-is)
    ///   "G"  → "3G"  (letter only → sender's subset + letter)
    ///   "2"  → "2B"  (digit only → unique sector in that subset)
    /// Returns null if the input doesn't match any shorthand pattern or if the
    /// digit-only form is ambiguous (multiple sectors).
    /// </summary>
    public static string? ExpandTcpShorthand(this ArtccConfigRoot config, string facilityId, string input, int senderSubset)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        if (input.Length >= 2 && char.IsLetter(input[^1]) && int.TryParse(input[..^1], out _))
        {
            return input;
        }

        if (input.Length == 1 && char.IsLetter(input[0]))
        {
            return $"{senderSubset}{input}";
        }

        if (int.TryParse(input, out var subset))
        {
            var tcps = config.GetFacilityTcps(facilityId);
            Tcp? match = null;
            int count = 0;
            foreach (var tcp in tcps)
            {
                if (tcp.Subset == subset)
                {
                    match = tcp;
                    count++;
                    if (count > 1)
                    {
                        return null;
                    }
                }
            }

            return match is not null ? $"{match.Subset}{match.SectorId}" : null;
        }

        return null;
    }

    // --- Facility lookup ---

    /// <summary>
    /// Walks the facility tree to find the facility with a given id. Returns
    /// null when not found.
    /// </summary>
    public static FacilityConfig? FindFacility(this ArtccConfigRoot config, string facilityId) => FindFacilityRec(config.Facility, facilityId);

    private static FacilityConfig? FindFacilityRec(FacilityConfig root, string facilityId)
    {
        if (root.Id == facilityId)
        {
            return root;
        }

        foreach (var child in root.ChildFacilities)
        {
            var found = FindFacilityRec(child, facilityId);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the ASDEX config for a facility (airport).
    /// </summary>
    public static AsdexConfig? GetAsdexConfigForFacility(this ArtccConfigRoot config, string facilityId) =>
        config.FindFacility(facilityId)?.AsdexConfiguration;

    /// <summary>
    /// Returns the SAAB SAID config for a facility (airport).
    /// </summary>
    public static SaidConfig? GetSaidConfigForFacility(this ArtccConfigRoot config, string facilityId) =>
        config.FindFacility(facilityId)?.SaidConfiguration;

    /// <summary>
    /// Returns the STARS config for a facility.
    /// </summary>
    public static StarsConfig? GetStarsConfigForFacility(this ArtccConfigRoot config, string facilityId) =>
        config.FindFacility(facilityId)?.StarsConfiguration;

    /// <summary>
    /// Returns the facility containing the position with the given callsign,
    /// or null when no such position exists.
    /// </summary>
    public static FacilityConfig? FindFacilityForPositionCallsign(this ArtccConfigRoot config, string callsign) =>
        FindFacilityForPositionCallsignRec(config.Facility, callsign);

    private static FacilityConfig? FindFacilityForPositionCallsignRec(FacilityConfig facility, string callsign)
    {
        foreach (var pos in facility.Positions)
        {
            if (pos.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase))
            {
                return facility;
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            var found = FindFacilityForPositionCallsignRec(child, callsign);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // --- Coordination channels ---

    /// <summary>
    /// Builds <see cref="CoordinationChannel"/> objects for every coordination
    /// list in the facility tree rooted at <paramref name="facilityId"/>.
    /// </summary>
    public static List<CoordinationChannel> GetCoordinationChannels(this ArtccConfigRoot config, string facilityId)
    {
        var facility = config.FindFacility(facilityId);
        if (facility is null)
        {
            return [];
        }

        var result = new List<CoordinationChannel>();
        CollectCoordinationChannels(config.Facility, facility, result);
        return result;
    }

    private static void CollectCoordinationChannels(FacilityConfig root, FacilityConfig facility, List<CoordinationChannel> result)
    {
        if (facility.StarsConfiguration is not null)
        {
            foreach (var list in facility.StarsConfiguration.Lists)
            {
                if (list.CoordinationChannel is null)
                {
                    continue;
                }

                var cc = list.CoordinationChannel;
                var sendingTcps = new List<Tcp>();
                foreach (var tcpId in cc.SendingTcpIds)
                {
                    var (tcp, _) = SearchTcpInFacility(root, tcpId);
                    if (tcp is not null)
                    {
                        sendingTcps.Add(tcp);
                    }
                }

                var receivers = new List<CoordinationReceiver>();
                foreach (var r in cc.Receivers)
                {
                    var (tcp, _) = SearchTcpInFacility(root, r.ReceivingTcpId);
                    if (tcp is not null)
                    {
                        receivers.Add(new CoordinationReceiver(tcp, r.AutoAcknowledge));
                    }
                }

                result.Add(
                    new CoordinationChannel
                    {
                        Id = cc.Id,
                        ListId = list.Id,
                        Title = list.Title,
                        SendingTcps = sendingTcps,
                        Receivers = receivers,
                    }
                );
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            CollectCoordinationChannels(root, child, result);
        }
    }

    // --- Asdex / TowerCab airport collection ---

    /// <summary>
    /// Collects all ASDEX airports declared anywhere in the facility tree.
    /// Uses <see cref="NavigationDatabase.Instance"/> to look up airport
    /// coordinates when a tower-cab override isn't present.
    /// </summary>
    public static List<AsdexAirportInfo> GetAllAsdexAirports(this ArtccConfigRoot config)
    {
        var result = new List<AsdexAirportInfo>();
        CollectAsdexAirports(config.Facility, NavigationDatabase.Instance, result);
        return result;
    }

    // SAID config carries no visibility range (unlike ASDE-X), so surface gating falls back to the
    // ASDE-X default range. The vertical limit is not config-driven — CrcVisibilityTracker applies a
    // fixed 2,500 ft AGL surface-display ceiling.
    private const double SaidDefaultRange = 15;

    /// <summary>
    /// Collects all SAAB SAID airports declared anywhere in the facility tree. Coordinates come
    /// from <c>SaabConfiguration.TowerLocation</c>, falling back to the facility's indexed
    /// position. Only the <see cref="SaidVendor.Saab"/> vendor is emitted (the only one CRC 2.17
    /// renders); other vendors parse but produce no SAID airport.
    /// </summary>
    public static List<SaidAirportInfo> GetAllSaidAirports(this ArtccConfigRoot config)
    {
        var result = new List<SaidAirportInfo>();
        CollectSaidAirports(config.Facility, NavigationDatabase.Instance, result);
        return result;
    }

    /// <summary>
    /// Collects all TowerCab airports declared anywhere in the facility tree.
    /// </summary>
    public static List<TowerCabAirportInfo> GetAllTowerCabAirports(this ArtccConfigRoot config)
    {
        var result = new List<TowerCabAirportInfo>();
        CollectTowerCabAirports(config.Facility, NavigationDatabase.Instance, result);
        return result;
    }

    private static void CollectAsdexAirports(FacilityConfig facility, NavigationDatabase? fixes, List<AsdexAirportInfo> result)
    {
        if (facility.AsdexConfiguration is { } asdex)
        {
            var (lat, lon) = GetFacilityLocation(facility, fixes);
            if (lat != 0 || lon != 0)
            {
                result.Add(new AsdexAirportInfo(facility.Id, lat, lon, asdex.TargetVisibilityRange, asdex.TargetVisibilityCeiling));
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            CollectAsdexAirports(child, fixes, result);
        }
    }

    private static void CollectSaidAirports(FacilityConfig facility, NavigationDatabase? fixes, List<SaidAirportInfo> result)
    {
        if (facility.SaidConfiguration is { Vendor: SaidVendor.Saab, SaabConfiguration: { } saab })
        {
            var tower = saab.TowerLocation;
            var (lat, lon) =
                (tower is not null) && ((tower.Lat != 0) || (tower.Lon != 0)) ? (tower.Lat, tower.Lon) : GetFacilityLocation(facility, fixes);

            if (lat != 0 || lon != 0)
            {
                result.Add(new SaidAirportInfo(facility.Id, lat, lon, SaidDefaultRange));
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            CollectSaidAirports(child, fixes, result);
        }
    }

    private static void CollectTowerCabAirports(FacilityConfig facility, NavigationDatabase? fixes, List<TowerCabAirportInfo> result)
    {
        if (facility.TowerCabConfiguration is { } tcab)
        {
            var (lat, lon) = GetFacilityLocation(facility, fixes);
            if (lat != 0 || lon != 0)
            {
                result.Add(new TowerCabAirportInfo(facility.Id, lat, lon, tcab.AircraftVisibilityCeiling));
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            CollectTowerCabAirports(child, fixes, result);
        }
    }

    private static (double Lat, double Lon) GetFacilityLocation(FacilityConfig facility, NavigationDatabase? fixes)
    {
        if (facility.TowerCabConfiguration?.TowerLocation is { } loc)
        {
            return (loc.Lat, loc.Lon);
        }

        if (fixes is null)
        {
            return (0, 0);
        }

        var pos = fixes.GetFixPosition(facility.Id);
        return pos is not null ? (pos.Value.Lat, pos.Value.Lon) : (0, 0);
    }

    // --- Strip bays ---

    /// <summary>
    /// Returns the strip bay matching the given user-typed name within a facility,
    /// using a case- and whitespace-insensitive match.
    /// </summary>
    public static StripBayConfig? GetStripBay(this ArtccConfigRoot config, string facilityId, string bayName) =>
        MatchBayInFacility(config.FindFacility(facilityId), bayName);

    private static StripBayConfig? MatchBayInFacility(FacilityConfig? facility, string bayName)
    {
        var stripsConfig = facility?.FlightStripsConfiguration;
        if (stripsConfig is null)
        {
            return null;
        }

        var direct = stripsConfig.StripBays.FirstOrDefault(b => b.Name.Equals(bayName, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return direct;
        }

        var normalized = StripWhitespace(bayName);
        return stripsConfig.StripBays.FirstOrDefault(b => StripWhitespace(b.Name).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// All strip bays a controller at the given position would see in vStrips:
    /// the position's own facility's bays plus any external bays linked from
    /// other facilities.
    /// </summary>
    public static IReadOnlyList<AccessibleBay> GetAllAccessibleStripBays(this ArtccConfigRoot config, string positionCallsign)
    {
        var ownerFacility = config.FindFacilityForPositionCallsign(positionCallsign);
        if (ownerFacility is null)
        {
            return [];
        }

        var result = new List<AccessibleBay>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bay in ownerFacility.FlightStripsConfiguration?.StripBays ?? [])
        {
            if (seen.Add(bay.Id))
            {
                result.Add(new AccessibleBay(ownerFacility, bay, IsExternal: false));
            }
        }

        foreach (var ext in ownerFacility.FlightStripsConfiguration?.ExternalBays ?? [])
        {
            var extFacility = config.FindFacility(ext.FacilityId);
            var extBay = extFacility?.FlightStripsConfiguration?.StripBays.FirstOrDefault(b => b.Id == ext.BayId);
            if (extFacility is not null && extBay is not null && seen.Add(extBay.Id))
            {
                result.Add(new AccessibleBay(extFacility, extBay, IsExternal: true));
            }
        }

        return result;
    }

    /// <summary>
    /// Looks up an accessible strip bay by user-typed name (case- and
    /// whitespace-insensitive).
    /// </summary>
    public static AccessibleBay? GetAccessibleStripBay(this ArtccConfigRoot config, string positionCallsign, string bayName)
    {
        var bays = config.GetAllAccessibleStripBays(positionCallsign);
        if (bays.Count == 0)
        {
            return null;
        }

        foreach (var entry in bays)
        {
            if (entry.Bay.Name.Equals(bayName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        var normalized = StripWhitespace(bayName);
        foreach (var entry in bays)
        {
            if (StripWhitespace(entry.Bay.Name).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the first own (non-external) accessible bay whose name starts with
    /// <paramref name="namePrefix"/> (case- and whitespace-insensitive).
    /// </summary>
    public static AccessibleBay? FindFirstOwnBayWithNamePrefix(this ArtccConfigRoot config, string positionCallsign, string namePrefix)
    {
        if (string.IsNullOrEmpty(namePrefix))
        {
            return null;
        }

        var bays = config.GetAllAccessibleStripBays(positionCallsign);
        if (bays.Count == 0)
        {
            return null;
        }

        var normalizedPrefix = StripWhitespace(namePrefix);
        foreach (var entry in bays)
        {
            if (entry.IsExternal)
            {
                continue;
            }

            if (StripWhitespace(entry.Bay.Name).StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>
    /// Reverse lookup: finds an accessible strip bay by its opaque id (ULID).
    /// </summary>
    public static AccessibleBay? GetAccessibleStripBayById(this ArtccConfigRoot config, string positionCallsign, string bayId)
    {
        var bays = config.GetAllAccessibleStripBays(positionCallsign);
        foreach (var entry in bays)
        {
            if (entry.Bay.Id == bayId)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Lists every facility the controller at <paramref name="positionCallsign"/>
    /// can open a strips view for: the position's own facility plus any
    /// descendant facility with flight-strips configuration.
    /// </summary>
    public static IReadOnlyList<AccessibleFacility> GetAccessibleFacilities(this ArtccConfigRoot config, string positionCallsign)
    {
        var ownFacility = config.FindFacilityForPositionCallsign(positionCallsign);
        if (ownFacility is null)
        {
            return [];
        }

        var result = new List<AccessibleFacility>();
        CollectStripFacilities(ownFacility, studentFacilityId: ownFacility.Id, result);
        return result;
    }

    private static void CollectStripFacilities(FacilityConfig facility, string studentFacilityId, List<AccessibleFacility> result)
    {
        if (facility.FlightStripsConfiguration is not null && facility.FlightStripsConfiguration.StripBays.Count > 0)
        {
            result.Add(new AccessibleFacility(facility.Id, facility.Name, IsStudentFacility: facility.Id == studentFacilityId));
        }

        foreach (var child in facility.ChildFacilities)
        {
            CollectStripFacilities(child, studentFacilityId, result);
        }
    }

    private static string StripWhitespace(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (!char.IsWhiteSpace(ch))
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    // --- Video map / area helpers ---

    /// <summary>
    /// Walks the facility tree and returns the first facility (self or descendant)
    /// with a STARS configuration. Used as a fallback when no airport-specific
    /// STARS facility is found.
    /// </summary>
    public static FacilityConfig? FindFirstStarsFacility(this ArtccConfigRoot config) => FindFirstStarsFacilityRec(config.Facility);

    private static FacilityConfig? FindFirstStarsFacilityRec(FacilityConfig facility)
    {
        if (facility.StarsConfiguration is not null)
        {
            return facility;
        }

        foreach (var child in facility.ChildFacilities)
        {
            var found = FindFirstStarsFacilityRec(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the nearest ancestor (or self) with a StarsConfiguration for a given
    /// airport facility ID. E.g. OAK → NCT (parent TRACON).
    /// </summary>
    public static FacilityConfig? FindStarsFacilityForAirport(this ArtccConfigRoot config, string airportId)
    {
        var path = new List<FacilityConfig>();
        if (!FindFacilityPath(config.Facility, airportId, path))
        {
            return null;
        }

        for (int i = path.Count - 1; i >= 0; i--)
        {
            if (path[i].StarsConfiguration is not null)
            {
                return path[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the path from <paramref name="current"/> to a target facility
    /// id. Returns true when the target is found; <paramref name="path"/> then
    /// contains the chain of facilities root → target.
    /// </summary>
    public static bool FindFacilityPath(FacilityConfig current, string targetId, List<FacilityConfig> path)
    {
        path.Add(current);

        if (current.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var child in current.ChildFacilities)
        {
            if (FindFacilityPath(child, targetId, path))
            {
                return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    /// <summary>
    /// Resolves an airport's position TCP codes by finding its positions' TCP
    /// IDs and looking them up in the STARS facility's TCP table.
    /// </summary>
    public static HashSet<string> ResolveAirportTcpCodes(this ArtccConfigRoot config, FacilityConfig starsFacility, string airportId)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var airport = config.FindFacility(airportId);
        if (airport is null || starsFacility.StarsConfiguration is null)
        {
            return codes;
        }

        var tcpIds = new HashSet<string>();
        foreach (var pos in airport.Positions)
        {
            if (pos.StarsConfiguration is { TcpId: { Length: > 0 } tcpId })
            {
                tcpIds.Add(tcpId);
            }
        }

        foreach (var tcp in starsFacility.StarsConfiguration.Tcps)
        {
            if (tcpIds.Contains(tcp.Id))
            {
                codes.Add($"{tcp.Subset}{tcp.SectorId}");
            }
        }

        return codes;
    }

    /// <summary>
    /// Searches the entire facility tree for a position whose
    /// <c>StarsConfiguration.TcpId</c> matches the given TCP ID.
    /// </summary>
    public static PositionConfig? FindPositionByTcpIdInTree(this ArtccConfigRoot config, string tcpId) =>
        FindPositionByTcpIdInTreeRec(config.Facility, tcpId);

    private static PositionConfig? FindPositionByTcpIdInTreeRec(FacilityConfig facility, string tcpId)
    {
        foreach (var pos in facility.Positions)
        {
            if (pos.StarsConfiguration is not null && pos.StarsConfiguration.TcpId == tcpId)
            {
                return pos;
            }
        }

        foreach (var child in facility.ChildFacilities)
        {
            var found = FindPositionByTcpIdInTreeRec(child, tcpId);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the position that owns a TCP code, then returns its STARS area's
    /// <c>UnderlyingAirports</c> list.
    /// </summary>
    public static List<string> ResolveAirportsForTcpCode(this ArtccConfigRoot config, FacilityConfig starsFacility, string tcpCode)
    {
        if (tcpCode.Length < 2 || starsFacility.StarsConfiguration is null)
        {
            return [];
        }

        if (!int.TryParse(tcpCode[..^1], out var subset))
        {
            return [];
        }

        var sectorId = tcpCode[^1..];

        string? tcpId = null;
        foreach (var tcp in starsFacility.StarsConfiguration.Tcps)
        {
            if (tcp.Subset == subset && tcp.SectorId.Equals(sectorId, StringComparison.OrdinalIgnoreCase))
            {
                tcpId = tcp.Id;
                break;
            }
        }

        if (tcpId is null)
        {
            return [];
        }

        var position = config.FindPositionByTcpIdInTree(tcpId);
        if (position?.StarsConfiguration is null || string.IsNullOrEmpty(position.StarsConfiguration.AreaId))
        {
            return [];
        }

        foreach (var area in starsFacility.StarsConfiguration.Areas)
        {
            if (area.Id == position.StarsConfiguration.AreaId)
            {
                return area.UnderlyingAirports;
            }
        }

        return [];
    }

    // --- Consolidation ---

    /// <summary>
    /// Returns all TCPs for a facility (used by StarsConsolidation).
    /// </summary>
    public static List<Tcp> GetFacilityTcps(this ArtccConfigRoot config, string facilityId)
    {
        var facility = config.FindFacility(facilityId);
        if (facility?.StarsConfiguration is null)
        {
            return [];
        }

        return facility.StarsConfiguration.Tcps.Select(tc => new Tcp(tc.Subset, tc.SectorId, tc.Id, tc.ParentTcpId)).ToList();
    }

    /// <summary>
    /// Builds consolidation items for all TCPs in a facility.
    /// </summary>
    public static List<ConsolidationItem> GetConsolidationItems(
        this ArtccConfigRoot config,
        string facilityId,
        Func<Tcp, bool> isAttended,
        ConsolidationState? manualOverrides = null
    )
    {
        var allTcps = config.GetFacilityTcps(facilityId);
        if (allTcps.Count == 0)
        {
            return [];
        }

        var autoConsolidate = config.IsAutoConsolidation(facilityId);
        return ConsolidationEngine.GetConsolidationItems(allTcps, autoConsolidate, isAttended, manualOverrides);
    }

    /// <summary>
    /// Returns the TCPs that would consolidate under <paramref name="tcp"/> if
    /// it were the only attended position in the facility.
    /// </summary>
    public static List<Tcp> GetDefaultConsolidation(this ArtccConfigRoot config, string facilityId, Tcp tcp)
    {
        var allTcps = config.GetFacilityTcps(facilityId);
        if (allTcps.Count == 0)
        {
            return [];
        }

        return ConsolidationEngine.GetDefaultConsolidation(allTcps, tcp);
    }

    /// <summary>
    /// Returns the attended TCP that currently owns <paramref name="tcp"/> via
    /// consolidation, or null when no attended owner can be resolved.
    /// </summary>
    public static Tcp? GetConsolidationOwner(
        this ArtccConfigRoot config,
        string facilityId,
        Tcp tcp,
        Func<Tcp, bool> isAttended,
        ConsolidationState? manualOverrides = null
    )
    {
        var allTcps = config.GetFacilityTcps(facilityId);
        if (allTcps.Count == 0)
        {
            return null;
        }

        var autoConsolidate = config.IsAutoConsolidation(facilityId);
        return ConsolidationEngine.GetConsolidationOwner(allTcps, autoConsolidate, tcp, isAttended, manualOverrides);
    }

    /// <summary>
    /// Returns whether the facility has automatic consolidation enabled.
    /// </summary>
    public static bool IsAutoConsolidation(this ArtccConfigRoot config, string facilityId) =>
        config.FindFacility(facilityId)?.StarsConfiguration?.AutomaticConsolidation ?? false;

    /// <summary>
    /// The published initial ("maintain") altitude in feet for an IFR departure on the given SID and
    /// enroute transition, from the departure airport's TDLS config: the transition's
    /// <c>DefaultInitialAlt</c> when set, otherwise the facility's primary <c>InitialAlts</c> value
    /// (some facilities, e.g. KIAH, leave the per-transition default blank and rely on the list).
    /// Returns null when the airport has no TDLS config, the SID isn't configured, or no value is published.
    /// </summary>
    public static int? GetSidInitialAltitudeFt(this ArtccConfigRoot? config, string departureAirportId, string? sidId, string? transitionId)
    {
        if (config?.Facility is null || string.IsNullOrWhiteSpace(departureAirportId))
        {
            return null;
        }

        var tdls = FindTdlsConfigForAirport(config.Facility, NormalizeFaaAirport(departureAirportId));
        if (tdls is null)
        {
            return null;
        }

        string? raw = null;
        if (!string.IsNullOrWhiteSpace(sidId))
        {
            var sid = tdls.Sids.FirstOrDefault(s => s.Name.Equals(sidId, StringComparison.OrdinalIgnoreCase));
            if (sid is not null)
            {
                var transition = transitionId is not null
                    ? sid.Transitions.FirstOrDefault(t => t.Name.Equals(transitionId, StringComparison.OrdinalIgnoreCase))
                    : null;
                raw =
                    transition?.DefaultInitialAlt
                    ?? sid.Transitions.Select(t => t.DefaultInitialAlt).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            }
        }

        raw ??= tdls.InitialAlts.FirstOrDefault()?.Value;
        return ParseTdlsAltitudeFt(raw);
    }

    private static TdlsConfig? FindTdlsConfigForAirport(FacilityConfig facility, string normalizedAirport)
    {
        if (facility.TdlsConfiguration is not null && NormalizeFaaAirport(facility.Id).Equals(normalizedAirport, StringComparison.OrdinalIgnoreCase))
        {
            return facility.TdlsConfiguration;
        }

        foreach (var child in facility.ChildFacilities)
        {
            var found = FindTdlsConfigForAirport(child, normalizedAirport);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string NormalizeFaaAirport(string airportId) => airportId.Length == 4 && (airportId[0] is 'K' or 'k') ? airportId[1..] : airportId;

    private static int? ParseTdlsAltitudeFt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // TDLS initial altitudes are published as full feet: "4000", "5000", sometimes "4000FT".
        var span = raw.AsSpan().Trim();
        if (span.EndsWith("FT", StringComparison.OrdinalIgnoreCase))
        {
            span = span[..^2].Trim();
        }

        return int.TryParse(span, out int ft) && ft > 0 ? ft : null;
    }
}
