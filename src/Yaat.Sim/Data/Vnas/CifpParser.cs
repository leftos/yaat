using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Single-pass ARINC 424 CIFP parser that extracts FAF fix
/// names per (airport, runway) and terminal waypoint coordinates.
/// </summary>
public static partial class CifpParser
{
    // Approach type priority: ILS > LOC > RNAV > everything else.
    // Lower value = higher priority.
    private static readonly Dictionary<char, int> ApproachTypePriority = new()
    {
        ['I'] = 0, // ILS
        ['L'] = 1, // LOC
        ['H'] = 2, // RNAV (GPS)
        ['R'] = 3, // RNAV
        ['P'] = 4, // GPS
    };

    private const int DefaultPriority = 10;

    public static CifpParseResult Parse(string cifpFilePath, ILogger? logger = null)
    {
        var fafByApproach = new Dictionary<string, FafCandidate>();
        var terminalWaypoints = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase);

        int approachRecords = 0;
        int waypointRecords = 0;

        foreach (var line in File.ReadLines(cifpFilePath))
        {
            if (line.Length < 50)
            {
                continue;
            }

            if (!line.StartsWith("SUSAP", StringComparison.Ordinal))
            {
                continue;
            }

            char subsection = line[12];

            if (subsection == 'F')
            {
                ProcessApproachRecord(line, fafByApproach);
                approachRecords++;
            }
            else if (subsection == 'C')
            {
                ProcessTerminalWaypoint(line, terminalWaypoints);
                waypointRecords++;
            }
        }

        // Convert per-approach FAF map to per-(airport, runway) map,
        // preferring higher-priority approach types
        var fafFixes = new Dictionary<(string Airport, string Runway), string>();

        foreach (var (_, candidate) in fafByApproach)
        {
            var key = (candidate.Airport, candidate.Runway);

            if (fafFixes.ContainsKey(key))
            {
                // Only replace if this approach has higher priority
                var existingKey = fafByApproach.Values.FirstOrDefault(c =>
                    c.Airport == candidate.Airport && c.Runway == candidate.Runway && c.FafFix == fafFixes[key]
                );
                if (existingKey is not null && candidate.Priority < existingKey.Priority)
                {
                    fafFixes[key] = candidate.FafFix;
                }
            }
            else
            {
                fafFixes[key] = candidate.FafFix;
            }
        }

        logger?.LogInformation(
            "CIFP parsed: {Approaches} approach records, "
                + "{Waypoints} waypoint records, "
                + "{FafCount} FAF fixes, "
                + "{WpCount} terminal waypoints",
            approachRecords,
            waypointRecords,
            fafFixes.Count,
            terminalWaypoints.Count
        );

        return new CifpParseResult(fafFixes, terminalWaypoints);
    }

    // Approach type code → human-readable name
    private static readonly Dictionary<char, string> ApproachTypeNames = new()
    {
        ['B'] = "LOC/DME BC",
        ['D'] = "VOR/DME",
        ['F'] = "FMS",
        ['G'] = "IGS",
        ['H'] = "RNAV(GPS)",
        ['I'] = "ILS",
        ['J'] = "GNSS",
        ['L'] = "LOC",
        ['N'] = "NDB",
        ['P'] = "GPS",
        ['Q'] = "NDB/DME",
        ['R'] = "RNAV",
        ['S'] = "VOR",
        ['T'] = "TACAN",
        ['U'] = "SDF",
        ['V'] = "VOR",
        ['W'] = "MLS",
        ['X'] = "LDA",
    };

    // Hold-in-lieu path terminators per AIM 5-4-9.1.5
    private static readonly HashSet<string> HoldInLieuTerminators = ["HA", "HF", "HM"];

    /// <summary>
    /// Parses terminal waypoint (subsection C) coordinates for a specific airport.
    /// Returns a dictionary of fix identifier → (lat, lon) for CIFP-internal waypoints
    /// that may not exist in NavData (e.g., RF arc center fixes like CFPTK).
    /// </summary>
    public static IReadOnlyDictionary<string, (double Lat, double Lon)> ParseTerminalWaypoints(
        string cifpFilePath,
        string airportIcao,
        ILogger? logger = null
    )
    {
        string normalizedIcao = airportIcao.ToUpperInvariant().PadRight(4);
        var waypoints = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in File.ReadLines(cifpFilePath))
        {
            if (line.Length < 50)
            {
                continue;
            }

            if (!line.StartsWith("SUSAP", StringComparison.Ordinal))
            {
                continue;
            }

            if (line[12] != 'C')
            {
                continue;
            }

            string icao = line[6..10];
            if (!icao.Equals(normalizedIcao, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ProcessTerminalWaypoint(line, waypoints);
        }

        logger?.LogInformation("CIFP terminal waypoints for {Airport}: {Count} parsed", airportIcao, waypoints.Count);

        return waypoints;
    }

    public static IReadOnlyList<CifpApproachProcedure> ParseApproaches(string cifpFilePath, string airportIcao, ILogger? logger = null)
    {
        string normalizedIcao = airportIcao.ToUpperInvariant().PadRight(4);

        // Accumulate raw leg data keyed by approach ID
        // Each leg tagged with route type and transition name
        var approachLegs = new Dictionary<string, List<RawApproachLeg>>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(cifpFilePath))
        {
            if (line.Length < 100)
            {
                continue;
            }

            if (!line.StartsWith("SUSAP", StringComparison.Ordinal))
            {
                continue;
            }

            // Subsection F = approach
            if (line[12] != 'F')
            {
                continue;
            }

            // Airport ICAO at positions 7-10 (0-indexed: 6-9)
            string icao = line[6..10];
            if (!icao.Equals(normalizedIcao, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var leg = ParseApproachLeg(line);
            if (leg is null)
            {
                continue;
            }

            if (!approachLegs.TryGetValue(leg.ApproachId, out var list))
            {
                list = [];
                approachLegs[leg.ApproachId] = list;
            }

            list.Add(leg);
        }

        // Load terminal waypoints for resolving RF arc center fixes
        var terminalWaypoints = ParseTerminalWaypoints(cifpFilePath, airportIcao, logger);

        var results = new List<CifpApproachProcedure>(approachLegs.Count);
        string faaAirport = normalizedIcao.Trim();
        if (faaAirport.StartsWith('K'))
        {
            faaAirport = faaAirport[1..];
        }

        foreach (var (approachId, rawLegs) in approachLegs)
        {
            var procedure = BuildApproachProcedure(faaAirport, approachId, rawLegs, terminalWaypoints);
            if (procedure is not null)
            {
                results.Add(procedure);
            }
        }

        logger?.LogInformation("CIFP approaches for {Airport}: {Count} procedures parsed", faaAirport, results.Count);

        return results;
    }

    /// <summary>
    /// Extracts shared column data from an ARINC 424 procedure leg record.
    /// Columns are identical for subsections D (SID), E (STAR), and F (approach).
    /// </summary>
    public static IReadOnlyList<CifpSidProcedure> ParseSids(string cifpFilePath, string airportIcao, ILogger? logger = null)
    {
        return ParseSidStarProcedures<CifpSidProcedure>(cifpFilePath, airportIcao, 'D', BuildSidProcedure, logger);
    }

    public static IReadOnlyList<CifpStarProcedure> ParseStars(string cifpFilePath, string airportIcao, ILogger? logger = null)
    {
        return ParseSidStarProcedures<CifpStarProcedure>(cifpFilePath, airportIcao, 'E', BuildStarProcedure, logger);
    }

    private static IReadOnlyList<T> ParseSidStarProcedures<T>(
        string cifpFilePath,
        string airportIcao,
        char subsection,
        Func<string, string, List<RawProcedureLeg>, IReadOnlyDictionary<string, (double Lat, double Lon)>?, T?> builder,
        ILogger? logger
    )
    {
        string normalizedIcao = airportIcao.ToUpperInvariant().PadRight(4);

        var legsByProcedure = new Dictionary<string, List<RawProcedureLeg>>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(cifpFilePath))
        {
            if (line.Length < 100)
            {
                continue;
            }

            if (!line.StartsWith("SUSAP", StringComparison.Ordinal))
            {
                continue;
            }

            if (line[12] != subsection)
            {
                continue;
            }

            string icao = line[6..10];
            if (!icao.Equals(normalizedIcao, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var leg = ParseProcedureLeg(line);
            if (leg is null)
            {
                continue;
            }

            if (!legsByProcedure.TryGetValue(leg.ProcedureId, out var list))
            {
                list = [];
                legsByProcedure[leg.ProcedureId] = list;
            }

            list.Add(leg);
        }

        // Load terminal waypoints for resolving RF arc center fixes
        var terminalWaypoints = ParseTerminalWaypoints(cifpFilePath, airportIcao, logger);

        string faaAirport = normalizedIcao.Trim();
        if (faaAirport.StartsWith('K'))
        {
            faaAirport = faaAirport[1..];
        }

        var results = new List<T>(legsByProcedure.Count);
        foreach (var (procedureId, rawLegs) in legsByProcedure)
        {
            var procedure = builder(faaAirport, procedureId, rawLegs, terminalWaypoints);
            if (procedure is not null)
            {
                results.Add(procedure);
            }
        }

        string kind = subsection == 'D' ? "SIDs" : "STARs";
        logger?.LogInformation("CIFP {Kind} for {Airport}: {Count} procedures parsed", kind, faaAirport, results.Count);

        return results;
    }

    /// <summary>
    /// Classifies SID/STAR transition legs by their transition name prefix.
    /// "RW*" → runway transition, empty/"ALL" → common, anything else → enroute.
    /// </summary>
    private static (
        List<CifpLeg> CommonLegs,
        Dictionary<string, CifpTransition> RunwayTransitions,
        Dictionary<string, CifpTransition> EnrouteTransitions
    ) ClassifySidStarLegs(List<RawProcedureLeg> rawLegs, IReadOnlyDictionary<string, (double Lat, double Lon)>? terminalWaypoints = null)
    {
        rawLegs.Sort(
            (a, b) =>
            {
                int cmp = a.RouteType.CompareTo(b.RouteType);
                return cmp != 0 ? cmp : a.Sequence.CompareTo(b.Sequence);
            }
        );

        var commonLegs = new List<CifpLeg>();
        var runwayLegs = new Dictionary<string, List<CifpLeg>>(StringComparer.Ordinal);
        var enrouteLegs = new Dictionary<string, List<CifpLeg>>(StringComparer.Ordinal);

        foreach (var raw in rawLegs)
        {
            var leg = BuildCifpLeg(raw, terminalWaypoints);

            string transName = raw.TransitionName;

            if (transName.Length == 0 || transName.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                commonLegs.Add(leg);
            }
            else if (transName.StartsWith("RW", StringComparison.OrdinalIgnoreCase))
            {
                if (!runwayLegs.TryGetValue(transName, out var list))
                {
                    list = [];
                    runwayLegs[transName] = list;
                }

                list.Add(leg);
            }
            else
            {
                if (!enrouteLegs.TryGetValue(transName, out var list))
                {
                    list = [];
                    enrouteLegs[transName] = list;
                }

                list.Add(leg);
            }
        }

        var runwayTransitions = new Dictionary<string, CifpTransition>(runwayLegs.Count, StringComparer.Ordinal);
        foreach (var (name, legs) in runwayLegs)
        {
            runwayTransitions[name] = new CifpTransition(name, legs);
        }

        var enrouteTransitions = new Dictionary<string, CifpTransition>(enrouteLegs.Count, StringComparer.Ordinal);
        foreach (var (name, legs) in enrouteLegs)
        {
            enrouteTransitions[name] = new CifpTransition(name, legs);
        }

        return (commonLegs, runwayTransitions, enrouteTransitions);
    }

    private static CifpSidProcedure? BuildSidProcedure(
        string airport,
        string procedureId,
        List<RawProcedureLeg> rawLegs,
        IReadOnlyDictionary<string, (double Lat, double Lon)>? terminalWaypoints
    )
    {
        if (procedureId.Length == 0)
        {
            return null;
        }

        var (common, runway, enroute) = ClassifySidStarLegs(rawLegs, terminalWaypoints);
        return new CifpSidProcedure(airport, procedureId, common, runway, enroute);
    }

    private static CifpStarProcedure? BuildStarProcedure(
        string airport,
        string procedureId,
        List<RawProcedureLeg> rawLegs,
        IReadOnlyDictionary<string, (double Lat, double Lon)>? terminalWaypoints
    )
    {
        if (procedureId.Length == 0)
        {
            return null;
        }

        var (common, runway, enroute) = ClassifySidStarLegs(rawLegs, terminalWaypoints);
        return new CifpStarProcedure(airport, procedureId, common, enroute, runway);
    }

    private static RawProcedureLeg? ParseProcedureLeg(string line)
    {
        // Procedure ID at positions 14-19 (0-indexed: 13-18)
        string procedureId = line[13..19].Trim();
        if (procedureId.Length == 0)
        {
            return null;
        }

        // Route type at position 20 (0-indexed: 19)
        char routeType = line[19];

        // Transition identifier at positions 21-25 (0-indexed: 20-24)
        string transitionName = line[20..25].Trim();

        // Sequence number at positions 27-29 (0-indexed: 26-28)
        string seqStr = line[26..29].Trim();
        if (!int.TryParse(seqStr, out int sequence))
        {
            sequence = 0;
        }

        // Fix identifier at positions 30-34 (0-indexed: 29-33)
        string fixId = line[29..34].Trim();

        // Waypoint description at positions 40-43 (0-indexed: 39-42)
        // Fly-over flag: 2nd character of waypoint description (position 41, 0-indexed: 40)
        bool isFlyOver = line.Length > 40 && line[40] == 'Y';

        // Fix role from character at position 43 (0-indexed: 42)
        CifpFixRole fixRole = CifpFixRole.None;
        if (line.Length > 42)
        {
            fixRole = line[42] switch
            {
                'A' or 'I' => CifpFixRole.IAF,
                'B' => CifpFixRole.IF,
                'D' or 'F' => CifpFixRole.FAF,
                'M' => CifpFixRole.MAHP,
                _ => CifpFixRole.None,
            };
        }

        // Turn direction at position 44 (0-indexed: 43)
        char? turnDir = null;
        if (line.Length > 43 && line[43] is 'L' or 'R')
        {
            turnDir = line[43];
        }

        // Path terminator at positions 48-49 (0-indexed: 47-48)
        string pathTermStr = line.Length > 48 ? line[47..49].Trim() : "";
        CifpPathTerminator pathTerm = ParsePathTerminator(pathTermStr);

        // Altitude description at position 83 (0-indexed: 82)
        char altDesc = line.Length > 82 ? line[82] : ' ';

        // Altitude 1 at positions 84-88 (0-indexed: 83-87)
        string alt1Str = line.Length > 87 ? line[83..88] : "";

        // Altitude 2 at positions 89-93 (0-indexed: 88-92)
        string alt2Str = line.Length > 92 ? line[88..93] : "";

        var altitude = ParseAltitudeRestriction(altDesc, alt1Str, alt2Str);

        // Speed at positions 100-102 (0-indexed: 99-101)
        string speedStr = line.Length > 101 ? line[99..102].Trim() : "";
        CifpSpeedRestriction? speed = null;
        if (int.TryParse(speedStr, out int speedKts) && speedKts > 0)
        {
            speed = new CifpSpeedRestriction(speedKts, true);
        }

        // Recommended navaid at positions 50-54 (0-indexed: 49-53)
        string? recommendedNavaid = null;
        if (line.Length > 53)
        {
            string navStr = line[49..54].Trim();
            if (navStr.Length > 0)
            {
                recommendedNavaid = navStr;
            }
        }

        // Arc radius at positions 56-61 (0-indexed: 55-60), thousandths of NM
        double? arcRadiusNm = null;
        if (line.Length > 60 && int.TryParse(line[55..61].Trim(), out int arcRadiusRaw) && arcRadiusRaw > 0)
        {
            arcRadiusNm = arcRadiusRaw / 1000.0;
        }

        // Theta at positions 62-65 (0-indexed: 61-64), tenths of degrees
        double? theta = null;
        if (line.Length > 64 && int.TryParse(line[61..65].Trim(), out int thetaRaw) && thetaRaw > 0)
        {
            theta = thetaRaw / 10.0;
        }

        // Rho at positions 66-69 (0-indexed: 65-68), tenths of NM
        double? rho = null;
        if (line.Length > 68 && int.TryParse(line[65..69].Trim(), out int rhoRaw) && rhoRaw > 0)
        {
            rho = rhoRaw / 10.0;
        }

        // Outbound course at positions 70-73 (0-indexed: 69-72), tenths of degrees
        double? outboundCourse = null;
        if (line.Length > 72 && int.TryParse(line[69..73].Trim(), out int courseRaw) && courseRaw > 0)
        {
            outboundCourse = courseRaw / 10.0;
        }

        // Leg distance at positions 74-77 (0-indexed: 73-76), tenths of NM
        double? legDistanceNm = null;
        if (line.Length > 76 && int.TryParse(line[73..77].Trim(), out int distRaw) && distRaw > 0)
        {
            legDistanceNm = distRaw / 10.0;
        }

        // Center fix identifier at positions 106-110 (0-indexed: 105-109)
        string? centerFixId = null;
        if (line.Length > 109)
        {
            string cfStr = line[105..110].Trim();
            if (cfStr.Length > 0)
            {
                centerFixId = cfStr;
            }
        }

        return new RawProcedureLeg(
            procedureId,
            routeType,
            transitionName,
            fixId,
            pathTerm,
            pathTermStr,
            turnDir,
            altitude,
            speed,
            fixRole,
            sequence,
            arcRadiusNm,
            centerFixId,
            recommendedNavaid,
            theta,
            rho,
            outboundCourse,
            legDistanceNm,
            isFlyOver
        );
    }

    private static RawApproachLeg? ParseApproachLeg(string line)
    {
        var raw = ParseProcedureLeg(line);
        if (raw is null)
        {
            return null;
        }

        // For approaches: route type 'A' = named transition, others = common
        string transName = raw.RouteType == 'A' ? raw.TransitionName : "";

        return new RawApproachLeg(
            raw.ProcedureId,
            raw.RouteType,
            transName,
            raw.FixIdentifier,
            raw.PathTerminator,
            raw.PathTerminatorRaw,
            raw.TurnDirection,
            raw.Altitude,
            raw.Speed,
            raw.FixRole,
            raw.Sequence,
            raw.ArcRadiusNm,
            raw.CenterFixId,
            raw.RecommendedNavaidId,
            raw.Theta,
            raw.Rho,
            raw.OutboundCourse,
            raw.LegDistanceNm,
            raw.IsFlyOver
        );
    }

    private static CifpApproachProcedure? BuildApproachProcedure(
        string airport,
        string approachId,
        List<RawApproachLeg> rawLegs,
        IReadOnlyDictionary<string, (double Lat, double Lon)>? terminalWaypoints = null
    )
    {
        if (approachId.Length == 0)
        {
            return null;
        }

        char typeCode = approachId[0];
        string typeName = ApproachTypeNames.GetValueOrDefault(typeCode, "UNKNOWN");
        string? runway = ParseRunwayFromApproachId(approachId);

        // Separate transition legs, common legs, and missed approach legs
        var transitionLegs = new Dictionary<string, List<CifpLeg>>(StringComparer.Ordinal);
        var commonLegs = new List<CifpLeg>();
        var missedLegs = new List<CifpLeg>();
        bool pastMahp = false;
        CifpLeg? holdInLieuLeg = null;

        // Sort by route type then sequence for deterministic ordering
        rawLegs.Sort(
            (a, b) =>
            {
                int cmp = a.RouteType.CompareTo(b.RouteType);
                return cmp != 0 ? cmp : a.Sequence.CompareTo(b.Sequence);
            }
        );

        foreach (var raw in rawLegs)
        {
            var leg = BuildCifpLegFromApproach(raw, terminalWaypoints);

            if (raw.RouteType == 'A')
            {
                // Transition leg
                if (!transitionLegs.TryGetValue(raw.TransitionName, out var tList))
                {
                    tList = [];
                    transitionLegs[raw.TransitionName] = tList;
                }

                tList.Add(leg);

                // Hold-in-lieu can appear in transitions too
                if (holdInLieuLeg is null && HoldInLieuTerminators.Contains(raw.PathTerminatorRaw))
                {
                    holdInLieuLeg = leg;
                }
            }
            else
            {
                // Common leg or missed approach leg
                if (pastMahp || raw.FixRole == CifpFixRole.MAHP)
                {
                    if (raw.FixRole == CifpFixRole.MAHP)
                    {
                        // MAHP itself goes in common legs; legs after it are missed approach
                        commonLegs.Add(leg);
                        pastMahp = true;
                    }
                    else
                    {
                        missedLegs.Add(leg);
                    }
                }
                else
                {
                    commonLegs.Add(leg);
                }

                // Check for hold-in-lieu in common legs
                if (holdInLieuLeg is null && HoldInLieuTerminators.Contains(raw.PathTerminatorRaw))
                {
                    holdInLieuLeg = leg;
                }
            }
        }

        var transitions = new Dictionary<string, CifpTransition>(transitionLegs.Count, StringComparer.Ordinal);
        foreach (var (name, legs) in transitionLegs)
        {
            transitions[name] = new CifpTransition(name, legs);
        }

        return new CifpApproachProcedure(
            airport,
            approachId,
            typeCode,
            typeName,
            runway,
            commonLegs,
            transitions,
            missedLegs,
            holdInLieuLeg is not null,
            holdInLieuLeg
        );
    }

    private static CifpLeg BuildCifpLeg(RawProcedureLeg raw, IReadOnlyDictionary<string, (double Lat, double Lon)>? terminalWaypoints)
    {
        double? arcCenterLat = null;
        double? arcCenterLon = null;

        if (raw.CenterFixId is not null && terminalWaypoints is not null && terminalWaypoints.TryGetValue(raw.CenterFixId, out var centerPos))
        {
            arcCenterLat = centerPos.Lat;
            arcCenterLon = centerPos.Lon;
        }

        return new CifpLeg(
            raw.FixIdentifier,
            raw.PathTerminator,
            raw.TurnDirection,
            raw.Altitude,
            raw.Speed,
            raw.FixRole,
            raw.Sequence,
            raw.OutboundCourse,
            raw.LegDistanceNm,
            null,
            raw.ArcRadiusNm,
            arcCenterLat,
            arcCenterLon,
            raw.RecommendedNavaidId,
            raw.Theta,
            raw.Rho,
            raw.IsFlyOver
        );
    }

    private static CifpLeg BuildCifpLegFromApproach(RawApproachLeg raw, IReadOnlyDictionary<string, (double Lat, double Lon)>? terminalWaypoints)
    {
        double? arcCenterLat = null;
        double? arcCenterLon = null;

        if (raw.CenterFixId is not null && terminalWaypoints is not null && terminalWaypoints.TryGetValue(raw.CenterFixId, out var centerPos))
        {
            arcCenterLat = centerPos.Lat;
            arcCenterLon = centerPos.Lon;
        }

        return new CifpLeg(
            raw.FixIdentifier,
            raw.PathTerminator,
            raw.TurnDirection,
            raw.Altitude,
            raw.Speed,
            raw.FixRole,
            raw.Sequence,
            raw.OutboundCourse,
            raw.LegDistanceNm,
            null,
            raw.ArcRadiusNm,
            arcCenterLat,
            arcCenterLon,
            raw.RecommendedNavaidId,
            raw.Theta,
            raw.Rho,
            raw.IsFlyOver
        );
    }

    private static CifpPathTerminator ParsePathTerminator(string s)
    {
        return s switch
        {
            "IF" => CifpPathTerminator.IF,
            "TF" => CifpPathTerminator.TF,
            "CF" => CifpPathTerminator.CF,
            "DF" => CifpPathTerminator.DF,
            "RF" => CifpPathTerminator.RF,
            "AF" => CifpPathTerminator.AF,
            "HA" => CifpPathTerminator.HA,
            "HF" => CifpPathTerminator.HF,
            "HM" => CifpPathTerminator.HM,
            "PI" => CifpPathTerminator.PI,
            "CA" => CifpPathTerminator.CA,
            "FA" => CifpPathTerminator.FA,
            "VA" => CifpPathTerminator.VA,
            "VM" => CifpPathTerminator.VM,
            "VI" => CifpPathTerminator.VI,
            "CI" => CifpPathTerminator.CI,
            _ => CifpPathTerminator.Other,
        };
    }

    internal static CifpAltitudeRestriction? ParseAltitudeRestriction(char description, string alt1Str, string alt2Str)
    {
        int? alt1 = ParseArinc424Altitude(alt1Str);
        int? alt2 = ParseArinc424Altitude(alt2Str);

        if (alt1 is null)
        {
            return null;
        }

        var type = description switch
        {
            '+' or 'H' => CifpAltitudeRestrictionType.AtOrAbove,
            '-' => CifpAltitudeRestrictionType.AtOrBelow,
            'B' when alt2 is not null => CifpAltitudeRestrictionType.Between,
            'G' or 'I' or 'J' => CifpAltitudeRestrictionType.GlideSlopeIntercept,
            _ => CifpAltitudeRestrictionType.At,
        };

        return new CifpAltitudeRestriction(type, alt1.Value, alt2);
    }

    internal static int? ParseArinc424Altitude(string s)
    {
        s = s.Trim();
        if (s.Length == 0)
        {
            return null;
        }

        // Flight level: "FL280", "FL28", " FL28"
        if (s.StartsWith("FL", StringComparison.Ordinal))
        {
            string flStr = s[2..].Trim();
            if (int.TryParse(flStr, out int fl))
            {
                return fl < 100 ? fl * 1000 : fl * 100;
            }

            return null;
        }

        // Numeric: value in tens of feet (e.g., "1700" = 17000ft)
        s = s.TrimStart('0');
        if (s.Length == 0)
        {
            return null;
        }

        return int.TryParse(s, out int val) ? val * 10 : null;
    }

    private sealed record RawProcedureLeg(
        string ProcedureId,
        char RouteType,
        string TransitionName,
        string FixIdentifier,
        CifpPathTerminator PathTerminator,
        string PathTerminatorRaw,
        char? TurnDirection,
        CifpAltitudeRestriction? Altitude,
        CifpSpeedRestriction? Speed,
        CifpFixRole FixRole,
        int Sequence,
        double? ArcRadiusNm,
        string? CenterFixId,
        string? RecommendedNavaidId,
        double? Theta,
        double? Rho,
        double? OutboundCourse,
        double? LegDistanceNm,
        bool IsFlyOver
    );

    private sealed record RawApproachLeg(
        string ApproachId,
        char RouteType,
        string TransitionName,
        string FixIdentifier,
        CifpPathTerminator PathTerminator,
        string PathTerminatorRaw,
        char? TurnDirection,
        CifpAltitudeRestriction? Altitude,
        CifpSpeedRestriction? Speed,
        CifpFixRole FixRole,
        int Sequence,
        double? ArcRadiusNm,
        string? CenterFixId,
        string? RecommendedNavaidId,
        double? Theta,
        double? Rho,
        double? OutboundCourse,
        double? LegDistanceNm,
        bool IsFlyOver
    );

    private static void ProcessApproachRecord(string line, Dictionary<string, FafCandidate> fafByApproach)
    {
        // Waypoint description code at position 43 (0-indexed: 42)
        char waypointDesc = line[42];
        if (waypointDesc is not ('D' or 'F'))
        {
            return; // Not a FAF
        }

        // Airport ICAO at positions 7-10 (0-indexed: 6-9)
        string icao = line[6..10].Trim();
        string airport = icao.StartsWith('K') ? icao[1..] : icao;

        // Approach ID at positions 14-19 (0-indexed: 13-18)
        string approachId = line[13..19].Trim();
        if (approachId.Length == 0)
        {
            return;
        }

        // Extract runway from approach ID
        string? runway = ParseRunwayFromApproachId(approachId);
        if (runway is null)
        {
            return;
        }

        // Fix identifier at positions 30-34 (0-indexed: 29-33)
        string fixId = line[29..34].Trim();
        if (fixId.Length == 0)
        {
            return;
        }

        // Approach type priority from first char of approach ID
        char typeCode = approachId[0];
        int priority = ApproachTypePriority.GetValueOrDefault(typeCode, DefaultPriority);

        string key = $"{airport}:{approachId}";

        // Keep the last FAF in each approach (highest sequence wins)
        if (!fafByApproach.TryGetValue(key, out var existing) || existing.Priority > priority)
        {
            fafByApproach[key] = new FafCandidate(airport, runway, fixId, priority);
        }
    }

    private static void ProcessTerminalWaypoint(string line, Dictionary<string, (double Lat, double Lon)> waypoints)
    {
        // Waypoint identifier at positions 14-18 (0-indexed: 13-17)
        string ident = line[13..18].Trim();
        if (ident.Length == 0 || waypoints.ContainsKey(ident))
        {
            return;
        }

        // Scan for N/S latitude marker starting from position 28
        int latStart = -1;
        int scanEnd = Math.Min(45, line.Length);
        for (int i = 28; i < scanEnd; i++)
        {
            if (line[i] is 'N' or 'S')
            {
                latStart = i;
                break;
            }
        }

        if (latStart < 0 || line.Length < latStart + 19)
        {
            return;
        }

        var lat = ParseArinc424Latitude(line.AsSpan(latStart, 9));
        var lon = ParseArinc424Longitude(line.AsSpan(latStart + 9, 10));

        if (lat is not null && lon is not null)
        {
            waypoints[ident] = (lat.Value, lon.Value);
        }
    }

    internal static double? ParseArinc424Latitude(ReadOnlySpan<char> s)
    {
        if (s.Length < 9)
        {
            return null;
        }

        char hemisphere = s[0];
        if (hemisphere is not ('N' or 'S'))
        {
            return null;
        }

        if (
            !int.TryParse(s[1..3], out int deg)
            || !int.TryParse(s[3..5], out int min)
            || !int.TryParse(s[5..7], out int sec)
            || !int.TryParse(s[7..9], out int hundredths)
        )
        {
            return null;
        }

        double result = deg + min / 60.0 + (sec + hundredths / 100.0) / 3600.0;
        return hemisphere == 'S' ? -result : result;
    }

    internal static double? ParseArinc424Longitude(ReadOnlySpan<char> s)
    {
        if (s.Length < 10)
        {
            return null;
        }

        char hemisphere = s[0];
        if (hemisphere is not ('E' or 'W'))
        {
            return null;
        }

        if (
            !int.TryParse(s[1..4], out int deg)
            || !int.TryParse(s[4..6], out int min)
            || !int.TryParse(s[6..8], out int sec)
            || !int.TryParse(s[8..10], out int hundredths)
        )
        {
            return null;
        }

        double result = deg + min / 60.0 + (sec + hundredths / 100.0) / 3600.0;
        return hemisphere == 'W' ? -result : result;
    }

    private static string? ParseRunwayFromApproachId(string approachId)
    {
        if (approachId.Length < 2)
        {
            return null;
        }

        // Skip first character (approach type code)
        string rest = approachId[1..];

        var match = RunwayPattern().Match(rest);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^(\d{1,2}[LRC]?)")]
    private static partial Regex RunwayPattern();

    private sealed record FafCandidate(string Airport, string Runway, string FafFix, int Priority);
}

/// <summary>
/// Result of parsing a CIFP file: FAF fix names per runway
/// and terminal waypoint coordinates.
/// </summary>
public sealed class CifpParseResult
{
    /// <summary>
    /// (airport FAA ID, runway ID) → FAF fix identifier.
    /// </summary>
    public Dictionary<(string Airport, string Runway), string> FafFixes { get; }

    /// <summary>
    /// Fix identifier → (lat, lon) for terminal waypoints.
    /// </summary>
    public Dictionary<string, (double Lat, double Lon)> TerminalWaypoints { get; }

    public CifpParseResult(
        Dictionary<(string Airport, string Runway), string> fafFixes,
        Dictionary<string, (double Lat, double Lon)> terminalWaypoints
    )
    {
        FafFixes = fafFixes;
        TerminalWaypoints = terminalWaypoints;
    }
}
