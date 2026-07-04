using static Yaat.Sim.Commands.CanonicalCommandType;

namespace Yaat.Sim.Commands;

public static class CommandRegistry
{
    public static IReadOnlyDictionary<CanonicalCommandType, CommandDefinition> All { get; } = Build();

    /// <summary>
    /// Reverse lookup: alias (uppercased) → CanonicalCommandType.
    /// Used by CommandParser as a fallback when the switch doesn't handle a verb.
    /// </summary>
    public static IReadOnlyDictionary<string, CanonicalCommandType> AliasToCanonicType { get; } = BuildAliasToCanonicType();

    public static CommandDefinition? Get(CanonicalCommandType type)
    {
        return All.GetValueOrDefault(type);
    }

    public static IReadOnlyList<string> AliasesFor(CanonicalCommandType type) =>
        All.TryGetValue(type, out var def) ? def.DefaultAliases : Array.Empty<string>();

    public static bool IsAliasFor(CanonicalCommandType type, string token)
    {
        foreach (var alias in AliasesFor(type))
        {
            if (string.Equals(alias, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<CommandDefinition> ByCategory(string category)
    {
        return All.Values.Where(d => d.Category == category).ToArray();
    }

    /// <summary>
    /// Renders the expected signature for a command suitable for inclusion in parse-error
    /// feedback. Examples: "CM &lt;altitude&gt;", "RWY &lt;runway&gt; [TAXI &lt;path&gt;]",
    /// "EXP | EXP &lt;altitude&gt;". Required parameters are rendered as &lt;name&gt;,
    /// optionals as [&lt;name&gt;], literals stay uppercase. Returns the bare verb if the
    /// command type isn't in the registry.
    /// </summary>
    public static string RenderSignature(CanonicalCommandType type)
    {
        if (!All.TryGetValue(type, out var def))
        {
            return type.ToString().ToUpperInvariant();
        }

        var verb = def.DefaultAliases.Length > 0 ? def.DefaultAliases[0] : def.Type.ToString().ToUpperInvariant();
        if (def.Overloads.Length == 0)
        {
            return verb;
        }

        var rendered = new List<string>(def.Overloads.Length);
        foreach (var overload in def.Overloads)
        {
            rendered.Add(RenderOverload(verb, overload));
        }

        return string.Join(" | ", rendered);
    }

    private static string RenderOverload(string verb, CommandOverload overload)
    {
        if (overload.Parameters.Length == 0)
        {
            return verb;
        }

        var parts = new List<string>(overload.Parameters.Length + 1) { verb };
        foreach (var param in overload.Parameters)
        {
            if (param.IsLiteral)
            {
                parts.Add(param.IsOptional ? $"[{param.Name.ToUpperInvariant()}]" : param.Name.ToUpperInvariant());
                continue;
            }

            parts.Add(param.IsOptional ? $"[<{param.Name}>]" : $"<{param.Name}>");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Returns the set of aliases (uppercased) for commands where every overload
    /// takes exactly one required non-literal parameter. Used by ExpandMultiCommand
    /// to split concatenated verb-arg pairs like "FH 270 CM 5000" → "FH 270, CM 5000".
    /// </summary>
    public static HashSet<string> SingleArgAliases { get; } = BuildSingleArgAliases();

    private static HashSet<string> BuildSingleArgAliases()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in All.Values)
        {
            if (def.Overloads.Length == 0)
            {
                continue;
            }

            // A verb is splittable if it has an overload with exactly 1 non-literal param
            // AND no overload has more than 1 non-literal param (avoids multi-arg verbs like DEPART).
            bool hasSingleArg = def.Overloads.Any(o => o.Parameters.Count(p => !p.IsLiteral) == 1);
            bool hasMultiArg = def.Overloads.Any(o => o.Parameters.Count(p => !p.IsLiteral) > 1);

            if (!hasSingleArg || hasMultiArg)
            {
                continue;
            }

            foreach (var alias in def.DefaultAliases)
            {
                result.Add(alias);
            }
        }

        return result;
    }

    private static Dictionary<string, CanonicalCommandType> BuildAliasToCanonicType()
    {
        var result = new Dictionary<string, CanonicalCommandType>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in All.Values)
        {
            foreach (var alias in def.DefaultAliases)
            {
                result.TryAdd(alias, def.Type);
            }
        }

        return result;
    }

    private static Dictionary<CanonicalCommandType, CommandDefinition> Build()
    {
        CommandDefinition[] defs =
        [
            .. HeadingCommands(),
            .. AltitudeSpeedCommands(),
            .. ForceCommands(),
            .. TransponderCommands(),
            .. NavigationCommands(),
            .. TowerCommands(),
            .. PatternCommands(),
            .. HoldCommands(),
            .. HelicopterCommands(),
            .. GroundCommands(),
            .. SimControlCommands(),
            .. TrackCommands(),
            .. DataCommands(),
            .. CoordinationCommands(),
            .. BroadcastCommands(),
            .. ApproachCommands(),
            .. QueueCommands(),
            .. ConsolidationCommands(),
            .. FlightPlanCommands(),
        ];
        return defs.ToDictionary(d => d.Type);
    }

    private static CommandDefinition[] HeadingCommands() =>
        [
            Cmd(FlyHeading, "Fly Heading", "Heading", false, ["FH", "H"], [O(null, [R("heading", "0-360")], "Fly assigned heading")]),
            Cmd(TurnLeft, "Turn Left", "Heading", false, ["TL", "L"], [O(null, [R("heading", "0-360")], "Turn left to heading")]),
            Cmd(TurnRight, "Turn Right", "Heading", false, ["TR", "R"], [O(null, [R("heading", "0-360")], "Turn right to heading")]),
            Cmd(
                RelativeLeft,
                "Relative Left",
                "Heading",
                false,
                ["RELL", "LT"],
                [O(null, [R("degrees", "1-360")], "Turn left by degrees")],
                syntaxPatterns: ["T{n}L"]
            ),
            Cmd(
                RelativeRight,
                "Relative Right",
                "Heading",
                false,
                ["RELR", "RT"],
                [O(null, [R("degrees", "1-360")], "Turn right by degrees")],
                syntaxPatterns: ["T{n}R"]
            ),
            Bare(FlyPresentHeading, "Fly Present Heading", "Heading", false, ["FPH", "FCH"]),
        ];

    private static CommandDefinition[] AltitudeSpeedCommands() =>
        [
            Cmd(
                ClimbMaintain,
                "Climb/Maintain",
                "Altitude / Speed",
                false,
                ["CM"],
                [O(null, [R("altitude", "altitude in hundreds")], "Climb and maintain altitude")]
            ),
            Cmd(
                DescendMaintain,
                "Descend/Maintain",
                "Altitude / Speed",
                false,
                ["DM"],
                [O(null, [R("altitude", "altitude in hundreds")], "Descend and maintain altitude")]
            ),
            Cmd(
                Speed,
                "Speed",
                "Altitude / Speed",
                false,
                ["SPD", "SPEED", "DS", "IS", "SLOW", "SL"],
                [O(null, [R("speed", "knots IAS")], "Maintain speed")]
            ),
            Cmd(
                ForceSpeedFinal,
                "Force Speed (Final)",
                "Altitude / Speed",
                false,
                ["SPEEDF", "SPDF", "SLF"],
                [O(null, [R("speed", "knots IAS (+/- ok)")], "Maintain speed, overriding the 5nm-final restriction")]
            ),
            Bare(ResumeNormalSpeed, "Resume Normal Speed", "Altitude / Speed", false, ["RNS", "NS"]),
            Bare(ReduceToFinalApproachSpeed, "Reduce to Final Approach Speed", "Altitude / Speed", false, ["RFAS", "FAS"]),
            Bare(DeleteSpeedRestrictions, "Delete Speed Restrictions", "Altitude / Speed", false, ["DSR"]),
            Cmd(
                Expedite,
                "Expedite",
                "Altitude / Speed",
                false,
                ["EXP"],
                [
                    O(null, [], "Expedite current climb/descent (or taxi when on the ground)"),
                    O("Altitude", [R("altitude", "altitude in hundreds")], "Expedite climb/descent to altitude"),
                ]
            ),
            Bare(NormalRate, "Normal Rate", "Altitude / Speed", false, ["NORM"]),
            Cmd(Mach, "Maintain Mach", "Altitude / Speed", false, ["MACH", "M"], [O(null, [R("mach", ".XX mach number")], "Maintain Mach number")]),
        ];

    private static CommandDefinition[] ForceCommands() =>
        [
            Cmd(ForceHeading, "Force Heading", "Sim Control", false, ["FHN"], [O(null, [R("heading", "0-360")], "Instantly set heading")]),
            Cmd(
                ForceAltitude,
                "Force Altitude",
                "Sim Control",
                false,
                ["CMN"],
                [O(null, [R("altitude", "altitude in hundreds")], "Instantly set altitude")]
            ),
            Cmd(
                ForceSpeed,
                "Force Speed",
                "Sim Control",
                false,
                ["SPDN", "SLN", "SPEEDN"],
                [O(null, [R("speed", "knots IAS")], "Instantly set speed")]
            ),
            Cmd(
                Warp,
                "Warp to Position",
                "Sim Control",
                false,
                ["WARP"],
                [
                    O(
                        null,
                        [
                            R("FRD", "fix/radial/distance"),
                            Opt("heading", "1-360 (omit to keep current)"),
                            Opt("altitude", "feet or hundreds (omit to keep current)"),
                            Opt("speed", "knots IAS (omit to keep current)"),
                        ],
                        "Teleport aircraft to position"
                    ),
                ]
            ),
            Cmd(
                WarpGround,
                "Warp Ground",
                "Sim Control",
                false,
                ["WARPG"],
                [O(null, [R("location", "C B / #42 / @B12 / $9")], "Teleport aircraft on ground")]
            ),
        ];

    private static CommandDefinition[] TransponderCommands() =>
        [
            Cmd(
                Squawk,
                "Squawk",
                "Transponder",
                false,
                ["SQ", "SQUAWK"],
                [O(null, [], "Assign random squawk code"), O("Code", [R("code", "0000-7777")], "Assign squawk code")]
            ),
            Bare(SquawkVfr, "Squawk VFR", "Transponder", false, ["SQVFR", "SQV"]),
            Bare(SquawkNormal, "Squawk Normal", "Transponder", false, ["SQNORM", "SN", "SQA", "SQON"]),
            Bare(SquawkStandby, "Squawk Standby", "Transponder", false, ["SQSBY", "SQS", "SS"]),
            Bare(Ident, "Ident", "Transponder", false, ["IDENT", "ID", "SQI", "SQID"]),
            Bare(RandomSquawk, "Random Squawk", "Transponder", false, ["RANDSQ"]),
            Bare(SquawkAll, "Squawk All", "Transponder", true, ["SQALL"]),
            Bare(SquawkNormalAll, "Squawk Normal All", "Transponder", true, ["SNALL"]),
            Bare(SquawkStandbyAll, "Squawk Standby All", "Transponder", true, ["SSALL"]),
        ];

    private static CommandDefinition[] NavigationCommands() =>
        [
            Cmd(DirectTo, "Direct To", "Navigation", false, ["DCT"], [O(null, [R("fix", "fix name")], "Proceed direct to fix")]),
            Cmd(
                ForceDirectTo,
                "Force Direct To Fix",
                "Navigation",
                false,
                ["DCTF"],
                [O(null, [R("fix", "fix name")], "Direct to fix (bypass validation)")]
            ),
            Cmd(
                AppendDirectTo,
                "Append Direct To",
                "Navigation",
                false,
                ["ADCT"],
                [O(null, [R("fix", "fix name")], "Append direct-to after current route")]
            ),
            Cmd(
                AppendForceDirectTo,
                "Append Force Direct To",
                "Navigation",
                false,
                ["ADCTF"],
                [O(null, [R("fix", "fix name")], "Append force direct-to after current route")]
            ),
            Cmd(
                TurnLeftDirectTo,
                "Turn Left Direct To",
                "Navigation",
                false,
                ["TLDCT"],
                [O(null, [R("fix", "fix name")], "Turn left, proceed direct to fix")]
            ),
            Cmd(
                TurnRightDirectTo,
                "Turn Right Direct To",
                "Navigation",
                false,
                ["TRDCT"],
                [O(null, [R("fix", "fix name")], "Turn right, proceed direct to fix")]
            ),
        ];

    private static CommandDefinition[] TowerCommands() =>
        [
            Cmd(
                LineUpAndWait,
                "Line Up and Wait",
                "Tower",
                false,
                ["LUAW", "POS", "LU", "PH"],
                [O(null, [], "Line up and wait")],
                [Mod("IMM", null, false), Mod("WD", null, false), Mod("ND", null, false)]
            ),
            Cmd(
                ClearedForTakeoff,
                "Cleared for Takeoff",
                "Tower",
                false,
                ["CTO"],
                [
                    O(null, [], "Cleared for takeoff (fly runway heading)"),
                    O("RH", [L("RH"), Opt("altitude", "alt")], "Fly runway heading on departure"),
                    O("OC", [L("OC"), Opt("altitude", "alt")], "Fly on course after departure"),
                    O("Heading", [R("heading", "0-360"), Opt("altitude", "alt")], "Fly heading on departure"),
                    O("LT", [L("LT"), R("heading", "0-360"), Opt("altitude", "alt")], "Turn left to heading on departure"),
                    O("RT", [L("RT"), R("heading", "0-360"), Opt("altitude", "alt")], "Turn right to heading on departure"),
                    O("MLT", [L("MLT"), Opt("runway", "runway designator")], "Make left traffic on departure"),
                    O("MRT", [L("MRT"), Opt("runway", "runway designator")], "Make right traffic on departure"),
                    O("DCT", [L("DCT"), R("fix", "fix name"), Opt("altitude", "alt")], "Proceed direct to fix on departure"),
                    O("TLDCT", [L("TLDCT"), R("fix", "fix name"), Opt("altitude", "alt")], "Turn left direct to fix on departure"),
                    O("TRDCT", [L("TRDCT"), R("fix", "fix name"), Opt("altitude", "alt")], "Turn right direct to fix on departure"),
                    O("MRC", [L("MRC"), Opt("altitude", "alt")], "Turn right crosswind on departure"),
                    O("MRD", [L("MRD"), Opt("altitude", "alt")], "Turn right downwind on departure"),
                    O("MLC", [L("MLC"), Opt("altitude", "alt")], "Turn left crosswind on departure"),
                    O("MLD", [L("MLD"), Opt("altitude", "alt")], "Turn left downwind on departure"),
                    O("MR270", [L("MR270"), Opt("altitude", "alt")], "Right 270 on departure"),
                    O("ML270", [L("ML270"), Opt("altitude", "alt")], "Left 270 on departure"),
                    O("360", [L("360"), Opt("altitude", "alt")], "360 overhead on departure"),
                ],
                [Mod("CWT", null, false), Mod("IMM", null, false), Mod("WD", null, false), Mod("ND", null, false)]
            ),
            Bare(CancelTakeoffClearance, "Cancel Takeoff Clearance", "Tower", false, ["CTOC"]),
            Cmd(
                GoAround,
                "Go Around",
                "Tower",
                false,
                ["GA"],
                [
                    O(null, [], "Go around (fly runway heading)"),
                    O("Heading", [R("heading", "0-360")], "Go around and fly heading"),
                    O("Heading+Alt", [R("heading", "0-360"), R("altitude", "alt")], "Go around, fly heading, climb to altitude"),
                    O("MLT", [L("MLT")], "Go around, make left traffic"),
                    O("MRT", [L("MRT")], "Go around, make right traffic"),
                    O("MLT+Alt", [L("MLT"), R("altitude", "pattern alt")], "Go around, left traffic at altitude"),
                    O("MRT+Alt", [L("MRT"), R("altitude", "pattern alt")], "Go around, right traffic at altitude"),
                ]
            ),
            Cmd(
                ClearedToLand,
                "Cleared to Land",
                "Tower",
                false,
                ["CLAND", "CL", "FS"],
                [O(null, [], "Cleared to land"), O(null, [R("runway", "runway")], "Cleared to land on runway")],
                [Mod("NODEL", null, false), Mod("CWT", null, false)]
            ),
            Cmd(
                LandAndHoldShort,
                "Land and Hold Short",
                "Tower",
                false,
                ["LAHSO"],
                [O(null, [R("runway", "hold short runway")], "Cleared to land, hold short of runway")]
            ),
            Bare(CancelLandingClearance, "Cancel Landing Clearance", "Tower", false, ["CLC", "CTLC"]),
            Bare(ForceLanding, "Force Landing", "Tower", false, ["CLANDF"]),
            Cmd(
                TouchAndGo,
                "Touch and Go",
                "Tower",
                false,
                ["TG"],
                [
                    O(null, [], "Touch and go"),
                    O("Runway", [R("runway", "runway designator")], "Touch and go runway"),
                    O("Traffic", [R("direction", "MLT/MRT")], "Touch and go, make traffic"),
                ]
            ),
            Cmd(
                StopAndGo,
                "Stop and Go",
                "Tower",
                false,
                ["SG"],
                [O(null, [], "Stop and go"), O("Traffic", [R("direction", "MLT/MRT")], "Stop and go, make traffic")]
            ),
            Cmd(
                LowApproach,
                "Low Approach",
                "Tower",
                false,
                ["LA"],
                [O(null, [], "Low approach"), O("Traffic", [R("direction", "MLT/MRT")], "Low approach, make traffic")]
            ),
            Cmd(
                ClearedForOption,
                "Cleared for the Option",
                "Tower",
                false,
                ["COPT"],
                [O(null, [], "Cleared for the option"), O("Traffic", [R("direction", "MLT/MRT")], "Option, make traffic")]
            ),
        ];

    private static CommandDefinition[] PatternCommands() =>
        [
            PatternEntry(EnterLeftDownwind, "Enter Left Downwind", ["ELD"]),
            PatternEntry(EnterRightDownwind, "Enter Right Downwind", ["ERD"]),
            PatternEntry(EnterLeftCrosswind, "Enter Left Crosswind", ["ELC"]),
            PatternEntry(EnterRightCrosswind, "Enter Right Crosswind", ["ERC"]),
            Cmd(
                EnterLeftBase,
                "Enter Left Base",
                "Pattern",
                false,
                ["ELB"],
                [
                    O(null, [], "Enter left base for current runway"),
                    O("Runway", [R("runway", "runway designator")], "Enter left base for runway"),
                    O("Runway + Distance", [R("runway", "runway designator"), R("distance", "nm from threshold")], "Enter left base at distance"),
                ]
            ),
            Cmd(
                EnterRightBase,
                "Enter Right Base",
                "Pattern",
                false,
                ["ERB"],
                [
                    O(null, [], "Enter right base for current runway"),
                    O("Runway", [R("runway", "runway designator")], "Enter right base for runway"),
                    O("Runway + Distance", [R("runway", "runway designator"), R("distance", "nm from threshold")], "Enter right base at distance"),
                ]
            ),
            PatternEntry(EnterFinal, "Enter Final", ["EF"]),
            Cmd(
                MakeLeftTraffic,
                "Make Left Traffic",
                "Pattern",
                false,
                ["MLT"],
                [
                    O(null, [], "Make left traffic for current runway"),
                    O("Runway", [R("runway", "runway designator")], "Make left traffic for runway"),
                    O("Altitude", [R("altitude", "pattern alt")], "Make left traffic at altitude"),
                    O("RunwayAltitude", [R("runway", "runway designator"), R("altitude", "pattern alt")], "Make left traffic for runway at altitude"),
                ]
            ),
            Cmd(
                MakeRightTraffic,
                "Make Right Traffic",
                "Pattern",
                false,
                ["MRT"],
                [
                    O(null, [], "Make right traffic for current runway"),
                    O("Runway", [R("runway", "runway designator")], "Make right traffic for runway"),
                    O("Altitude", [R("altitude", "pattern alt")], "Make right traffic at altitude"),
                    O(
                        "RunwayAltitude",
                        [R("runway", "runway designator"), R("altitude", "pattern alt")],
                        "Make right traffic for runway at altitude"
                    ),
                ]
            ),
            Bare(TurnCrosswind, "Turn Crosswind", "Pattern", false, ["TC"]),
            Bare(TurnDownwind, "Turn Downwind", "Pattern", false, ["TD"]),
            Bare(TurnBase, "Turn Base", "Pattern", false, ["TB"]),
            Cmd(
                ExtendPattern,
                "Extend Pattern Leg",
                "Pattern",
                false,
                ["EXT", "EXTEND"],
                [
                    O(null, [], "Extend current pattern leg"),
                    O("Leg", [R("leg", "pattern leg")], "Extend specific pattern leg (rolls back one leg if already past it)"),
                ]
            ),
            Bare(MakeShortApproach, "Make Short Approach", "Pattern", false, ["SA", "MSA"]),
            Bare(MakeNormalApproach, "Make Normal Approach", "Pattern", false, ["MNA"]),
            Bare(Cancel270, "Cancel 270", "Pattern", false, ["NO270"]),
            Bare(MakeLeft360, "Make Left 360", "Pattern", false, ["L360", "ML3", "ML360"]),
            Bare(MakeRight360, "Make Right 360", "Pattern", false, ["R360", "MR3", "MR360"]),
            Bare(MakeLeft270, "Make Left 270", "Pattern", false, ["L270"]),
            Bare(MakeRight270, "Make Right 270", "Pattern", false, ["R270"]),
            Cmd(
                PatternSize,
                "Pattern Size",
                "Pattern",
                false,
                ["PS", "PATTSIZE"],
                [O(null, [R("multiplier", "e.g. 0.5 / 1.0 / 2.0")], "Set traffic pattern size multiplier")]
            ),
            Cmd(
                MakeLeftSTurns,
                "S-Turns (Initial Left)",
                "Pattern",
                false,
                ["MLS"],
                [O(null, [], "Make S-turns, initial turn left"), O("Count", [R("count", "number of turns")], "Make N S-turns, initial turn left")]
            ),
            Cmd(
                MakeRightSTurns,
                "S-Turns (Initial Right)",
                "Pattern",
                false,
                ["MRS"],
                [O(null, [], "Make S-turns, initial turn right"), O("Count", [R("count", "number of turns")], "Make N S-turns, initial turn right")]
            ),
            Cmd(
                OffsetLeftPattern,
                "Offset Pattern Left",
                "Pattern",
                false,
                ["OFL", "OFFSETL"],
                [
                    O(null, [], "Dogleg left, hold 0.5 NM left of current pattern heading"),
                    O("OffsetNm", [R("offsetNm", "0.1-2.0 NM")], "Dogleg left, hold N NM left of current pattern heading"),
                ]
            ),
            Cmd(
                OffsetRightPattern,
                "Offset Pattern Right",
                "Pattern",
                false,
                ["OFR", "OFFSETR"],
                [
                    O(null, [], "Dogleg right, hold 0.5 NM right of current pattern heading"),
                    O("OffsetNm", [R("offsetNm", "0.1-2.0 NM")], "Dogleg right, hold N NM right of current pattern heading"),
                ]
            ),
            Bare(Plan270, "Plan 270 at Next Turn", "Pattern", false, ["P270", "PLAN270"]),
            Bare(CircleAirport, "Circle Airport", "Pattern", false, ["CA", "CIRCLE"]),
        ];

    private static CommandDefinition[] HoldCommands() =>
        [
            Bare(HoldPresentPosition360Left, "Hold (360 Left)", "Hold", false, ["HPPL"]),
            Bare(HoldPresentPosition360Right, "Hold (360 Right)", "Hold", false, ["HPPR"]),
            Bare(HoldPresentPositionHover, "Hold Present Position", "Hold", false, ["HPP"]),
            Cmd(HoldAtFixLeft, "Hold at Fix (Left)", "Hold", false, ["HFIXL"], [O(null, [R("fix", "fix name")], "Hold at fix with left turns")]),
            Cmd(HoldAtFixRight, "Hold at Fix (Right)", "Hold", false, ["HFIXR"], [O(null, [R("fix", "fix name")], "Hold at fix with right turns")]),
            Cmd(HoldAtFixHover, "Hold at Fix", "Hold", false, ["HFIX"], [O(null, [R("fix", "fix name")], "Hold/hover at fix")]),
        ];

    private static CommandDefinition[] HelicopterCommands() =>
        [
            Cmd(
                AirTaxi,
                "Air Taxi",
                "Helicopter",
                false,
                ["ATXI"],
                [O(null, [], "Air taxi to destination"), O("Helipad", [R("helipad", "helipad/gate ID")], "Air taxi to helipad")]
            ),
            Cmd(
                Land,
                "Land",
                "Helicopter",
                false,
                ["LAND"],
                [O(null, [R("helipad", "helipad/gate ID")], "Land at helipad")],
                [Mod("NODEL", null, false)]
            ),
            Cmd(
                ClearedTakeoffPresent,
                "Cleared Takeoff Present Position",
                "Helicopter",
                false,
                ["CTOPP"],
                [
                    O(null, [], "Vertical liftoff to a hover, hold present position at 25 ft AGL"),
                    O("Hover alt", [R("agl", "+0XX AGL")], "Vertical liftoff to a hover at +0XX ft AGL (e.g. +002 = 200 ft)"),
                    O("OC", [L("OC"), Opt("altitude", "alt")], "Fly on course after liftoff"),
                    O("Heading", [R("heading", "0-360"), Opt("altitude", "alt")], "Fly heading after liftoff"),
                    O("LT", [L("LT"), R("heading", "0-360"), Opt("altitude", "alt")], "Turn left to heading after liftoff"),
                    O("RT", [L("RT"), R("heading", "0-360"), Opt("altitude", "alt")], "Turn right to heading after liftoff"),
                    O("DCT", [L("DCT"), R("fix", "fix name"), Opt("altitude", "alt")], "Proceed direct to fix after liftoff"),
                    O("TLDCT", [L("TLDCT"), R("fix", "fix name"), Opt("altitude", "alt")], "Turn left direct to fix after liftoff"),
                    O("TRDCT", [L("TRDCT"), R("fix", "fix name"), Opt("altitude", "alt")], "Turn right direct to fix after liftoff"),
                ]
            ),
        ];

    private static CommandDefinition[] GroundCommands() =>
        [
            Cmd(
                Pushback,
                "Pushback",
                "Ground",
                false,
                ["PUSH"],
                [
                    O(null, [], "Pushback (auto heading)"),
                    O("Cardinal", [R("orientation", "<C/>C or FACE C/TAIL C, C∈N/NE/E/SE/S/SW/W/NW")], "Pushback with cardinal facing"),
                    O("Onto", [R("taxiway", "taxiway/exit")], "Pushback onto taxiway"),
                    O("Onto+facing", [R("taxiway", "taxiway"), R("facing_taxiway", "taxiway")], "Onto taxiway facing toward another taxiway"),
                    O("Onto+cardinal", [R("taxiway", "taxiway"), R("orientation", "<C/>C or FACE C/TAIL C")], "Onto taxiway with cardinal hint"),
                ],
                [Mod("@", "parking", false), Mod("$", "spot", false)]
            ),
            Cmd(
                Taxi,
                "Taxi",
                "Ground",
                false,
                ["TAXI"],
                [O(null, [R("route", "taxiway names")], "Taxi via route")],
                [
                    Mod("RWY", "runway", false),
                    Mod("HS", "taxiway/runway", true),
                    Mod("CROSS", "runway", true),
                    Mod("NODEL", null, false),
                    Mod("@", "parking", false),
                    Mod("$", "spot", false),
                ]
            ),
            Bare(HoldPosition, "Hold Position", "Ground", false, ["HOLD", "HP"]),
            Cmd(
                Resume,
                "Resume Taxi",
                "Ground",
                false,
                ["RES", "RESUME"],
                [O(null, [], "Resume taxi")],
                [Mod("CROSS", "runway", true), Mod("HS", "taxiway/runway", true)]
            ),
            Cmd(
                CrossRunway,
                "Cross Runway",
                "Ground",
                false,
                ["CROSS"],
                [O(null, [], "Cross next hold-short"), O(null, [R("runway", "runway designator")], "Cross runway")]
            ),
            Cmd(HoldShort, "Hold Short", "Ground", false, ["HS"], [O(null, [R("taxiway", "taxiway/runway")], "Hold short of taxiway or runway")]),
            Cmd(
                AssignRunway,
                "Assign Runway",
                "Ground",
                false,
                ["RWY"],
                [O(null, [R("runway", "runway number")], "Assign departure/arrival runway")],
                [Mod("TAXI", null, false)]
            ),
            Cmd(
                FollowGround,
                "Follow (Ground)",
                "Ground",
                false,
                ["FOLLOWG", "FOLG"],
                [O(null, [R("callsign", "traffic callsign")], "Follow traffic on ground")]
            ),
            Cmd(
                GiveWay,
                "Give Way",
                "Ground",
                false,
                ["GIVEWAY", "BEHIND", "GW"],
                [O(null, [R("callsign", "traffic callsign")], "Give way to traffic")]
            ),
            Cmd(
                ExitLeft,
                "Exit Left",
                "Ground",
                false,
                ["EL", "EXITL"],
                [O(null, [], "Exit runway to the left"), O("Taxiway", [R("taxiway", "taxiway name")], "Exit runway left onto taxiway")],
                [Mod("NODEL", null, false), Mod("EXP", null, false)]
            ),
            Cmd(
                ExitRight,
                "Exit Right",
                "Ground",
                false,
                ["ER", "EXITR"],
                [O(null, [], "Exit runway to the right"), O("Taxiway", [R("taxiway", "taxiway name")], "Exit runway right onto taxiway")],
                [Mod("NODEL", null, false), Mod("EXP", null, false)]
            ),
            Cmd(
                ExitTaxiway,
                "Exit Taxiway",
                "Ground",
                false,
                ["EXIT"],
                [O(null, [R("taxiway", "taxiway name")], "Exit onto taxiway")],
                [Mod("NODEL", null, false), Mod("EXP", null, false)]
            ),
            Cmd(
                TaxiAll,
                "Taxi All",
                "Ground",
                true,
                ["TAXIALL"],
                [O(null, [R("destination", "runway, @parking, or $spot")], "Taxi all parked aircraft to destination (A* pathfinding)")]
            ),
            Cmd(
                TaxiAuto,
                "Taxi Auto",
                "Ground",
                false,
                ["TAXIAUTO"],
                [O(null, [R("destination", "runway or @parking")], "Auto-route taxi to runway or parking (A* pathfinding)")]
            ),
            Bare(BreakConflict, "Break Conflict", "Ground", false, ["BREAK"]),
            Bare(ClearRunway, "Clear Runway", "Ground", false, ["CLRWY", "CLEARRWY"]),
            Bare(Go, "Begin Takeoff Roll", "Tower", false, ["GO"]),
        ];

    private static CommandDefinition[] SimControlCommands() =>
        [
            Bare(Delete, "Delete", "Sim Control", false, ["DEL", "X"]),
            Bare(CancelAutoDelete, "Cancel Auto-Delete", "Sim Control", false, ["NODEL"]),
            Bare(Pause, "Pause", "Sim Control", true, ["PAUSE", "P"]),
            Bare(Unpause, "Unpause", "Sim Control", true, ["UNPAUSE", "U", "UN", "UNP", "UP"]),
            Cmd(SimRate, "Sim Rate", "Sim Control", true, ["SIMRATE"], [O(null, [R("rate", "1-8")], "Set simulation speed")]),
            Cmd(
                SetTurnRate,
                "Set Turn Rate",
                "Sim Control",
                false,
                ["TRATE"],
                [O(null, [Opt("rate", "deg/sec, 0.5-45; omit to clear")], "Set aircraft turn rate")]
            ),
            Cmd(
                Wait,
                "Wait (seconds)",
                "Sim Control",
                false,
                ["WAIT", "DELAY"],
                [O(null, [R("seconds", "delay in seconds")], "Wait before next command")]
            ),
            Cmd(
                WaitDistance,
                "Wait (distance)",
                "Sim Control",
                false,
                ["WAITD"],
                [O(null, [R("distance", "nm from fix")], "Wait until distance from fix")]
            ),
            Cmd(
                CanonicalCommandType.Timer,
                "Timer",
                "Sim Control",
                false,
                ["TIMER", "TMR"],
                [
                    O(
                        null,
                        [R("duration", "mm:ss or seconds"), Opt("message", "free text")],
                        "Set a timer; SAYs the message (or \"timer expired\") on expiry"
                    ),
                    O("Cancel", [L("CANCEL"), R("id", "timer id or ALL")], "Cancel a running timer"),
                ]
            ),
            Cmd(
                Add,
                "Add Aircraft",
                "Sim Control",
                true,
                ["ADD"],
                [
                    O(
                        null,
                        [
                            R("rules", "IFR/VFR"),
                            R("weight", "S/S+/L/H"),
                            R("engine", "J/T/P/H"),
                            R("-bearing", "from airport"),
                            R("distance", "nm"),
                            R("altitude", "in hundreds"),
                        ],
                        "Add airborne aircraft"
                    ),
                    O(
                        "Arrival on STAR",
                        [
                            R("rules", "IFR"),
                            R("weight", "S/S+/L/H"),
                            R("engine", "J/T/P/H"),
                            R("wpt.star.rwy", "e.g. TBARR.TBARR4.34R"),
                            Opt("altitude", "in hundreds"),
                            Opt("SPspeed", "e.g. SP250"),
                            Opt("LVL", "hold level"),
                            Opt("airport", "ICAO, dflt primary"),
                        ],
                        "Add an IFR arrival established on a STAR (descend via, or level with LVL)"
                    ),
                ]
            ),
            Bare(SpawnNow, "Spawn Now", "Sim Control", false, ["SPAWN"]),
            Cmd(
                SpawnDelay,
                "Set Spawn Delay",
                "Sim Control",
                false,
                ["SPAWNDELAY"],
                [O(null, [R("seconds", "delay in seconds")], "Set deferred spawn delay")]
            ),
            Cmd(
                HoldForRelease,
                "Hold for Release",
                "Sim Control",
                true,
                ["HFR"],
                [O(null, [R("airport", "airport ID")], "Arm hold-for-release for an airport's IFR departures")]
            ),
            Cmd(
                DisarmHoldForRelease,
                "Disarm Hold for Release",
                "Sim Control",
                true,
                ["HFROFF"],
                [O(null, [R("airport", "airport ID")], "Disarm hold-for-release (auto-releases anything still held)")]
            ),
            Cmd(
                ReleaseDeparture,
                "Release Departure",
                "Sim Control",
                true,
                ["REL", "CTOA"],
                [
                    O(null, [R("target", "airport or callsign")], "Release the next pending departure at a field, or a specific callsign"),
                    O(
                        "Spaced",
                        [R("airport", "airport ID"), R("interval", "minutes between releases")],
                        "Release the whole field's queue, auto-spaced"
                    ),
                ]
            ),
            Cmd(
                Cfr,
                "Call For Release",
                "Sim Control",
                false,
                ["CFR"],
                [
                    O(
                        null,
                        [Opt("time", "HHMM Zulu release time")],
                        "Release the selected departure with a −2/+1 min CFR window; alerts if it departs outside it (no time = immediate release from now)"
                    ),
                    O("Clear", [L("OFF")], "Clear the release window"),
                    O("Check", [L("CHECK")], "Report the release-window status without changing it"),
                ]
            ),
        ];

    private static CommandDefinition[] TrackCommands() =>
        [
            Cmd(
                SetActivePosition,
                "Act As Position",
                "Track Operations",
                true,
                ["AS"],
                [O(null, [R("position", "position ID")], "Set active radar position")]
            ),
            Cmd(
                TrackAircraft,
                "Track",
                "Track Operations",
                false,
                ["TRACK"],
                [O(null, [], "Track aircraft"), O("Position", [R("position", "position ID")], "Track with position")]
            ),
            Bare(DropTrack, "Drop Track", "Track Operations", false, ["DROP"]),
            Cmd(
                Contact,
                "Contact",
                "Track Operations",
                false,
                ["CT", "CONT"],
                [
                    O(null, [], "Contact next controller (auto-resolves to handoff target)"),
                    O("Position", [R("position", "TCP code or position callsign")], "Contact specific position"),
                ]
            ),
            Bare(FrequencyChangeApproved, "Frequency Change Approved", "Track Operations", false, ["FCA"]),
            Bare(ClearedBravoAirspace, "Cleared Bravo Airspace", "Track Operations", false, ["CLBRV", "CBRV", "BRAVO"]),
            Bare(AcknowledgePilotContact, "Acknowledge Pilot Contact", "Track Operations", false, ["STBY", "STANDBY", "ROGER", "RGR"]),
            Cmd(
                InitiateHandoff,
                "Handoff",
                "Track Operations",
                false,
                ["HO"],
                [O(null, [], "Initiate handoff"), O("Position", [R("position", "position ID")], "Initiate handoff to position")]
            ),
            Cmd(
                ForceHandoff,
                "Force Handoff",
                "Track Operations",
                false,
                ["HOF"],
                [O(null, [R("position", "position ID")], "Force handoff to position")]
            ),
            Cmd(
                AcceptHandoff,
                "Accept Handoff",
                "Track Operations",
                false,
                ["ACCEPT", "A"],
                [O(null, [], "Accept handoff"), O("Callsign", [R("callsign", "aircraft callsign")], "Accept specific callsign")]
            ),
            Bare(CancelHandoff, "Cancel Handoff", "Track Operations", false, ["CANCEL"]),
            Bare(AcceptAllHandoffs, "Accept All Handoffs", "Track Operations", true, ["ACCEPTALL"]),
            Cmd(
                InitiateHandoffAll,
                "Handoff All",
                "Track Operations",
                true,
                ["HOALL"],
                [O(null, [R("position", "position ID")], "Handoff all tracked aircraft")]
            ),
            Cmd(
                PointOut,
                "Point Out",
                "Track Operations",
                false,
                ["PO"],
                [O(null, [], "Point out"), O("Position", [R("position", "position ID")], "Point out to position")]
            ),
            Bare(Acknowledge, "Acknowledge", "Track Operations", false, ["OK"]),
            Bare(RejectPointout, "Reject Pointout", "Track Operations", false, ["PORJ"]),
            Bare(RetractPointout, "Retract Pointout", "Track Operations", false, ["PORT"]),
            Bare(AcknowledgeConflictAlert, "Acknowledge Conflict Alert", "Track Operations", false, ["CAACK"]),
            Bare(InhibitConflictAlert, "Inhibit Conflict Alert", "Track Operations", false, ["CAINH", "CAI"]),
            Cmd(
                PilotReportedAltitude,
                "Pilot Reported Altitude",
                "Data Operations",
                false,
                ["PRA"],
                [O(null, [R("altitude", "altitude in hundreds (0 = clear)")], "Set pilot reported altitude")]
            ),
            Cmd(
                LeaderDirection,
                "Leader Direction",
                "Display Operations",
                false,
                ["LDR"],
                [O(null, [R("direction", "1-9 (5 = default)")], "Set leader line direction")]
            ),
            Cmd(
                JRing,
                "J-Ring",
                "Display Operations",
                false,
                ["JRING"],
                [O(null, [], "Clear J-Ring"), O(null, [R("radius", "radius")], "Set J-Ring")]
            ),
            Cmd(Cone, "Cone", "Display Operations", false, ["CONE"], [O(null, [], "Clear cone"), O(null, [R("radius", "radius")], "Set cone")]),
            Cmd(
                GhostTrack,
                "Ghost Track",
                "Track Operations",
                true,
                ["GHOST"],
                [
                    O(
                        null,
                        [R("callsign", "aircraft callsign"), R("runway", "runway designator")],
                        "Create ghost track off runway (scenario airport)"
                    ),
                    O(
                        "Airport",
                        [R("callsign", "aircraft callsign"), R("airport", "airport ICAO"), R("runway", "runway designator")],
                        "Create ghost track off runway at airport"
                    ),
                    O(
                        "Position",
                        [R("callsign", "aircraft callsign"), R("lat", "latitude"), R("lon", "longitude")],
                        "Create ghost track at exact position"
                    ),
                ]
            ),
            Cmd(
                RepositionToLocation,
                "Reposition Datablock To Location",
                "Track Operations",
                true,
                ["RPOSLOC"],
                [
                    O(
                        null,
                        [R("callsign", "aircraft callsign"), R("lat", "latitude"), R("lon", "longitude")],
                        "Park datablock at a location (STARS TRK RPOS)"
                    ),
                ]
            ),
            Cmd(
                RepositionMove,
                "Reposition Datablock To Track",
                "Track Operations",
                true,
                ["RPOSMOVE"],
                [O(null, [R("from", "source callsign"), R("to", "target callsign")], "Move datablock onto another track (STARS TRK RPOS)")]
            ),
        ];

    private static CommandDefinition[] DataCommands() =>
        [
            Cmd(
                Annotate,
                "Annotate Strip Box",
                "Strip Operations",
                false,
                ["ANNOTATE", "AN", "BOX"],
                [O(null, [R("box", "1-9"), R("text", "annotation text")], "Write text in strip annotation box")]
            ),
            Cmd(
                StripMove,
                "Move Strip to Bay",
                "Strip Operations",
                false,
                ["STRIP"],
                [
                    O(null, [R("dest", "bay")], "Move flight strip to top of bay's first rack"),
                    O(null, [R("dest", "bay/rack")], "Move flight strip to first-available slot in rack"),
                    O(null, [R("dest", "bay/rack/index")], "Move flight strip to specific 1-based position"),
                ]
            ),
            Cmd(
                StripScan,
                "Scan Strip to External Bay",
                "Strip Operations",
                false,
                ["SCAN"],
                [
                    O(null, [R("dest", "external-bay")], "Copy flight strip to external bay's first rack (originator keeps its strip)"),
                    O(null, [R("dest", "external-bay/rack")], "Copy flight strip to first-available slot in external-bay's rack"),
                    O(null, [R("dest", "external-bay/rack/index")], "Copy flight strip to specific 1-based position in external bay"),
                ]
            ),
            Cmd(StripDelete, "Delete Flight Strip", "Strip Operations", false, ["STRIPD"], [O(null, [], "Delete the aircraft's flight strip")]),
            Cmd(
                StripOffset,
                "Toggle Strip Offset",
                "Strip Operations",
                false,
                ["STRIPO"],
                [O(null, [], "Toggle offset on the aircraft's flight strip")]
            ),
            Cmd(
                HalfStripMove,
                "Move Half-Strip",
                "Strip Operations",
                false,
                ["HSM", "HALFSTRIPMOVE"],
                [
                    O("Aircraft-scoped", [R("dest", "dest-bay[/rack[/index]]")], "Move half-strip matching callsign to destination"),
                    O(
                        "Global key",
                        [R("key", "first line of half-strip"), R("dest", "dest-bay[/rack[/index]]")],
                        "Move half-strip by key to destination"
                    ),
                    O(
                        "Explicit source",
                        [R("src", "src-bay[/rack]"), R("key", "first line"), R("dest", "dest-bay[/rack[/index]]")],
                        "Move with source bay disambiguation"
                    ),
                ]
            ),
            Cmd(
                HalfStripOffset,
                "Toggle Half-Strip Offset",
                "Strip Operations",
                false,
                ["HSO", "HALFSTRIPOFFSET"],
                [
                    O("Aircraft-scoped", [], "Toggle offset on half-strip whose first line is the callsign"),
                    O("Global", [R("key", "first line of half-strip")], "Toggle offset on half-strip by key"),
                    O("Explicit bay", [R("bay", "bay[/rack]"), R("key", "first line")], "Toggle with bay disambiguation"),
                ]
            ),
            Cmd(
                HalfStripSlide,
                "Slide Half-Strip Left/Right",
                "Strip Operations",
                false,
                ["HSS", "HALFSTRIPSLIDE"],
                [
                    O("Aircraft-scoped", [], "Slide half-strip whose first line is the callsign (toggles left/right)"),
                    O("Global", [R("key", "first line of half-strip")], "Slide half-strip by key"),
                    O("Explicit bay", [R("bay", "bay[/rack]"), R("key", "first line")], "Slide with bay disambiguation"),
                ]
            ),
            Cmd(
                SeparatorCreate,
                "Create Separator",
                "Strip Operations",
                false,
                ["SEP", "SEPARATOR"],
                [
                    O(null, [R("style", "H|W|R|G"), R("dest", "bay")], "Create separator at bay's first rack, top"),
                    O(null, [R("style", "H|W|R|G"), R("dest", "bay/rack")], "Create separator at top of rack"),
                    O(
                        null,
                        [R("style", "H|W|R|G"), R("dest", "bay/rack/index"), R("label", "optional label")],
                        "Create separator at 1-based slot with optional label"
                    ),
                ]
            ),
            Cmd(
                SeparatorDelete,
                "Delete Separator",
                "Strip Operations",
                false,
                ["SEPD", "SEPARATORDEL"],
                [
                    O("By label", [R("dest", "bay/rack"), R("label", "separator label")], "Delete separator by label (preferred)"),
                    O("By position", [R("dest", "bay/rack"), R("index", "1-based slot")], "Delete separator at position (fallback)"),
                ]
            ),
            Cmd(
                SeparatorEdit,
                "Edit Separator Label",
                "Strip Operations",
                false,
                ["SEPE"],
                [
                    O("By position", [R("dest", "bay/rack/index"), R("label", "new label text")], "Atomic separator label edit at the given slot"),
                    O(
                        "By id",
                        [R("stripId", "separator id"), R("label", "new label text")],
                        "Atomic separator label edit by strip id (powers inline edit)"
                    ),
                ]
            ),
            Cmd(
                SeparatorMove,
                "Move Separator",
                "Strip Operations",
                false,
                ["SEPM"],
                [
                    O(
                        null,
                        [R("stripId", "separator id"), R("dest", "bay/rack/index")],
                        "Relocate a separator to a new rack slot (preserves label and style)"
                    ),
                ]
            ),
            // ── vTDLS ─────────────────────────────────────────────
            Cmd(
                TdlsQueue,
                "Queue PDC for Aircraft",
                "vTDLS",
                false,
                ["TDLSQ"],
                [O(null, [], "Queue a Pending PDC for the aircraft's filed departure facility (auto-gen also emits this internally)")]
            ),
            Cmd(
                TdlsSend,
                "Send PDC",
                "vTDLS",
                false,
                ["TDLSS"],
                [
                    O(
                        null,
                        [R("fields", "Expect|Sid|Transition|Climbout|Climbvia|InitialAlt|ContactInfo|DepFreq|LocalInfo")],
                        "Send the queued PDC with nine '|'-separated fields (empty between separators = null)"
                    ),
                ]
            ),
            Cmd(
                TdlsWilco,
                "Force PDC Wilco",
                "vTDLS",
                false,
                ["TDLSW"],
                [O(null, [], "Manually mark the Sent PDC as WILCO'd (normally auto-fired)")]
            ),
            Cmd(
                TdlsDump,
                "Dump PDC",
                "vTDLS",
                false,
                ["TDLSDUMP", "TDLSD"],
                [O(null, [], "Remove the PDC from TDLS — clearance must now be given by voice. Terminal: cannot be re-added this session.")]
            ),
            Cmd(
                BlankCreate,
                "Create Blank Strip",
                "Strip Operations",
                false,
                ["BLANK", "BLANKSTRIP"],
                [
                    O(null, [], "Create blank in the printer queue"),
                    O(null, [R("dest", "bay")], "Create blank at bay's first rack, top"),
                    O(null, [R("dest", "bay/rack")], "Create blank at top of rack"),
                    O(null, [R("dest", "bay/rack/index")], "Create blank at 1-based slot"),
                ]
            ),
            Cmd(
                BlankDelete,
                "Delete Blank Strip",
                "Strip Operations",
                false,
                ["BLANKD", "BLANKSTRIPDEL"],
                [
                    O(null, [R("dest", "bay")], "Delete any one blank from the bay (blanks are fungible)"),
                    O(null, [R("dest", "bay/rack")], "Delete any one blank from the specific rack"),
                ]
            ),
            Cmd(
                HalfStripCreate,
                "Create Half-Strip",
                "Strip Operations",
                false,
                ["HSC", "HALFSTRIPCREATE"],
                [
                    O(
                        null,
                        [R("bay", "bay[/rack]"), R("lines", @"line1\line2\... (up to 6)")],
                        "Create half-strip in bay (callsign becomes line 1 if aircraft selected)"
                    ),
                ]
            ),
            Cmd(
                HalfStripAmend,
                "Amend Half-Strip",
                "Strip Operations",
                false,
                ["HSA", "HALFSTRIPAMEND"],
                [
                    O("Auto-search", [R("body", @"key\new1\new2\...")], "Amend half-strip by first-line key (auto-search across bays)"),
                    O("Explicit bay", [R("bay", "bay[/rack]"), R("body", @"key\new1\new2\...")], "Amend with explicit bay disambiguation"),
                    O("Aircraft-scoped", [], "With aircraft selected: lookup by callsign, replace lines 2-6"),
                ]
            ),
            Cmd(
                HalfStripDelete,
                "Delete Half-Strip",
                "Strip Operations",
                false,
                ["HSD", "HALFSTRIPDEL"],
                [
                    O("Auto-search", [R("key", "first line of half-strip")], "Delete half-strip by first-line key (auto-search across bays)"),
                    O("Explicit bay", [R("bay", "bay[/rack]"), R("key", "first line of half-strip")], "Delete with explicit bay disambiguation"),
                    O("Aircraft-scoped", [], "With aircraft selected: delete half-strip whose first line is the callsign"),
                ]
            ),
            Cmd(
                HalfStripEdit,
                "Edit Half-Strip Fields",
                "Strip Operations",
                false,
                ["HSE", "HALFSTRIPEDIT"],
                [
                    O(
                        null,
                        [R("stripId", "half-strip id"), R("fields", @"line0\line1\... (up to 6, empty cells preserved)")],
                        "Replace a half-strip's full FieldValues array by stripId (drives the inline 3×2 cell grid)"
                    ),
                ]
            ),
            Cmd(
                Scratchpad1,
                "Scratchpad 1",
                "Data Operations",
                false,
                ["SP1", "SP", "SCRATCHPAD"],
                [O(null, [], "Clear scratchpad 1"), O(null, [R("text", "up to 3 chars (4 if facility allows)")], "Set scratchpad 1")]
            ),
            Cmd(
                Scratchpad2,
                "Scratchpad 2",
                "Data Operations",
                false,
                ["SP2"],
                [O(null, [], "Clear scratchpad 2"), O(null, [R("text", "up to 3 chars (4 if facility allows)")], "Set scratchpad 2")]
            ),
            Cmd(
                Note,
                "Note",
                "Data Operations",
                false,
                ["NOTE"],
                [O(null, [], "Clear note"), O(null, [R("text", "freetext note, max 40 chars")], "Set instructor note")]
            ),
            Cmd(
                TemporaryAltitude,
                "Temporary Altitude",
                "Data Operations",
                false,
                ["TEMPALT", "TA", "TEMP", "QQ"],
                [O(null, [R("altitude", "altitude in hundreds")], "Set temporary altitude")]
            ),
            Cmd(
                Cruise,
                "Cruise Altitude",
                "Data Operations",
                false,
                ["CRUISE", "QZ"],
                [O(null, [R("altitude", "altitude in hundreds")], "Set cruise altitude")]
            ),
            Bare(OnHandoff, "On Handoff", "Track Operations", false, ["ONHO", "ONH"]),
            Bare(OnHoldShort, "On Hold-Short", "Track Operations", false, ["ONHS"]),
            Cmd(
                AsdexScratchpad1,
                "ASDE-X Scratchpad 1",
                "ASDE-X",
                false,
                ["ASDXSP1"],
                [O(null, [], "Clear ASDE-X scratchpad 1"), O(null, [R("text", "display text")], "Set ASDE-X scratchpad 1")]
            ),
            Cmd(
                AsdexScratchpad2,
                "ASDE-X Scratchpad 2",
                "ASDE-X",
                false,
                ["ASDXSP2"],
                [O(null, [], "Clear ASDE-X scratchpad 2"), O(null, [R("text", "display text")], "Set ASDE-X scratchpad 2")]
            ),
            Cmd(
                AsdexCallsign,
                "ASDE-X Callsign Override",
                "ASDE-X",
                false,
                ["ASDXCS"],
                [O(null, [], "Clear ASDE-X callsign override"), O(null, [R("text", "display callsign")], "Override ASDE-X callsign")]
            ),
            Cmd(
                AsdexBeaconCode,
                "ASDE-X Beacon Code Override",
                "ASDE-X",
                false,
                ["ASDXBCN"],
                [O(null, [], "Clear ASDE-X beacon override"), O(null, [R("code", "beacon code")], "Override ASDE-X beacon code")]
            ),
            Cmd(
                AsdexCategory,
                "ASDE-X Category Override",
                "ASDE-X",
                false,
                ["ASDXCAT"],
                [O(null, [], "Clear ASDE-X category override"), O(null, [R("cat", "wake category")], "Override ASDE-X category")]
            ),
            Cmd(
                AsdexAircraftType,
                "ASDE-X Aircraft Type Override",
                "ASDE-X",
                false,
                ["ASDXTYPE"],
                [O(null, [], "Clear ASDE-X type override"), O(null, [R("type", "aircraft type")], "Override ASDE-X aircraft type")]
            ),
            Cmd(
                AsdexFix,
                "ASDE-X Fix Override",
                "ASDE-X",
                false,
                ["ASDXFIX"],
                [O(null, [], "Clear ASDE-X fix override"), O(null, [R("fix", "fix identifier")], "Override ASDE-X fix")]
            ),
            Bare(AsdexTagTarget, "ASDE-X Tag Target (untermination)", "ASDE-X", false, ["ASDXTAG"]),
            Bare(AsdexTerminate, "ASDE-X Terminate Track", "ASDE-X", false, ["ASDXTERM"]),
            Bare(AsdexSuspend, "ASDE-X Suspend Track", "ASDE-X", false, ["ASDXSUSP"]),
            Bare(AsdexUnsuspend, "ASDE-X Unsuspend Track", "ASDE-X", false, ["ASDXUSUS"]),
            Bare(AsdexInhibitAlerts, "ASDE-X Inhibit Alerts", "ASDE-X", false, ["ASDXINHIB"]),
            Bare(AsdexEnableAllAlerts, "ASDE-X Enable All Alerts", "ASDE-X", true, ["ASDXALERTS"]),
        ];

    private static CommandDefinition[] CoordinationCommands() =>
        [
            Cmd(
                CoordinationRelease,
                "Release (Rundown)",
                "Coordination",
                false,
                ["RD"],
                [O(null, [], "Release on default channel"), O("Channel", [R("channel", "coordination channel")], "Release on specific channel")]
            ),
            Cmd(
                CoordinationHold,
                "Hold Release",
                "Coordination",
                false,
                ["RDH"],
                [
                    O(null, [], "Hold release on default channel"),
                    O("Channel", [R("channel", "coordination channel")], "Hold release on specific channel"),
                ]
            ),
            Cmd(
                CoordinationRecall,
                "Recall Release",
                "Coordination",
                false,
                ["RDR"],
                [
                    O(null, [], "Recall release on default channel"),
                    O("Channel", [R("channel", "coordination channel")], "Recall release on specific channel"),
                ]
            ),
            Cmd(
                CoordinationAcknowledge,
                "Acknowledge Release",
                "Coordination",
                false,
                ["RDACK"],
                [
                    O(null, [], "Acknowledge release on default channel"),
                    O("Channel", [R("channel", "coordination channel")], "Acknowledge release on specific channel"),
                ]
            ),
            Cmd(
                CoordinationAutoAck,
                "Toggle Auto-Ack",
                "Coordination",
                true,
                ["RDAUTO"],
                [O(null, [R("channel", "coordination channel")], "Toggle auto-acknowledge for channel")]
            ),
        ];

    private static CommandDefinition[] BroadcastCommands() =>
        [
            Cmd(Say, "Say", "Broadcast", false, ["SAY", "SAYF"], [O(null, [R("message", "free text")], "Broadcast pilot message")]),
            Bare(SaySpeed, "Say Speed", "Broadcast", false, ["SSPD"]),
            Bare(SayMach, "Say Mach", "Broadcast", false, ["SMACH"]),
            Bare(SayExpectedApproach, "Say Expected Approach", "Broadcast", false, ["SEAPP"]),
            Bare(SayAltitude, "Say Altitude", "Broadcast", false, ["SALT"]),
            Bare(SayHeading, "Say Heading", "Broadcast", false, ["SHDG"]),
            Bare(SayPosition, "Say Position", "Broadcast", false, ["SPOS"]),
            Cmd(
                Report,
                "Report",
                "Approach",
                false,
                ["REPORT"],
                [
                    O("Pattern leg", [R("leg", "base/final/crosswind/downwind")], "Report turning the leg (each circuit)"),
                    O("N-mile final", [R("miles", "distance NM"), L("FINAL")], "Report n-mile final"),
                    O("At fix", [R("fix", "fix name")], "Report passing the fix"),
                    O("Cancel", [R("off", "OFF [leg]")], "Stop a standing report (OFF [leg]; bare OFF cancels all)"),
                ]
            ),
        ];

    private static CommandDefinition[] ApproachCommands() =>
        [
            Cmd(
                ExpectApproach,
                "Expect Approach",
                "Approach",
                false,
                ["EAPP", "EXPECT"],
                [O(null, [R("approach", "approach ID")], "Advise expected approach")]
            ),
            Cmd(
                ClearedApproach,
                "Cleared Approach",
                "Approach",
                false,
                ["CAPP", "CTL"],
                [O(null, [], "Auto-resolve approach"), O("Approach", [R("approach", "approach ID")], "Clear for approach")]
            ),
            Cmd(JoinApproach, "Join Approach", "Approach", false, ["JAPP"], [O(null, [R("approach", "approach ID")], "Join approach course")]),
            Cmd(
                ClearedApproachStraightIn,
                "Cleared Straight-In",
                "Approach",
                false,
                ["CAPPSI"],
                [O(null, [R("approach", "approach ID")], "Clear for straight-in approach")]
            ),
            Cmd(
                JoinApproachStraightIn,
                "Join Straight-In",
                "Approach",
                false,
                ["JAPPSI"],
                [O(null, [R("approach", "approach ID")], "Join straight-in approach")]
            ),
            Cmd(
                ClearedApproachForce,
                "Cleared Approach (Force)",
                "Approach",
                false,
                ["CAPPF"],
                [O(null, [R("approach", "approach ID")], "Clear for approach (force)")]
            ),
            Cmd(
                JoinApproachForce,
                "Join Approach (Force)",
                "Approach",
                false,
                ["JAPPF"],
                [O(null, [R("approach", "approach ID")], "Join approach (force)")]
            ),
            Cmd(
                JoinFinalApproachCourse,
                "Join Final Approach Course",
                "Approach",
                false,
                ["JFAC", "JLOC", "JF"],
                [O(null, [], "Auto-resolve approach"), O("Approach", [R("approach", "approach ID")], "Join final approach course")]
            ),
            Cmd(
                JoinStar,
                "Join STAR",
                "Approach",
                false,
                ["JARR", "ARR", "STAR", "JSTAR"],
                [O(null, [R("STAR", "STAR name"), R("entry_fix", "entry fix")], "Join STAR at entry fix")]
            ),
            Cmd(JoinAirway, "Join Airway", "Approach", false, ["JAWY"], [O(null, [R("airway", "airway ID")], "Intercept and join airway")]),
            Cmd(
                JoinRadialOutbound,
                "Join Radial Outbound",
                "Approach",
                false,
                ["JRADO", "JRAD"],
                [O(null, [R("radial", "FIX + bearing")], "Join radial outbound")]
            ),
            Cmd(
                JoinRadialInbound,
                "Join Radial Inbound",
                "Approach",
                false,
                ["JRADI", "JICRS"],
                [O(null, [R("radial", "FIX + bearing")], "Join radial inbound")]
            ),
            Cmd(
                HoldingPattern,
                "Holding Pattern",
                "Approach",
                false,
                ["HOLDP"],
                [
                    O(
                        null,
                        [R("fix", "hold fix"), R("inbound", "inbound heading"), R("leg", "leg length min"), R("turn", "L/R turn direction")],
                        "Enter holding pattern"
                    ),
                ]
            ),
            Cmd(
                PositionTurnAltitudeClearance,
                "PTAC",
                "Approach",
                false,
                ["PTAC"],
                [
                    O(null, [], "Present heading, present altitude, auto approach"),
                    O(null, [R("heading", "0-360 or PH"), R("altitude", "hundreds or PA")], "Heading + altitude, auto approach"),
                    O(
                        null,
                        [R("heading", "0-360 or PH"), R("altitude", "hundreds or PA"), R("approach", "approach ID")],
                        "Position turn altitude clearance"
                    ),
                ]
            ),
            Cmd(
                PositionTurnAltitudeClearanceForce,
                "PTAC (Force)",
                "Approach",
                false,
                ["PTACF"],
                [
                    O(null, [], "Forced: present heading, present altitude, auto approach"),
                    O(null, [R("heading", "0-360 or PH"), R("altitude", "hundreds or PA")], "Forced heading + altitude, auto approach"),
                    O(
                        null,
                        [R("heading", "0-360 or PH"), R("altitude", "hundreds or PA"), R("approach", "approach ID")],
                        "Forced position turn altitude clearance (bypasses 30\u00b0 intercept gate)"
                    ),
                ]
            ),
            Cmd(
                ClimbVia,
                "Climb Via",
                "Approach",
                false,
                ["CVIA"],
                [
                    O(null, [], "Resume SID altitude constraints"),
                    O("Altitude", [R("altitude", "altitude in hundreds")], "Climb via except maintain altitude"),
                ]
            ),
            Cmd(
                DescendVia,
                "Descend Via",
                "Approach",
                false,
                ["DVIA"],
                [
                    O(null, [], "Resume STAR altitude constraints"),
                    O("Altitude", [R("altitude", "altitude in hundreds")], "Descend via except maintain altitude"),
                ]
            ),
            Cmd(
                CrossFix,
                "Cross Fix",
                "Approach",
                false,
                ["CFIX", "CF"],
                [O(null, [R("fix", "fix name"), R("constraint", "A/B + altitude")], "Cross fix at altitude constraint")]
            ),
            Cmd(
                DepartFix,
                "Depart Fix",
                "Approach",
                false,
                ["DEPART", "DEP", "D"],
                [O(null, [R("fix", "fix name"), R("heading", "0-360")], "Depart fix on heading")]
            ),
            Cmd(
                ListApproaches,
                "List Approaches",
                "Approach",
                true,
                ["APPS"],
                [O(null, [], "List approaches for primary airport"), O("Airport", [R("airport", "airport ID")], "List approaches for airport")]
            ),
            Cmd(
                ClearedVisualApproach,
                "Cleared Visual Approach",
                "Approach",
                false,
                ["CVA", "VISUAL"],
                [O(null, [R("runway", "runway designator")], "Clear for visual approach to runway")]
            ),
            Cmd(
                ClearedVisualApproachForce,
                "Cleared Visual Approach (Force)",
                "Approach",
                false,
                ["CVAF", "VISUALF"],
                [O(null, [R("runway", "runway designator")], "Clear for visual approach without RFIS/RTIS first (RPO-only)")]
            ),
            Cmd(
                Follow,
                "Follow Traffic",
                "Approach",
                false,
                ["FOLLOW", "FOL"],
                [O(null, [], "Follow last-reported traffic in sight"), O("Target", [R("callsign", "traffic callsign")], "Follow specific traffic")]
            ),
            Cmd(
                FollowForce,
                "Follow Traffic (Force)",
                "Approach",
                false,
                ["FOLLOWF", "FOLF"],
                [
                    O(null, [], "Follow last-reported traffic without RTIS first (RPO-only)"),
                    O("Target", [R("callsign", "traffic callsign")], "Follow specific traffic without RTIS first (RPO-only)"),
                ]
            ),
            Cmd(
                ReportFieldInSight,
                "Report Field In Sight",
                "Approach",
                true,
                ["RFIS"],
                [
                    O(null, [], "RPO shorthand: report field in sight"),
                    O("Descriptive", [R("clock", "1-12"), R("miles", "NM")], "Issue field advisory"),
                ]
            ),
            Bare(ReportFieldInSightForced, "Report Field In Sight (Forced)", "Approach", true, ["RFISF"]),
            Cmd(
                ReportTrafficInSight,
                "Report Traffic In Sight",
                "Approach",
                true,
                ["RTIS"],
                [
                    O(null, [], "RPO shorthand: report traffic in sight"),
                    O("Target", [R("callsign", "traffic callsign")], "RPO shorthand: report specific traffic in sight"),
                    O(
                        "Descriptive",
                        [
                            R("clock", "1-12"),
                            R("miles", "NM"),
                            R("direction", "N/NE/E/SE/S/SW/W/NW"),
                            R("type", "aircraft type"),
                            Opt("altitude", "altitude (optional)"),
                        ],
                        "Issue traffic advisory"
                    ),
                    O(
                        "Relative",
                        [R("position", "NOSE/NL/NR/L/R/LR/RR/TAIL"), R("miles", "NM"), R("type", "aircraft type")],
                        "VFR relative-position advisory"
                    ),
                    O(
                        "Pattern",
                        [
                            R("leg", "UW/XW/DW/BASE/FINAL"),
                            R("side", "L/R (omit for FINAL)"),
                            R("miles", "NM"),
                            R("runway", "runway"),
                            R("type", "aircraft type"),
                        ],
                        "VFR pattern-leg advisory"
                    ),
                    O("Landmark", [L("OVER"), R("landmark", "fix / VFR point"), R("type", "aircraft type")], "VFR landmark advisory"),
                ]
            ),
            Cmd(
                ReportTrafficInSightForced,
                "Report Traffic In Sight (Forced)",
                "Approach",
                true,
                ["RTISF"],
                [O(null, [], "Force traffic in sight"), O("Target", [R("callsign", "traffic callsign")], "Force specific traffic in sight")]
            ),
            Cmd(
                SafetyAlert,
                "Safety Alert",
                "Approach",
                true,
                ["SAFAL"],
                [
                    O(null, [R("clock", "1-12"), R("miles", "NM")], "Issue traffic safety alert"),
                    O(
                        null,
                        [R("clock", "1-12"), R("miles", "NM"), Opt("turn", "L/R"), Opt("vertical", "C/D")],
                        "Issue traffic safety alert with action"
                    ),
                ]
            ),
            Bare(WakeAdvisory, "Caution Wake Turbulence", "Approach", true, ["CWT"]),
        ];

    private static CommandDefinition[] QueueCommands() =>
        [
            Cmd(
                DeleteQueuedCommands,
                "Delete Queued Commands",
                "Queue",
                false,
                ["DELAT", "DELCOND", "DC", "CXL", "CLR"],
                [O(null, [], "Delete all queued conditionals"), O("Index", [R("index", "1-based index")], "Delete specific conditional")]
            ),
            Bare(ShowQueuedCommands, "Show Queued Commands", "Queue", false, ["SHOWAT", "SHOWCOND"]),
        ];

    private static CommandDefinition[] FlightPlanCommands() =>
        [
            Cmd(
                ChangeDestination,
                "Change Destination",
                "Flight Plan",
                false,
                ["APT", "DEST"],
                [O(null, [R("airport", "airport ID")], "Change destination airport")]
            ),
            Cmd(
                CreateFlightPlan,
                "Create Flight Plan (IFR)",
                "Flight Plan",
                false,
                ["FP"],
                [
                    O(
                        null,
                        [R("type", "aircraft type"), R("altitude", "cruise altitude"), R("route", "departure route destination")],
                        "Create IFR flight plan"
                    ),
                ]
            ),
            Cmd(
                CreateVfrFlightPlan,
                "Create Flight Plan (VFR)",
                "Flight Plan",
                false,
                ["VP"],
                [
                    O(
                        null,
                        [R("type", "aircraft type"), R("altitude", "cruise altitude"), R("route", "departure route destination")],
                        "Create VFR flight plan"
                    ),
                ]
            ),
            Cmd(
                CreateAbbreviatedFlightPlan,
                "Flight Data (Abbreviated FP)",
                "Flight Plan",
                false,
                ["DA"],
                [O(null, [R("fields", "beacon scratchpad type altitude rules")], "Create abbreviated flight plan (optional fields, any order)")]
            ),
            Cmd(
                SetRemarks,
                "Set Remarks",
                "Flight Plan",
                false,
                ["REMARKS", "REM"],
                [O(null, [R("text", "remarks text")], "Set flight plan remarks")]
            ),
            Bare(CancelIfr, "Cancel IFR", "Flight Plan", false, ["CIFR"]),
        ];

    private static CommandDefinition[] ConsolidationCommands() =>
        [
            Cmd(Consolidate, "Consolidate", "Consolidation", true, ["CON"], [O(null, [R("positions", "position IDs")], "Consolidate positions")]),
            Cmd(
                ConsolidateFull,
                "Consolidate (Full)",
                "Consolidation",
                true,
                ["CON+"],
                [O(null, [R("positions", "position IDs")], "Full consolidate positions")]
            ),
            Cmd(
                Deconsolidate,
                "Deconsolidate",
                "Consolidation",
                true,
                ["DECON"],
                [O(null, [R("position", "position ID")], "Deconsolidate position")]
            ),
        ];

    // --- Helpers ---

    private static CommandParameter R(string name, string typeHint)
    {
        return new CommandParameter(name, typeHint, false);
    }

    private static CommandParameter L(string name)
    {
        return new CommandParameter(name, "literal", false, IsLiteral: true);
    }

    private static CommandParameter Opt(string name, string typeHint)
    {
        return new CommandParameter(name, typeHint, true);
    }

    private static CommandOverload O(string? variantLabel, CommandParameter[] parameters, string? usageHint)
    {
        return new CommandOverload(variantLabel, parameters, usageHint);
    }

    private static CompoundModifier Mod(string keyword, string? argHint, bool repeatable)
    {
        return new CompoundModifier(keyword, argHint, repeatable);
    }

    private static CommandDefinition Bare(CanonicalCommandType type, string label, string category, bool isGlobal, string[] aliases)
    {
        return new CommandDefinition(type, label, category, isGlobal, aliases, [O(null, [], null)])
        {
            ProducesPilotUnable = DefaultProducesPilotUnable(category, isGlobal),
        };
    }

    private static CommandDefinition Cmd(
        CanonicalCommandType type,
        string label,
        string category,
        bool isGlobal,
        string[] aliases,
        CommandOverload[] overloads,
        CompoundModifier[]? modifiers = null,
        string[]? syntaxPatterns = null
    )
    {
        return new CommandDefinition(type, label, category, isGlobal, aliases, overloads, modifiers, syntaxPatterns)
        {
            ProducesPilotUnable = DefaultProducesPilotUnable(category, isGlobal),
        };
    }

    private static CommandDefinition PatternEntry(CanonicalCommandType type, string label, string[] aliases)
    {
        return new CommandDefinition(
            type,
            label,
            "Pattern",
            false,
            aliases,
            [O(null, [], $"{label} for current runway"), O("Runway", [R("runway", "runway designator")], $"{label} for runway")]
        )
        {
            ProducesPilotUnable = true,
        };
    }

    private static bool DefaultProducesPilotUnable(string category, bool isGlobal)
    {
        if (isGlobal)
        {
            return false;
        }

        return category
            is "Heading"
                or "Altitude / Speed"
                or "Navigation"
                or "Tower"
                or "Pattern"
                or "Hold"
                or "Helicopter"
                or "Ground"
                or "Approach";
    }
}
