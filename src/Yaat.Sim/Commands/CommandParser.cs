using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Commands;

public static class CommandParser
{
    private static readonly HashSet<string> UnsupportedVerbs = [];

    /// <summary>
    /// Parses a compound command string that may contain ';' (sequential blocks)
    /// and ',' (parallel commands within a block). Returns null if any part fails to parse.
    /// </summary>
    public static CompoundCommand? ParseCompound(string input, IFixLookup fixes, string? aircraftRoute = null)
    {
        var trimmed = CommandSchemeParser.ExpandMultiCommand(CommandSchemeParser.ExpandWait(CommandSchemeParser.ExpandSpeedUntil(input.Trim())));
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        // Check if this is a compound command (contains ; or ,)
        bool isCompound = trimmed.Contains(';') || trimmed.Contains(',');

        if (!isCompound)
        {
            // Check for standalone LV/AT conditions (makes it compound even without ; or ,)
            var upperCheck = trimmed.ToUpperInvariant();
            isCompound = upperCheck.StartsWith("LV ") || upperCheck.StartsWith("AT ") || upperCheck.StartsWith("ATFN ");

            // GIVEWAY/BEHIND/GW are compound only if they have 3+ tokens (condition form)
            if (!isCompound && (upperCheck.StartsWith("GIVEWAY ") || upperCheck.StartsWith("BEHIND ") || upperCheck.StartsWith("GW ")))
            {
                var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                isCompound = tokens.Length >= 3;
            }
        }

        if (!isCompound)
        {
            // Single command — wrap in a compound structure
            var single = Parse(trimmed, fixes, aircraftRoute);
            if (single is null)
            {
                return null;
            }

            return new CompoundCommand([new ParsedBlock(null, [single])]);
        }

        var blockStrings = trimmed.Split(';');
        var blocks = new List<ParsedBlock>();

        foreach (var blockStr in blockStrings)
        {
            var parsed = ParseBlock(blockStr.Trim(), fixes, aircraftRoute);
            if (parsed is null)
            {
                return null;
            }

            blocks.AddRange(parsed);
        }

        if (blocks.Count == 0)
        {
            return null;
        }

        return new CompoundCommand(blocks);
    }

    private static List<ParsedBlock>? ParseBlock(string blockStr, IFixLookup fixes, string? aircraftRoute)
    {
        if (string.IsNullOrWhiteSpace(blockStr))
        {
            return null;
        }

        BlockCondition? condition = null;
        var remaining = blockStr;

        // Check for LV or AT condition prefix
        var upper = remaining.ToUpperInvariant();
        if (upper.StartsWith("LV "))
        {
            var condResult = ParseLvCondition(remaining, fixes);
            if (condResult is null)
            {
                return null;
            }

            condition = condResult.Value.Condition;
            remaining = condResult.Value.Remainder;
        }
        else if (upper.StartsWith("AT "))
        {
            var condResult = ParseAtCondition(remaining, fixes);
            if (condResult is null)
            {
                return null;
            }

            condition = condResult.Value.Condition;
            remaining = condResult.Value.Remainder;
        }
        else if (upper.StartsWith("ATFN "))
        {
            var condResult = ParseAtfnCondition(remaining);
            if (condResult is null)
            {
                return null;
            }

            condition = condResult.Value.Condition;
            remaining = condResult.Value.Remainder;
        }
        else if (upper.StartsWith("GIVEWAY ") || upper.StartsWith("BEHIND ") || upper.StartsWith("GW "))
        {
            var condResult = ParseGiveWayCondition(remaining);
            if (condResult is null)
            {
                return null;
            }

            condition = condResult.Value.Condition;
            remaining = condResult.Value.Remainder;
        }

        if (string.IsNullOrWhiteSpace(remaining))
        {
            return null;
        }

        // After condition extraction, apply expansions to the remainder
        var expanded = CommandSchemeParser.ExpandMultiCommand(CommandSchemeParser.ExpandWait(CommandSchemeParser.ExpandSpeedUntil(remaining)));
        if (expanded.Contains(';'))
        {
            // Expansion produced additional blocks — first gets this block's condition,
            // subsequent become standalone blocks
            var subBlocks = expanded.Split(';');
            var results = new List<ParsedBlock>();

            for (int i = 0; i < subBlocks.Length; i++)
            {
                var sub = subBlocks[i].Trim();
                if (string.IsNullOrEmpty(sub))
                {
                    continue;
                }

                if (i == 0)
                {
                    var cmds = ParseCommandList(sub, fixes, aircraftRoute);
                    if (cmds is null)
                    {
                        return null;
                    }

                    results.Add(new ParsedBlock(condition, cmds));
                }
                else
                {
                    // Recursive call for subsequent blocks (they may have their own conditions)
                    var subParsed = ParseBlock(sub, fixes, aircraftRoute);
                    if (subParsed is null)
                    {
                        return null;
                    }

                    results.AddRange(subParsed);
                }
            }

            return results.Count > 0 ? results : null;
        }

        remaining = expanded;

        // Split remaining by ',' for parallel commands
        var commands = ParseCommandList(remaining, fixes, aircraftRoute);
        if (commands is null)
        {
            return null;
        }

        return [new ParsedBlock(condition, commands)];
    }

    private static List<ParsedCommand>? ParseCommandList(string input, IFixLookup fixes, string? aircraftRoute)
    {
        // SAY/APREQ consume entire remainder as literal text — don't split on comma
        var upperCheck = input.TrimStart().ToUpperInvariant();
        if (upperCheck.StartsWith("SAY ") || upperCheck.StartsWith("APREQ"))
        {
            var cmd = Parse(input.Trim(), fixes, aircraftRoute);
            return cmd is not null ? [cmd] : null;
        }

        var commandStrings = input.Split(',');
        var commands = new List<ParsedCommand>();

        foreach (var cmdStr in commandStrings)
        {
            var trimmedCmd = cmdStr.Trim();
            var cmd = Parse(trimmedCmd, fixes, aircraftRoute);
            if (cmd is not null)
            {
                commands.Add(cmd);
                continue;
            }

            // Try expanding concatenated commands: "FH 270 CM 5000" → "FH 270, CM 5000"
            var expanded = CommandSchemeParser.ExpandMultiCommand(trimmedCmd);
            if (expanded == trimmedCmd)
            {
                return null;
            }

            foreach (var subCmd in expanded.Split(','))
            {
                var parsed = Parse(subCmd.Trim(), fixes, aircraftRoute);
                if (parsed is null)
                {
                    return null;
                }

                commands.Add(parsed);
            }
        }

        return commands.Count > 0 ? commands : null;
    }

    private static (BlockCondition Condition, string Remainder)? ParseLvCondition(string input, IFixLookup fixes)
    {
        // "LV 050 FH 090" → condition=LevelCondition(5000), remainder="FH 090"
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        // parts[0] = "LV", parts[1] = altitude (numeric or AGL), parts[2..] = remaining
        int? altitude = AltitudeResolver.Resolve(parts[1], fixes);
        if (altitude is null)
        {
            return null;
        }

        var remainder = string.Join(' ', parts.Skip(2));
        return (new LevelCondition(altitude.Value), remainder);
    }

    private static (BlockCondition Condition, string Remainder)? ParseAtCondition(string input, IFixLookup fixes)
    {
        // "AT SUNOL FH 090" → condition=AtFixCondition(SUNOL, lat, lon), remainder="FH 090"
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        var token = parts[1].ToUpperInvariant();
        var remainder = string.Join(' ', parts.Skip(2));

        // Try direct fix lookup first
        var pos = fixes.GetFixPosition(token);
        if (pos is not null)
        {
            return (new AtFixCondition(token, pos.Value.Lat, pos.Value.Lon), remainder);
        }

        // Try FRD/FR parse to preserve radial/distance info
        var parsed = FrdResolver.ParseFrd(token);
        if (parsed is null)
        {
            return null;
        }

        var (fixName, radial, distance) = parsed.Value;
        var fixPos = fixes.GetFixPosition(fixName);
        if (fixPos is null)
        {
            return null;
        }

        return (new AtFixCondition(fixName, fixPos.Value.Lat, fixPos.Value.Lon, radial, distance), remainder);
    }

    private static (BlockCondition Condition, string Remainder)? ParseGiveWayCondition(string input)
    {
        // "GIVEWAY SWA5456 TAXI T U W" → condition=GiveWayCondition(SWA5456), remainder="TAXI T U W"
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        var targetCallsign = parts[1].ToUpperInvariant();
        var remainder = string.Join(' ', parts.Skip(2));
        return (new GiveWayCondition(targetCallsign), remainder);
    }

    /// <summary>
    /// Parses a single command string. Extended to support DCT verb.
    /// </summary>
    public static ParsedCommand? Parse(string input, IFixLookup? fixes = null, string? aircraftRoute = null)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToUpperInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

        if (UnsupportedVerbs.Contains(verb))
        {
            return new UnsupportedCommand(trimmed);
        }

        if (verb == "DCT" && fixes is not null)
        {
            return ParseDirectTo(arg, fixes, aircraftRoute);
        }

        if (verb == "DCTF" && fixes is not null)
        {
            return ParseForceDirectTo(arg, fixes, aircraftRoute);
        }

        if (verb == "ADCT" && fixes is not null)
        {
            return ParseAppendDirectTo(arg, fixes, aircraftRoute);
        }

        if (verb == "ADCTF" && fixes is not null)
        {
            return ParseAppendForceDirectTo(arg, fixes, aircraftRoute);
        }

        return verb switch
        {
            "FH" => ParseHeading(arg, h => new FlyHeadingCommand(h)),
            "TL" => ParseHeading(arg, h => new TurnLeftCommand(h)),
            "TR" => ParseHeading(arg, h => new TurnRightCommand(h)),
            "LT" => ParseDegrees(arg, d => new LeftTurnCommand(d)),
            "RT" => ParseDegrees(arg, d => new RightTurnCommand(d)),
            "FPH" when arg is null => new FlyPresentHeadingCommand(),
            "CM" => ParseAltitude(arg, fixes, a => new ClimbMaintainCommand(a)),
            "DM" => ParseAltitude(arg, fixes, a => new DescendMaintainCommand(a)),
            "SPD" => ParseSpeed(arg),
            "RNS" or "NS" when arg is null => new ResumeNormalSpeedCommand(),
            "RFAS" when arg is null => new ReduceToFinalApproachSpeedCommand(),
            "DSR" when arg is null => new DeleteSpeedRestrictionsCommand(),
            "EXP" => ParseExpedite(arg),
            "NORM" when arg is null => new NormalRateCommand(),
            "MACH" or "M" when arg is not null => ParseMach(arg),
            "FHN" => ParseHeading(arg, h => new ForceHeadingCommand(h)),
            "CMN" => ParseAltitude(arg, fixes, a => new ForceAltitudeCommand(a)),
            "SPDN" or "SLN" => ParseForceSpeed(arg),
            "WARP" => ParseWarp(arg, fixes),
            "WARPG" when arg is not null => ParseWarpGround(arg),
            "SQ" or "SQUAWK" => ParseSquawkOrReset(arg),
            "SQVFR" or "SQV" when arg is null => new SquawkVfrCommand(),
            "SQNORM" or "SN" or "SQA" or "SQON" when arg is null => new SquawkNormalCommand(),
            "SQSBY" or "SQS" when arg is null => new SquawkStandbyCommand(),
            "SQI" or "SQID" or "ID" when arg is null => new IdentCommand(),
            "IDENT" when arg is null => new IdentCommand(),
            "RANDSQ" when arg is null => new RandomSquawkCommand(),
            "SQALL" when arg is null => new SquawkAllCommand(),
            "SNALL" when arg is null => new SquawkNormalAllCommand(),
            "SSALL" when arg is null => new SquawkStandbyAllCommand(),
            "DEL" when arg is null => new DeleteCommand(),
            "PAUSE" when arg is null => new PauseCommand(),
            "UNPAUSE" when arg is null => new UnpauseCommand(),
            "SIMRATE" => ParseInt(arg, r => new SimRateCommand(r)),
            "SPAWN" when arg is null => new SpawnNowCommand(),
            "SPAWNDELAY" => ParseInt(arg, s => new SpawnDelayCommand(s)),
            "DELAY" or "WAIT" => ParseWaitSeconds(arg),
            "WAITD" => ParseWaitDistance(arg),
            // Tower commands
            "LUAW" or "POS" or "LU" or "PH" when arg is null => new LineUpAndWaitCommand(),
            "CTO" => DepartureCommandParser.ParseCtoArg(arg, fixes),
            "CTOMRT" => DepartureCommandParser.ParseCtoArg("MRT" + (arg is not null ? " " + arg : ""), fixes),
            "CTOMLT" => DepartureCommandParser.ParseCtoArg("MLT" + (arg is not null ? " " + arg : ""), fixes),
            "CTOC" when arg is null => new CancelTakeoffClearanceCommand(),
            "GA" => ParseGoAround(arg, fixes),
            "GAMRT" when arg is null => new GoAroundCommand(TrafficPattern: PatternDirection.Right),
            "GAMLT" when arg is null => new GoAroundCommand(TrafficPattern: PatternDirection.Left),
            "CTL" when arg is null or "NODEL" => new ClearedToLandCommand(arg?.Equals("NODEL", StringComparison.OrdinalIgnoreCase) == true),
            "LAHSO" when arg is not null => new LandAndHoldShortCommand(arg.Trim().ToUpperInvariant()),
            "EL" when arg is null or "NODEL" => new ExitLeftCommand(arg?.Equals("NODEL", StringComparison.OrdinalIgnoreCase) == true),
            "ER" when arg is null or "NODEL" => new ExitRightCommand(arg?.Equals("NODEL", StringComparison.OrdinalIgnoreCase) == true),
            "EXIT" when arg is not null => GroundCommandParser.ParseExitTaxiway(arg),
            // Pattern commands
            "ELD" => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterLeftDownwindCommand(rwy)),
            "ERD" => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterRightDownwindCommand(rwy)),
            "ELC" => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterLeftCrosswindCommand(rwy)),
            "ERC" => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterRightCrosswindCommand(rwy)),
            "ELB" => DepartureCommandParser.ParsePatternBaseEntry(arg),
            "ERB" => DepartureCommandParser.ParsePatternBaseEntry(arg, right: true),
            "EF" => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterFinalCommand(rwy)),
            "MLT" => new MakeLeftTrafficCommand(arg?.ToUpperInvariant()),
            "MRT" => new MakeRightTrafficCommand(arg?.ToUpperInvariant()),
            "TC" when arg is null => new TurnCrosswindCommand(),
            "TD" when arg is null => new TurnDownwindCommand(),
            "TB" when arg is null => new TurnBaseCommand(),
            "EXT" when arg is null => new ExtendDownwindCommand(),
            "SA" or "MSA" when arg is null => new MakeShortApproachCommand(),
            "MNA" when arg is null => new MakeNormalApproachCommand(),
            "NO270" when arg is null => new Cancel270Command(),
            "PS" or "PATTSIZE" when arg is not null => ParsePatternSize(arg),
            "MLS" => ParseSTurns(arg, TurnDirection.Left),
            "MRS" => ParseSTurns(arg, TurnDirection.Right),
            "P270" or "PLAN270" when arg is null => new Plan270Command(),
            "L360" when arg is null => new MakeLeft360Command(),
            "R360" when arg is null => new MakeRight360Command(),
            "L270" when arg is null => new MakeLeft270Command(),
            "R270" when arg is null => new MakeRight270Command(),
            "CA" or "CIRCLE" when arg is null => new CircleAirportCommand(),
            "SEQ" => ParseSequence(arg),
            // Option approach / special ops commands
            "TG" when arg is null => new TouchAndGoCommand(),
            "SG" when arg is null => new StopAndGoCommand(),
            "LA" when arg is null => new LowApproachCommand(),
            "COPT" when arg is null => new ClearedForOptionCommand(),
            // Hold commands
            "HPPL" when arg is null => new HoldPresentPosition360Command(TurnDirection.Left),
            "HPPR" when arg is null => new HoldPresentPosition360Command(TurnDirection.Right),
            "HPP" when arg is null => new HoldPresentPositionHoverCommand(),
            "HFIXL" => ParseHoldAtFix(arg, fixes, TurnDirection.Left),
            "HFIXR" => ParseHoldAtFix(arg, fixes, TurnDirection.Right),
            "HFIX" => ParseHoldAtFixHover(arg, fixes),
            // Helicopter commands
            "ATXI" => new AirTaxiCommand(arg?.Trim().ToUpperInvariant()),
            "LAND" when arg is not null => ParseLand(arg),
            "CTOPP" when arg is null => new ClearedTakeoffPresentCommand(),
            // Ground commands
            "PUSH" => GroundCommandParser.ParsePushback(arg),
            "TAXI" => GroundCommandParser.ParseTaxi(arg),
            "RWY" => GroundCommandParser.ParseRwyTaxi(arg),
            "HOLD" or "HP" when arg is null => new HoldPositionCommand(),
            "HOLD" when arg is not null => ApproachCommandParser.ParseHold(arg, fixes),
            "RES" or "RESUME" when arg is null => new ResumeCommand(),
            "CROSS" => GroundCommandParser.ParseCross(arg),
            "HS" => GroundCommandParser.ParseHoldShort(arg),
            "FOLLOW" or "FOL" => GroundCommandParser.ParseFollow(arg),
            "GIVEWAY" or "BEHIND" or "GW" => GroundCommandParser.ParseGiveWay(arg),
            "TAXIALL" => GroundCommandParser.ParseTaxiAll(arg),
            "BREAK" when arg is null => new BreakConflictCommand(),
            "GO" when arg is null => new GoCommand(),
            // Approach commands
            "EAPP" or "EXPECT" => ParseExpectApproach(arg),
            "CAPP" => ApproachCommandParser.ParseCapp(arg, fixes, false),
            "CAPPF" => ApproachCommandParser.ParseCapp(arg, fixes, true),
            "CAPPSI" => ApproachCommandParser.ParseCappSi(arg, fixes),
            "JAPP" => ApproachCommandParser.ParseJapp(arg, fixes, false),
            "JAPPF" => ApproachCommandParser.ParseJapp(arg, fixes, true),
            "JAPPSI" => ApproachCommandParser.ParseJappSi(arg, fixes),
            "JFAC" or "JLOC" or "JF" => ApproachCommandParser.ParseJfac(arg),
            "JARR" or "ARR" or "JSTAR" or "STAR" => ApproachCommandParser.ParseJarr(arg),
            "JAWY" => ApproachCommandParser.ParseJawy(arg),
            "JRADO" or "JRAD" => ApproachCommandParser.ParseJrado(arg, fixes),
            "JRADI" or "JICRS" => ApproachCommandParser.ParseJradi(arg, fixes),
            "HOLDP" => ApproachCommandParser.ParseHold(arg, fixes),
            "PTAC" => ApproachCommandParser.ParsePtac(arg, fixes),
            "CVIA" => ApproachCommandParser.ParseCvia(arg),
            "DVIA" => ApproachCommandParser.ParseDvia(arg, fixes),
            "CFIX" or "CF" => ApproachCommandParser.ParseCfix(arg, fixes),
            "DEPART" or "DEP" => ApproachCommandParser.ParseDepart(arg, fixes),
            "APPS" => ApproachCommandParser.ParseApps(arg),
            "CVA" or "VISUAL" => ApproachCommandParser.ParseCva(arg),
            "RFIS" when arg is null => new ReportFieldInSightCommand(),
            "RTIS" => new ReportTrafficInSightCommand(arg?.Trim().ToUpperInvariant()),
            // Track commands
            "AS" => ParseTcpArg(arg, tcp => new SetActivePositionCommand(tcp)),
            "TRACK" when arg is null => new TrackAircraftCommand(),
            "DROP" when arg is null => new DropTrackCommand(),
            "HO" => ParseTcpArg(arg, tcp => new InitiateHandoffCommand(tcp)),
            "HOF" => ParseTcpArg(arg, tcp => new ForceHandoffCommand(tcp)),
            "ACCEPT" or "A" when arg is null => new AcceptHandoffCommand(),
            "CANCEL" when arg is null => new CancelHandoffCommand(),
            "ACCEPTALL" when arg is null => new AcceptAllHandoffsCommand(),
            "HOALL" => ParseTcpArg(arg, tcp => new InitiateHandoffAllCommand(tcp)),
            "PO" => ParseTcpArg(arg, tcp => new PointOutCommand(tcp)),
            "OK" when arg is null => new AcknowledgeCommand(),
            "ANNOTATE" or "AN" or "BOX" when arg is not null => ParseStripAnnotate(arg),
            "STRIP" when arg is not null => new StripPushCommand(arg.Trim().ToUpperInvariant()),
            "SP1" or "SCRATCHPAD" when arg is not null => new Scratchpad1Command(arg.Trim().ToUpperInvariant()),
            "SP2" when arg is not null => new Scratchpad2Command(arg.Trim().ToUpperInvariant()),
            "TEMPALT" or "TA" or "TEMP" or "QQ" => ParseAltitudeHundreds(arg, h => new TemporaryAltitudeCommand(h)),
            "CRUISE" or "QZ" => ParseAltitudeHundreds(arg, h => new CruiseCommand(h)),
            "ONHO" or "ONH" when arg is null => new OnHandoffCommand(),
            "SAY" when arg is not null => new SayCommand(arg),
            "APREQ" => new SayCommand("APREQ" + (arg is not null ? " " + arg : "")),
            "SSPD" when arg is null => new SaySpeedCommand(),
            "SS" when arg is null => new SquawkStandbyCommand(),
            "DELAT" when arg is null => new DeleteQueuedCommand(),
            "DELAT" => int.TryParse(arg, out var delAtBlock) ? new DeleteQueuedCommand(delAtBlock) : null,
            "SHOWAT" when arg is null => new ShowQueuedCommand(),
            // Coordination commands
            "RD" => new CoordinationReleaseCommand(arg?.Trim().ToUpperInvariant()),
            "RDH" => ParseHoldArgs(arg),
            "RDR" => new CoordinationRecallCommand(arg?.Trim().ToUpperInvariant()),
            "RDACK" => new CoordinationAcknowledgeCommand(arg?.Trim().ToUpperInvariant()),
            "RDAUTO" => ParseOptionalListId(arg, listId => new CoordinationAutoAckCommand(listId)),
            // Consolidation
            "CON" => ParseConsolidate(arg, false),
            "CON+" => ParseConsolidate(arg, true),
            "DECON" => ParseDeconsolidate(arg),
            // Flight plan amendments
            "APT" or "DEST" => ParseChangeDestination(arg),
            "FP" => ParseCreateFlightPlan(arg, "IFR"),
            "VP" => ParseCreateFlightPlan(arg, "VFR"),
            "REMARKS" or "REM" when arg is not null => new SetRemarksCommand(arg),
            _ => null,
        };
    }

    /// <summary>
    /// Parses RDH [listId] [text].
    /// </summary>
    private static ParsedCommand ParseHoldArgs(string? arg)
    {
        if (arg is null)
        {
            return new CoordinationHoldCommand(null, null);
        }

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return new CoordinationHoldCommand(null, null);
        }

        var listId = parts[0].Trim().ToUpperInvariant();
        var text = parts.Length > 1 ? parts[1].Trim() : null;
        return new CoordinationHoldCommand(listId, text);
    }

    /// <summary>
    /// Requires a non-empty arg as list ID.
    /// </summary>
    private static ParsedCommand? ParseOptionalListId(string? arg, Func<string, ParsedCommand> factory)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        return factory(arg.Trim().ToUpperInvariant());
    }

    private static ParsedCommand? ParseTcpArg(string? arg, Func<string, ParsedCommand> factory)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        return factory(arg.Trim().ToUpperInvariant());
    }

    private static ParsedCommand? ParseAltitudeHundreds(string? arg, Func<int, ParsedCommand> factory)
    {
        if (arg is null || !int.TryParse(arg, out var value) || value <= 0)
        {
            return null;
        }

        return factory(value);
    }

    private static DirectToCommand? ParseDirectTo(string? arg, IFixLookup fixes, string? aircraftRoute)
    {
        if (arg is null)
        {
            return null;
        }

        var fixNames = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fixNames.Length == 0)
        {
            return null;
        }

        var resolved = new List<ResolvedFix>();
        foreach (var name in fixNames)
        {
            var pos = fixes.GetFixPosition(name);
            if (pos is not null)
            {
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), pos.Value.Lat, pos.Value.Lon));
                continue;
            }

            // Try FRD (Fix-Radial-Distance) resolution: e.g., "JFK090020"
            var frd = FrdResolver.Resolve(name, fixes);
            if (frd is null)
            {
                return null;
            }

            resolved.Add(new ResolvedFix(name.ToUpperInvariant(), frd.Latitude, frd.Longitude));
        }

        // If the last fix is in the aircraft's route, append remaining route fixes
        if (aircraftRoute is not null)
        {
            RouteChainer.AppendRouteRemainder(resolved, aircraftRoute, fixes);
        }

        return new DirectToCommand(resolved);
    }

    private static AppendDirectToCommand? ParseAppendDirectTo(string? arg, IFixLookup fixes, string? aircraftRoute)
    {
        if (arg is null)
        {
            return null;
        }

        var fixNames = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fixNames.Length == 0)
        {
            return null;
        }

        var resolved = new List<ResolvedFix>();
        foreach (var name in fixNames)
        {
            var pos = fixes.GetFixPosition(name);
            if (pos is not null)
            {
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), pos.Value.Lat, pos.Value.Lon));
                continue;
            }

            var frd = FrdResolver.Resolve(name, fixes);
            if (frd is null)
            {
                return null;
            }

            resolved.Add(new ResolvedFix(name.ToUpperInvariant(), frd.Latitude, frd.Longitude));
        }

        if (aircraftRoute is not null)
        {
            RouteChainer.AppendRouteRemainder(resolved, aircraftRoute, fixes);
        }

        return new AppendDirectToCommand(resolved);
    }

    private static AppendForceDirectToCommand? ParseAppendForceDirectTo(string? arg, IFixLookup fixes, string? aircraftRoute)
    {
        if (arg is null)
        {
            return null;
        }

        var fixNames = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fixNames.Length == 0)
        {
            return null;
        }

        var resolved = new List<ResolvedFix>();
        foreach (var name in fixNames)
        {
            var pos = fixes.GetFixPosition(name);
            if (pos is not null)
            {
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), pos.Value.Lat, pos.Value.Lon));
                continue;
            }

            var frd = FrdResolver.Resolve(name, fixes);
            if (frd is null)
            {
                return null;
            }

            resolved.Add(new ResolvedFix(name.ToUpperInvariant(), frd.Latitude, frd.Longitude));
        }

        if (aircraftRoute is not null)
        {
            RouteChainer.AppendRouteRemainder(resolved, aircraftRoute, fixes);
        }

        return new AppendForceDirectToCommand(resolved);
    }

    private static ForceDirectToCommand? ParseForceDirectTo(string? arg, IFixLookup fixes, string? aircraftRoute)
    {
        if (arg is null)
        {
            return null;
        }

        var fixNames = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fixNames.Length == 0)
        {
            return null;
        }

        var resolved = new List<ResolvedFix>();
        foreach (var name in fixNames)
        {
            var pos = fixes.GetFixPosition(name);
            if (pos is not null)
            {
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), pos.Value.Lat, pos.Value.Lon));
                continue;
            }

            var frd = FrdResolver.Resolve(name, fixes);
            if (frd is null)
            {
                return null;
            }

            resolved.Add(new ResolvedFix(name.ToUpperInvariant(), frd.Latitude, frd.Longitude));
        }

        if (aircraftRoute is not null)
        {
            RouteChainer.AppendRouteRemainder(resolved, aircraftRoute, fixes);
        }

        return new ForceDirectToCommand(resolved);
    }

    private static ParsedCommand? ParseExpectApproach(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var approachId = tokens[0].ToUpperInvariant();
        var airportCode = tokens.Length > 1 ? tokens[1].ToUpperInvariant() : null;
        return new ExpectApproachCommand(approachId, airportCode);
    }

    /// <summary>
    /// Parses GA (no args), GA MRT/MLT (pattern direction),
    /// or GA hdg alt (2 args). Heading can be a number (1-360)
    /// or RH (runway heading). Altitude uses AltitudeResolver.
    /// </summary>
    private static ParsedCommand? ParseGoAround(string? arg, IFixLookup? fixes)
    {
        if (arg is null)
        {
            return new GoAroundCommand();
        }

        if (arg.Equals("MRT", StringComparison.OrdinalIgnoreCase))
        {
            return new GoAroundCommand(TrafficPattern: PatternDirection.Right);
        }

        if (arg.Equals("MLT", StringComparison.OrdinalIgnoreCase))
        {
            return new GoAroundCommand(TrafficPattern: PatternDirection.Left);
        }

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        int? heading = null;
        if (!parts[0].Equals("RH", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(parts[0], out var h) || h < 1 || h > 360)
            {
                return null;
            }

            heading = h;
        }

        int? altitude = AltitudeResolver.Resolve(parts[1], fixes);
        if (altitude is null)
        {
            return null;
        }

        return new GoAroundCommand(heading, altitude);
    }

    private static ParsedCommand? ParseHoldAtFix(string? arg, IFixLookup? fixes, TurnDirection direction)
    {
        if (arg is null || fixes is null)
        {
            return null;
        }

        var fixName = arg.Trim().ToUpperInvariant();
        var pos = fixes.GetFixPosition(fixName);
        if (pos is null)
        {
            var frd = FrdResolver.Resolve(fixName, fixes);
            if (frd is null)
            {
                return null;
            }
            return new HoldAtFixOrbitCommand(fixName, frd.Latitude, frd.Longitude, direction);
        }

        return new HoldAtFixOrbitCommand(fixName, pos.Value.Lat, pos.Value.Lon, direction);
    }

    private static ParsedCommand? ParseHoldAtFixHover(string? arg, IFixLookup? fixes)
    {
        if (arg is null || fixes is null)
        {
            return null;
        }

        var fixName = arg.Trim().ToUpperInvariant();
        var pos = fixes.GetFixPosition(fixName);
        if (pos is null)
        {
            var frd = FrdResolver.Resolve(fixName, fixes);
            if (frd is null)
            {
                return null;
            }
            return new HoldAtFixHoverCommand(fixName, frd.Latitude, frd.Longitude);
        }

        return new HoldAtFixHoverCommand(fixName, pos.Value.Lat, pos.Value.Lon);
    }

    private static ParsedCommand ParseLand(string arg)
    {
        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var raw = tokens[0];
        bool noDelete = tokens.Length > 1 && tokens[1].Equals("NODEL", StringComparison.OrdinalIgnoreCase);

        // @prefix = parking/helipad spot (strip @), no prefix = taxiway name
        if (raw.StartsWith('@'))
        {
            return new LandCommand(raw[1..].ToUpperInvariant(), noDelete, IsTaxiway: false);
        }

        return new LandCommand(raw.ToUpperInvariant(), noDelete, IsTaxiway: true);
    }

    private static ParsedCommand? ParseSequence(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var number) || number < 1)
        {
            return null;
        }

        var follow = parts.Length > 1 ? parts[1].Trim().ToUpperInvariant() : null;
        return new SequenceCommand(number, follow);
    }

    private static ParsedCommand? ParseWaitSeconds(string? arg)
    {
        if (arg is null || !int.TryParse(arg, out var seconds) || seconds <= 0)
        {
            return null;
        }

        return new WaitCommand(seconds);
    }

    private static ParsedCommand? ParseWaitDistance(string? arg)
    {
        if (arg is null || !double.TryParse(arg, out var distNm) || distNm <= 0)
        {
            return null;
        }

        return new WaitDistanceCommand(distNm);
    }

    private static ParsedCommand? ParseHeading(string? arg, Func<int, ParsedCommand> factory)
    {
        if (arg is null || !int.TryParse(arg, out var heading))
        {
            return null;
        }

        if (heading < 1 || heading > 360)
        {
            return null;
        }

        return factory(heading);
    }

    private static ParsedCommand? ParseDegrees(string? arg, Func<int, ParsedCommand> factory)
    {
        if (arg is null || !int.TryParse(arg, out var degrees))
        {
            return null;
        }

        if (degrees < 1 || degrees > 359)
        {
            return null;
        }

        return factory(degrees);
    }

    internal static ParsedCommand? ParseAltitude(string? arg, IFixLookup? fixes, Func<int, ParsedCommand> factory)
    {
        int? altitude = AltitudeResolver.Resolve(arg, fixes);
        return altitude is null ? null : factory(altitude.Value);
    }

    private static ParsedCommand? ParseWarp(string? arg, IFixLookup? fixes)
    {
        // WARP <FRD|FIX> <heading> <altitude> <speed>
        if (arg is null || fixes is null)
        {
            return null;
        }

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        var posToken = parts[0].ToUpperInvariant();

        // Try direct fix lookup first
        double lat,
            lon;
        var fixPos = fixes.GetFixPosition(posToken);
        if (fixPos is not null)
        {
            lat = fixPos.Value.Lat;
            lon = fixPos.Value.Lon;
        }
        else
        {
            // Try FRD resolution
            var frd = FrdResolver.Resolve(posToken, fixes);
            if (frd is null)
            {
                return null;
            }

            lat = frd.Latitude;
            lon = frd.Longitude;
        }

        if (!int.TryParse(parts[1], out var heading) || heading < 1 || heading > 360)
        {
            return null;
        }

        int? altitude = AltitudeResolver.Resolve(parts[2], fixes);
        if (altitude is null)
        {
            return null;
        }

        if (!int.TryParse(parts[3], out var speed) || speed <= 0)
        {
            return null;
        }

        return new WarpCommand(posToken, lat, lon, heading, altitude.Value, speed);
    }

    private static ParsedCommand? ParseWarpGround(string arg)
    {
        // WARPG <nodeRef>    — e.g., WARPG #42
        // WARPG @<parking>   — e.g., WARPG @B12
        // WARPG <tw1> <tw2>  — e.g., WARPG C B
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && TaxiPathfinder.IsNodeReference(parts[0]))
        {
            return new WarpGroundCommand("", "", TaxiPathfinder.ParseNodeId(parts[0]));
        }

        if (parts.Length == 1 && parts[0].StartsWith('@') && parts[0].Length > 1)
        {
            return new WarpGroundCommand("", "", ParkingName: parts[0][1..]);
        }

        if (parts.Length != 2)
        {
            return null;
        }

        return new WarpGroundCommand(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());
    }

    private static ParsedCommand? ParseForceSpeed(string? arg)
    {
        if (arg is null || !int.TryParse(arg, out var speed))
        {
            return null;
        }

        return new ForceSpeedCommand(speed);
    }

    private static ParsedCommand? ParseSpeed(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        // Check for trailing +/- modifier
        if (arg.EndsWith('+') && int.TryParse(arg[..^1], out var floorSpeed))
        {
            return new SpeedCommand(floorSpeed, SpeedModifier.Floor);
        }

        if (arg.EndsWith('-') && int.TryParse(arg[..^1], out var ceilSpeed))
        {
            return new SpeedCommand(ceilSpeed, SpeedModifier.Ceiling);
        }

        if (!int.TryParse(arg, out var speed))
        {
            return null;
        }

        return new SpeedCommand(speed);
    }

    private static ParsedCommand ParseExpedite(string? arg)
    {
        if (arg is null)
        {
            return new ExpediteCommand();
        }

        int? altitude = AltitudeResolver.Resolve(arg, fixes: null);
        return altitude is not null ? new ExpediteCommand(altitude.Value) : new ExpediteCommand();
    }

    private static ParsedCommand? ParseMach(string arg)
    {
        // Accept ".82", "0.82", or "82" (hundredths)
        var trimmed = arg.Trim();

        if (trimmed.StartsWith('.'))
        {
            trimmed = "0" + trimmed;
        }

        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mach))
        {
            // If value >= 1, treat as hundredths (e.g., "82" → 0.82)
            if (mach >= 1.0)
            {
                mach /= 100.0;
            }

            if (mach > 0 && mach < 1.0)
            {
                return new MachCommand(mach);
            }
        }

        return null;
    }

    private static ParsedCommand ParsePatternSize(string arg)
    {
        if (
            double.TryParse(arg.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double size)
            && size > 0
        )
        {
            return new PatternSizeCommand(size);
        }

        return new UnsupportedCommand($"PS {arg}");
    }

    private static ParsedCommand ParseSTurns(string? arg, TurnDirection direction)
    {
        if (arg is null)
        {
            return direction == TurnDirection.Left ? new MakeLeftSTurnsCommand() : new MakeRightSTurnsCommand();
        }

        if (int.TryParse(arg.Trim(), out int count) && count >= 1 && count <= 10)
        {
            return direction == TurnDirection.Left ? new MakeLeftSTurnsCommand(count) : new MakeRightSTurnsCommand(count);
        }

        return new UnsupportedCommand($"{(direction == TurnDirection.Left ? "MLS" : "MRS")} {arg}");
    }

    private static (BlockCondition Condition, string Remainder)? ParseAtfnCondition(string input)
    {
        // "ATFN 10 SPD 180" → condition=DistanceFinalCondition(10), remainder="SPD 180"
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        if (!double.TryParse(parts[1], out var distNm) || distNm <= 0)
        {
            return null;
        }

        var remainder = string.Join(' ', parts.Skip(2));
        return (new DistanceFinalCondition(distNm), remainder);
    }

    private static ParsedCommand? ParseInt(string? arg, Func<int, ParsedCommand> factory)
    {
        if (arg is null || !int.TryParse(arg, out var value))
        {
            return null;
        }

        return factory(value);
    }

    private static ParsedCommand? ParseConsolidate(string? arg, bool full)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        return new ConsolidateCommand(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant(), full);
    }

    private static ParsedCommand? ParseDeconsolidate(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        return new DeconsolidateCommand(arg.Trim().ToUpperInvariant());
    }

    private static ParsedCommand? ParseChangeDestination(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        return new ChangeDestinationCommand(arg.Trim().ToUpperInvariant());
    }

    private static ParsedCommand? ParseCreateFlightPlan(string? arg, string flightRules)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        var aircraftType = parts[0].ToUpperInvariant();

        if (!int.TryParse(parts[1], out var altRaw))
        {
            return null;
        }

        // IFR altitude in hundreds (≤999 → multiply by 100), VFR is absolute
        int cruiseAltitude = flightRules == "IFR" && altRaw <= 999 ? altRaw * 100 : altRaw;

        var route = string.Join(" ", parts.Skip(2).Select(p => p.ToUpperInvariant()));
        return new CreateFlightPlanCommand(flightRules, aircraftType, cruiseAltitude, route);
    }

    private static ParsedCommand? ParseStripAnnotate(string arg)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var box))
        {
            return null;
        }

        // Accept 1-9 directly, or 10-18 as aliases for 1-9
        if (box >= 10 && box <= 18)
        {
            box -= 9;
        }

        if (box < 1 || box > 9)
        {
            return null;
        }

        var text = parts.Length > 1 ? parts[1].Trim() : null;
        return new StripAnnotateCommand(box, text);
    }

    private static ParsedCommand? ParseSquawkOrReset(string? arg)
    {
        if (arg is null)
        {
            return new SquawkResetCommand();
        }

        if (!uint.TryParse(arg, out var code))
        {
            return null;
        }

        if (code > 7777)
        {
            return null;
        }

        // Validate each digit is 0-7 (octal)
        var temp = code;
        for (int i = 0; i < 4; i++)
        {
            if (temp % 10 > 7)
            {
                return null;
            }

            temp /= 10;
        }

        return new SquawkCommand(code);
    }

    /// <summary>
    /// Determines if a token is a runway identifier rather than a distance.
    /// Contains L/C/R suffix → runway. Contains decimal → not runway.
    /// Exactly 2 digits (01-36) → runway. Otherwise numeric → not runway.
    /// </summary>
    internal static bool IsRunwayArg(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        // Has L/C/R suffix → runway
        char last = char.ToUpperInvariant(token[^1]);
        if (last is 'L' or 'C' or 'R' && token.Length >= 2)
        {
            return true;
        }

        // Has decimal → distance
        if (token.Contains('.'))
        {
            return false;
        }

        // Exactly 2 digits → runway
        if (token.Length == 2 && char.IsDigit(token[0]) && char.IsDigit(token[1]))
        {
            return true;
        }

        // Otherwise numeric → distance
        return false;
    }
}
