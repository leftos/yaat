using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public record ParsedInput(CanonicalCommandType Type, string? Argument);

/// <summary>
/// Result of parsing a compound input string with ';' and ',' separators.
/// Contains the full canonical string to send to the server.
/// </summary>
public record CompoundParseResult(string CanonicalString);

public static class CommandSchemeParser
{
    private static readonly HashSet<string> PassthroughVerbs = new(StringComparer.OrdinalIgnoreCase) { "LV", "AT", "ATFN" };

    /// <summary>
    /// Returns true if the argument is a valid altitude: numeric (e.g., "050", "5000")
    /// or AGL format with '+' separator (e.g., "KOAK+010").
    /// </summary>
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

    /// <summary>
    /// Parses a compound input string (may contain ';' and ',' separators).
    /// Returns the canonical string to send to the server, or null if any part fails.
    /// </summary>
    public static CompoundParseResult? ParseCompound(string input, CommandScheme scheme)
    {
        var trimmed = ExpandSpeedUntil(input.Trim());
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        bool isCompound = trimmed.Contains(';') || trimmed.Contains(',');
        var upper = trimmed.ToUpperInvariant();
        if (!isCompound)
        {
            isCompound =
                upper.StartsWith("LV ")
                || upper.StartsWith("AT ")
                || upper.StartsWith("ATFN ")
                || upper.StartsWith("GIVEWAY ")
                || upper.StartsWith("BEHIND ");
        }

        if (!isCompound)
        {
            // Single command
            var parsed = Parse(trimmed, scheme);
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
            if (tokens.Length < 3)
            {
                return null;
            }

            parts.Add($"AT {tokens[1].ToUpperInvariant()}");
            remaining = tokens[2];
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
        else if (upper.StartsWith("GIVEWAY ") || upper.StartsWith("BEHIND "))
        {
            var tokens = remaining.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                return null;
            }

            parts.Add($"GIVEWAY {tokens[1].ToUpperInvariant()}");
            remaining = tokens[2];
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
            if (parsed is null)
            {
                return null;
            }

            canonicalCommands.Add(ToCanonical(parsed.Type, parsed.Argument));
        }

        if (canonicalCommands.Count == 0)
        {
            return null;
        }

        if (parts.Count > 0)
        {
            return $"{string.Join(" ", parts)} {string.Join(", ", canonicalCommands)}";
        }

        return string.Join(", ", canonicalCommands);
    }

    public static ParsedInput? Parse(string input, CommandScheme scheme)
    {
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

        return ParseSpaceSeparated(trimmed, scheme);
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

    /// <summary>
    /// Commands that should NOT be matched by the concatenation fallback
    /// because they are text-arg, always space-separated, or have special handling.
    /// </summary>
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

        // Relative turns: T{digits}L / T{digits}R
        if (scheme.Patterns.TryGetValue(CanonicalCommandType.RelativeLeft, out var relLeftPattern))
        {
            foreach (var alias in relLeftPattern.Aliases)
            {
                if (verb.Length > alias.Length + 1 && verb.StartsWith(alias, StringComparison.OrdinalIgnoreCase) && char.IsDigit(verb[alias.Length]))
                {
                    var lastChar = verb[^1];
                    if (lastChar is 'L' or 'R')
                    {
                        var deg = verb[alias.Length..^1];
                        if (int.TryParse(deg, out _))
                        {
                            var type = lastChar == 'L' ? CanonicalCommandType.RelativeLeft : CanonicalCommandType.RelativeRight;
                            return new ParsedInput(type, deg);
                        }
                    }
                }
            }
        }

        foreach (var (type, pattern) in scheme.Patterns)
        {
            if (!MatchesAnyAlias(verb, pattern))
            {
                continue;
            }

            bool hasOptionalArg = pattern.Format.Contains("{arg?}");
            bool hasRequiredArg = !hasOptionalArg && pattern.Format.Contains("{arg}");

            if (hasRequiredArg && arg is null)
            {
                continue;
            }

            if (!hasRequiredArg && !hasOptionalArg && arg is not null)
            {
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
        var candidates = new List<(string Alias, CanonicalCommandType Type, CommandPattern Pattern)>();
        foreach (var (type, pattern) in scheme.Patterns)
        {
            if (!pattern.Format.Contains("{arg"))
            {
                continue;
            }

            if (IsConcatenationExcluded(type))
            {
                continue;
            }

            foreach (var alias in pattern.Aliases)
            {
                candidates.Add((alias, type, pattern));
            }
        }

        candidates.Sort((a, b) => b.Alias.Length.CompareTo(a.Alias.Length));

        foreach (var (alias, type, _) in candidates)
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

        return null;
    }

    /// <summary>
    /// Rewrites "30 [TAXI] T U W [HS ...]" → "T U W RWY 30 [HS ...]"
    /// so the canonical form uses TAXI verb with RWY keyword.
    /// </summary>
    private static string? RewriteRwyToTaxiArg(string arg)
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
    /// Expands "SPD X UNTIL Y" shorthand to "SPD X; ATFN Y RNS" within each semicolon-separated block.
    /// Handles chained UNTIL: "SPD 210 UNTIL 10; SPD 180 UNTIL 5" → "SPD 210; ATFN 10 SPD 180; ATFN 5 RNS".
    /// </summary>
    internal static string ExpandSpeedUntil(string input)
    {
        // Split by semicolons to process blocks independently
        var blocks = input.Split(';');
        var result = new List<string>();

        for (int i = 0; i < blocks.Length; i++)
        {
            var block = blocks[i].Trim();
            var upper = block.ToUpperInvariant();

            // Match "SPD X UNTIL Y" pattern (with optional +/- modifier on X)
            var match = System.Text.RegularExpressions.Regex.Match(upper, @"^(SPD\s+\d+[+\-]?)\s+UNTIL\s+(\d+(?:\.\d+)?)$");
            if (!match.Success)
            {
                result.Add(block);
                continue;
            }

            var spdPart = block[..match.Groups[1].Length].Trim();
            var distPart = match.Groups[2].Value;

            // Look at the next block to determine what happens at the ATFN distance
            string atfnCommand;
            if (i + 1 < blocks.Length)
            {
                var nextBlock = blocks[i + 1].Trim();
                var nextUpper = nextBlock.ToUpperInvariant();
                var nextMatch = System.Text.RegularExpressions.Regex.Match(nextUpper, @"^(SPD\s+\d+[+\-]?)\s+UNTIL\s+(\d+(?:\.\d+)?)$");
                if (nextMatch.Success)
                {
                    // Chain: "SPD 210 UNTIL 10; SPD 180 UNTIL 5" → "SPD 210; ATFN 10 SPD 180; ATFN 5 RNS"
                    var nextSpdPart = blocks[i + 1].Trim()[..nextMatch.Groups[1].Length].Trim();
                    var nextDistPart = nextMatch.Groups[2].Value;
                    result.Add(spdPart);
                    result.Add($"ATFN {distPart} {nextSpdPart}");
                    result.Add($"ATFN {nextDistPart} RNS");
                    i++; // skip the next block, we consumed it
                    continue;
                }
            }

            // Single UNTIL: "SPD 210 UNTIL 10" → "SPD 210; ATFN 10 RNS"
            atfnCommand = "RNS";
            result.Add(spdPart);
            result.Add($"ATFN {distPart} {atfnCommand}");
        }

        return string.Join("; ", result);
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
