using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public record ParsedInput(
    CanonicalCommandType Type,
    string? Argument);

public static class CommandSchemeParser
{
    public static ParsedInput? Parse(
        string input, CommandScheme scheme)
    {
        var trimmed = input.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (scheme.Name == "VICE")
            return ParseVice(trimmed, scheme);

        return ParseSpaceSeparated(trimmed, scheme);
    }

    public static string ToCanonical(
        CanonicalCommandType type, string? argument)
    {
        var canonical = CommandScheme.AtcTrainer();
        if (!canonical.Patterns.TryGetValue(type, out var pattern))
            return "";

        if (argument is null)
            return pattern.Verb;

        return $"{pattern.Verb} {argument}";
    }

    private static ParsedInput? ParseSpaceSeparated(
        string input, CommandScheme scheme)
    {
        var parts = input.Split(
            ' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0];
        var arg = parts.Length > 1 ? parts[1].Trim() : null;

        foreach (var (type, pattern) in scheme.Patterns)
        {
            if (!string.Equals(
                verb, pattern.Verb,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool needsArg = pattern.Format.Contains("{arg}");
            if (needsArg && arg is null)
                continue;
            if (!needsArg && arg is not null)
                continue;

            return new ParsedInput(type, arg);
        }

        return null;
    }

    private static ParsedInput? ParseVice(
        string input, CommandScheme scheme)
    {
        // VICE relative turns: T{deg}L or T{deg}R
        if (input.Length >= 3
            && input[0] == 'T'
            && char.IsDigit(input[1]))
        {
            var lastChar = input[^1];
            if (lastChar is 'L' or 'R')
            {
                var deg = input[1..^1];
                if (int.TryParse(deg, out _))
                {
                    var type = lastChar == 'L'
                        ? CanonicalCommandType.RelativeLeft
                        : CanonicalCommandType.RelativeRight;
                    return new ParsedInput(type, deg);
                }
            }
        }

        // VICE no-arg commands: X (delete), H (fly present hdg)
        if (input == "X")
            return new ParsedInput(
                CanonicalCommandType.Delete, null);
        if (input == "H")
            return new ParsedInput(
                CanonicalCommandType.FlyPresentHeading, null);

        // Space-separated global commands (PAUSE, UNPAUSE, SIMRATE)
        if (input.StartsWith("PAUSE"))
            return new ParsedInput(
                CanonicalCommandType.Pause, null);
        if (input.StartsWith("UNPAUSE"))
            return new ParsedInput(
                CanonicalCommandType.Unpause, null);
        if (input.StartsWith("SIMRATE"))
        {
            var parts = input.Split(
                ' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1
                ? new ParsedInput(
                    CanonicalCommandType.SimRate, parts[1].Trim())
                : null;
        }

        // VICE single-letter verb + digits: H270, L180, C240...
        if (input.Length >= 2 && char.IsLetter(input[0]))
        {
            var verb = input[0].ToString();
            var arg = input[1..];

            // SQ is two letters
            if (input.Length >= 3
                && input.StartsWith("SQ")
                && char.IsDigit(input[2]))
            {
                verb = "SQ";
                arg = input[2..];
            }

            if (!int.TryParse(arg, out _))
                return null;

            foreach (var (type, pattern) in scheme.Patterns)
            {
                if (type is CanonicalCommandType.RelativeLeft
                    or CanonicalCommandType.RelativeRight
                    or CanonicalCommandType.Delete
                    or CanonicalCommandType.FlyPresentHeading
                    or CanonicalCommandType.Pause
                    or CanonicalCommandType.Unpause)
                {
                    continue;
                }

                if (string.Equals(
                    verb, pattern.Verb,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return new ParsedInput(type, arg);
                }
            }
        }

        return null;
    }
}
