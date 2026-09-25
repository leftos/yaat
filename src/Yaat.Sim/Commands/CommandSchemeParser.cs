using System.Text.RegularExpressions;
using Yaat.Sim.Data;

namespace Yaat.Sim.Commands;

public record ParsedInput(CanonicalCommandType Type, string? Argument);

public record CompoundParseResult(string CanonicalString);

/// <summary>
/// A parse failure produced by <see cref="CommandSchemeParser"/>. <paramref name="Verb"/> is
/// the user-typed verb (uppercased), <paramref name="Reason"/> is a short descriptive phrase,
/// and <paramref name="Expected"/> is the rendered command signature when the verb was
/// recognized but its arguments did not match — null when the verb itself was unrecognized.
/// </summary>
public record ParseFailure(string Verb, string Reason, string? Expected = null);

public static class CommandSchemeParser
{
    public static CompoundParseResult? ParseCompound(string input, CommandScheme scheme) => ParseCompound(input, scheme, out _);

    public static CompoundParseResult? ParseCompound(string input, CommandScheme scheme, out ParseFailure? failure)
    {
        failure = null;
        string aliasNormalized = SplitTrailingGiveWay(NormalizeSeparatorAliases(input.Trim()));
        string trimmed = ExpandMultiCommand(ExpandWait(ExpandSpeedUntil(aliasNormalized, scheme)));
        trimmed = CommaBeforeCondition.Replace(trimmed, ";");
        trimmed = CommaBeforeGiveWayCondition.Replace(trimmed, ";");
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        bool isCompound = trimmed.Contains(';') || trimmed.Contains(',');
        string upper = trimmed.ToUpperInvariant();
        if (!isCompound)
        {
            isCompound =
                upper.StartsWith("LV ")
                || upper.StartsWith("AT ")
                || upper.StartsWith("ATFN ")
                || upper.StartsWith("ONHO ")
                || upper.StartsWith("ONH ")
                || upper.StartsWith("ONHS ")
                || upper.StartsWith("OTG ");

            // GIVEWAY/BEHIND/GW are compound only if they have 3+ tokens (condition form)
            if (!isCompound && (upper.StartsWith("GIVEWAY ") || upper.StartsWith("BEHIND ") || upper.StartsWith("GW ")))
            {
                string[] tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                isCompound = tokens.Length >= 3;
            }
        }

        if (!isCompound)
        {
            // Single command
            ParsedInput? parsed = Parse(trimmed, scheme, out failure);
            if (parsed is null)
            {
                return null;
            }

            return new CompoundParseResult(ToCanonical(parsed.Type, parsed.Argument));
        }

        // Split by ';' for sequential blocks
        var blocks = trimmed.Split(';').Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
        var canonicalBlocks = new List<string>();

        for (int i = 0; i < blocks.Count; i++)
        {
            string? canonicalBlock = ParseBlockToCanonical(blocks[i], scheme, out failure, out bool bareCondition);
            if (canonicalBlock is null)
            {
                return null;
            }

            // A bare condition only stands as the last thing in the input. With a block after it the
            // command was dropped — `AT SUNOL; DM 020` for `AT SUNOL DM 020`.
            if (bareCondition && (i < blocks.Count - 1))
            {
                failure = EmptyConditionFailure(canonicalBlock, ';', blocks[i + 1]);
                return null;
            }

            canonicalBlocks.Add(canonicalBlock);
        }

        if (canonicalBlocks.Count == 0)
        {
            return null;
        }

        // CFIX implicit AT: when the first block is a CFIX command, subsequent blocks
        // without an explicit condition get an implicit AT <fixname> prefix.
        if (canonicalBlocks.Count >= 2)
        {
            InjectCfixImplicitAtCondition(canonicalBlocks);
        }

        return new CompoundParseResult(string.Join("; ", canonicalBlocks));
    }

    /// <summary>
    /// Splits a trailing standalone give-way clause off a single TAXI command so it dispatches as a
    /// parallel command in the same block: <c>TAXI A A1 1R GIVEWAY KLM605</c> →
    /// <c>TAXI A A1 1R, GIVEWAY KLM605</c>. Parallel (one block, applied in source order) is the only
    /// form that works at runtime — a sequential give-way block sits untriggered behind the active
    /// ground phase and never fires.
    ///
    /// Only fires for a single TAXI command with no existing separators whose route ends with a
    /// GIVEWAY/BEHIND/GW alias followed by a callsign-shaped token. The callsign-shape guard avoids
    /// mis-splitting a taxiway literally named "GW"; the give-way *condition* form
    /// (<c>GIVEWAY cs &lt;ground-verb&gt; …</c>) is left untouched.
    /// </summary>
    public static string SplitTrailingGiveWay(string input)
    {
        if (string.IsNullOrEmpty(input) || input.Contains(',') || input.Contains(';'))
        {
            return input;
        }

        string[] tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4 || !CommandRegistry.IsAliasFor(CanonicalCommandType.Taxi, tokens[0]))
        {
            return input;
        }

        // Require at least one route token before the give-way keyword (i >= 2) and a callsign-shaped
        // token immediately after it; everything from the keyword onward becomes the give-way clause.
        for (int i = 2; i < tokens.Length - 1; i++)
        {
            if (!CommandRegistry.IsAliasFor(CanonicalCommandType.GiveWay, tokens[i]) || !LooksLikeCallsign(tokens[i + 1]))
            {
                continue;
            }

            // A ground verb after the callsign means this is the give-way *condition* form, which keeps
            // its own grammar — don't split.
            if (i + 2 < tokens.Length && CommandParser.IsGiveWayConditionVerb(tokens[i + 2]))
            {
                continue;
            }

            return $"{string.Join(' ', tokens[..i])}, {string.Join(' ', tokens[i..])}";
        }

        return input;
    }

    /// <summary>
    /// True when a block is a GIVEWAY/BEHIND/GW *condition* (callsign followed by a ground command
    /// verb), e.g. <c>GIVEWAY SWA5456 TAXI S T</c>. Mirrors the server's
    /// <see cref="CommandParser.IsGiveWayConditionVerb"/> disambiguation so a standalone give-way
    /// (<c>GIVEWAY &lt;callsign&gt;</c>) is parsed as a command, not rejected as a broken condition.
    /// </summary>
    private static bool IsGiveWayConditionBlock(string block)
    {
        string upper = block.ToUpperInvariant();
        if (!(upper.StartsWith("GIVEWAY ") || upper.StartsWith("BEHIND ") || upper.StartsWith("GW ")))
        {
            return false;
        }

        string[] tokens = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (tokens.Length >= 3) && CommandParser.IsGiveWayConditionVerb(tokens[2]);
    }

    /// <summary>
    /// True when every parallel command in a canonical command list is a WAIT/WAITD —
    /// the client mirror of the server merge's <c>IsWaitLikeCommand</c> gate.
    /// </summary>
    private static bool IsWaitOnlyCommandList(string canonicalCommands)
    {
        string[] commands = canonicalCommands.Split(',');
        foreach (string command in commands)
        {
            string trimmed = command.Trim();
            if (!(trimmed.StartsWith("WAIT ", StringComparison.Ordinal) || trimmed.StartsWith("WAITD ", StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return commands.Length > 0;
    }

    /// <summary>
    /// True when a canonical block string carries its own condition (or splits into multiple blocks) —
    /// the client mirror of the server merge's stop rule for absorbing WAIT payload sub-blocks.
    /// </summary>
    private static bool IsConditionLedBlock(string canonicalBlock)
    {
        if (canonicalBlock.Contains(';'))
        {
            return true;
        }

        string upper = canonicalBlock.ToUpperInvariant();
        return upper.StartsWith("LV ")
            || upper.StartsWith("AT ")
            || upper.StartsWith("ATFN ")
            || upper is "ONHO" or "ONHS" or "OTG"
            || upper.StartsWith("ONHO ")
            || upper.StartsWith("ONHS ")
            || upper.StartsWith("OTG ")
            || IsGiveWayConditionBlock(canonicalBlock);
    }

    /// <summary>
    /// Heuristic for a callsign token: alphanumeric, at least 3 chars, containing a digit, and either
    /// two or more letters (airline + flight number) or an N-registration. Distinguishes real callsigns
    /// from short taxiway labels like <c>A1</c> or <c>B7</c>.
    /// </summary>
    private static bool LooksLikeCallsign(string token)
    {
        int letters = 0;
        int digits = 0;
        foreach (char ch in token)
        {
            if (char.IsLetter(ch))
            {
                letters++;
            }
            else if (char.IsDigit(ch))
            {
                digits++;
            }
            else
            {
                return false;
            }
        }

        if ((digits == 0) || (token.Length < 3))
        {
            return false;
        }

        return (letters >= 2) || (char.ToUpperInvariant(token[0]) == 'N');
    }

    private static readonly HashSet<string> CanonicalConditionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "LV",
        "AT",
        "ATFN",
        "ONHO",
        "ONHS",
        "OTG",
        "GIVEWAY",
        "WAIT",
    };

    /// <summary>
    /// The failure for a condition with no command of its own that is followed by another block or
    /// command — <c>AT SUNOL; DM 020</c>, a typo of <c>AT SUNOL DM 020</c>. A bare condition only
    /// stands as the last thing in the input. <paramref name="condition"/> is the condition's text and
    /// <paramref name="next"/> the raw text of what follows the separator, both as typed.
    /// </summary>
    internal static ParseFailure EmptyConditionFailure(string condition, char separator, string next)
    {
        string normalized = string.Join(' ', condition.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        string verb = normalized.Split(' ', 2)[0];
        return new ParseFailure(verb, $"has no command before the '{separator}' — did you mean '{normalized} {next.Trim()}'?");
    }

    /// <summary>
    /// The same failure as one line, for the server's verb-less failure reason: the client's pre-send
    /// check and the server refuse with the same words.
    /// </summary>
    internal static string EmptyConditionMessage(string condition, char separator, string next)
    {
        ParseFailure failure = EmptyConditionFailure(condition, separator, next);
        return $"{failure.Verb} {failure.Reason}";
    }

    /// <summary>
    /// Splits an empty condition off a block that opens with a parallel separator, e.g. the
    /// <c>DM 020</c> of <c>AT SUNOL, DM 020</c>. The head before the comma is parsed on its own: only
    /// when it is a bare condition — the condition and no command — is the comma a sign of the dropped
    /// command, and the returned failure suggests writing the command before the separator.
    /// </summary>
    private static bool TrySplitBareConditionBeforeComma(string block, CommandScheme scheme, out ParseFailure? failure)
    {
        failure = null;
        int comma = block.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        string head = block[..comma].Trim();
        string next = block[(comma + 1)..].Trim();
        if ((head.Length == 0) || (next.Length == 0))
        {
            return false;
        }

        string? headCanonical = ParseBlockToCanonical(head, scheme, out _, out bool headIsBare);
        if (!headIsBare)
        {
            return false;
        }

        failure = EmptyConditionFailure(headCanonical!, ',', next);
        return true;
    }

    /// <summary>
    /// When the first canonical block is a CFIX command, inject AT {fixname} on subsequent
    /// blocks that don't already have a condition prefix (AT, LV, ATFN, ONHO, GIVEWAY, WAIT).
    /// Mutates <paramref name="canonicalBlocks"/> in place.
    /// </summary>
    private static void InjectCfixImplicitAtCondition(List<string> canonicalBlocks)
    {
        string firstBlock = canonicalBlocks[0];
        if (!firstBlock.StartsWith("CFIX ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Extract fix name: canonical form is "CFIX <fixname> <alt> [speed]"
        string[] tokens = firstBlock.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return;
        }

        string fixName = tokens[1];

        for (int i = 1; i < canonicalBlocks.Count; i++)
        {
            string block = canonicalBlocks[i];
            string firstToken = block.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (CanonicalConditionKeywords.Contains(firstToken))
            {
                continue;
            }

            canonicalBlocks[i] = $"AT {fixName} {block}";
        }
    }

    private static string? ParseBlockToCanonical(string block, CommandScheme scheme, out ParseFailure? failure, out bool bareCondition)
    {
        failure = null;
        bareCondition = false;
        if (TrySplitBareConditionBeforeComma(block, scheme, out ParseFailure? commaFailure))
        {
            failure = commaFailure;
            return null;
        }

        var parts = new List<string>();
        string remaining = block;

        // Check for LV or AT prefix
        string upper = remaining.ToUpperInvariant();
        if (upper.StartsWith("LV "))
        {
            string[] tokens = remaining.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                failure = new ParseFailure("LV", "expects an altitude and a command (e.g. LV 5000 CM 190)");
                return null;
            }

            if (!CommandParser.IsAltitudeArg(tokens[1]))
            {
                failure = new ParseFailure("LV", "expects an altitude (e.g. LV 5000 CM 190)");
                return null;
            }

            parts.Add($"LV {tokens[1]}");
            remaining = tokens[2];
        }
        else if (upper.StartsWith("AT "))
        {
            string[] tokens = remaining.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                failure = new ParseFailure("AT", "expects a fix or altitude and a command (e.g. AT BRIXX CM 190)");
                return null;
            }

            // AT with altitude requires a following command (AT 5000 CM 190)
            if (CommandParser.IsAltitudeArg(tokens[1]) && tokens.Length < 3)
            {
                failure = new ParseFailure("AT", "expects a command after the altitude (e.g. AT 5000 CM 190)");
                return null;
            }

            parts.Add($"AT {tokens[1].ToUpperInvariant()}");
            remaining = tokens.Length >= 3 ? tokens[2] : "";
        }
        else if (upper.StartsWith("ATFN "))
        {
            string[] tokens = remaining.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                failure = new ParseFailure("ATFN", "expects a distance and a command (e.g. ATFN 5 CM 190)");
                return null;
            }

            if (!double.TryParse(tokens[1], out _))
            {
                failure = new ParseFailure("ATFN", "expects a numeric distance (e.g. ATFN 5 CM 190)");
                return null;
            }

            parts.Add($"ATFN {tokens[1]}");
            remaining = tokens[2];
        }
        else if (IsGiveWayConditionBlock(remaining))
        {
            // GIVEWAY/BEHIND/GW as a *condition* (callsign + ground verb). A standalone give-way
            // (GIVEWAY <callsign>) is not a condition — it falls through to the command parser below.
            string[] tokens = remaining.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            parts.Add($"GIVEWAY {tokens[1].ToUpperInvariant()}");
            remaining = tokens[2];
        }
        // ONH is the documented short alias of ONHO (COMMANDS.md), and the server's CommandParser
        // accepts both. ONHS is a different verb and does not match here — the trailing space is
        // part of the prefix.
        else if (upper.StartsWith("ONHO ") || upper.StartsWith("ONH "))
        {
            string[] tokens = remaining.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                failure = new ParseFailure("ONHO", "expects a command (e.g. ONHO CM 190)");
                return null;
            }

            remaining = tokens[1];
            string remainderUpper = remaining.ToUpperInvariant();

            // ONHO followed by another condition → emit ONHO as a standalone block,
            // then recursively parse the remainder as a separate block
            if (remainderUpper.StartsWith("AT ") || remainderUpper.StartsWith("LV ") || remainderUpper.StartsWith("ATFN "))
            {
                string? innerCanonical = ParseBlockToCanonical(remaining, scheme, out failure, out _);
                if (innerCanonical is null)
                {
                    return null;
                }

                return $"ONHO; {innerCanonical}";
            }

            parts.Add("ONHO");
        }
        else if (upper.StartsWith("ONHS "))
        {
            string[] tokens = remaining.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                failure = new ParseFailure("ONHS", "expects a command (e.g. ONHS CM 190)");
                return null;
            }

            remaining = tokens[1];
            string remainderUpper = remaining.ToUpperInvariant();

            if (remainderUpper.StartsWith("AT ") || remainderUpper.StartsWith("LV ") || remainderUpper.StartsWith("ATFN "))
            {
                string? innerCanonical = ParseBlockToCanonical(remaining, scheme, out failure, out _);
                if (innerCanonical is null)
                {
                    return null;
                }

                return $"ONHS; {innerCanonical}";
            }

            parts.Add("ONHS");
        }
        else if (upper.StartsWith("OTG "))
        {
            string[] tokens = remaining.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                failure = new ParseFailure("OTG", "expects a command (e.g. OTG MLT 28L)");
                return null;
            }

            remaining = tokens[1];
            string remainderUpper = remaining.ToUpperInvariant();

            if (remainderUpper.StartsWith("AT ") || remainderUpper.StartsWith("LV ") || remainderUpper.StartsWith("ATFN "))
            {
                string? innerCanonical = ParseBlockToCanonical(remaining, scheme, out failure, out _);
                if (innerCanonical is null)
                {
                    return null;
                }

                return $"OTG; {innerCanonical}";
            }

            parts.Add("OTG");
        }

        // Apply ExpandWait and ExpandSpeedUntil to the remainder after condition extraction
        string expandedRemainder = ExpandMultiCommand(ExpandWait(ExpandSpeedUntil(remaining, scheme)));
        if (expandedRemainder.Contains(';'))
        {
            // Expansion produced additional blocks — split and handle each
            var subBlocks = expandedRemainder.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (subBlocks.Count == 0)
            {
                return null;
            }

            // First sub-block gets the condition prefix
            string? firstCmds = ParseCommandList(subBlocks[0], scheme, out failure);
            if (firstCmds is null)
            {
                return null;
            }

            var tailCanonicals = new List<string>();
            foreach (string? subBlock in subBlocks.Skip(1))
            {
                string? canonicalBlock = ParseBlockToCanonical(subBlock, scheme, out failure, out _);
                if (canonicalBlock is null)
                {
                    return null;
                }

                tailCanonicals.Add(canonicalBlock);
            }

            // A condition-led WAIT compound (`AT TTE WAIT 170 DM 110`) must stay ONE canonical block:
            // a top-level `;` split would strand the payload as an unconditioned block the server can
            // never re-attach — its leading-WAIT merge (CommandParser.ParseBlock) only absorbs payload
            // sub-blocks within a single block string. Mirror that merge's stop rule here: absorb
            // space-joined payload blocks until one introduces its own condition or splits further.
            int mergeCount = 0;
            if ((parts.Count > 0) && IsWaitOnlyCommandList(firstCmds))
            {
                while ((mergeCount < tailCanonicals.Count) && !IsConditionLedBlock(tailCanonicals[mergeCount]))
                {
                    mergeCount++;
                }
            }

            string firstBlock = parts.Count > 0 ? $"{string.Join(" ", parts)} {firstCmds}" : firstCmds;
            if (mergeCount > 0)
            {
                firstBlock = $"{firstBlock} {string.Join(" ", tailCanonicals.Take(mergeCount))}";
            }

            var allCanonicalCommands = new List<string> { firstBlock };
            allCanonicalCommands.AddRange(tailCanonicals.Skip(mergeCount));

            return string.Join("; ", allCanonicalCommands);
        }

        remaining = expandedRemainder;

        // Bare condition with no following command (e.g., "AT BRIXX")
        if (string.IsNullOrWhiteSpace(remaining) && parts.Count > 0)
        {
            bareCondition = true;
            return string.Join(" ", parts);
        }

        string? commandResult = ParseCommandList(remaining, scheme, out failure);
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

    private static string? ParseCommandList(string remaining, CommandScheme scheme, out ParseFailure? failure)
    {
        failure = null;
        // SAY, TIMER and BM consume their entire remainder as literal text — don't split on comma
        string trimmedRemaining = remaining.TrimStart();
        if (
            StartsWithSchemeAlias(trimmedRemaining, scheme, CanonicalCommandType.Say)
            || StartsWithSchemeAlias(trimmedRemaining, scheme, CanonicalCommandType.Timer)
            || StartsWithSchemeAlias(trimmedRemaining, scheme, CanonicalCommandType.Bookmark)
        )
        {
            ParsedInput? parsed = Parse(remaining.Trim(), scheme, out failure);
            return parsed is not null ? ToCanonical(parsed.Type, parsed.Argument) : null;
        }

        // Split remaining by ',' for parallel commands
        string[] commandStrings = remaining.Split(',');
        var canonicalCommands = new List<string>();

        foreach (string cmdStr in commandStrings)
        {
            string cmd = cmdStr.Trim();
            if (string.IsNullOrEmpty(cmd))
            {
                continue;
            }

            ParsedInput? parsed = Parse(cmd, scheme, out failure);
            if (parsed is not null)
            {
                canonicalCommands.Add(ToCanonical(parsed.Type, parsed.Argument));
                continue;
            }

            // Try expanding concatenated commands: "FH 270 CM 5000" → "FH 270, CM 5000"
            string expanded = ExpandMultiCommand(cmd);
            if (expanded == cmd)
            {
                if (failure is null)
                {
                    string verb = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
                    failure = new ParseFailure(verb, "is not a recognized command");
                }

                return null;
            }

            foreach (string subCmd in expanded.Split(','))
            {
                ParsedInput? subParsed = Parse(subCmd.Trim(), scheme, out failure);
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

    public static ParsedInput? Parse(string input, CommandScheme scheme) => Parse(input, scheme, out _);

    public static ParsedInput? Parse(string input, CommandScheme scheme, out ParseFailure? failure)
    {
        failure = null;
        string trimmed = input.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        // Text-arg commands are always space-separated regardless of scheme mode.
        // Check longer prefixes first (HFIXL/HFIXR before HFIX).
        ParsedInput? textArgMatch = ParseTextArgCommand(trimmed, scheme);
        if (textArgMatch is not null)
        {
            return textArgMatch;
        }

        return ParseSpaceSeparated(trimmed, scheme, out failure);
    }

    public static string ToCanonical(CanonicalCommandType type, string? argument)
    {
        var canonical = CommandScheme.Default();
        if (!canonical.Patterns.TryGetValue(type, out CommandPattern? pattern))
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
        CanonicalCommandType.Timer,
        CanonicalCommandType.Bookmark,
        CanonicalCommandType.CreateFlightPlan,
        CanonicalCommandType.CreateVfrFlightPlan,
        CanonicalCommandType.CreateAbbreviatedFlightPlan,
        CanonicalCommandType.SetRemarks,
        CanonicalCommandType.Note,
    ];

    private static ParsedInput? ParseTextArgCommand(string input, CommandScheme scheme)
    {
        // Handle CTOMRT/CTOMLT legacy merged forms
        if (input.StartsWith("CTOMRT", StringComparison.OrdinalIgnoreCase) && (input.Length == 6 || input[6] == ' '))
        {
            string suffix = input.Length > 7 ? " " + input[7..].Trim() : "";
            string arg = "MRT" + suffix;
            return new ParsedInput(CanonicalCommandType.ClearedForTakeoff, arg.Trim());
        }
        if (input.StartsWith("CTOMLT", StringComparison.OrdinalIgnoreCase) && (input.Length == 6 || input[6] == ' '))
        {
            string suffix = input.Length > 7 ? " " + input[7..].Trim() : "";
            string arg = "MLT" + suffix;
            return new ParsedInput(CanonicalCommandType.ClearedForTakeoff, arg.Trim());
        }

        // Build (alias, type) pairs from the scheme, longest alias first so
        // HFIXL matches before HFIX.
        var candidates = new List<(string Alias, CanonicalCommandType Type)>();
        foreach (CanonicalCommandType type in TextArgCommandTypes)
        {
            if (!scheme.Patterns.TryGetValue(type, out CommandPattern? pattern))
            {
                continue;
            }

            foreach (string alias in pattern.Aliases)
            {
                candidates.Add((alias, type));
            }
        }

        candidates.Sort((a, b) => b.Alias.Length.CompareTo(a.Alias.Length));

        foreach ((string? alias, CanonicalCommandType type) in candidates)
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

            string arg = input[(alias.Length + 1)..].Trim();
            return arg.Length > 0 ? new ParsedInput(type, arg) : null;
        }

        return null;
    }

    private static bool MatchesAnyAlias(string token, CommandPattern pattern)
    {
        foreach (string alias in pattern.Aliases)
        {
            if (string.Equals(token, alias, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ParsedInput? ParseSpaceSeparated(string input, CommandScheme scheme, out ParseFailure? failure)
    {
        failure = null;
        string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts[0];
        string? arg = parts.Length > 1 ? parts[1].Trim() : null;

        // RWY {runway} [TAXI] {path} → rewrite to Taxi with RWY keyword
        if (string.Equals(verb, "RWY", StringComparison.OrdinalIgnoreCase) && arg is not null)
        {
            string? rewritten = CommandParser.RewriteRwyToTaxiArg(arg);
            if (rewritten is not null)
            {
                return new ParsedInput(CanonicalCommandType.Taxi, rewritten);
            }
        }

        // Relative turns: T{digits}L / T{digits}R (hardcoded T prefix)
        if (verb.Length >= 3 && verb.StartsWith('T') && char.IsDigit(verb[1]) && verb[^1] is 'L' or 'R' && int.TryParse(verb[1..^1], out _))
        {
            CanonicalCommandType type = verb[^1] == 'L' ? CanonicalCommandType.RelativeLeft : CanonicalCommandType.RelativeRight;
            return new ParsedInput(type, verb[1..^1]);
        }

        string? verbMatchReason = null;
        CanonicalCommandType? verbMatchType = null;
        foreach ((CanonicalCommandType type, CommandPattern? pattern) in scheme.Patterns)
        {
            if (!MatchesAnyAlias(verb, pattern))
            {
                continue;
            }

            ArgMode argMode = CommandRegistry.Get(type)?.ArgMode ?? ArgMode.None;

            if (argMode == ArgMode.Required && arg is null)
            {
                verbMatchReason = "requires an argument";
                verbMatchType = type;
                continue;
            }

            if (argMode == ArgMode.None && arg is not null)
            {
                verbMatchReason = "does not accept arguments";
                verbMatchType = type;
                continue;
            }

            if (type == CanonicalCommandType.SpawnDelay)
            {
                string? normalized = NormalizeDelayArg(arg);
                return normalized is not null ? new ParsedInput(type, normalized) : null;
            }

            return new ParsedInput(type, arg);
        }

        // Concatenation fallback: try prefix-matching aliases when verb+digits are
        // written without a space (e.g. FH270, CM240, H270, SQ1234).
        // Try longer aliases first to avoid matching "S" when "SQ" would work.
        var candidates = new List<(string Alias, CanonicalCommandType Type)>();
        foreach ((CanonicalCommandType type, CommandPattern? pattern) in scheme.Patterns)
        {
            ArgMode concatArgMode = CommandRegistry.Get(type)?.ArgMode ?? ArgMode.None;
            if (concatArgMode == ArgMode.None)
            {
                continue;
            }

            if (CommandParser.IsConcatenationExcluded(type))
            {
                continue;
            }

            foreach (string alias in pattern.Aliases)
            {
                candidates.Add((alias, type));
            }
        }

        candidates.Sort((a, b) => b.Alias.Length.CompareTo(a.Alias.Length));

        foreach ((string? alias, CanonicalCommandType type) in candidates)
        {
            if (!input.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string remainder = input[alias.Length..];
            if (remainder.Length == 0)
            {
                continue;
            }

            bool isAltitudeCommand = type is CanonicalCommandType.ClimbMaintain or CanonicalCommandType.DescendMaintain;
            if (isAltitudeCommand ? !CommandParser.IsAltitudeArg(remainder) : !int.TryParse(remainder, out _))
            {
                continue;
            }

            return new ParsedInput(type, remainder);
        }

        if (verbMatchReason is not null)
        {
            string? expected = verbMatchType is { } t ? CommandRegistry.RenderSignature(t) : null;
            failure = new ParseFailure(verb, verbMatchReason, expected);
        }
        else
        {
            // No alias matched the verb at all and concatenation/RWY rewrite didn't apply —
            // the verb is genuinely unrecognized. Populate ParseFailure so the UI surfaces a
            // descriptive message instead of falling back to a generic placeholder.
            failure = new ParseFailure(verb, "is not a recognized command — try autocomplete or check the command reference");
        }

        return null;
    }

    /// <summary>
    /// Substitutes the word aliases <c>THEN</c> for <c>;</c> and <c>AND</c> for <c>,</c> so users can
    /// write human-readable compounds like <c>H180 AND D250 THEN CTO 28R</c>. Case-insensitive.
    /// Skips text inside a free-text argument (<see cref="IsFreeTextArgVerb"/>: SAY/SAYF, TIMER/BOOKMARK
    /// labels, and the coordination message RDTXT) so messages like <c>SAYF READING YOU LOUD AND
    /// CLEAR</c> and <c>RDTXT /1 fly direct THEN accept</c> are preserved verbatim. Such a verb at block
    /// start (after <c>;</c> or input start) consumes the whole block per the parser's literal-SAY rule;
    /// after a <c>,</c> it consumes only until the next
    /// <c>,</c> or <c>;</c>. Transparent prefixes — <c>WAIT</c>/<c>DELAY</c>/<c>WAITD</c> and the
    /// condition verbs <c>AT</c>/<c>LV</c>/<c>ATFN</c> (each with one argument token) and
    /// <c>ONHO</c>/<c>ONH</c>/<c>ONHS</c>/<c>OTG</c> (bare) — don't end the command start, so SAY after
    /// <c>WAIT 1</c> or <c>AT FIX</c> still begins a literal message.
    /// </summary>
    public static string NormalizeSeparatorAliases(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var sb = new System.Text.StringBuilder(input.Length);
        bool blockStart = true;
        bool subCommandStart = true;
        bool inSayArg = false;
        bool sayBlockwide = false;
        int pendingPrefixArgs = 0;
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];

            if (c == ';')
            {
                sb.Append(c);
                blockStart = true;
                subCommandStart = true;
                inSayArg = false;
                sayBlockwide = false;
                pendingPrefixArgs = 0;
                i++;
                continue;
            }

            if (c == ',')
            {
                sb.Append(c);
                subCommandStart = true;
                pendingPrefixArgs = 0;
                if (inSayArg && !sayBlockwide)
                {
                    inSayArg = false;
                }

                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                sb.Append(c);
                i++;
                continue;
            }

            int start = i;
            while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] != ',' && input[i] != ';')
            {
                i++;
            }

            ReadOnlySpan<char> token = input.AsSpan(start, i - start);

            // A transparent prefix's argument token is copied verbatim: it must neither become a
            // separator nor start a SAY literal, and the command start it guards stays open.
            if (pendingPrefixArgs > 0)
            {
                sb.Append(token);
                pendingPrefixArgs--;
                continue;
            }

            if ((!inSayArg) && subCommandStart && TransparentPrefixArgCount(token) is int prefixArgs)
            {
                sb.Append(token);
                pendingPrefixArgs = prefixArgs;
                continue;
            }

            if ((!inSayArg) && (SeparatorAliasFor(token) is { } separator))
            {
                sb.Append(separator);
                blockStart = blockStart || (separator == ';');
                subCommandStart = true;
            }
            else if (subCommandStart && IsFreeTextArgVerb(token))
            {
                sb.Append(token);
                inSayArg = true;
                sayBlockwide = blockStart;
                blockStart = false;
                subCommandStart = false;
            }
            else
            {
                sb.Append(token);
                blockStart = false;
                subCommandStart = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The word aliases for the compound separators: <c>THEN</c> stands for <c>;</c> and <c>AND</c> for <c>,</c>. This
    /// is the one table of them — <see cref="NormalizeSeparatorAliases"/> substitutes from it and
    /// <see cref="CompoundPolicy"/> reads it back when mapping a normalized offset onto the line as typed, so a third
    /// alias is added here and nowhere else.
    /// </summary>
    public static readonly IReadOnlyList<(string Word, char Separator)> SeparatorAliases = [("THEN", ';'), ("AND", ',')];

    /// <summary>The separator a token stands for when it is one of the word aliases, or null for an ordinary token.</summary>
    private static char? SeparatorAliasFor(ReadOnlySpan<char> token)
    {
        foreach ((string? word, char separator) in SeparatorAliases)
        {
            if (token.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return separator;
            }
        }

        return null;
    }

    /// <summary>
    /// A verb whose argument is a free-text message running to the end of its block: SAY/SAYF, the labelled
    /// TIMER/BOOKMARK verbs, and the coordination message RDTXT. The words THEN and AND inside one are message
    /// content, so the substitution is suspended until the block ends. <c>RDH</c> is deliberately absent: its text is
    /// optional, so <c>RDH 1 THEN SQVFR</c> stays the chain <c>RDH 1; SQVFR</c> is, and an alias word typed inside an
    /// RDH message fails loudly on the unknown tail rather than silently minting a hold whose text is "THEN SQVFR".
    /// </summary>
    private static bool IsFreeTextArgVerb(ReadOnlySpan<char> token) =>
        token.Equals("SAY", StringComparison.OrdinalIgnoreCase)
        || token.Equals("SAYF", StringComparison.OrdinalIgnoreCase)
        || token.Equals("TIMER", StringComparison.OrdinalIgnoreCase)
        || token.Equals("TMR", StringComparison.OrdinalIgnoreCase)
        || token.Equals("BM", StringComparison.OrdinalIgnoreCase)
        || token.Equals("BOOKMARK", StringComparison.OrdinalIgnoreCase)
        || token.Equals("RDTXT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Number of argument tokens a transparent prefix consumes at a command start, or null when the
    /// token is not a transparent prefix. WAIT/DELAY/WAITD and the conditions AT/LV/ATFN carry one
    /// argument; ONHO/ONH/ONHS/OTG are bare.
    /// </summary>
    private static int? TransparentPrefixArgCount(ReadOnlySpan<char> token)
    {
        if (
            token.Equals("WAIT", StringComparison.OrdinalIgnoreCase)
            || token.Equals("DELAY", StringComparison.OrdinalIgnoreCase)
            || token.Equals("WAITD", StringComparison.OrdinalIgnoreCase)
            || token.Equals("AT", StringComparison.OrdinalIgnoreCase)
            || token.Equals("LV", StringComparison.OrdinalIgnoreCase)
            || token.Equals("ATFN", StringComparison.OrdinalIgnoreCase)
        )
        {
            return 1;
        }

        if (
            token.Equals("ONHO", StringComparison.OrdinalIgnoreCase)
            || token.Equals("ONH", StringComparison.OrdinalIgnoreCase)
            || token.Equals("ONHS", StringComparison.OrdinalIgnoreCase)
            || token.Equals("OTG", StringComparison.OrdinalIgnoreCase)
        )
        {
            return 0;
        }

        return null;
    }

    /// <summary>
    /// Splits concatenated commands like "FH 270 CM 5000 SPD 190" into "FH 270, CM 5000, SPD 190".
    /// Uses greedy parsing via NavigationDatabase.Instance: each verb consumes as many tokens as
    /// CommandParser.Parse can handle, only splitting when adding the next token fails.
    /// Falls back to the heuristic (strict single-arg verb-arg pairs) when the greedy parse does
    /// not produce multiple parts.
    /// </summary>
    public static string ExpandMultiCommand(string input)
    {
        if (input.Contains(',') || input.Contains(';'))
        {
            return input;
        }

        string[] tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length >= 2)
        {
            return ExpandMultiCommandGreedy(tokens);
        }

        if (tokens.Length < 4 || tokens.Length % 2 != 0)
        {
            return string.Join(' ', tokens);
        }

        return ExpandMultiCommandHeuristic(tokens);
    }

    internal static readonly HashSet<string> ConditionPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ONHO",
        "ONH",
        "AT",
        "ATFN",
        "LV",
        "GIVEWAY",
        "BEHIND",
        "GW",
    };

    /// <summary>
    /// Promotes commas before condition keywords to semicolons so that
    /// "cm 020, at oak30num cm 014" parses as "cm 020; at oak30num cm 014".
    /// </summary>
    private static readonly Regex CommaBeforeCondition = new(@",\s*(?=(?:AT|LV|ATFN|ONHO|ONH)\s)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Promotes a comma before GIVEWAY/BEHIND/GW to a semicolon ONLY in the condition form
    /// (callsign followed by a ground command verb), e.g. "taxi S, GIVEWAY X TAXI T" →
    /// "taxi S; GIVEWAY X TAXI T". A standalone give-way (GIVEWAY &lt;callsign&gt;) keeps its comma so it
    /// stays a parallel command in the same block — a sequential give-way block never fires behind an
    /// active ground phase. Ground verbs come from the registry to track
    /// <see cref="CommandParser.IsGiveWayConditionVerb"/>.
    /// </summary>
    private static readonly Regex CommaBeforeGiveWayCondition = BuildCommaBeforeGiveWayConditionRegex();

    private static Regex BuildCommaBeforeGiveWayConditionRegex()
    {
        IEnumerable<string> groundVerbs = new[]
        {
            CanonicalCommandType.Taxi,
            CanonicalCommandType.AssignRunway,
            CanonicalCommandType.Pushback,
            CanonicalCommandType.ForcedPushback,
            CanonicalCommandType.FollowGround,
        }
            .SelectMany(CommandRegistry.AliasesFor)
            .Select(Regex.Escape);
        string alternation = string.Join("|", groundVerbs);
        return new Regex($@",\s*(?=(?:GIVEWAY|BEHIND|GW)\s+\S+\s+(?:{alternation})(?:\s|$))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// Greedy splitting: each verb consumes tokens until Parse fails, then the next token
    /// must be a new verb. Falls back to returning input unchanged if splitting fails.
    /// </summary>
    private static string ExpandMultiCommandGreedy(string[] tokens)
    {
        // Don't split commands that start with condition prefixes — ParseBlock handles those
        if (ConditionPrefixes.Contains(tokens[0]))
        {
            return string.Join(' ', tokens);
        }

        var parts = new List<string>();
        int i = 0;

        while (i < tokens.Length)
        {
            // Current token should be a verb — try parsing with increasing arg counts
            string? lastGood = null;
            int lastGoodEnd = i;

            for (int end = i + 1; end <= tokens.Length; end++)
            {
                string candidate = string.Join(' ', tokens[i..end]);
                ParseResult<ParsedCommand> result = CommandParser.Parse(candidate);
                if (result.IsSuccess)
                {
                    lastGood = candidate;
                    lastGoodEnd = end;
                }
            }

            if (lastGood is null)
            {
                // First token doesn't parse as any command — can't split
                return string.Join(' ', tokens);
            }

            parts.Add(lastGood);
            i = lastGoodEnd;
        }

        return parts.Count >= 2 ? string.Join(", ", parts) : string.Join(' ', tokens);
    }

    /// <summary>
    /// Heuristic splitting: strict alternating verb-arg pairs where all verbs are single-arg.
    /// Used when no NavigationDatabase is available (client-side scheme parsing).
    /// </summary>
    private static string ExpandMultiCommandHeuristic(string[] tokens)
    {
        if (tokens.Length < 4 || tokens.Length % 2 != 0)
        {
            return string.Join(' ', tokens);
        }

        HashSet<string> singleArgVerbs = CommandRegistry.SingleArgAliases;

        for (int i = 0; i < tokens.Length; i += 2)
        {
            if (!singleArgVerbs.Contains(tokens[i]))
            {
                return string.Join(' ', tokens);
            }
        }

        var parts = new List<string>();
        for (int i = 0; i < tokens.Length; i += 2)
        {
            parts.Add($"{tokens[i]} {tokens[i + 1]}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Expands "SPD X UNTIL Y" shorthand within each semicolon-separated block.
    /// Supports distance-based UNTIL (numeric Y → ATFN), fix-based UNTIL (alpha Y → AT),
    /// and ATCTrainer alias (SPD X FIXNAME → AT).
    /// </summary>
    public static string ExpandSpeedUntil(string input) => ExpandSpeedUntil(input, CommandScheme.Default());

    public static string ExpandSpeedUntil(string input, CommandScheme scheme)
    {
        IReadOnlyList<string> speedAliases = GetAliases(scheme, CanonicalCommandType.Speed);
        // Split by semicolons to process blocks independently
        string[] blocks = input.Split(';');
        var result = new List<string>();

        for (int i = 0; i < blocks.Length; i++)
        {
            string block = blocks[i].Trim();

            // Match "SPD X UNTIL Y" where Y is numeric (distance)
            if (TryParseSpeedUntilDistance(block, speedAliases, out string? spdPart, out string? distPart))
            {
                // Look at the next block for chaining
                if (i + 1 < blocks.Length)
                {
                    string nextBlock = blocks[i + 1].Trim();
                    if (TryParseSpeedUntilDistance(nextBlock, speedAliases, out string? nextSpdPart, out string? nextDistPart))
                    {
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
            if (TryParseSpeedUntilFix(block, speedAliases, out spdPart, out string? fixName))
            {
                result.Add(spdPart);
                result.Add($"AT {fixName} RNS");
                continue;
            }

            // Match "SPD X FIXNAME" (ATCTrainer alias for SPD X UNTIL FIXNAME)
            if (TryParseSpeedFixAlias(block, speedAliases, out spdPart, out fixName))
            {
                result.Add(spdPart);
                result.Add($"AT {fixName} RNS");
                continue;
            }

            result.Add(block);
        }

        return string.Join("; ", result);
    }

    private static IReadOnlyList<string> GetAliases(CommandScheme scheme, CanonicalCommandType type) =>
        scheme.Patterns.TryGetValue(type, out CommandPattern? pattern) ? pattern.Aliases : CommandRegistry.AliasesFor(type);

    private static bool StartsWithSchemeAlias(string input, CommandScheme scheme, CanonicalCommandType type)
    {
        foreach (string alias in GetAliases(scheme, type))
        {
            if (input.Length <= alias.Length)
            {
                continue;
            }

            if ((input[alias.Length] == ' ') && (input.StartsWith(alias, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSchemeAlias(string token, IReadOnlyList<string> aliases)
    {
        foreach (string alias in aliases)
        {
            if (string.Equals(alias, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseSpeedUntilDistance(string block, IReadOnlyList<string> speedAliases, out string spdPart, out string distPart)
    {
        spdPart = "";
        distPart = "";

        string[] tokens = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (
            (tokens.Length != 4)
            || (!IsSchemeAlias(tokens[0], speedAliases))
            || (!IsSpeedToken(tokens[1]))
            || (!tokens[2].Equals("UNTIL", StringComparison.OrdinalIgnoreCase))
            || (!IsDistanceToken(tokens[3]))
        )
        {
            return false;
        }

        spdPart = $"{tokens[0]} {tokens[1]}";
        distPart = tokens[3];
        return true;
    }

    private static bool TryParseSpeedUntilFix(string block, IReadOnlyList<string> speedAliases, out string spdPart, out string fixName)
    {
        spdPart = "";
        fixName = "";

        string[] tokens = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (
            (tokens.Length != 4)
            || (!IsSchemeAlias(tokens[0], speedAliases))
            || (!IsSpeedToken(tokens[1]))
            || (!tokens[2].Equals("UNTIL", StringComparison.OrdinalIgnoreCase))
            || (!IsFixToken(tokens[3]))
        )
        {
            return false;
        }

        spdPart = $"{tokens[0]} {tokens[1]}";
        fixName = tokens[3].ToUpperInvariant();
        return true;
    }

    private static bool TryParseSpeedFixAlias(string block, IReadOnlyList<string> speedAliases, out string spdPart, out string fixName)
    {
        spdPart = "";
        fixName = "";

        string[] tokens = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if ((tokens.Length != 3) || (!IsSchemeAlias(tokens[0], speedAliases)) || (!IsSpeedToken(tokens[1])) || (!IsFixToken(tokens[2])))
        {
            return false;
        }

        spdPart = $"{tokens[0]} {tokens[1]}";
        fixName = tokens[2].ToUpperInvariant();
        return true;
    }

    private static bool IsSpeedToken(string token) => Regex.IsMatch(token, @"^\d+[+\-]?$");

    private static bool IsDistanceToken(string token) => Regex.IsMatch(token, @"^\d+(?:\.\d+)?$");

    private static bool IsFixToken(string token) => Regex.IsMatch(token, @"^[A-Z]{2,5}$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Expands "WAIT N cmd" and "DELAY N cmd" patterns into "WAIT N; cmd".
    /// Handles chaining: "WAIT 5 WAIT 10 FH 270" → "WAIT 5; WAIT 10; FH 270".
    /// Also normalizes standalone DELAY N to WAIT N.
    /// </summary>
    public static string ExpandWait(string input)
    {
        string[] blocks = input.Split(';');
        var result = new List<string>();

        foreach (string rawBlock in blocks)
        {
            string block = rawBlock.Trim();
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
        string[] tokens = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return;
        }

        string upper0 = tokens[0].ToUpperInvariant();

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
            string rewritten = "AT " + string.Join(" ", tokens[1..]);
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
        string remainder = string.Join(" ", tokens[2..]);
        ExpandWaitBlock(remainder, result);
    }

    private static string? NormalizeDelayArg(string? arg)
    {
        if (arg is null)
        {
            return null;
        }

        if (int.TryParse(arg, out int secs) && secs >= 0)
        {
            return secs.ToString();
        }

        int colonIdx = arg.IndexOf(':');
        if (
            colonIdx > 0
            && colonIdx < arg.Length - 1
            && int.TryParse(arg[..colonIdx], out int minutes)
            && int.TryParse(arg[(colonIdx + 1)..], out int seconds)
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
