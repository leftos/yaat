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
        var textArgMatch = ParseTextArgCommand(trimmed);
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
            return pattern.Verb;
        }

        return $"{pattern.Verb} {argument}";
    }

    private static readonly (string Verb, CanonicalCommandType Type)[] TextArgCommands =
    [
        ("HFIXL", CanonicalCommandType.HoldAtFixLeft),
        ("HFIXR", CanonicalCommandType.HoldAtFixRight),
        ("HFIX", CanonicalCommandType.HoldAtFixHover),
        ("DCT", CanonicalCommandType.DirectTo),
    ];

    private static ParsedInput? ParseTextArgCommand(string input)
    {
        foreach (var (verb, type) in TextArgCommands)
        {
            if (!input.StartsWith(verb, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (input.Length == verb.Length)
            {
                return null;
            }

            if (input[verb.Length] != ' ')
            {
                continue;
            }

            var arg = input[(verb.Length + 1)..].Trim();
            return arg.Length > 0 ? new ParsedInput(type, arg) : null;
        }

        return null;
    }

    private static ParsedInput? ParseSpaceSeparated(string input, CommandScheme scheme)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0];
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

        foreach (var (type, pattern) in scheme.Patterns)
        {
            if (!string.Equals(verb, pattern.Verb, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool needsArg = pattern.Format.Contains("{arg}");
            if (needsArg && arg is null)
            {
                continue;
            }

            if (!needsArg && arg is not null)
            {
                continue;
            }

            return new ParsedInput(type, arg);
        }

        return null;
    }

    private static ParsedInput? ParseConcatenated(string input, CommandScheme scheme)
    {
        // Concatenated relative turns: T{deg}L or T{deg}R
        if (input.Length >= 3 && input[0] == 'T' && char.IsDigit(input[1]))
        {
            var lastChar = input[^1];
            if (lastChar is 'L' or 'R')
            {
                var deg = input[1..^1];
                if (int.TryParse(deg, out _))
                {
                    var type = lastChar == 'L' ? CanonicalCommandType.RelativeLeft : CanonicalCommandType.RelativeRight;
                    return new ParsedInput(type, deg);
                }
            }
        }

        // No-arg concatenated commands (Delete, FlyPresentHeading)
        foreach (var (type, pattern) in scheme.Patterns)
        {
            if (pattern.Format.Contains("{arg}"))
            {
                continue;
            }

            if (type is CanonicalCommandType.Pause or CanonicalCommandType.Unpause or CanonicalCommandType.SimRate)
            {
                continue;
            }
            if (string.Equals(input, pattern.Verb, StringComparison.OrdinalIgnoreCase))
            {
                return new ParsedInput(type, null);
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
                    or CanonicalCommandType.DirectTo
                    or CanonicalCommandType.HoldAtFixLeft
                    or CanonicalCommandType.HoldAtFixRight
                    or CanonicalCommandType.HoldAtFixHover
            )
            {
                continue;
            }

            if (!input.StartsWith(pattern.Verb, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var arg = input[pattern.Verb.Length..];
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
}
