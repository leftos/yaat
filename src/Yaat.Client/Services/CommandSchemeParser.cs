using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public record ParsedInput(CanonicalCommandType Type, string? Argument);

public static class CommandSchemeParser
{
    public static ParsedInput? Parse(string input, CommandScheme scheme)
    {
        var trimmed = input.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
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
            )
            {
                continue;
            }

            if (!input.StartsWith(pattern.Verb, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var arg = input[pattern.Verb.Length..];
            if (arg.Length == 0 || !int.TryParse(arg, out _))
            {
                continue;
            }

            return new ParsedInput(type, arg);
        }

        return null;
    }
}
