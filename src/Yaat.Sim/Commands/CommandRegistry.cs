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

    public static IReadOnlyList<CommandDefinition> ByCategory(string category)
    {
        return All.Values.Where(d => d.Category == category).ToArray();
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
                    O(null, [], "Expedite current climb/descent"),
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
            Cmd(ForceSpeed, "Force Speed", "Sim Control", false, ["SPDN", "SLN"], [O(null, [R("speed", "knots IAS")], "Instantly set speed")]),
            Cmd(
                Warp,
                "Warp to Position",
                "Sim Control",
                false,
                ["WARP"],
                [
                    O(
                        null,
                        [R("FRD", "fix/radial/distance"), R("heading", "0-360"), R("altitude", "in hundreds"), R("speed", "knots IAS")],
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
                [O(null, [R("location", "C B / #42 / @B12")], "Teleport aircraft on ground")]
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
        ];

    private static CommandDefinition[] TowerCommands() =>
        [
            Bare(LineUpAndWait, "Line Up and Wait", "Tower", false, ["LUAW", "POS", "LU", "PH"]),
            Cmd(
                ClearedForTakeoff,
                "Cleared for Takeoff",
                "Tower",
                false,
                ["CTO"],
                [
                    O(null, [], "Cleared for takeoff (fly runway heading)"),
                    O("RH", [L("RH")], "Fly runway heading on departure"),
                    O("OC", [L("OC")], "Fly on course after departure"),
                    O("Heading", [R("heading", "0-360")], "Fly heading on departure"),
                    O("LT", [L("LT"), R("heading", "0-360")], "Turn left to heading on departure"),
                    O("RT", [L("RT"), R("heading", "0-360")], "Turn right to heading on departure"),
                    O("MLT", [L("MLT")], "Make left traffic on departure"),
                    O("MRT", [L("MRT")], "Make right traffic on departure"),
                    O("DCT", [L("DCT"), R("fix", "fix name")], "Proceed direct to fix on departure"),
                    O("MRC", [L("MRC")], "Turn right crosswind on departure"),
                    O("MRD", [L("MRD")], "Turn right downwind on departure"),
                    O("MLC", [L("MLC")], "Turn left crosswind on departure"),
                    O("MLD", [L("MLD")], "Turn left downwind on departure"),
                    O("MR270", [L("MR270")], "Right 270 on departure"),
                    O("ML270", [L("ML270")], "Left 270 on departure"),
                    O("360", [L("360")], "360 overhead on departure"),
                ]
            ),
            Bare(CancelTakeoffClearance, "Cancel Takeoff Clearance", "Tower", false, ["CTOC"]),
            Cmd(
                GoAround,
                "Go Around",
                "Tower",
                false,
                ["GA"],
                [O(null, [], "Go around (fly runway heading)"), O("Heading", [R("heading", "0-360")], "Go around and fly heading")]
            ),
            Cmd(
                ClearedToLand,
                "Cleared to Land",
                "Tower",
                false,
                ["CLAND", "CL", "FS"],
                [O(null, [], "Cleared to land")],
                [Mod("NODEL", null, false)]
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
            Cmd(
                TouchAndGo,
                "Touch and Go",
                "Tower",
                false,
                ["TG"],
                [O(null, [], "Touch and go"), O("Runway", [R("runway", "runway designator")], "Touch and go runway")]
            ),
            Bare(StopAndGo, "Stop and Go", "Tower", false, ["SG"]),
            Bare(LowApproach, "Low Approach", "Tower", false, ["LA"]),
            Bare(ClearedForOption, "Cleared for the Option", "Tower", false, ["COPT"]),
            Cmd(
                Sequence,
                "Landing Sequence",
                "Tower",
                false,
                ["SEQ"],
                [O(null, [R("number", "sequence position"), R("callsign", "traffic to follow")], "Set landing sequence position")]
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
                [O(null, [], "Make left traffic for current runway"), O("Runway", [R("runway", "runway designator")], "Make left traffic for runway")]
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
                ]
            ),
            Bare(TurnCrosswind, "Turn Crosswind", "Pattern", false, ["TC"]),
            Bare(TurnDownwind, "Turn Downwind", "Pattern", false, ["TD"]),
            Bare(TurnBase, "Turn Base", "Pattern", false, ["TB"]),
            Bare(ExtendDownwind, "Extend Downwind", "Pattern", false, ["EXT"]),
            Bare(MakeShortApproach, "Make Short Approach", "Pattern", false, ["SA", "MSA"]),
            Bare(MakeNormalApproach, "Make Normal Approach", "Pattern", false, ["MNA"]),
            Bare(Cancel270, "Cancel 270", "Pattern", false, ["NO270"]),
            Bare(MakeLeft360, "Make Left 360", "Pattern", false, ["L360"]),
            Bare(MakeRight360, "Make Right 360", "Pattern", false, ["R360"]),
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
            Bare(ClearedTakeoffPresent, "Cleared Takeoff Present Position", "Helicopter", false, ["CTOPP"]),
        ];

    private static CommandDefinition[] GroundCommands() =>
        [
            Cmd(
                Pushback,
                "Pushback",
                "Ground",
                false,
                ["PUSH"],
                [O(null, [], "Pushback (auto heading)"), O("Heading", [R("heading", "0-360")], "Pushback facing heading")],
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
            Bare(Resume, "Resume Taxi", "Ground", false, ["RES", "RESUME"]),
            Cmd(CrossRunway, "Cross Runway", "Ground", false, ["CROSS"], [O(null, [R("runway", "runway designator")], "Cross runway")]),
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
            Cmd(Follow, "Follow", "Ground", false, ["FOLLOW", "FOL"], [O(null, [R("callsign", "traffic callsign")], "Follow traffic")]),
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
                [Mod("NODEL", null, false)]
            ),
            Cmd(
                ExitRight,
                "Exit Right",
                "Ground",
                false,
                ["ER", "EXITR"],
                [O(null, [], "Exit runway to the right"), O("Taxiway", [R("taxiway", "taxiway name")], "Exit runway right onto taxiway")],
                [Mod("NODEL", null, false)]
            ),
            Cmd(
                ExitTaxiway,
                "Exit Taxiway",
                "Ground",
                false,
                ["EXIT"],
                [O(null, [R("taxiway", "taxiway name")], "Exit onto taxiway")],
                [Mod("NODEL", null, false)]
            ),
            Cmd(
                TaxiAll,
                "Taxi All",
                "Ground",
                true,
                ["TAXIALL"],
                [O(null, [R("destination", "runway, @parking, or $spot")], "Taxi all parked aircraft to destination (A* pathfinding)")]
            ),
            Bare(BreakConflict, "Break Conflict", "Ground", false, ["BREAK"]),
            Bare(Go, "Begin Takeoff Roll", "Tower", false, ["GO"]),
        ];

    private static CommandDefinition[] SimControlCommands() =>
        [
            Bare(Delete, "Delete", "Sim Control", false, ["DEL", "X"]),
            Bare(Pause, "Pause", "Sim Control", true, ["PAUSE", "P"]),
            Bare(Unpause, "Unpause", "Sim Control", true, ["UNPAUSE", "U", "UN", "UNP", "UP"]),
            Cmd(SimRate, "Sim Rate", "Sim Control", true, ["SIMRATE"], [O(null, [R("rate", "1-8")], "Set simulation speed")]),
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
                            R("weight", "H/J/L/S"),
                            R("engine", "J/T/P"),
                            R("-bearing", "from airport"),
                            R("distance", "nm"),
                            R("altitude", "in hundreds"),
                        ],
                        "Add airborne aircraft"
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
            Cmd(StripPush, "Push Strip to Bay", "Strip Operations", false, ["STRIP"], [O(null, [R("bay", "bay name")], "Push flight strip to bay")]),
            Cmd(
                Scratchpad1,
                "Scratchpad 1",
                "Data Operations",
                false,
                ["SP1", "SP", "SCRATCHPAD"],
                [O(null, [], "Clear scratchpad 1"), O(null, [R("text", "up to 3 chars")], "Set scratchpad 1")]
            ),
            Cmd(
                Scratchpad2,
                "Scratchpad 2",
                "Data Operations",
                false,
                ["SP2"],
                [O(null, [], "Clear scratchpad 2"), O(null, [R("text", "up to 3 chars")], "Set scratchpad 2")]
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
            Bare(ReportFieldInSight, "Report Field In Sight", "Approach", true, ["RFIS"]),
            Cmd(
                ReportTrafficInSight,
                "Report Traffic In Sight",
                "Approach",
                true,
                ["RTIS"],
                [O(null, [], "Report traffic in sight"), O("Target", [R("callsign", "traffic callsign")], "Report specific traffic in sight")]
            ),
        ];

    private static CommandDefinition[] QueueCommands() =>
        [
            Cmd(
                DeleteQueuedCommands,
                "Delete Queued Commands",
                "Queue",
                false,
                ["DELAT"],
                [O(null, [], "Delete all queued commands"), O("Index", [R("index", "1-based index")], "Delete specific queued command")]
            ),
            Bare(ShowQueuedCommands, "Show Queued Commands", "Queue", false, ["SHOWAT"]),
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
                SetRemarks,
                "Set Remarks",
                "Flight Plan",
                false,
                ["REMARKS", "REM"],
                [O(null, [R("text", "remarks text")], "Set flight plan remarks")]
            ),
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
        return new CommandDefinition(type, label, category, isGlobal, aliases, [O(null, [], null)]);
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
        return new CommandDefinition(type, label, category, isGlobal, aliases, overloads, modifiers, syntaxPatterns);
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
        );
    }
}
