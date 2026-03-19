using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using static Yaat.Sim.Commands.CanonicalCommandType;
using PR = Yaat.Sim.Commands.ParseResult<Yaat.Sim.Commands.ParsedCommand>;

namespace Yaat.Sim.Commands;

public static class CommandParser
{
    /// <summary>
    /// Parses a compound command string that may contain ';' (sequential blocks)
    /// and ',' (parallel commands within a block). Returns a failure reason if any part fails to parse.
    /// Pass a <paramref name="debugLog"/> (e.g. Console.Out) to trace each decision point.
    /// </summary>
    public static ParseResult<CompoundCommand> ParseCompound(string input, string? aircraftRoute = null, TextWriter? debugLog = null)
    {
        var trimmed = CommandSchemeParser.ExpandMultiCommand(CommandSchemeParser.ExpandWait(CommandSchemeParser.ExpandSpeedUntil(input.Trim())));
        debugLog?.WriteLine($"[ParseCompound] input=\"{input.Trim()}\" expanded=\"{trimmed}\"");
        if (string.IsNullOrEmpty(trimmed))
        {
            debugLog?.WriteLine("[ParseCompound] => empty after expansion, returning fail");
            return ParseResult<CompoundCommand>.Fail("empty after expansion");
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
            var single = Parse(trimmed, aircraftRoute);
            debugLog?.WriteLine($"[ParseCompound] single Parse => {(single.IsSuccess ? single.Value!.GetType().Name : $"FAIL: {single.Reason}")}");
            if (!single.IsSuccess)
            {
                return ParseResult<CompoundCommand>.Fail(single.Reason!);
            }

            return ParseResult<CompoundCommand>.Ok(new CompoundCommand([new ParsedBlock(null, [single.Value!])]));
        }

        var blockStrings = trimmed.Split(';');
        var blocks = new List<ParsedBlock>();

        for (int i = 0; i < blockStrings.Length; i++)
        {
            var blockTrimmed = blockStrings[i].Trim();
            debugLog?.WriteLine($"[ParseCompound] block[{i}]=\"{blockTrimmed}\"");
            var parsed = ParseBlock(blockTrimmed, aircraftRoute, debugLog);
            if (parsed is null)
            {
                debugLog?.WriteLine($"[ParseCompound] block[{i}] => FAILED");
                return ParseResult<CompoundCommand>.Fail(_lastBlockFailure ?? $"failed to parse block: {blockTrimmed}");
            }

            blocks.AddRange(parsed);
        }

        if (blocks.Count == 0)
        {
            return ParseResult<CompoundCommand>.Fail("no blocks parsed");
        }

        return ParseResult<CompoundCommand>.Ok(new CompoundCommand(blocks));
    }

    // Thread-local storage for propagating block/command failure reasons through ParseBlock
    [ThreadStatic]
    private static string? _lastBlockFailure;

    private static List<ParsedBlock>? ParseBlock(string blockStr, string? aircraftRoute, TextWriter? debugLog = null)
    {
        if (string.IsNullOrWhiteSpace(blockStr))
        {
            _lastBlockFailure = "empty block";
            return null;
        }

        BlockCondition? condition = null;
        var remaining = blockStr;

        // Check for LV or AT condition prefix
        var upper = remaining.ToUpperInvariant();
        if (upper.StartsWith("LV "))
        {
            var condResult = ParseLvCondition(remaining);
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
            var condResult = ParseAtCondition(remaining);
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
                _lastBlockFailure = "ONHO requires a command";
                return null;
            }

            remaining = tokens[1];
            var remainingUpper = remaining.ToUpperInvariant();

            // ONHO followed by another condition (AT/LV/ATFN) → two sequential blocks:
            // block 1 = ONHO (no commands), block 2 = inner condition + commands
            if (remainingUpper.StartsWith("AT ") || remainingUpper.StartsWith("LV ") || remainingUpper.StartsWith("ATFN "))
            {
                var innerBlocks = ParseBlock(remaining, aircraftRoute, debugLog);
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
                var innerBlocks = ParseBlock(remaining, aircraftRoute, debugLog);
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
            _lastBlockFailure = "empty command after condition";
            return null;
        }

        // After condition extraction, apply expansions to the remainder
        var expanded = CommandSchemeParser.ExpandMultiCommand(CommandSchemeParser.ExpandWait(CommandSchemeParser.ExpandSpeedUntil(remaining)));
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
                    var cmds = ParseCommandList(sub, aircraftRoute, debugLog);
                    if (cmds is null)
                    {
                        return null;
                    }

                    results.Add(new ParsedBlock(condition, cmds));
                }
                else
                {
                    // Recursive call for subsequent blocks (they may have their own conditions)
                    var subParsed = ParseBlock(sub, aircraftRoute, debugLog);
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
        var commands = ParseCommandList(remaining, aircraftRoute, debugLog);
        if (commands is null)
        {
            return null;
        }

        return [new ParsedBlock(condition, commands)];
    }

    private static List<ParsedCommand>? ParseCommandList(string input, string? aircraftRoute, TextWriter? debugLog = null)
    {
        // SAY consumes entire remainder as literal text — don't split on comma
        var upperCheck = input.TrimStart().ToUpperInvariant();
        if (upperCheck.StartsWith("SAY ") || upperCheck.StartsWith("SAYF "))
        {
            var cmd = Parse(input.Trim(), aircraftRoute);
            if (!cmd.IsSuccess)
            {
                _lastBlockFailure = cmd.Reason;
                return null;
            }

            return [cmd.Value!];
        }

        var commandStrings = input.Split(',');
        var commands = new List<ParsedCommand>();

        foreach (var cmdStr in commandStrings)
        {
            var trimmedCmd = cmdStr.Trim();
            var cmd = Parse(trimmedCmd, aircraftRoute);
            if (cmd.IsSuccess)
            {
                debugLog?.WriteLine($"    [ParseCommandList] \"{trimmedCmd}\" => {cmd.Value!.GetType().Name}");
                commands.Add(cmd.Value!);
                continue;
            }

            // Try expanding concatenated commands: "FH 270 CM 5000" → "FH 270, CM 5000"
            var expanded = CommandSchemeParser.ExpandMultiCommand(trimmedCmd);
            if (expanded == trimmedCmd)
            {
                debugLog?.WriteLine($"    [ParseCommandList] \"{trimmedCmd}\" => FAILED (no expansion available)");
                _lastBlockFailure = cmd.Reason;
                return null;
            }

            debugLog?.WriteLine($"    [ParseCommandList] \"{trimmedCmd}\" expanded to \"{expanded}\"");
            foreach (var subCmd in expanded.Split(','))
            {
                var parsed = Parse(subCmd.Trim(), aircraftRoute);
                if (!parsed.IsSuccess)
                {
                    debugLog?.WriteLine($"    [ParseCommandList] sub \"{subCmd.Trim()}\" => FAILED");
                    _lastBlockFailure = parsed.Reason;
                    return null;
                }

                debugLog?.WriteLine($"    [ParseCommandList] sub \"{subCmd.Trim()}\" => {parsed.Value!.GetType().Name}");
                commands.Add(parsed.Value!);
            }
        }

        return commands.Count > 0 ? commands : null;
    }

    private static (BlockCondition Condition, string Remainder)? ParseLvCondition(string input)
    {
        // "LV 050 FH 090" → condition=LevelCondition(5000), remainder="FH 090"
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            _lastBlockFailure = "LV requires altitude and a command";
            return null;
        }

        // parts[0] = "LV", parts[1] = altitude (numeric or AGL), parts[2..] = remaining
        int? altitude = AltitudeResolver.Resolve(parts[1]);
        if (altitude is null)
        {
            _lastBlockFailure = $"invalid LV altitude '{parts[1]}'";
            return null;
        }

        var remainder = string.Join(' ', parts.Skip(2));
        return (new LevelCondition(altitude.Value), remainder);
    }

    private static (BlockCondition Condition, string Remainder)? ParseAtCondition(string input)
    {
        // "AT 5000 CM 190" → condition=LevelCondition(5000), remainder="CM 190"
        // "AT SUNOL FH 090" → condition=AtFixCondition(SUNOL, lat, lon), remainder="FH 090"
        // "AT BRIXX" → condition=AtFixCondition(BRIXX, lat, lon), remainder=""
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            _lastBlockFailure = "AT requires a fix name or altitude";
            return null;
        }

        var token = parts[1].ToUpperInvariant();
        var remainder = parts.Length >= 3 ? string.Join(' ', parts.Skip(2)) : "";

        // Try altitude first — "AT 5000 ..." means "when reaching altitude 5000"
        int? altitude = AltitudeResolver.Resolve(token);
        if (altitude is not null)
        {
            return (new LevelCondition(altitude.Value), remainder);
        }

        // Try direct fix lookup
        var navDb = NavigationDatabase.Instance;
        var pos = navDb.GetFixPosition(token);
        if (pos is not null)
        {
            return (new AtFixCondition(token, pos.Value.Lat, pos.Value.Lon), remainder);
        }

        // Try FRD/FR parse to preserve radial/distance info
        var parsed = FrdResolver.ParseFrd(token);
        if (parsed is not null)
        {
            var (fixName, radial, distance) = parsed.Value;
            var fixPos = navDb.GetFixPosition(fixName);
            if (fixPos is not null)
            {
                return (new AtFixCondition(fixName, fixPos.Value.Lat, fixPos.Value.Lon, radial, distance), remainder);
            }

            // Bare name (no radial/distance) = same as direct fix lookup — use simple message
            // FRD with radial/distance = mention the FRD context
            _lastBlockFailure = radial is not null
                ? $"fix '{fixName}' not found (from FRD '{token}')"
                : $"'{token}' is not a known fix or valid altitude";
            return null;
        }

        _lastBlockFailure = $"'{token}' is not a known fix or valid altitude";
        return null;
    }

    private static (BlockCondition Condition, string Remainder)? ParseGiveWayCondition(string input)
    {
        // "GIVEWAY SWA5456 TAXI T U W" → condition=GiveWayCondition(SWA5456), remainder="TAXI T U W"
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            _lastBlockFailure = "GIVEWAY requires callsign and a command";
            return null;
        }

        var targetCallsign = parts[1].ToUpperInvariant();
        var remainder = string.Join(' ', parts.Skip(2));
        return (new GiveWayCondition(targetCallsign), remainder);
    }

    /// <summary>
    /// Parses a single command string. Extended to support DCT verb.
    /// </summary>
    public static PR Parse(string input, string? aircraftRoute = null)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return PR.Fail("empty command");
        }

        // T{n}L/T{n}R concatenated relative turns (e.g. T30L → LeftTurnCommand(30))
        var upper = trimmed.ToUpperInvariant();
        if (upper.Length >= 3 && upper[0] == 'T' && char.IsDigit(upper[1]) && upper[^1] is 'L' or 'R' && int.TryParse(upper[1..^1], out var relDeg))
        {
            return PR.Ok(upper[^1] == 'L' ? new LeftTurnCommand(relDeg) : new RightTurnCommand(relDeg));
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToUpperInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

        // Legacy merged forms not in registry
        switch (verb)
        {
            case "CTOMRT":
                return PR.Ok(DepartureCommandParser.ParseCtoArg("MRT" + (arg is not null ? " " + arg : "")));
            case "CTOMLT":
                return PR.Ok(DepartureCommandParser.ParseCtoArg("MLT" + (arg is not null ? " " + arg : "")));
            case "CTORH" when arg is null:
                return PR.Ok(new ClearedForTakeoffCommand(new RunwayHeadingDeparture()));
            case "GAMRT" when arg is null:
                return PR.Ok(new GoAroundCommand(TrafficPattern: PatternDirection.Right));
            case "GAMLT" when arg is null:
                return PR.Ok(new GoAroundCommand(TrafficPattern: PatternDirection.Left));
            case "T" when arg is not null:
                return ParseTurnWithDirection(arg);
        }

        // RWY {runway} [TAXI] {path} → rewrite to Taxi command
        if (verb == "RWY" && arg is not null)
        {
            var rewritten = RewriteRwyToTaxiArg(arg);
            if (rewritten is not null)
            {
                return ParseByType(Taxi, rewritten, aircraftRoute, trimmed);
            }
        }

        // Resolve alias → CanonicalCommandType via registry
        if (!CommandRegistry.AliasToCanonicType.TryGetValue(verb, out var type))
        {
            return TryConcatenation(upper);
        }

        return ParseByType(type, arg, aircraftRoute, trimmed);
    }

    private static PR ParseByType(CanonicalCommandType type, string? arg, string? aircraftRoute, string rawInput)
    {
        return type switch
        {
            // Heading
            FlyHeading => ParseHeading(arg, h => new FlyHeadingCommand(h)),
            TurnLeft => ParseHeading(arg, h => new TurnLeftCommand(h)),
            TurnRight => ParseHeading(arg, h => new TurnRightCommand(h)),
            RelativeLeft => ParseDegrees(arg, d => new LeftTurnCommand(d)),
            RelativeRight => ParseDegrees(arg, d => new RightTurnCommand(d)),
            FlyPresentHeading when arg is null => PR.Ok(new FlyPresentHeadingCommand()),
            // Altitude / Speed
            ClimbMaintain => ParseAltitude(arg, a => new ClimbMaintainCommand(a)),
            DescendMaintain => ParseAltitude(arg, a => new DescendMaintainCommand(a)),
            Speed => ParseSpeed(arg),
            ResumeNormalSpeed when arg is null => PR.Ok(new ResumeNormalSpeedCommand()),
            ReduceToFinalApproachSpeed when arg is null => PR.Ok(new ReduceToFinalApproachSpeedCommand()),
            DeleteSpeedRestrictions when arg is null => PR.Ok(new DeleteSpeedRestrictionsCommand()),
            Expedite => PR.Ok(ParseExpedite(arg)),
            NormalRate when arg is null => PR.Ok(new NormalRateCommand()),
            Mach when arg is not null => ParseMach(arg),
            // Force commands
            ForceHeading => ParseHeading(arg, h => new ForceHeadingCommand(h)),
            ForceAltitude => ParseAltitude(arg, a => new ForceAltitudeCommand(a)),
            ForceSpeed => ParseForceSpeed(arg),
            Warp => ParseWarp(arg),
            WarpGround when arg is not null => ParseWarpGround(arg),
            // Transponder
            Squawk => ParseSquawkOrReset(arg),
            SquawkVfr when arg is null or "1200" => PR.Ok(new SquawkVfrCommand()),
            SquawkNormal when arg is null => PR.Ok(new SquawkNormalCommand()),
            SquawkStandby when arg is null => PR.Ok(new SquawkStandbyCommand()),
            Ident when arg is null => PR.Ok(new IdentCommand()),
            RandomSquawk when arg is null => PR.Ok(new RandomSquawkCommand()),
            SquawkAll when arg is null => PR.Ok(new SquawkAllCommand()),
            SquawkNormalAll when arg is null => PR.Ok(new SquawkNormalAllCommand()),
            SquawkStandbyAll when arg is null => PR.Ok(new SquawkStandbyAllCommand()),
            // Navigation
            DirectTo => ParseDirectTo(arg, aircraftRoute),
            ForceDirectTo => ParseForceDirectTo(arg, aircraftRoute),
            AppendDirectTo => ParseAppendDirectTo(arg, aircraftRoute),
            AppendForceDirectTo => ParseAppendForceDirectTo(arg, aircraftRoute),
            // Sim control
            Delete when arg is null => PR.Ok(new DeleteCommand()),
            Pause when arg is null => PR.Ok(new PauseCommand()),
            Unpause when arg is null => PR.Ok(new UnpauseCommand()),
            SimRate => ParseInt(arg, r => new SimRateCommand(r)),
            Wait => ParseWaitSeconds(arg),
            WaitDistance => ParseWaitDistance(arg),
            SpawnNow when arg is null => PR.Ok(new SpawnNowCommand()),
            SpawnDelay => ParseInt(arg, s => new SpawnDelayCommand(s)),
            Add when arg is not null => PR.Ok(new AddAircraftCommand(arg)),
            // Tower
            LineUpAndWait when arg is null => PR.Ok(new LineUpAndWaitCommand()),
            ClearedForTakeoff => PR.Ok(DepartureCommandParser.ParseCtoArg(arg)),
            CancelTakeoffClearance when arg is null => PR.Ok(new CancelTakeoffClearanceCommand()),
            GoAround => ParseGoAround(arg),
            ClearedToLand when arg is null or "NODEL" => PR.Ok(
                new ClearedToLandCommand(arg?.Equals("NODEL", StringComparison.OrdinalIgnoreCase) == true)
            ),
            ClearedToLand => PR.Fail("CL does not accept a runway argument; assign runway first with a pattern entry command"),
            LandAndHoldShort when arg is not null => PR.Ok(new LandAndHoldShortCommand(arg.Trim().ToUpperInvariant())),
            CancelLandingClearance when arg is null => PR.Ok(new CancelLandingClearanceCommand()),
            Sequence => ParseSequence(arg),
            // Pattern
            EnterLeftDownwind => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterLeftDownwindCommand(rwy)),
            EnterRightDownwind => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterRightDownwindCommand(rwy)),
            EnterLeftCrosswind => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterLeftCrosswindCommand(rwy)),
            EnterRightCrosswind => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterRightCrosswindCommand(rwy)),
            EnterLeftBase => DepartureCommandParser.ParsePatternBaseEntry(arg),
            EnterRightBase => DepartureCommandParser.ParsePatternBaseEntry(arg, right: true),
            EnterFinal => DepartureCommandParser.ParsePatternRunwayEntry(arg, rwy => new EnterFinalCommand(rwy)),
            MakeLeftTraffic => PR.Ok(new MakeLeftTrafficCommand(arg?.ToUpperInvariant())),
            MakeRightTraffic => PR.Ok(new MakeRightTrafficCommand(arg?.ToUpperInvariant())),
            TurnCrosswind when arg is null => PR.Ok(new TurnCrosswindCommand()),
            TurnDownwind when arg is null => PR.Ok(new TurnDownwindCommand()),
            TurnBase when arg is null => PR.Ok(new TurnBaseCommand()),
            ExtendDownwind when arg is null => PR.Ok(new ExtendDownwindCommand()),
            MakeShortApproach when arg is null => PR.Ok(new MakeShortApproachCommand()),
            MakeNormalApproach when arg is null => PR.Ok(new MakeNormalApproachCommand()),
            Cancel270 when arg is null => PR.Ok(new Cancel270Command()),
            PatternSize when arg is not null => PR.Ok(ParsePatternSize(arg)),
            MakeLeftSTurns => PR.Ok(ParseSTurns(arg, TurnDirection.Left)),
            MakeRightSTurns => PR.Ok(ParseSTurns(arg, TurnDirection.Right)),
            Plan270 when arg is null => PR.Ok(new Plan270Command()),
            MakeLeft360 when arg is null => PR.Ok(new MakeLeft360Command()),
            MakeRight360 when arg is null => PR.Ok(new MakeRight360Command()),
            MakeLeft270 when arg is null => PR.Ok(new MakeLeft270Command()),
            MakeRight270 when arg is null => PR.Ok(new MakeRight270Command()),
            CircleAirport when arg is null => PR.Ok(new CircleAirportCommand()),
            // Option / special ops
            TouchAndGo => PR.Ok(new TouchAndGoCommand(arg?.Trim().ToUpperInvariant())),
            StopAndGo when arg is null => PR.Ok(new StopAndGoCommand()),
            LowApproach when arg is null => PR.Ok(new LowApproachCommand()),
            ClearedForOption when arg is null => PR.Ok(new ClearedForOptionCommand()),
            // Hold
            HoldPresentPosition360Left when arg is null => PR.Ok(new HoldPresentPosition360Command(TurnDirection.Left)),
            HoldPresentPosition360Right when arg is null => PR.Ok(new HoldPresentPosition360Command(TurnDirection.Right)),
            HoldPresentPositionHover when arg is null => PR.Ok(new HoldPresentPositionHoverCommand()),
            HoldAtFixLeft => ParseHoldAtFix(arg, TurnDirection.Left),
            HoldAtFixRight => ParseHoldAtFix(arg, TurnDirection.Right),
            HoldAtFixHover => ParseHoldAtFixHover(arg),
            // Helicopter
            AirTaxi => PR.Ok(new AirTaxiCommand(arg?.Trim().ToUpperInvariant())),
            Land when arg is not null => PR.Ok(ParseLand(arg)),
            ClearedTakeoffPresent when arg is null => PR.Ok(new ClearedTakeoffPresentCommand()),
            // Ground — HOLD is overloaded: bare = HoldPosition, with args = HoldingPattern
            Pushback => GroundCommandParser.ParsePushback(arg),
            Taxi => GroundCommandParser.ParseTaxi(arg),
            AssignRunway => GroundCommandParser.ParseRwyTaxi(arg),
            HoldPosition when arg is null => PR.Ok(new HoldPositionCommand()),
            HoldPosition when arg is not null => ApproachCommandParser.ParseHold(arg),
            Resume when arg is null => PR.Ok(new ResumeCommand()),
            CrossRunway => GroundCommandParser.ParseCross(arg),
            HoldShort => GroundCommandParser.ParseHoldShort(arg),
            Follow => GroundCommandParser.ParseFollow(arg),
            GiveWay => GroundCommandParser.ParseGiveWay(arg),
            TaxiAll => GroundCommandParser.ParseTaxiAll(arg),
            BreakConflict when arg is null => PR.Ok(new BreakConflictCommand()),
            CanonicalCommandType.Go when arg is null => PR.Ok(new GoCommand()),
            ExitLeft when arg is null or "NODEL" => PR.Ok(new ExitLeftCommand(arg?.Equals("NODEL", StringComparison.OrdinalIgnoreCase) == true)),
            ExitLeft => PR.Ok(new ExitLeftCommand(false, arg.Trim().ToUpperInvariant())),
            ExitRight when arg is null or "NODEL" => PR.Ok(new ExitRightCommand(arg?.Equals("NODEL", StringComparison.OrdinalIgnoreCase) == true)),
            ExitRight => PR.Ok(new ExitRightCommand(false, arg.Trim().ToUpperInvariant())),
            ExitTaxiway when arg is not null => PR.Ok(GroundCommandParser.ParseExitTaxiway(arg)),
            // Approach
            ExpectApproach => ParseExpectApproach(arg),
            ClearedApproach => ApproachCommandParser.ParseCapp(arg, false),
            ClearedApproachForce => ApproachCommandParser.ParseCapp(arg, true),
            ClearedApproachStraightIn => ApproachCommandParser.ParseCappSi(arg),
            JoinApproach => ApproachCommandParser.ParseJapp(arg, false),
            JoinApproachForce => ApproachCommandParser.ParseJapp(arg, true),
            JoinApproachStraightIn => ApproachCommandParser.ParseJappSi(arg),
            JoinFinalApproachCourse => ApproachCommandParser.ParseJfac(arg),
            JoinStar => ApproachCommandParser.ParseJarr(arg),
            JoinAirway => ApproachCommandParser.ParseJawy(arg),
            JoinRadialOutbound => ApproachCommandParser.ParseJrado(arg),
            JoinRadialInbound => ApproachCommandParser.ParseJradi(arg),
            HoldingPattern => ApproachCommandParser.ParseHold(arg),
            PositionTurnAltitudeClearance => ApproachCommandParser.ParsePtac(arg),
            ClimbVia => ApproachCommandParser.ParseCvia(arg),
            DescendVia => ApproachCommandParser.ParseDvia(arg),
            CrossFix => ApproachCommandParser.ParseCfix(arg),
            DepartFix => ApproachCommandParser.ParseDepart(arg),
            ListApproaches => PR.Ok(ApproachCommandParser.ParseApps(arg)),
            ClearedVisualApproach => ApproachCommandParser.ParseCva(arg),
            ReportFieldInSight when arg is null => PR.Ok(new ReportFieldInSightCommand()),
            ReportTrafficInSight => PR.Ok(new ReportTrafficInSightCommand(arg?.Trim().ToUpperInvariant())),
            // Track operations
            SetActivePosition => ParseTcpArg(arg, tcp => new SetActivePositionCommand(tcp)),
            TrackAircraft => PR.Ok(new TrackAircraftCommand(arg?.Trim().ToUpperInvariant())),
            DropTrack when arg is null => PR.Ok(new DropTrackCommand()),
            InitiateHandoff when arg is null => PR.Ok(new InitiateHandoffCommand(null)),
            InitiateHandoff => ParseTcpArg(arg, tcp => new InitiateHandoffCommand(tcp)),
            ForceHandoff => ParseTcpArg(arg, tcp => new ForceHandoffCommand(tcp)),
            AcceptHandoff => PR.Ok(new AcceptHandoffCommand(arg?.Trim().ToUpperInvariant())),
            CancelHandoff when arg is null => PR.Ok(new CancelHandoffCommand()),
            AcceptAllHandoffs when arg is null => PR.Ok(new AcceptAllHandoffsCommand()),
            InitiateHandoffAll => ParseTcpArg(arg, tcp => new InitiateHandoffAllCommand(tcp)),
            PointOut => PR.Ok(new PointOutCommand(arg?.Trim().ToUpperInvariant())),
            Acknowledge when arg is null => PR.Ok(new AcknowledgeCommand()),
            // Data operations
            Annotate when arg is not null => ParseStripAnnotate(arg),
            StripPush when arg is not null => PR.Ok(new StripPushCommand(arg.Trim().ToUpperInvariant())),
            Scratchpad1 when arg is null => PR.Ok(new Scratchpad1Command("")),
            Scratchpad1 => PR.Ok(new Scratchpad1Command(arg!.Trim().ToUpperInvariant())),
            Scratchpad2 when arg is null => PR.Ok(new Scratchpad2Command("")),
            Scratchpad2 => PR.Ok(new Scratchpad2Command(arg!.Trim().ToUpperInvariant())),
            TemporaryAltitude when arg is null => PR.Ok(new TemporaryAltitudeCommand(0)),
            TemporaryAltitude when int.TryParse(arg, out var taVal) && taVal == 0 => PR.Ok(new TemporaryAltitudeCommand(0)),
            TemporaryAltitude => ParseAltitudeHundreds(arg, h => new TemporaryAltitudeCommand(h)),
            Cruise => ParseAltitudeHundreds(arg, h => new CruiseCommand(h)),
            OnHandoff when arg is null => PR.Ok(new OnHandoffCommand()),
            // Broadcast
            Say when arg is not null => PR.Ok(new SayCommand(arg)),
            SaySpeed when arg is null => PR.Ok(new SaySpeedCommand()),
            SayMach when arg is null => PR.Ok(new SayMachCommand()),
            SayExpectedApproach when arg is null => PR.Ok(new SayExpectedApproachCommand()),
            // Queue
            DeleteQueuedCommands when arg is null => PR.Ok(new DeleteQueuedCommand()),
            DeleteQueuedCommands => int.TryParse(arg, out var delAtBlock)
                ? PR.Ok(new DeleteQueuedCommand(delAtBlock))
                : PR.Fail($"invalid block number '{arg}'"),
            ShowQueuedCommands when arg is null => PR.Ok(new ShowQueuedCommand()),
            // Coordination
            CoordinationRelease => PR.Ok(new CoordinationReleaseCommand(arg?.Trim().ToUpperInvariant())),
            CoordinationHold => PR.Ok(ParseHoldArgs(arg)),
            CoordinationRecall => PR.Ok(new CoordinationRecallCommand(arg?.Trim().ToUpperInvariant())),
            CoordinationAcknowledge => PR.Ok(new CoordinationAcknowledgeCommand(arg?.Trim().ToUpperInvariant())),
            CoordinationAutoAck => ParseOptionalListId(arg, listId => new CoordinationAutoAckCommand(listId)),
            // Consolidation
            Consolidate => ParseConsolidate(arg, false),
            ConsolidateFull => ParseConsolidate(arg, true),
            Deconsolidate => ParseDeconsolidate(arg),
            // Flight plan
            ChangeDestination => ParseChangeDestination(arg),
            CreateFlightPlan => ParseCreateFlightPlan(arg, "IFR"),
            CreateVfrFlightPlan => ParseCreateFlightPlan(arg, "VFR"),
            SetRemarks when arg is not null => PR.Ok(new SetRemarksCommand(arg)),
            // Verbs with arg-required guards: return fail when arg missing (invalid usage)
            Mach
            or WarpGround
            or LandAndHoldShort
            or PatternSize
            or Land
            or ExitTaxiway
            or Annotate
            or StripPush
            or Say
            or SetRemarks
            or Add when arg is null => PR.Fail($"{type} requires an argument"),
            // Registry-known but not yet handled
            _ => PR.Ok(new UnsupportedCommand(rawInput)),
        };
    }

    /// <summary>
    /// Concatenation fallback: tries prefix-matching registry aliases when verb+digits
    /// are written without a space (e.g. FH270, CM240, SQ1234).
    /// </summary>
    private static PR TryConcatenation(string upperInput)
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

            return ParseByType(type, remainder, null, upperInput);
        }

        return PR.Fail($"unknown command '{upperInput}'");
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

    private static PR ParseOptionalListId(string? arg, Func<string, ParsedCommand> factory)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("requires a list ID argument");
        }

        return PR.Ok(factory(arg.Trim().ToUpperInvariant()));
    }

    private static PR ParseTcpArg(string? arg, Func<string, ParsedCommand> factory)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("requires a TCP code argument");
        }

        return PR.Ok(factory(arg.Trim().ToUpperInvariant()));
    }

    private static PR ParseAltitudeHundreds(string? arg, Func<int, ParsedCommand> factory)
    {
        if (arg is null)
        {
            return PR.Fail("requires an altitude argument");
        }

        // Strip ERAM-style P prefix (e.g., P110 → 110)
        var cleaned = arg;
        if (cleaned.Length > 1 && cleaned[0] is 'P' or 'p' && char.IsDigit(cleaned[1]))
        {
            cleaned = cleaned[1..];
        }

        if (!int.TryParse(cleaned, out var value) || value <= 0)
        {
            return PR.Fail($"invalid altitude '{arg}'");
        }

        return PR.Ok(factory(value));
    }

    private static PR ParseDirectTo(string? arg, string? aircraftRoute)
    {
        if (arg is null)
        {
            return PR.Fail("DCT requires at least one fix name");
        }

        var fixNames = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fixNames.Length == 0)
        {
            return PR.Fail("DCT requires at least one fix name");
        }

        var navDb = NavigationDatabase.Instance;
        var resolved = new List<ResolvedFix>();
        var skipped = new List<string>();
        foreach (var name in fixNames)
        {
            var pos = navDb.GetFixPosition(name);
            if (pos is not null)
            {
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), pos.Value.Lat, pos.Value.Lon));
                continue;
            }

            // Try FRD (Fix-Radial-Distance) resolution: e.g., "JFK090020"
            var frd = FrdResolver.Resolve(name, navDb);
            if (frd is not null)
            {
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), frd.Latitude, frd.Longitude));
                continue;
            }

            skipped.Add(name.ToUpperInvariant());
        }

        if (resolved.Count == 0)
        {
            return PR.Fail($"no fixes found (tried: {string.Join(", ", fixNames.Select(n => n.ToUpperInvariant()))})");
        }

        // If the last fix is in the aircraft's route, append remaining route fixes
        if (aircraftRoute is not null)
        {
            RouteChainer.AppendRouteRemainder(resolved, aircraftRoute);
        }

        return PR.Ok(new DirectToCommand(resolved, skipped));
    }

    private static PR ParseAppendDirectTo(string? arg, string? aircraftRoute)
    {
        if (arg is null)
        {
            return PR.Fail("ADCT requires at least one fix name");
        }

        var fixNames = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fixNames.Length == 0)
        {
            return PR.Fail("ADCT requires at least one fix name");
        }

        var navDb = NavigationDatabase.Instance;
        var resolved = new List<ResolvedFix>();
        var skipped = new List<string>();
        foreach (var name in fixNames)
        {
            var pos = navDb.GetFixPosition(name);
            if (pos is not null)
            {
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), pos.Value.Lat, pos.Value.Lon));
                continue;
            }

            var frd = FrdResolver.Resolve(name, navDb);
            if (frd is not null)
            {
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), frd.Latitude, frd.Longitude));
                continue;
            }

            skipped.Add(name.ToUpperInvariant());
        }

        if (resolved.Count == 0)
        {
            return PR.Fail($"no fixes found (tried: {string.Join(", ", fixNames.Select(n => n.ToUpperInvariant()))})");
        }

        if (aircraftRoute is not null)
        {
            RouteChainer.AppendRouteRemainder(resolved, aircraftRoute);
        }

        return PR.Ok(new AppendDirectToCommand(resolved, skipped));
    }

    private static PR ParseAppendForceDirectTo(string? arg, string? aircraftRoute)
    {
        if (arg is null)
        {
            return PR.Fail("AFDCT requires at least one fix name");
        }

        var fixNames = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fixNames.Length == 0)
        {
            return PR.Fail("AFDCT requires at least one fix name");
        }

        var navDb = NavigationDatabase.Instance;
        var resolved = new List<ResolvedFix>();
        var skipped = new List<string>();
        foreach (var name in fixNames)
        {
            var pos = navDb.GetFixPosition(name);
            if (pos is not null)
            {
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), pos.Value.Lat, pos.Value.Lon));
                continue;
            }

            var frd = FrdResolver.Resolve(name, navDb);
            if (frd is not null)
            {
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), frd.Latitude, frd.Longitude));
                continue;
            }

            skipped.Add(name.ToUpperInvariant());
        }

        if (resolved.Count == 0)
        {
            return PR.Fail($"no fixes found (tried: {string.Join(", ", fixNames.Select(n => n.ToUpperInvariant()))})");
        }

        if (aircraftRoute is not null)
        {
            RouteChainer.AppendRouteRemainder(resolved, aircraftRoute);
        }

        return PR.Ok(new AppendForceDirectToCommand(resolved, skipped));
    }

    private static PR ParseForceDirectTo(string? arg, string? aircraftRoute)
    {
        if (arg is null)
        {
            return PR.Fail("FDCT requires at least one fix name");
        }

        var fixNames = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fixNames.Length == 0)
        {
            return PR.Fail("FDCT requires at least one fix name");
        }

        var navDb = NavigationDatabase.Instance;
        var resolved = new List<ResolvedFix>();
        var skipped = new List<string>();
        var altConstraints = new Dictionary<int, ConstrainedFixAltitude>();
        bool hasConstraints = false;

        foreach (var token in fixNames)
        {
            // Check for inline altitude constraint: FIXNAME/altToken
            string name;
            string? altToken = null;
            int slashIdx = token.IndexOf('/');
            if (slashIdx > 0 && slashIdx < token.Length - 1)
            {
                name = token[..slashIdx];
                altToken = token[(slashIdx + 1)..];
            }
            else
            {
                name = token;
            }

            var pos = navDb.GetFixPosition(name);
            if (pos is not null)
            {
                int fixIndex = resolved.Count;
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), pos.Value.Lat, pos.Value.Lon));

                if (altToken is not null)
                {
                    var (altitude, altType) = ApproachCommandParser.ParseCfixAltitudeToken(altToken);
                    if (altitude is not null)
                    {
                        altConstraints[fixIndex] = new ConstrainedFixAltitude(altitude.Value, altType);
                        hasConstraints = true;
                    }
                }

                continue;
            }

            var frd = FrdResolver.Resolve(name, navDb);
            if (frd is not null)
            {
                int fixIndex = resolved.Count;
                resolved.Add(new ResolvedFix(name.ToUpperInvariant(), frd.Latitude, frd.Longitude));

                if (altToken is not null)
                {
                    var (altitude, altType) = ApproachCommandParser.ParseCfixAltitudeToken(altToken);
                    if (altitude is not null)
                    {
                        altConstraints[fixIndex] = new ConstrainedFixAltitude(altitude.Value, altType);
                        hasConstraints = true;
                    }
                }

                continue;
            }

            skipped.Add(name.ToUpperInvariant());
        }

        if (resolved.Count == 0)
        {
            return PR.Fail($"no fixes found (tried: {string.Join(", ", fixNames.Select(n => n.ToUpperInvariant()))})");
        }

        if (hasConstraints)
        {
            return PR.Ok(new ConstrainedForceDirectToCommand(resolved, altConstraints, null, skipped));
        }

        if (aircraftRoute is not null)
        {
            RouteChainer.AppendRouteRemainder(resolved, aircraftRoute);
        }

        return PR.Ok(new ForceDirectToCommand(resolved, skipped));
    }

    private static PR ParseExpectApproach(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("requires an approach ID");
        }

        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return PR.Fail("requires an approach ID");
        }

        var approachId = tokens[0].ToUpperInvariant();
        var airportCode = tokens.Length > 1 ? tokens[1].ToUpperInvariant() : null;
        return PR.Ok(new ExpectApproachCommand(approachId, airportCode));
    }

    /// <summary>
    /// Parses GA (no args), GA MRT/MLT (pattern direction),
    /// or GA hdg alt (2 args). Heading can be a number (1-360)
    /// or RH (runway heading). Altitude uses AltitudeResolver.
    /// </summary>
    private static PR ParseGoAround(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new GoAroundCommand());
        }

        if (arg.Equals("MRT", StringComparison.OrdinalIgnoreCase))
        {
            return PR.Ok(new GoAroundCommand(TrafficPattern: PatternDirection.Right));
        }

        if (arg.Equals("MLT", StringComparison.OrdinalIgnoreCase))
        {
            return PR.Ok(new GoAroundCommand(TrafficPattern: PatternDirection.Left));
        }

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return PR.Fail($"invalid go-around args '{arg}' (expected heading altitude or MRT/MLT)");
        }

        int? heading = null;
        if (!parts[0].Equals("RH", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(parts[0], out var h) || h < 1 || h > 360)
            {
                return PR.Fail($"invalid go-around heading '{parts[0]}' (expected 1-360 or RH)");
            }

            heading = h;
        }

        int? altitude = AltitudeResolver.Resolve(parts[1]);
        if (altitude is null)
        {
            return PR.Fail($"invalid go-around altitude '{parts[1]}'");
        }

        return PR.Ok(new GoAroundCommand(heading, altitude));
    }

    private static PR ParseHoldAtFix(string? arg, TurnDirection direction)
    {
        if (arg is null)
        {
            return PR.Fail("hold at fix requires a fix name");
        }

        var navDb = NavigationDatabase.Instance;
        var fixName = arg.Trim().ToUpperInvariant();
        var pos = navDb.GetFixPosition(fixName);
        if (pos is null)
        {
            var frd = FrdResolver.Resolve(fixName, navDb);
            if (frd is null)
            {
                return PR.Fail($"fix '{fixName}' not found");
            }
            return PR.Ok(new HoldAtFixOrbitCommand(fixName, frd.Latitude, frd.Longitude, direction));
        }

        return PR.Ok(new HoldAtFixOrbitCommand(fixName, pos.Value.Lat, pos.Value.Lon, direction));
    }

    private static PR ParseHoldAtFixHover(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("hold at fix hover requires a fix name");
        }

        var navDb = NavigationDatabase.Instance;
        var fixName = arg.Trim().ToUpperInvariant();
        var pos = navDb.GetFixPosition(fixName);
        if (pos is null)
        {
            var frd = FrdResolver.Resolve(fixName, navDb);
            if (frd is null)
            {
                return PR.Fail($"fix '{fixName}' not found");
            }
            return PR.Ok(new HoldAtFixHoverCommand(fixName, frd.Latitude, frd.Longitude));
        }

        return PR.Ok(new HoldAtFixHoverCommand(fixName, pos.Value.Lat, pos.Value.Lon));
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

    private static PR ParseSequence(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("sequence requires a number");
        }

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var number) || number < 1)
        {
            return PR.Fail($"invalid sequence number '{arg}'");
        }

        var follow = parts.Length > 1 ? parts[1].Trim().ToUpperInvariant() : null;
        return PR.Ok(new SequenceCommand(number, follow));
    }

    private static PR ParseWaitSeconds(string? arg)
    {
        if (arg is null || !int.TryParse(arg, out var seconds) || seconds < 0)
        {
            return PR.Fail($"invalid wait seconds '{arg}'");
        }

        return PR.Ok(new WaitCommand(seconds));
    }

    private static PR ParseWaitDistance(string? arg)
    {
        if (arg is null || !double.TryParse(arg, out var distNm) || distNm <= 0)
        {
            return PR.Fail($"invalid wait distance '{arg}'");
        }

        return PR.Ok(new WaitDistanceCommand(distNm));
    }

    private static PR ParseHeading(string? arg, Func<int, ParsedCommand> factory)
    {
        if (arg is null || !int.TryParse(arg, out var heading))
        {
            return PR.Fail($"invalid heading '{arg}' (expected 001-360)");
        }

        if (heading < 1 || heading > 360)
        {
            return PR.Fail($"invalid heading '{arg}' (expected 001-360)");
        }

        return PR.Ok(factory(heading));
    }

    /// <summary>
    /// Parses "T {degrees} {direction}" — ATCTrainer turn command.
    /// Direction: L, LEFT, R, RIGHT.
    /// </summary>
    private static PR ParseTurnWithDirection(string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var degrees))
        {
            return PR.Fail($"invalid turn format '{arg}' (expected degrees direction)");
        }

        if (degrees < 1 || degrees > 359)
        {
            return PR.Fail($"invalid turn degrees '{parts[0]}' (expected 1-359)");
        }

        return parts[1].ToUpperInvariant() switch
        {
            "L" or "LEFT" => PR.Ok(new LeftTurnCommand(degrees)),
            "R" or "RIGHT" => PR.Ok(new RightTurnCommand(degrees)),
            _ => PR.Fail($"invalid turn direction '{parts[1]}' (expected L/R)"),
        };
    }

    private static PR ParseDegrees(string? arg, Func<int, ParsedCommand> factory)
    {
        if (arg is null || !int.TryParse(arg, out var degrees))
        {
            return PR.Fail($"invalid turn degrees '{arg}' (expected 1-359)");
        }

        if (degrees < 1 || degrees > 359)
        {
            return PR.Fail($"invalid turn degrees '{arg}' (expected 1-359)");
        }

        return PR.Ok(factory(degrees));
    }

    internal static PR ParseAltitude(string? arg, Func<int, ParsedCommand> factory)
    {
        int? altitude = AltitudeResolver.Resolve(arg);
        return altitude is null ? PR.Fail($"invalid altitude '{arg}'") : PR.Ok(factory(altitude.Value));
    }

    private static PR ParseWarp(string? arg)
    {
        // WARP <FRD|FIX> <heading> <altitude> <speed>
        if (arg is null)
        {
            return PR.Fail("WARP requires fix heading altitude speed");
        }

        var navDb = NavigationDatabase.Instance;

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return PR.Fail($"invalid warp format '{arg}' (expected fix heading altitude speed)");
        }

        var posToken = parts[0].ToUpperInvariant();

        // Try direct fix lookup first
        double lat,
            lon;
        var fixPos = navDb.GetFixPosition(posToken);
        if (fixPos is not null)
        {
            lat = fixPos.Value.Lat;
            lon = fixPos.Value.Lon;
        }
        else
        {
            // Try FRD resolution
            var frd = FrdResolver.Resolve(posToken, navDb);
            if (frd is null)
            {
                return PR.Fail($"fix '{posToken}' not found");
            }

            lat = frd.Latitude;
            lon = frd.Longitude;
        }

        if (!int.TryParse(parts[1], out var heading) || heading < 1 || heading > 360)
        {
            return PR.Fail($"invalid warp heading '{parts[1]}'");
        }

        int? altitude = AltitudeResolver.Resolve(parts[2]);
        if (altitude is null)
        {
            return PR.Fail($"invalid warp altitude '{parts[2]}'");
        }

        if (!int.TryParse(parts[3], out var speed) || speed <= 0)
        {
            return PR.Fail($"invalid warp speed '{parts[3]}'");
        }

        return PR.Ok(new WarpCommand(posToken, lat, lon, heading, altitude.Value, speed));
    }

    private static PR ParseWarpGround(string arg)
    {
        // WARPG <nodeRef>    — e.g., WARPG #42
        // WARPG @<parking>   — e.g., WARPG @B12
        // WARPG <tw1> <tw2>  — e.g., WARPG C B
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && TaxiPathfinder.IsNodeReference(parts[0]))
        {
            return PR.Ok(new WarpGroundCommand("", "", TaxiPathfinder.ParseNodeId(parts[0])));
        }

        if (parts.Length == 1 && parts[0].StartsWith('@') && parts[0].Length > 1)
        {
            return PR.Ok(new WarpGroundCommand("", "", ParkingName: parts[0][1..]));
        }

        if (parts.Length != 2)
        {
            return PR.Fail($"invalid WARPG format '{arg}' (expected nodeRef, @parking, or tw1 tw2)");
        }

        return PR.Ok(new WarpGroundCommand(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant()));
    }

    private static PR ParseForceSpeed(string? arg)
    {
        if (arg is null || !int.TryParse(arg, out var speed))
        {
            return PR.Fail($"invalid force speed '{arg}'");
        }

        return PR.Ok(new ForceSpeedCommand(speed));
    }

    private static PR ParseSpeed(string? arg)
    {
        if (arg is null)
        {
            return PR.Fail("speed requires an argument");
        }

        // Check for trailing +/- modifier
        if (arg.EndsWith('+') && int.TryParse(arg[..^1], out var floorSpeed))
        {
            return PR.Ok(new SpeedCommand(floorSpeed, SpeedModifier.Floor));
        }

        if (arg.EndsWith('-') && int.TryParse(arg[..^1], out var ceilSpeed))
        {
            return PR.Ok(new SpeedCommand(ceilSpeed, SpeedModifier.Ceiling));
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
            return PR.Fail($"invalid speed '{arg}'");
        }

        if (speedParts.Length == 1)
        {
            return PR.Ok(new SpeedCommand(speed));
        }

        // Second token must be a single word (no spaces) — if it contains spaces,
        // the extra tokens belong to a subsequent command, not to SPD.
        if (speedParts[1].Contains(' '))
        {
            return PR.Fail($"invalid speed argument '{arg}'");
        }

        // Second token must not be a known command verb — if it is, it belongs to the next command.
        var secondUpper = speedParts[1].ToUpperInvariant();
        if (CommandRegistry.AliasToCanonicType.ContainsKey(secondUpper))
        {
            return PR.Fail($"'{secondUpper}' is a command, not a waypoint");
        }

        // Single termination waypoint — ignored for now (not yet modeled),
        // but parse successfully so scenario validation passes
        return PR.Ok(new SpeedCommand(speed));
    }

    private static ParsedCommand ParseExpedite(string? arg)
    {
        if (arg is null)
        {
            return new ExpediteCommand();
        }

        int? altitude = AltitudeResolver.Resolve(arg);
        return altitude is not null ? new ExpediteCommand(altitude.Value) : new ExpediteCommand();
    }

    private static PR ParseMach(string arg)
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
                return PR.Ok(new MachCommand(mach));
            }
        }

        return PR.Fail($"invalid Mach number '{arg}'");
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
            _lastBlockFailure = "ATFN requires distance and a command";
            return null;
        }

        if (!double.TryParse(parts[1], out var distNm) || distNm <= 0)
        {
            _lastBlockFailure = $"invalid ATFN distance '{parts[1]}'";
            return null;
        }

        var remainder = string.Join(' ', parts.Skip(2));
        return (new DistanceFinalCondition(distNm), remainder);
    }

    private static PR ParseInt(string? arg, Func<int, ParsedCommand> factory)
    {
        if (arg is null || !int.TryParse(arg, out var value))
        {
            return PR.Fail($"invalid number '{arg}'");
        }

        return PR.Ok(factory(value));
    }

    private static PR ParseConsolidate(string? arg, bool full)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("consolidate requires two TCP codes");
        }

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return PR.Fail("consolidate requires exactly two TCP codes");
        }

        return PR.Ok(new ConsolidateCommand(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant(), full));
    }

    private static PR ParseDeconsolidate(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("deconsolidate requires a TCP code");
        }

        return PR.Ok(new DeconsolidateCommand(arg.Trim().ToUpperInvariant()));
    }

    private static PR ParseChangeDestination(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("change destination requires an airport code");
        }

        return PR.Ok(new ChangeDestinationCommand(arg.Trim().ToUpperInvariant()));
    }

    private static PR ParseCreateFlightPlan(string? arg, string flightRules)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("create flight plan requires type altitude route");
        }

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return PR.Fail("create flight plan requires type altitude route");
        }

        var aircraftType = parts[0].ToUpperInvariant();

        if (!int.TryParse(parts[1], out var altRaw))
        {
            return PR.Fail($"invalid altitude '{parts[1]}'");
        }

        // IFR altitude in hundreds (≤999 → multiply by 100), VFR is absolute
        int cruiseAltitude = flightRules == "IFR" && altRaw <= 999 ? altRaw * 100 : altRaw;

        var route = string.Join(" ", parts.Skip(2).Select(p => p.ToUpperInvariant()));
        return PR.Ok(new CreateFlightPlanCommand(flightRules, aircraftType, cruiseAltitude, route));
    }

    private static PR ParseStripAnnotate(string arg)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var box))
        {
            return PR.Fail($"invalid strip annotation box '{arg}'");
        }

        // Accept 1-9 directly, or 10-18 as aliases for 1-9
        if (box >= 10 && box <= 18)
        {
            box -= 9;
        }

        if (box < 1 || box > 9)
        {
            return PR.Fail($"invalid strip annotation box {box} (expected 1-9)");
        }

        var text = parts.Length > 1 ? parts[1].Trim() : null;
        return PR.Ok(new StripAnnotateCommand(box, text));
    }

    private static PR ParseSquawkOrReset(string? arg)
    {
        if (arg is null)
        {
            return PR.Ok(new SquawkResetCommand());
        }

        if (!uint.TryParse(arg, out var code))
        {
            return PR.Fail($"invalid squawk code '{arg}'");
        }

        if (code > 7777)
        {
            return PR.Fail($"invalid squawk code '{arg}' (max 7777)");
        }

        // Validate each digit is 0-7 (octal)
        var temp = code;
        for (int i = 0; i < 4; i++)
        {
            if (temp % 10 > 7)
            {
                return PR.Fail($"invalid squawk code '{arg}' (digits must be 0-7)");
            }

            temp /= 10;
        }

        return PR.Ok(new SquawkCommand(code));
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
