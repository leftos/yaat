using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using static Yaat.Sim.Commands.CanonicalCommandType;

namespace Yaat.Sim.Commands;

public static class CommandParser
{
    /// <summary>
    /// Parses a compound command string that may contain ';' (sequential blocks)
    /// and ',' (parallel commands within a block). Returns null if any part fails to parse.
    /// Pass a <paramref name="debugLog"/> (e.g. Console.Out) to trace each decision point.
    /// </summary>
    public static CompoundCommand? ParseCompound(string input, IFixLookup fixes, string? aircraftRoute = null, TextWriter? debugLog = null)
    {
        var trimmed = CommandSchemeParser.ExpandMultiCommand(
            CommandSchemeParser.ExpandWait(CommandSchemeParser.ExpandSpeedUntil(input.Trim())),
            fixes
        );
        debugLog?.WriteLine($"[ParseCompound] input=\"{input.Trim()}\" expanded=\"{trimmed}\"");
        if (string.IsNullOrEmpty(trimmed))
        {
            debugLog?.WriteLine("[ParseCompound] => empty after expansion, returning null");
            return null;
        }

        // Check if this is a compound command (contains ; or ,)
        bool isCompound = trimmed.Contains(';') || trimmed.Contains(',');

        if (!isCompound)
        {
            // Check for standalone LV/AT conditions (makes it compound even without ; or ,)
            var upperCheck = trimmed.ToUpperInvariant();
            isCompound =
                upperCheck.StartsWith("LV ") || upperCheck.StartsWith("AT ") || upperCheck.StartsWith("ATFN ") || upperCheck.StartsWith("ONHO ");

            // GIVEWAY/BEHIND/GW are compound only when followed by callsign + TAXI or RWY command
            if (!isCompound && (upperCheck.StartsWith("GIVEWAY ") || upperCheck.StartsWith("BEHIND ") || upperCheck.StartsWith("GW ")))
            {
                var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                isCompound =
                    tokens.Length >= 4
                    && (tokens[2].Equals("TAXI", StringComparison.OrdinalIgnoreCase) || tokens[2].Equals("RWY", StringComparison.OrdinalIgnoreCase));
            }
        }

        debugLog?.WriteLine($"[ParseCompound] isCompound={isCompound}");

        if (!isCompound)
        {
            // Single command — wrap in a compound structure
            var single = Parse(trimmed, fixes, aircraftRoute);
            debugLog?.WriteLine($"[ParseCompound] single Parse => {(single is null ? "null" : single.GetType().Name)}");
            if (single is null)
            {
                return null;
            }

            return new CompoundCommand([new ParsedBlock(null, [single])]);
        }

        var blockStrings = trimmed.Split(';');
        var blocks = new List<ParsedBlock>();

        for (int i = 0; i < blockStrings.Length; i++)
        {
            var blockTrimmed = blockStrings[i].Trim();
            debugLog?.WriteLine($"[ParseCompound] block[{i}]=\"{blockTrimmed}\"");
            var parsed = ParseBlock(blockTrimmed, fixes, aircraftRoute, debugLog);
            if (parsed is null)
            {
                debugLog?.WriteLine($"[ParseCompound] block[{i}] => FAILED");
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

    private static List<ParsedBlock>? ParseBlock(string blockStr, IFixLookup fixes, string? aircraftRoute, TextWriter? debugLog = null)
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
                debugLog?.WriteLine($"  [ParseBlock] LV condition parse failed for \"{blockStr}\"");
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
                debugLog?.WriteLine($"  [ParseBlock] AT condition parse failed for \"{blockStr}\"");
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
                debugLog?.WriteLine($"  [ParseBlock] ATFN condition parse failed for \"{blockStr}\"");
                return null;
            }

            condition = condResult.Value.Condition;
            remaining = condResult.Value.Remainder;
        }
        else if (upper.StartsWith("GIVEWAY ") || upper.StartsWith("BEHIND ") || upper.StartsWith("GW "))
        {
            // GW is a condition only when followed by callsign + TAXI or RWY command.
            // "GW JBU987 TAXI T U" → condition. "GW JBU987 N" → standalone command with location.
            var gwTokens = remaining.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (
                gwTokens.Length < 3
                || (!gwTokens[2].Equals("TAXI", StringComparison.OrdinalIgnoreCase) && !gwTokens[2].Equals("RWY", StringComparison.OrdinalIgnoreCase))
            )
            {
                // Not a condition form — fall through to command list parsing
                debugLog?.WriteLine($"  [ParseBlock] GW not in condition form (no TAXI/RWY after callsign), treating as command");
            }
            else
            {
                var condResult = ParseGiveWayCondition(remaining);
                if (condResult is null)
                {
                    debugLog?.WriteLine($"  [ParseBlock] GW condition parse failed for \"{blockStr}\"");
                    return null;
                }

                condition = condResult.Value.Condition;
                remaining = condResult.Value.Remainder;
                debugLog?.WriteLine(
                    $"  [ParseBlock] GW condition => callsign={((GiveWayCondition)condition).TargetCallsign}, remainder=\"{remaining}\""
                );
            }
        }
        else if (upper.StartsWith("ONHO ") || upper.StartsWith("ONH "))
        {
            var tokens = remaining.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                return null;
            }

            remaining = tokens[1];
            var remainingUpper = remaining.ToUpperInvariant();

            // ONHO followed by another condition (AT/LV/ATFN) → two sequential blocks:
            // block 1 = ONHO (no commands), block 2 = inner condition + commands
            if (remainingUpper.StartsWith("AT ") || remainingUpper.StartsWith("LV ") || remainingUpper.StartsWith("ATFN "))
            {
                var innerBlocks = ParseBlock(remaining, fixes, aircraftRoute, debugLog);
                if (innerBlocks is null)
                {
                    return null;
                }

                var onhoBlock = new ParsedBlock(new OnHandoffCondition(), []);
                return [onhoBlock, .. innerBlocks];
            }

            condition = new OnHandoffCondition();
        }

        if (condition is not null)
        {
            debugLog?.WriteLine($"  [ParseBlock] condition={condition.GetType().Name}, remainder=\"{remaining}\"");

            // Chained conditions: remainder starts with another AT/LV/ATFN → split into sequential blocks
            var remUpper = remaining.ToUpperInvariant();
            if (remUpper.StartsWith("AT ") || remUpper.StartsWith("LV ") || remUpper.StartsWith("ATFN "))
            {
                var innerBlocks = ParseBlock(remaining, fixes, aircraftRoute, debugLog);
                if (innerBlocks is null)
                {
                    return null;
                }

                var outerBlock = new ParsedBlock(condition, []);
                return [outerBlock, .. innerBlocks];
            }
        }

        // Bare condition with no following command (e.g., "AT BRIXX")
        if (string.IsNullOrWhiteSpace(remaining) && condition is not null)
        {
            return [new ParsedBlock(condition, [])];
        }

        if (string.IsNullOrWhiteSpace(remaining))
        {
            return null;
        }

        // After condition extraction, apply expansions to the remainder
        var expanded = CommandSchemeParser.ExpandMultiCommand(CommandSchemeParser.ExpandWait(CommandSchemeParser.ExpandSpeedUntil(remaining)), fixes);
        if (expanded != remaining)
        {
            debugLog?.WriteLine($"  [ParseBlock] remainder expanded: \"{remaining}\" => \"{expanded}\"");
        }

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
                    var cmds = ParseCommandList(sub, fixes, aircraftRoute, debugLog);
                    if (cmds is null)
                    {
                        return null;
                    }

                    results.Add(new ParsedBlock(condition, cmds));
                }
                else
                {
                    // Recursive call for subsequent blocks (they may have their own conditions)
                    var subParsed = ParseBlock(sub, fixes, aircraftRoute, debugLog);
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
        var commands = ParseCommandList(remaining, fixes, aircraftRoute, debugLog);
        if (commands is null)
        {
            return null;
        }

        return [new ParsedBlock(condition, commands)];
    }

    private static List<ParsedCommand>? ParseCommandList(string input, IFixLookup fixes, string? aircraftRoute, TextWriter? debugLog = null)
    {
        // SAY consumes entire remainder as literal text — don't split on comma
        var upperCheck = input.TrimStart().ToUpperInvariant();
        if (upperCheck.StartsWith("SAY ") || upperCheck.StartsWith("SAYF "))
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
                debugLog?.WriteLine($"    [ParseCommandList] \"{trimmedCmd}\" => {cmd.GetType().Name}");
                commands.Add(cmd);
                continue;
            }

            // Try expanding concatenated commands: "FH 270 CM 5000" → "FH 270, CM 5000"
            var expanded = CommandSchemeParser.ExpandMultiCommand(trimmedCmd, fixes);
            if (expanded == trimmedCmd)
            {
                debugLog?.WriteLine($"    [ParseCommandList] \"{trimmedCmd}\" => FAILED (no expansion available)");
                return null;
            }

            debugLog?.WriteLine($"    [ParseCommandList] \"{trimmedCmd}\" expanded to \"{expanded}\"");
            foreach (var subCmd in expanded.Split(','))
            {
                var parsed = Parse(subCmd.Trim(), fixes, aircraftRoute);
                if (parsed is null)
                {
                    debugLog?.WriteLine($"    [ParseCommandList] sub \"{subCmd.Trim()}\" => FAILED");
                    return null;
                }

                debugLog?.WriteLine($"    [ParseCommandList] sub \"{subCmd.Trim()}\" => {parsed.GetType().Name}");
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
        // "AT BRIXX" → condition=AtFixCondition(BRIXX, lat, lon), remainder=""
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var token = parts[1].ToUpperInvariant();
        var remainder = parts.Length >= 3 ? string.Join(' ', parts.Skip(2)) : "";

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

        // T{n}L/T{n}R concatenated relative turns (e.g. T30L → LeftTurnCommand(30))
        var upper = trimmed.ToUpperInvariant();
        if (upper.Length >= 3 && upper[0] == 'T' && char.IsDigit(upper[1]) && upper[^1] is 'L' or 'R' && int.TryParse(upper[1..^1], out var relDeg))
        {
            return upper[^1] == 'L' ? new LeftTurnCommand(relDeg) : new RightTurnCommand(relDeg);
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToUpperInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

        // Legacy merged forms not in registry
        switch (verb)
        {
            case "CTOMRT":
                return DepartureCommandParser.ParseCtoArg("MRT" + (arg is not null ? " " + arg : ""), fixes);
            case "CTOMLT":
                return DepartureCommandParser.ParseCtoArg("MLT" + (arg is not null ? " " + arg : ""), fixes);
            case "CTORH" when arg is null:
                return new ClearedForTakeoffCommand(new RunwayHeadingDeparture());
            case "GAMRT" when arg is null:
                return new GoAroundCommand(TrafficPattern: PatternDirection.Right);
            case "GAMLT" when arg is null:
                return new GoAroundCommand(TrafficPattern: PatternDirection.Left);
            case "T" when arg is not null:
                return ParseTurnWithDirection(arg);
        }

        // RWY {runway} [TAXI] {path} → rewrite to Taxi command
        if (verb == "RWY" && arg is not null)
        {
            var rewritten = RewriteRwyToTaxiArg(arg);
            if (rewritten is not null)
            {
                return ParseByType(Taxi, rewritten, fixes, aircraftRoute, trimmed);
            }
        }

        // Resolve alias → CanonicalCommandType via registry
        if (!CommandRegistry.AliasToCanonicType.TryGetValue(verb, out var type))
        {
            return TryConcatenation(upper, fixes);
        }

        return ParseByType(type, arg, fixes, aircraftRoute, trimmed);
    }

    private static ParsedCommand? ParseByType(CanonicalCommandType type, string? arg, IFixLookup? fixes, string? aircraftRoute, string rawInput)
    {
        return type switch
        {
            // Heading
            FlyHeading => ParseHeading(arg, h => new FlyHeadingCommand(h)),
            TurnLeft => ParseHeading(arg, h => new TurnLeftCommand(h)),
            TurnRight => ParseHeading(arg, h => new TurnRightCommand(h)),
            RelativeLeft => ParseDegrees(arg, d => new LeftTurnCommand(d)),
            RelativeRight => ParseDegrees(arg, d => new RightTurnCommand(d)),
            FlyPresentHeading when arg is null => new FlyPresentHeadingCommand(),
            // Altitude / Speed
            ClimbMaintain => ParseAltitude(arg, fixes, a => new ClimbMaintainCommand(a)),
            DescendMaintain => ParseAltitude(arg, fixes, a => new DescendMaintainCommand(a)),
            Speed => ParseSpeed(arg),
            ResumeNormalSpeed when arg is null => new ResumeNormalSpeedCommand(),
            ReduceToFinalApproachSpeed when arg is null => new ReduceToFinalApproachSpeedCommand(),
            DeleteSpeedRestrictions when arg is null => new DeleteSpeedRestrictionsCommand(),
            Expedite => ParseExpedite(arg),
            NormalRate when arg is null => new NormalRateCommand(),
            Mach when arg is not null => ParseMach(arg),
            // Force commands
            ForceHeading => ParseHeading(arg, h => new ForceHeadingCommand(h)),
            ForceAltitude => ParseAltitude(arg, fixes, a => new ForceAltitudeCommand(a)),
            ForceSpeed => ParseForceSpeed(arg),
            Warp => ParseWarp(arg, fixes),
            WarpGround when arg is not null => ParseWarpGround(arg),
            // Transponder
            Squawk => ParseSquawkOrReset(arg),
            SquawkVfr when arg is null or "1200" => new SquawkVfrCommand(),
            SquawkNormal when arg is null => new SquawkNormalCommand(),
            SquawkStandby when arg is null => new SquawkStandbyCommand(),
            Ident when arg is null => new IdentCommand(),
            RandomSquawk when arg is null => new RandomSquawkCommand(),
            SquawkAll when arg is null => new SquawkAllCommand(),
            SquawkNormalAll when arg is null => new SquawkNormalAllCommand(),
            SquawkStandbyAll when arg is null => new SquawkStandbyAllCommand(),
            // Navigation
            DirectTo when fixes is not null => ParseDirectTo(arg, fixes, aircraftRoute),
            ForceDirectTo when fixes is not null => ParseForceDirectTo(arg, fixes, aircraftRoute),
            AppendDirectTo when fixes is not null => ParseAppendDirectTo(arg, fixes, aircraftRoute),
            AppendForceDirectTo when fixes is not null => ParseAppendForceDirectTo(arg, fixes, aircraftRoute),
            // Sim control
            Delete when arg is null => new DeleteCommand(),
            Pause when arg is null => new PauseCommand(),
            Unpause when arg is null => new UnpauseCommand(),
            SimRate => ParseInt(arg, r => new SimRateCommand(r)),
            Wait => ParseWaitSeconds(arg),
            WaitDistance => ParseWaitDistance(arg),
            SpawnNow when arg is null => new SpawnNowCommand(),
            SpawnDelay => ParseInt(arg, s => new SpawnDelayCommand(s)),
            Add when arg is not null => new AddAircraftCommand(arg),
            // Tower
            LineUpAndWait when arg is null => new LineUpAndWaitCommand(),
            ClearedForTakeoff => DepartureCommandParser.ParseCtoArg(arg, fixes),
            CancelTakeoffClearance when arg is null => new CancelTakeoffClearanceCommand(),
            GoAround => ParseGoAround(arg, fixes),
            ClearedToLand when arg is null or "NODEL" => new ClearedToLandCommand(arg?.Equals("NODEL", StringComparison.OrdinalIgnoreCase) == true),
            LandAndHoldShort when arg is not null => new LandAndHoldShortCommand(arg.Trim().ToUpperInvariant()),
            CancelLandingClearance when arg is null => new CancelLandingClearanceCommand(),
            Sequence => ParseSequence(arg),
            // Pattern
            EnterLeftDownwind => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterLeftDownwindCommand(rwy)),
            EnterRightDownwind => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterRightDownwindCommand(rwy)),
            EnterLeftCrosswind => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterLeftCrosswindCommand(rwy)),
            EnterRightCrosswind => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterRightCrosswindCommand(rwy)),
            EnterLeftBase => DepartureCommandParser.ParsePatternBaseEntry(arg),
            EnterRightBase => DepartureCommandParser.ParsePatternBaseEntry(arg, right: true),
            EnterFinal => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterFinalCommand(rwy)),
            MakeLeftTraffic => new MakeLeftTrafficCommand(arg?.ToUpperInvariant()),
            MakeRightTraffic => new MakeRightTrafficCommand(arg?.ToUpperInvariant()),
            TurnCrosswind when arg is null => new TurnCrosswindCommand(),
            TurnDownwind when arg is null => new TurnDownwindCommand(),
            TurnBase when arg is null => new TurnBaseCommand(),
            ExtendDownwind when arg is null => new ExtendDownwindCommand(),
            MakeShortApproach when arg is null => new MakeShortApproachCommand(),
            MakeNormalApproach when arg is null => new MakeNormalApproachCommand(),
            Cancel270 when arg is null => new Cancel270Command(),
            PatternSize when arg is not null => ParsePatternSize(arg),
            MakeLeftSTurns => ParseSTurns(arg, TurnDirection.Left),
            MakeRightSTurns => ParseSTurns(arg, TurnDirection.Right),
            Plan270 when arg is null => new Plan270Command(),
            MakeLeft360 when arg is null => new MakeLeft360Command(),
            MakeRight360 when arg is null => new MakeRight360Command(),
            MakeLeft270 when arg is null => new MakeLeft270Command(),
            MakeRight270 when arg is null => new MakeRight270Command(),
            CircleAirport when arg is null => new CircleAirportCommand(),
            // Option / special ops
            TouchAndGo => new TouchAndGoCommand(arg?.Trim().ToUpperInvariant()),
            StopAndGo when arg is null => new StopAndGoCommand(),
            LowApproach when arg is null => new LowApproachCommand(),
            ClearedForOption when arg is null => new ClearedForOptionCommand(),
            // Hold
            HoldPresentPosition360Left when arg is null => new HoldPresentPosition360Command(TurnDirection.Left),
            HoldPresentPosition360Right when arg is null => new HoldPresentPosition360Command(TurnDirection.Right),
            HoldPresentPositionHover when arg is null => new HoldPresentPositionHoverCommand(),
            HoldAtFixLeft => ParseHoldAtFix(arg, fixes, TurnDirection.Left),
            HoldAtFixRight => ParseHoldAtFix(arg, fixes, TurnDirection.Right),
            HoldAtFixHover => ParseHoldAtFixHover(arg, fixes),
            // Helicopter
            AirTaxi => new AirTaxiCommand(arg?.Trim().ToUpperInvariant()),
            Land when arg is not null => ParseLand(arg),
            ClearedTakeoffPresent when arg is null => new ClearedTakeoffPresentCommand(),
            // Ground — HOLD is overloaded: bare = HoldPosition, with args = HoldingPattern
            Pushback => GroundCommandParser.ParsePushback(arg),
            Taxi => GroundCommandParser.ParseTaxi(arg),
            AssignRunway => GroundCommandParser.ParseRwyTaxi(arg),
            HoldPosition when arg is null => new HoldPositionCommand(),
            HoldPosition when arg is not null => ApproachCommandParser.ParseHold(arg, fixes),
            Resume when arg is null => new ResumeCommand(),
            CrossRunway => GroundCommandParser.ParseCross(arg),
            HoldShort => GroundCommandParser.ParseHoldShort(arg),
            Follow => GroundCommandParser.ParseFollow(arg),
            GiveWay => GroundCommandParser.ParseGiveWay(arg),
            TaxiAll => GroundCommandParser.ParseTaxiAll(arg),
            BreakConflict when arg is null => new BreakConflictCommand(),
            CanonicalCommandType.Go when arg is null => new GoCommand(),
            ExitLeft when arg is null or "NODEL" => new ExitLeftCommand(arg?.Equals("NODEL", StringComparison.OrdinalIgnoreCase) == true),
            ExitLeft => new ExitLeftCommand(false, arg?.Trim().ToUpperInvariant()),
            ExitRight when arg is null or "NODEL" => new ExitRightCommand(arg?.Equals("NODEL", StringComparison.OrdinalIgnoreCase) == true),
            ExitRight => new ExitRightCommand(false, arg?.Trim().ToUpperInvariant()),
            ExitTaxiway when arg is not null => GroundCommandParser.ParseExitTaxiway(arg),
            // Approach
            ExpectApproach => ParseExpectApproach(arg),
            ClearedApproach => ApproachCommandParser.ParseCapp(arg, fixes, false),
            ClearedApproachForce => ApproachCommandParser.ParseCapp(arg, fixes, true),
            ClearedApproachStraightIn => ApproachCommandParser.ParseCappSi(arg, fixes),
            JoinApproach => ApproachCommandParser.ParseJapp(arg, fixes, false),
            JoinApproachForce => ApproachCommandParser.ParseJapp(arg, fixes, true),
            JoinApproachStraightIn => ApproachCommandParser.ParseJappSi(arg, fixes),
            JoinFinalApproachCourse => ApproachCommandParser.ParseJfac(arg),
            JoinStar => ApproachCommandParser.ParseJarr(arg),
            JoinAirway => ApproachCommandParser.ParseJawy(arg),
            JoinRadialOutbound => ApproachCommandParser.ParseJrado(arg, fixes),
            JoinRadialInbound => ApproachCommandParser.ParseJradi(arg, fixes),
            HoldingPattern => ApproachCommandParser.ParseHold(arg, fixes),
            PositionTurnAltitudeClearance => ApproachCommandParser.ParsePtac(arg, fixes),
            ClimbVia => ApproachCommandParser.ParseCvia(arg),
            DescendVia => ApproachCommandParser.ParseDvia(arg, fixes),
            CrossFix => ApproachCommandParser.ParseCfix(arg, fixes),
            DepartFix => ApproachCommandParser.ParseDepart(arg, fixes),
            ListApproaches => ApproachCommandParser.ParseApps(arg),
            ClearedVisualApproach => ApproachCommandParser.ParseCva(arg),
            ReportFieldInSight when arg is null => new ReportFieldInSightCommand(),
            ReportTrafficInSight => new ReportTrafficInSightCommand(arg?.Trim().ToUpperInvariant()),
            // Track operations
            SetActivePosition => ParseTcpArg(arg, tcp => new SetActivePositionCommand(tcp)),
            TrackAircraft => new TrackAircraftCommand(arg?.Trim().ToUpperInvariant()),
            DropTrack when arg is null => new DropTrackCommand(),
            InitiateHandoff when arg is null => new InitiateHandoffCommand(null),
            InitiateHandoff => ParseTcpArg(arg, tcp => new InitiateHandoffCommand(tcp)),
            ForceHandoff => ParseTcpArg(arg, tcp => new ForceHandoffCommand(tcp)),
            AcceptHandoff => new AcceptHandoffCommand(arg?.Trim().ToUpperInvariant()),
            CancelHandoff when arg is null => new CancelHandoffCommand(),
            AcceptAllHandoffs when arg is null => new AcceptAllHandoffsCommand(),
            InitiateHandoffAll => ParseTcpArg(arg, tcp => new InitiateHandoffAllCommand(tcp)),
            PointOut => new PointOutCommand(arg?.Trim().ToUpperInvariant()),
            Acknowledge when arg is null => new AcknowledgeCommand(),
            // Data operations
            Annotate when arg is not null => ParseStripAnnotate(arg),
            StripPush when arg is not null => new StripPushCommand(arg.Trim().ToUpperInvariant()),
            Scratchpad1 when arg is not null => new Scratchpad1Command(arg.Trim().ToUpperInvariant()),
            Scratchpad2 when arg is not null => new Scratchpad2Command(arg.Trim().ToUpperInvariant()),
            TemporaryAltitude when arg is null => new TemporaryAltitudeCommand(0),
            TemporaryAltitude when int.TryParse(arg, out var taVal) && taVal == 0 => new TemporaryAltitudeCommand(0),
            TemporaryAltitude => ParseAltitudeHundreds(arg, h => new TemporaryAltitudeCommand(h)),
            Cruise => ParseAltitudeHundreds(arg, h => new CruiseCommand(h)),
            OnHandoff when arg is null => new OnHandoffCommand(),
            // Broadcast
            Say when arg is not null => new SayCommand(arg),
            SaySpeed when arg is null => new SaySpeedCommand(),
            // Queue
            DeleteQueuedCommands when arg is null => new DeleteQueuedCommand(),
            DeleteQueuedCommands => int.TryParse(arg, out var delAtBlock) ? new DeleteQueuedCommand(delAtBlock) : null,
            ShowQueuedCommands when arg is null => new ShowQueuedCommand(),
            // Coordination
            CoordinationRelease => new CoordinationReleaseCommand(arg?.Trim().ToUpperInvariant()),
            CoordinationHold => ParseHoldArgs(arg),
            CoordinationRecall => new CoordinationRecallCommand(arg?.Trim().ToUpperInvariant()),
            CoordinationAcknowledge => new CoordinationAcknowledgeCommand(arg?.Trim().ToUpperInvariant()),
            CoordinationAutoAck => ParseOptionalListId(arg, listId => new CoordinationAutoAckCommand(listId)),
            // Consolidation
            Consolidate => ParseConsolidate(arg, false),
            ConsolidateFull => ParseConsolidate(arg, true),
            Deconsolidate => ParseDeconsolidate(arg),
            // Flight plan
            ChangeDestination => ParseChangeDestination(arg),
            CreateFlightPlan => ParseCreateFlightPlan(arg, "IFR"),
            CreateVfrFlightPlan => ParseCreateFlightPlan(arg, "VFR"),
            SetRemarks when arg is not null => new SetRemarksCommand(arg),
            // Verbs with arg-required guards: return null when arg missing (invalid usage)
            Mach
            or WarpGround
            or LandAndHoldShort
            or PatternSize
            or Land
            or ExitTaxiway
            or Annotate
            or StripPush
            or Scratchpad1
            or Scratchpad2
            or Say
            or SetRemarks
            or Add when arg is null => null,
            // Registry-known but not yet handled
            _ => new UnsupportedCommand(rawInput),
        };
    }

    /// <summary>
    /// Concatenation fallback: tries prefix-matching registry aliases when verb+digits
    /// are written without a space (e.g. FH270, CM240, SQ1234).
    /// </summary>
    private static ParsedCommand? TryConcatenation(string upperInput, IFixLookup? fixes)
    {
        foreach (var (alias, type) in ConcatenationCandidates.Value)
        {
            if (!upperInput.StartsWith(alias, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = upperInput[alias.Length..];
            if (remainder.Length == 0)
            {
                continue;
            }

            bool isAltitudeCommand = type is ClimbMaintain or DescendMaintain;
            if (isAltitudeCommand ? !IsAltitudeArg(remainder) : !int.TryParse(remainder, out _))
            {
                continue;
            }

            return ParseByType(type, remainder, fixes, null, upperInput);
        }

        return null;
    }

    private static readonly Lazy<List<(string Alias, CanonicalCommandType Type)>> ConcatenationCandidates = new(BuildConcatenationCandidates);

    private static List<(string Alias, CanonicalCommandType Type)> BuildConcatenationCandidates()
    {
        var candidates = new List<(string Alias, CanonicalCommandType Type)>();
        foreach (var def in CommandRegistry.All.Values)
        {
            if (def.ArgMode == ArgMode.None)
            {
                continue;
            }

            if (IsConcatenationExcluded(def.Type))
            {
                continue;
            }

            foreach (var alias in def.DefaultAliases)
            {
                candidates.Add((alias.ToUpperInvariant(), def.Type));
            }
        }

        candidates.Sort((a, b) => b.Alias.Length.CompareTo(a.Alias.Length));
        return candidates;
    }

    internal static bool IsConcatenationExcluded(CanonicalCommandType type)
    {
        return type
            is RelativeLeft
                or RelativeRight
                or Pause
                or Unpause
                or SimRate
                or Wait
                or WaitDistance
                or Add
                or DirectTo
                or AppendDirectTo
                or HoldAtFixLeft
                or HoldAtFixRight
                or HoldAtFixHover
                or SpawnNow
                or SpawnDelay
                or Taxi
                or CrossRunway
                or HoldShort
                or Follow
                or Say;
    }

    internal static bool IsAltitudeArg(string arg)
    {
        if (int.TryParse(arg, out _))
        {
            return true;
        }

        var plusIndex = arg.IndexOf('+');
        if (plusIndex <= 0 || plusIndex == arg.Length - 1)
        {
            return false;
        }

        return int.TryParse(arg[(plusIndex + 1)..], out _);
    }

    /// <summary>
    /// Rewrites "RWY {runway} [TAXI] {path}" to "{path} RWY {runway}" for Taxi command.
    /// </summary>
    internal static string? RewriteRwyToTaxiArg(string arg)
    {
        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return null;
        }

        var runway = tokens[0].ToUpperInvariant();
        int startIdx = 1;

        if (startIdx < tokens.Length && tokens[startIdx].Equals("TAXI", StringComparison.OrdinalIgnoreCase))
        {
            startIdx++;
        }

        if (startIdx >= tokens.Length)
        {
            return null;
        }

        var remaining = string.Join(" ", tokens[startIdx..]);
        return $"{remaining} RWY {runway}";
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
        if (arg is null || !int.TryParse(arg, out var seconds) || seconds < 0)
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

    /// <summary>
    /// Parses "T {degrees} {direction}" — ATCTrainer turn command.
    /// Direction: L, LEFT, R, RIGHT.
    /// </summary>
    private static ParsedCommand? ParseTurnWithDirection(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var degrees))
        {
            return null;
        }

        if (degrees < 1 || degrees > 359)
        {
            return null;
        }

        return parts[1].ToUpperInvariant() switch
        {
            "L" or "LEFT" => new LeftTurnCommand(degrees),
            "R" or "RIGHT" => new RightTurnCommand(degrees),
            _ => null,
        };
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

        // SPD M.80 or SPD M80 → delegate to Mach
        if (arg.StartsWith('M') || arg.StartsWith('m'))
        {
            return ParseMach(arg[1..]);
        }

        // SPD {speed} {termination_fix} — speed until waypoint, then resume normal
        var speedParts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (!int.TryParse(speedParts[0], out var speed))
        {
            return null;
        }

        if (speedParts.Length == 1)
        {
            return new SpeedCommand(speed);
        }

        // Second token is a termination waypoint — ignored for now (not yet modeled),
        // but parse successfully so scenario validation passes
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
