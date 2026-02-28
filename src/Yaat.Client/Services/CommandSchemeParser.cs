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
    private static readonly HashSet<string> PassthroughVerbs = new(StringComparer.OrdinalIgnoreCase) { "LV", "AT" };

    /// <summary>
    /// Returns true if the argument is a valid altitude: numeric (e.g., "050", "5000")
    /// or AGL format with an airport prefix (e.g., "KOAK010").
    /// </summary>
    private static bool IsAltitudeArg(string arg)
    {
        if (int.TryParse(arg, out _))
        {
            return true;
        }

        // AGL: leading letters + trailing digits
        int splitIndex = -1;
        for (int i = 0; i < arg.Length; i++)
        {
            if (char.IsDigit(arg[i]))
            {
                splitIndex = i;
                break;
            }
        }

        if (splitIndex <= 0)
        {
            return false;
        }

        return int.TryParse(arg[splitIndex..], out _);
    }

    /// <summary>
    /// Parses a compound input string (may contain ';' and ',' separators).
    /// Returns the canonical string to send to the server, or null if any part fails.
    /// </summary>
    public static CompoundParseResult? ParseCompound(string input, CommandScheme scheme)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        bool isCompound = trimmed.Contains(';') || trimmed.Contains(',');
        var upper = trimmed.ToUpperInvariant();
        if (!isCompound)
        {
            isCompound = upper.StartsWith("LV ") || upper.StartsWith("AT ");
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

        if (scheme.ParseMode == CommandParseMode.Concatenated)
        {
            return ParseConcatenated(trimmed, scheme);
        }

        return ParseSpaceSeparated(trimmed, scheme);
    }

    public static string ToCanonical(CanonicalCommandType type, string? argument)
    {
        var canonical = CommandScheme.AtcTrainer();
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
    ];

    private static ParsedInput? ParseTextArgCommand(string input, CommandScheme scheme)
    {
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

    private static ParsedInput? ParseSpaceSeparated(string input, CommandScheme scheme)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0];
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

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

        return null;
    }

    private static bool StartsWithAnyAlias(
        string input, CommandPattern pattern, out string matchedAlias)
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

    private static ParsedInput? ParseConcatenated(string input, CommandScheme scheme)
    {
        // Concatenated relative turns: T{deg}L or T{deg}R
        // Check any alias for RelativeLeft/RelativeRight that matches pattern {alias}{digits}{L|R}
        if (scheme.Patterns.TryGetValue(CanonicalCommandType.RelativeLeft, out var relLeftPattern))
        {
            foreach (var alias in relLeftPattern.Aliases)
            {
                if (input.Length > alias.Length + 1
                    && input.StartsWith(alias, StringComparison.OrdinalIgnoreCase)
                    && char.IsDigit(input[alias.Length]))
                {
                    var lastChar = input[^1];
                    if (lastChar is 'L' or 'R')
                    {
                        var deg = input[alias.Length..^1];
                        if (int.TryParse(deg, out _))
                        {
                            var type = lastChar == 'L'
                                ? CanonicalCommandType.RelativeLeft
                                : CanonicalCommandType.RelativeRight;
                            return new ParsedInput(type, deg);
                        }
                    }
                }
            }
        }

        // No-arg and optional-arg concatenated commands (Delete, FlyPresentHeading, pattern entry)
        foreach (var (type, pattern) in scheme.Patterns)
        {
            if (pattern.Format.Contains("{arg}") && !pattern.Format.Contains("{arg?}"))
            {
                continue;
            }

            if (type is CanonicalCommandType.Pause or CanonicalCommandType.Unpause
                or CanonicalCommandType.SimRate or CanonicalCommandType.Add
                or CanonicalCommandType.SpawnNow or CanonicalCommandType.SpawnDelay)
            {
                continue;
            }

            // Exact match (no arg)
            if (MatchesAnyAlias(input, pattern))
            {
                return new ParsedInput(type, null);
            }

            // Optional-arg commands: check for space-separated arg after verb
            if (pattern.Format.Contains("{arg?}")
                && StartsWithAnyAlias(input, pattern, out var optAlias)
                && input.Length > optAlias.Length
                && input[optAlias.Length] == ' ')
            {
                var optArg = input[(optAlias.Length + 1)..].Trim();
                if (optArg.Length > 0)
                {
                    return new ParsedInput(type, optArg);
                }
            }
        }

        // Space-separated global commands (PAUSE, UNPAUSE, SIMRATE)
        if (input.StartsWith("PAUSE"))
        {
            return new ParsedInput(CanonicalCommandType.Pause, null);
        }

        if (input.StartsWith("UNPAUSE"))
        {
            return new ParsedInput(CanonicalCommandType.Unpause, null);
        }

        if (input.StartsWith("SIMRATE"))
        {
            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? new ParsedInput(CanonicalCommandType.SimRate, parts[1].Trim()) : null;
        }

        if (input.StartsWith("ADD ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? new ParsedInput(CanonicalCommandType.Add, parts[1].Trim()) : null;
        }

        if (string.Equals(input, "SPAWN", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedInput(CanonicalCommandType.SpawnNow, null);
        }

        if (input.StartsWith("DELAY ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                var normalized = NormalizeDelayArg(parts[1].Trim());
                return normalized is not null ? new ParsedInput(CanonicalCommandType.SpawnDelay, normalized) : null;
            }
            return null;
        }

        // Concatenated verb + digits: H270, L180, C240, SQ1234...
        // Try longer verb matches first (SQ before S)
        foreach (var (type, pattern) in scheme.Patterns)
        {
            if (!pattern.Format.Contains("{arg}"))
            {
                continue;
            }

            if (
                type
                is CanonicalCommandType.RelativeLeft
                    or CanonicalCommandType.RelativeRight
                    or CanonicalCommandType.Pause
                    or CanonicalCommandType.Unpause
                    or CanonicalCommandType.SimRate
                    or CanonicalCommandType.Add
                    or CanonicalCommandType.DirectTo
                    or CanonicalCommandType.HoldAtFixLeft
                    or CanonicalCommandType.HoldAtFixRight
                    or CanonicalCommandType.HoldAtFixHover
                    or CanonicalCommandType.SpawnNow
                    or CanonicalCommandType.SpawnDelay
            )
            {
                continue;
            }

            if (!StartsWithAnyAlias(input, pattern, out var matchedAlias))
            {
                continue;
            }

            var arg = input[matchedAlias.Length..];
            if (arg.Length == 0)
            {
                continue;
            }

            bool isAltitudeCommand = type is CanonicalCommandType.ClimbMaintain or CanonicalCommandType.DescendMaintain;
            if (isAltitudeCommand ? !IsAltitudeArg(arg) : !int.TryParse(arg, out _))
            {
                continue;
            }

            return new ParsedInput(type, arg);
        }

        return null;
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
        if (colonIdx > 0 && colonIdx < arg.Length - 1
            && int.TryParse(arg[..colonIdx], out var minutes)
            && int.TryParse(arg[(colonIdx + 1)..], out var seconds)
            && minutes >= 0 && seconds >= 0 && seconds < 60)
        {
            return (minutes * 60 + seconds).ToString();
        }

        return null;
    }
}
