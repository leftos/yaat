using System.Text.RegularExpressions;

namespace Yaat.Sim.Commands;

public record ParsedInput(CanonicalCommandType Type, string? Argument);

public record CompoundParseResult(string CanonicalString);

public record ParseFailure(string Verb, string Reason);

public static class CommandSchemeParser
{
    private static readonly HashSet<string> PassthroughVerbs = new(StringComparer.OrdinalIgnoreCase) { "LV", "AT", "ATFN" };

    private static bool IsAltitudeArg(string arg)
    {
        if (int.TryParse(arg, out _))
        {
            return true;
        }

        // AGL: {letters}+{digits}
        var plusIndex = arg.IndexOf('+');
        if (plusIndex <= 0 || plusIndex == arg.Length - 1)
        {
            return false;
        }

        return int.TryParse(arg[(plusIndex + 1)..], out _);
    }

    public static CompoundParseResult? ParseCompound(string input, CommandScheme scheme)
    {
        return ParseCompound(input, scheme, out _);
    }

    public static CompoundParseResult? ParseCompound(string input, CommandScheme scheme, out ParseFailure? failure)
    {
        failure = null;
        var trimmed = ExpandMultiCommand(ExpandWait(ExpandSpeedUntil(input.Trim())));
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        bool isCompound = trimmed.Contains(';') || trimmed.Contains(',');
        var upper = trimmed.ToUpperInvariant();
        if (!isCompound)
        {
            isCompound = upper.StartsWith("LV ") || upper.StartsWith("AT ") || upper.StartsWith("ATFN ");

            // GIVEWAY/BEHIND/GW are compound only if they have 3+ tokens (condition form)
            if (!isCompound && (upper.StartsWith("GIVEWAY ") || upper.StartsWith("BEHIND ") || upper.StartsWith("GW ")))
            {
                var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                isCompound = tokens.Length >= 3;
            }
        }

        if (!isCompound)
        {
            // Single command
            var parsed = Parse(trimmed, scheme, out failure);
            if (parsed is null)
            {
                return null;
            }

            return new CompoundParseResult(ToCanonical(parsed.Type, parsed.Argument));
        }

        // Split by ';' for sequential blocks
        var blockStrings = trimmed.Split(';');
        var canonicalBlocks = new List<string>();

        foreach (var blockStr in blockStrings)
        {
            var block = blockStr.Trim();
            if (string.IsNullOrEmpty(block))
            {
                continue;
            }

            var canonicalBlock = ParseBlockToCanonical(block, scheme);
            if (canonicalBlock is null)
            {
                return null;
            }

            canonicalBlocks.Add(canonicalBlock);
        }

        if (canonicalBlocks.Count == 0)
        {
            return null;
        }

        return new CompoundParseResult(string.Join("; ", canonicalBlocks));
    }

    private static string? ParseBlockToCanonical(string block, CommandScheme scheme)
    {
        var parts = new List<string>();
        var remaining = block;

        // Check for LV or AT prefix
        var upper = remaining.ToUpperInvariant();
        if (upper.StartsWith("LV "))
        {
            var tokens = remaining.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                return null;
            }

            if (!IsAltitudeArg(tokens[1]))
            {
                return null;
            }

            parts.Add($"LV {tokens[1]}");
            remaining = tokens[2];
        }
        else if (upper.StartsWith("AT "))
        {
            var tokens = remaining.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                return null;
            }

            parts.Add($"AT {tokens[1].ToUpperInvariant()}");
            remaining = tokens.Length >= 3 ? tokens[2] : "";
        }
        else if (upper.StartsWith("ATFN "))
        {
            var tokens = remaining.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                return null;
            }

            if (!double.TryParse(tokens[1], out _))
            {
                return null;
            }

            parts.Add($"ATFN {tokens[1]}");
            remaining = tokens[2];
        }
        else if (upper.StartsWith("GIVEWAY ") || upper.StartsWith("BEHIND ") || upper.StartsWith("GW "))
        {
            var tokens = remaining.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                return null;
            }

            parts.Add($"GIVEWAY {tokens[1].ToUpperInvariant()}");
            remaining = tokens[2];
        }

        // Apply ExpandWait and ExpandSpeedUntil to the remainder after condition extraction
        var expandedRemainder = ExpandMultiCommand(ExpandWait(ExpandSpeedUntil(remaining)));
        if (expandedRemainder.Contains(';'))
        {
            // Expansion produced additional blocks — split and handle each
            var subBlocks = expandedRemainder.Split(';');
            var allCanonicalCommands = new List<string>();

            for (int i = 0; i < subBlocks.Length; i++)
            {
                var subBlock = subBlocks[i].Trim();
                if (string.IsNullOrEmpty(subBlock))
                {
                    continue;
                }

                if (i == 0)
                {
                    // First sub-block gets the condition prefix
                    var cmds = ParseCommandList(subBlock, scheme);
                    if (cmds is null)
                    {
                        return null;
                    }

                    if (parts.Count > 0)
                    {
                        allCanonicalCommands.Add($"{string.Join(" ", parts)} {cmds}");
                    }
                    else
                    {
                        allCanonicalCommands.Add(cmds);
                    }
                }
                else
                {
                    // Subsequent sub-blocks from expansion are standalone
                    var canonicalBlock = ParseBlockToCanonical(subBlock, scheme);
                    if (canonicalBlock is null)
                    {
                        return null;
                    }

                    allCanonicalCommands.Add(canonicalBlock);
                }
            }

            return string.Join("; ", allCanonicalCommands);
        }

        remaining = expandedRemainder;

        // Bare condition with no following command (e.g., "AT BRIXX")
        if (string.IsNullOrWhiteSpace(remaining) && parts.Count > 0)
        {
            return string.Join(" ", parts);
        }

        var commandResult = ParseCommandList(remaining, scheme);
        if (commandResult is null)
        {
            return null;
        }

        if (parts.Count > 0)
        {
            return $"{string.Join(" ", parts)} {commandResult}";
        }

        return commandResult;
    }

    private static string? ParseCommandList(string remaining, CommandScheme scheme)
    {
        // SAY/APREQ consume entire remainder as literal text — don't split on comma
        var upperCheck = remaining.TrimStart().ToUpperInvariant();
        if (upperCheck.StartsWith("SAY ") || upperCheck.StartsWith("APREQ"))
        {
            var parsed = Parse(remaining.Trim(), scheme);
            return parsed is not null ? ToCanonical(parsed.Type, parsed.Argument) : null;
        }

        // Split remaining by ',' for parallel commands
        var commandStrings = remaining.Split(',');
        var canonicalCommands = new List<string>();

        foreach (var cmdStr in commandStrings)
        {
            var cmd = cmdStr.Trim();
            if (string.IsNullOrEmpty(cmd))
            {
                continue;
            }

            var parsed = Parse(cmd, scheme);
            if (parsed is not null)
            {
                canonicalCommands.Add(ToCanonical(parsed.Type, parsed.Argument));
                continue;
            }

            // Try expanding concatenated commands: "FH 270 CM 5000" → "FH 270, CM 5000"
            var expanded = ExpandMultiCommand(cmd);
            if (expanded == cmd)
            {
                return null;
            }

            foreach (var subCmd in expanded.Split(','))
            {
                var subParsed = Parse(subCmd.Trim(), scheme);
                if (subParsed is null)
                {
                    return null;
                }

                canonicalCommands.Add(ToCanonical(subParsed.Type, subParsed.Argument));
            }
        }

        if (canonicalCommands.Count == 0)
        {
            return null;
        }

        return string.Join(", ", canonicalCommands);
    }

    public static ParsedInput? Parse(string input, CommandScheme scheme)
    {
        return Parse(input, scheme, out _);
    }

    public static ParsedInput? Parse(string input, CommandScheme scheme, out ParseFailure? failure)
    {
        failure = null;
        var trimmed = input.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        // Text-arg commands are always space-separated regardless of scheme mode.
        // Check longer prefixes first (HFIXL/HFIXR before HFIX).
        var textArgMatch = ParseTextArgCommand(trimmed, scheme);
        if (textArgMatch is not null)
        {
            return textArgMatch;
        }

        return ParseSpaceSeparated(trimmed, scheme, out failure);
    }

    public static string ToCanonical(CanonicalCommandType type, string? argument)
    {
        var canonical = CommandScheme.Default();
        if (!canonical.Patterns.TryGetValue(type, out var pattern))
        {
            return "";
        }

        if (argument is null)
        {
            return pattern.PrimaryVerb;
        }

        return $"{pattern.PrimaryVerb} {argument}";
    }

    private static readonly CanonicalCommandType[] TextArgCommandTypes =
    [
        CanonicalCommandType.HoldAtFixLeft,
        CanonicalCommandType.HoldAtFixRight,
        CanonicalCommandType.HoldAtFixHover,
        CanonicalCommandType.DirectTo,
        CanonicalCommandType.AppendDirectTo,
        CanonicalCommandType.ClearedForTakeoff,
        CanonicalCommandType.Say,
        CanonicalCommandType.CreateFlightPlan,
        CanonicalCommandType.CreateVfrFlightPlan,
        CanonicalCommandType.SetRemarks,
    ];

    private static ParsedInput? ParseTextArgCommand(string input, CommandScheme scheme)
    {
        // Handle CTOMRT/CTOMLT legacy merged forms
        if (input.StartsWith("CTOMRT", StringComparison.OrdinalIgnoreCase) && (input.Length == 6 || input[6] == ' '))
        {
            var suffix = input.Length > 7 ? " " + input[7..].Trim() : "";
            var arg = "MRT" + suffix;
            return new ParsedInput(CanonicalCommandType.ClearedForTakeoff, arg.Trim());
        }
        if (input.StartsWith("CTOMLT", StringComparison.OrdinalIgnoreCase) && (input.Length == 6 || input[6] == ' '))
        {
            var suffix = input.Length > 7 ? " " + input[7..].Trim() : "";
            var arg = "MLT" + suffix;
            return new ParsedInput(CanonicalCommandType.ClearedForTakeoff, arg.Trim());
        }

        // APREQ [text] → SAY APREQ [text]
        if (input.StartsWith("APREQ", StringComparison.OrdinalIgnoreCase) && (input.Length == 5 || input[5] == ' '))
        {
            var remainder = input.Length > 6 ? input[6..].Trim() : "";
            var sayArg = remainder.Length > 0 ? "APREQ " + remainder : "APREQ";
            return new ParsedInput(CanonicalCommandType.Say, sayArg);
        }

        // Build (alias, type) pairs from the scheme, longest alias first so
        // HFIXL matches before HFIX.
        var candidates = new List<(string Alias, CanonicalCommandType Type)>();
        foreach (var type in TextArgCommandTypes)
        {
            if (!scheme.Patterns.TryGetValue(type, out var pattern))
            {
                continue;
            }

            foreach (var alias in pattern.Aliases)
            {
                candidates.Add((alias, type));
            }
        }

        candidates.Sort((a, b) => b.Alias.Length.CompareTo(a.Alias.Length));

        foreach (var (alias, type) in candidates)
        {
            if (!input.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (input.Length == alias.Length)
            {
                // Bare command with no arg — return null so the normal
                // parser handles {arg?} commands (like CTO with no modifier)
                return null;
            }

            if (input[alias.Length] != ' ')
            {
                continue;
            }

            var arg = input[(alias.Length + 1)..].Trim();
            return arg.Length > 0 ? new ParsedInput(type, arg) : null;
        }

        return null;
    }

    private static bool MatchesAnyAlias(string token, CommandPattern pattern)
    {
        foreach (var alias in pattern.Aliases)
        {
            if (string.Equals(token, alias, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithAnyAlias(string input, CommandPattern pattern, out string matchedAlias)
    {
        foreach (var alias in pattern.Aliases)
        {
            if (input.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
            {
                matchedAlias = alias;
                return true;
            }
        }

        matchedAlias = "";
        return false;
    }

    private static bool IsConcatenationExcluded(CanonicalCommandType type)
    {
        return type
            is CanonicalCommandType.RelativeLeft
                or CanonicalCommandType.RelativeRight
                or CanonicalCommandType.Pause
                or CanonicalCommandType.Unpause
                or CanonicalCommandType.SimRate
                or CanonicalCommandType.Wait
                or CanonicalCommandType.WaitDistance
                or CanonicalCommandType.Add
                or CanonicalCommandType.DirectTo
                or CanonicalCommandType.AppendDirectTo
                or CanonicalCommandType.HoldAtFixLeft
                or CanonicalCommandType.HoldAtFixRight
                or CanonicalCommandType.HoldAtFixHover
                or CanonicalCommandType.SpawnNow
                or CanonicalCommandType.SpawnDelay
                or CanonicalCommandType.Taxi
                or CanonicalCommandType.CrossRunway
                or CanonicalCommandType.HoldShort
                or CanonicalCommandType.Follow
                or CanonicalCommandType.Say;
    }

    private static ParsedInput? ParseSpaceSeparated(string input, CommandScheme scheme)
    {
        return ParseSpaceSeparated(input, scheme, out _);
    }

    private static ParsedInput? ParseSpaceSeparated(string input, CommandScheme scheme, out ParseFailure? failure)
    {
        failure = null;
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0];
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

        // RWY {runway} [TAXI] {path} → rewrite to Taxi with RWY keyword
        if (string.Equals(verb, "RWY", StringComparison.OrdinalIgnoreCase) && arg is not null)
        {
            var rewritten = RewriteRwyToTaxiArg(arg);
            if (rewritten is not null)
            {
                return new ParsedInput(CanonicalCommandType.Taxi, rewritten);
            }
        }

        // Relative turns: T{digits}L / T{digits}R (hardcoded T prefix)
        if (verb.Length >= 3 && verb.StartsWith('T') && char.IsDigit(verb[1]) && verb[^1] is 'L' or 'R' && int.TryParse(verb[1..^1], out _))
        {
            var type = verb[^1] == 'L' ? CanonicalCommandType.RelativeLeft : CanonicalCommandType.RelativeRight;
            return new ParsedInput(type, verb[1..^1]);
        }

        string? verbMatchReason = null;
        foreach (var (type, pattern) in scheme.Patterns)
        {
            if (!MatchesAnyAlias(verb, pattern))
            {
                continue;
            }

            var argMode = CommandRegistry.Get(type)?.ArgMode ?? ArgMode.None;

            if (argMode == ArgMode.Required && arg is null)
            {
                verbMatchReason = "requires an argument";
                continue;
            }

            if (argMode == ArgMode.None && arg is not null)
            {
                verbMatchReason = "does not accept arguments";
                continue;
            }

            if (type == CanonicalCommandType.SpawnDelay)
            {
                var normalized = NormalizeDelayArg(arg);
                return normalized is not null ? new ParsedInput(type, normalized) : null;
            }

            return new ParsedInput(type, arg);
        }

        // Concatenation fallback: try prefix-matching aliases when verb+digits are
        // written without a space (e.g. FH270, CM240, H270, SQ1234).
        // Try longer aliases first to avoid matching "S" when "SQ" would work.
        var candidates = new List<(string Alias, CanonicalCommandType Type)>();
        foreach (var (type, pattern) in scheme.Patterns)
        {
            var concatArgMode = CommandRegistry.Get(type)?.ArgMode ?? ArgMode.None;
            if (concatArgMode == ArgMode.None)
            {
                continue;
            }

            if (IsConcatenationExcluded(type))
            {
                continue;
            }

            foreach (var alias in pattern.Aliases)
            {
                candidates.Add((alias, type));
            }
        }

        candidates.Sort((a, b) => b.Alias.Length.CompareTo(a.Alias.Length));

        foreach (var (alias, type) in candidates)
        {
            if (!input.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = input[alias.Length..];
            if (remainder.Length == 0)
            {
                continue;
            }

            bool isAltitudeCommand = type is CanonicalCommandType.ClimbMaintain or CanonicalCommandType.DescendMaintain;
            if (isAltitudeCommand ? !IsAltitudeArg(remainder) : !int.TryParse(remainder, out _))
            {
                continue;
            }

            return new ParsedInput(type, remainder);
        }

        if (verbMatchReason is not null)
        {
            failure = new ParseFailure(verb, verbMatchReason);
        }

        return null;
    }

    internal static string? RewriteRwyToTaxiArg(string arg)
    {
        var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return null;
        }

        // First token is the runway
        var runway = tokens[0].ToUpperInvariant();
        int startIdx = 1;

        // Skip optional TAXI keyword
        if (startIdx < tokens.Length && tokens[startIdx].Equals("TAXI", StringComparison.OrdinalIgnoreCase))
        {
            startIdx++;
        }

        if (startIdx >= tokens.Length)
        {
            return null;
        }

        // Remaining tokens are the path [HS ...]
        var remaining = string.Join(" ", tokens[startIdx..]);
        return $"{remaining} RWY {runway}";
    }

    /// <summary>
    /// Heading/altitude verbs that take exactly one token as argument.
    /// Used by ExpandMultiCommand to split e.g. "FH 270 CM 5000" → "FH 270, CM 5000".
    /// </summary>
    private static readonly HashSet<string> HeadingAltVerbs = new(StringComparer.OrdinalIgnoreCase) { "FH", "TL", "TR", "CM", "DM", "SPD" };

    /// <summary>
    /// Splits concatenated heading/altitude commands within a single command string.
    /// "FH 270 CM 5000" → "FH 270, CM 5000". Returns original string if not splittable.
    /// </summary>
    public static string ExpandMultiCommand(string input)
    {
        if (input.Contains(',') || input.Contains(';'))
        {
            return input;
        }

        var tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 4)
        {
            return input;
        }

        var verb1 = tokens[0].ToUpperInvariant();
        var verb2 = tokens[2].ToUpperInvariant();

        if (HeadingAltVerbs.Contains(verb1) && HeadingAltVerbs.Contains(verb2))
        {
            return $"{tokens[0]} {tokens[1]}, {tokens[2]} {tokens[3]}";
        }

        return input;
    }

    /// <summary>
    /// Expands "SPD X UNTIL Y" shorthand within each semicolon-separated block.
    /// Supports distance-based UNTIL (numeric Y → ATFN), fix-based UNTIL (alpha Y → AT),
    /// and ATCTrainer alias (SPD X FIXNAME → AT).
    /// </summary>
    public static string ExpandSpeedUntil(string input)
    {
        // Split by semicolons to process blocks independently
        var blocks = input.Split(';');
        var result = new List<string>();

        for (int i = 0; i < blocks.Length; i++)
        {
            var block = blocks[i].Trim();
            var upper = block.ToUpperInvariant();

            // Match "SPD X UNTIL Y" where Y is numeric (distance)
            var distMatch = Regex.Match(upper, @"^(SPD\s+\d+[+\-]?)\s+UNTIL\s+(\d+(?:\.\d+)?)$");
            if (distMatch.Success)
            {
                var spdPart = block[..distMatch.Groups[1].Length].Trim();
                var distPart = distMatch.Groups[2].Value;

                // Look at the next block for chaining
                if (i + 1 < blocks.Length)
                {
                    var nextBlock = blocks[i + 1].Trim();
                    var nextUpper = nextBlock.ToUpperInvariant();
                    var nextMatch = Regex.Match(nextUpper, @"^(SPD\s+\d+[+\-]?)\s+UNTIL\s+(\d+(?:\.\d+)?)$");
                    if (nextMatch.Success)
                    {
                        var nextSpdPart = blocks[i + 1].Trim()[..nextMatch.Groups[1].Length].Trim();
                        var nextDistPart = nextMatch.Groups[2].Value;
                        result.Add(spdPart);
                        result.Add($"ATFN {distPart} {nextSpdPart}");
                        result.Add($"ATFN {nextDistPart} RNS");
                        i++;
                        continue;
                    }
                }

                result.Add(spdPart);
                result.Add($"ATFN {distPart} RNS");
                continue;
            }

            // Match "SPD X UNTIL FIXNAME" where FIXNAME is 2-5 alpha chars (fix-based)
            var fixUntilMatch = Regex.Match(upper, @"^(SPD\s+\d+[+\-]?)\s+UNTIL\s+([A-Z]{2,5})$");
            if (fixUntilMatch.Success)
            {
                var spdPart = block[..fixUntilMatch.Groups[1].Length].Trim();
                var fixName = fixUntilMatch.Groups[2].Value;
                result.Add(spdPart);
                result.Add($"AT {fixName} RNS");
                continue;
            }

            // Match "SPD X FIXNAME" (ATCTrainer alias for SPD X UNTIL FIXNAME)
            var fixAliasMatch = Regex.Match(upper, @"^(SPD\s+\d+[+\-]?)\s+([A-Z]{2,5})$");
            if (fixAliasMatch.Success)
            {
                var spdPart = block[..fixAliasMatch.Groups[1].Length].Trim();
                var fixName = fixAliasMatch.Groups[2].Value;
                result.Add(spdPart);
                result.Add($"AT {fixName} RNS");
                continue;
            }

            result.Add(block);
        }

        return string.Join("; ", result);
    }

    /// <summary>
    /// Expands "WAIT N cmd" and "DELAY N cmd" patterns into "WAIT N; cmd".
    /// Handles chaining: "WAIT 5 WAIT 10 FH 270" → "WAIT 5; WAIT 10; FH 270".
    /// Also normalizes standalone DELAY N to WAIT N.
    /// </summary>
    public static string ExpandWait(string input)
    {
        var blocks = input.Split(';');
        var result = new List<string>();

        foreach (var rawBlock in blocks)
        {
            var block = rawBlock.Trim();
            if (string.IsNullOrEmpty(block))
            {
                continue;
            }

            ExpandWaitBlock(block, result);
        }

        return string.Join("; ", result);
    }

    private static void ExpandWaitBlock(string block, List<string> result)
    {
        var tokens = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return;
        }

        var upper0 = tokens[0].ToUpperInvariant();

        // Not a WAIT/DELAY block — pass through as-is
        if (upper0 is not ("WAIT" or "DELAY"))
        {
            // Scan interior tokens for WAIT/DELAY boundaries within a condition remainder
            // e.g., "AT PIECH WAIT 5 SPD 210" is handled at the block level by ParseBlock,
            // but "FH 270 WAIT 5 CM 2000" needs splitting here.
            // This is handled by Phase 4 (auto-split at verb boundaries), not here.
            result.Add(block);
            return;
        }

        // Need at least WAIT N
        if (tokens.Length < 2)
        {
            result.Add(block);
            return;
        }

        // WAIT FIXNAME ... → AT FIXNAME ... (fix name instead of numeric delay)
        if (!int.TryParse(tokens[1], out _))
        {
            var rewritten = "AT " + string.Join(" ", tokens[1..]);
            ExpandWaitBlock(rewritten, result);
            return;
        }

        // WAIT N (standalone) — normalize DELAY to WAIT
        if (tokens.Length == 2)
        {
            result.Add($"WAIT {tokens[1]}");
            return;
        }

        // WAIT N followed by more tokens — split at boundary
        result.Add($"WAIT {tokens[1]}");

        // Remainder after WAIT N — may itself start with WAIT/DELAY
        var remainder = string.Join(" ", tokens[2..]);
        ExpandWaitBlock(remainder, result);
    }

    private static string? NormalizeDelayArg(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        if (int.TryParse(arg, out var secs) && secs >= 0)
        {
            return secs.ToString();
        }

        var colonIdx = arg.IndexOf(':');
        if (
            colonIdx > 0
            && colonIdx < arg.Length - 1
            && int.TryParse(arg[..colonIdx], out var minutes)
            && int.TryParse(arg[(colonIdx + 1)..], out var seconds)
            && minutes >= 0
            && seconds >= 0
            && seconds < 60
        )
        {
            return (minutes * 60 + seconds).ToString();
        }

        return null;
    }
}
