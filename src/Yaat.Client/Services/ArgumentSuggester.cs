using System.Collections.ObjectModel;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.Services;

/// <summary>
/// Provides argument-level autocomplete suggestions driven by CommandRegistry metadata.
/// Literal overload parameters become selectable options; TypeHint-based parameters
/// (runway, fix) get contextual suggestions from the loaded data.
/// </summary>
internal static class ArgumentSuggester
{
    /// <summary>
    /// Tries to add argument suggestions for the current command verb + partial argument.
    /// Returns true if this command type has argument suggestions (even if none matched the partial).
    /// </summary>
    internal static bool TryAddArgumentSuggestions(
        string fragment,
        string fullText,
        CommandScheme scheme,
        AircraftModel? targetAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        FixDatabase? fixDb,
        string? primaryAirportId,
        int maxSuggestions
    )
    {
        var words = fragment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 1)
        {
            return false;
        }

        int verbIndex = FindVerbIndex(words, scheme);
        if (verbIndex < 0)
        {
            return false;
        }

        var verb = words[verbIndex];
        var hasTrailingSpace = fragment.EndsWith(' ');
        var wordsAfterVerb = words.Length - verbIndex - 1;
        var isAfterVerb = hasTrailingSpace || wordsAfterVerb > 0;

        if (!isAfterVerb)
        {
            return false;
        }

        // RWY is a special rewrite verb not in the registry
        if (string.Equals(verb, "RWY", StringComparison.OrdinalIgnoreCase))
        {
            var argsAfterVerb = words.Length - verbIndex - 1;
            if (argsAfterVerb <= 1 || (!hasTrailingSpace && argsAfterVerb == 1))
            {
                var partial = hasTrailingSpace ? "" : (wordsAfterVerb > 0 ? words[^1] : "");
                var prefix = FixSuggester.GetTextBeforeLastWord(fullText);
                AddRunwaySuggestions(partial, prefix, suggestions, fixDb, primaryAirportId, maxSuggestions);
            }

            return true;
        }

        var def = FindCommandDefinition(verb, scheme);
        if (def is null)
        {
            return false;
        }

        return AddRegistrySuggestions(
            def,
            words,
            verbIndex,
            hasTrailingSpace,
            fullText,
            targetAircraft,
            suggestions,
            fixDb,
            primaryAirportId,
            maxSuggestions
        );
    }

    private static bool AddRegistrySuggestions(
        CommandDefinition def,
        string[] words,
        int verbIndex,
        bool hasTrailingSpace,
        string fullText,
        AircraftModel? targetAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        FixDatabase? fixDb,
        string? primaryAirportId,
        int maxSuggestions
    )
    {
        var wordsAfterVerb = words.Length - verbIndex - 1;
        // Parameter index: which positional arg the user is currently typing
        // If trailing space, they've completed wordsAfterVerb args and are starting the next
        int paramIndex = hasTrailingSpace ? wordsAfterVerb : Math.Max(0, wordsAfterVerb - 1);
        var partial = hasTrailingSpace ? "" : (wordsAfterVerb > 0 ? words[^1] : "");
        var prefix = FixSuggester.GetTextBeforeLastWord(fullText);

        // Collect what kinds of suggestions exist at this parameter position
        bool hasLiterals = false;
        bool hasRunway = false;
        bool hasFix = false;

        foreach (var overload in def.Overloads)
        {
            if (paramIndex >= overload.Parameters.Length)
            {
                continue;
            }

            // Check that any earlier literal params match what the user actually typed
            if (!OverloadMatchesPrecedingArgs(overload, words, verbIndex, paramIndex))
            {
                continue;
            }

            var param = overload.Parameters[paramIndex];
            if (param.IsLiteral)
            {
                hasLiterals = true;
            }
            else if (IsRunwayHint(param.TypeHint))
            {
                hasRunway = true;
            }
            else if (IsFixHint(param.TypeHint))
            {
                hasFix = true;
            }
        }

        if (!hasLiterals && !hasRunway && !hasFix)
        {
            return false;
        }

        // Add literal options from overloads at this param position
        if (hasLiterals)
        {
            AddOverloadLiteralSuggestions(def, words, verbIndex, paramIndex, partial, prefix, suggestions, maxSuggestions);
        }

        // Add contextual suggestions based on TypeHint
        if (hasRunway)
        {
            AddRunwaySuggestions(partial, prefix, suggestions, fixDb, primaryAirportId, maxSuggestions);
        }

        if (hasFix)
        {
            FixSuggester.AddFixSuggestions(partial, prefix, targetAircraft, suggestions, fixDb, maxSuggestions);
        }

        return true;
    }

    private static bool OverloadMatchesPrecedingArgs(CommandOverload overload, string[] words, int verbIndex, int paramIndex)
    {
        for (int i = 0; i < paramIndex && i < overload.Parameters.Length; i++)
        {
            var param = overload.Parameters[i];
            if (!param.IsLiteral)
            {
                continue;
            }

            var wordIndex = verbIndex + 1 + i;
            if (wordIndex >= words.Length)
            {
                return false;
            }

            if (!string.Equals(words[wordIndex], param.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddOverloadLiteralSuggestions(
        CommandDefinition def,
        string[] words,
        int verbIndex,
        int paramIndex,
        string partial,
        string prefix,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        // Track which literal values we've already added to avoid duplicates
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var overload in def.Overloads)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                break;
            }

            if (paramIndex >= overload.Parameters.Length)
            {
                continue;
            }

            if (!OverloadMatchesPrecedingArgs(overload, words, verbIndex, paramIndex))
            {
                continue;
            }

            var param = overload.Parameters[paramIndex];
            if (!param.IsLiteral)
            {
                continue;
            }

            if (!seen.Add(param.Name))
            {
                continue;
            }

            var description = overload.UsageHint ?? overload.VariantLabel ?? "";
            AddOption(param.Name, description, partial, prefix, suggestions, maxSuggestions);
        }
    }

    private static bool IsRunwayHint(string typeHint)
    {
        return typeHint.Contains("runway", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFixHint(string typeHint)
    {
        return typeHint.Contains("fix name", StringComparison.OrdinalIgnoreCase)
            || typeHint.Contains("approach ID", StringComparison.OrdinalIgnoreCase);
    }

    internal static CommandDefinition? FindCommandDefinition(string verb, CommandScheme scheme)
    {
        foreach (var (type, def) in CommandRegistry.All)
        {
            if (!scheme.Patterns.TryGetValue(type, out var pattern))
            {
                continue;
            }

            foreach (var alias in pattern.Aliases)
            {
                if (string.Equals(alias, verb, StringComparison.OrdinalIgnoreCase))
                {
                    return def;
                }
            }
        }

        return null;
    }

    private static void AddRunwaySuggestions(
        string partial,
        string prefix,
        ObservableCollection<SuggestionItem> suggestions,
        FixDatabase? fixDb,
        string? primaryAirportId,
        int maxSuggestions
    )
    {
        if (fixDb is null || string.IsNullOrEmpty(primaryAirportId))
        {
            return;
        }

        var runways = fixDb.GetRunways(primaryAirportId);
        foreach (var rwy in runways)
        {
            string[] designators = rwy.Id.End1.Equals(rwy.Id.End2, StringComparison.OrdinalIgnoreCase) ? [rwy.Id.End1] : [rwy.Id.End1, rwy.Id.End2];

            foreach (var designator in designators)
            {
                if (suggestions.Count >= maxSuggestions)
                {
                    return;
                }

                if (partial.Length > 0 && !designator.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.Command,
                        Text = designator,
                        Description = "Runway",
                        InsertText = prefix + designator + " ",
                    }
                );
            }
        }
    }

    private static void AddOption(
        string value,
        string description,
        string partial,
        string prefix,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        if (suggestions.Count >= maxSuggestions)
        {
            return;
        }

        if (partial.Length > 0 && !value.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        suggestions.Add(
            new SuggestionItem
            {
                Kind = SuggestionKind.Command,
                Text = value,
                Description = description,
                InsertText = prefix + value + " ",
            }
        );
    }

    private static int FindVerbIndex(string[] words, CommandScheme scheme)
    {
        if (IsRecognizedVerb(words[0], scheme))
        {
            return 0;
        }

        if (words.Length >= 2 && IsRecognizedVerb(words[1], scheme))
        {
            return 1;
        }

        return -1;
    }

    private static bool IsRecognizedVerb(string token, CommandScheme scheme)
    {
        // RWY is a special rewrite verb not in the registry
        if (string.Equals(token, "RWY", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Any verb that maps to a registry command with arguments gets suggestions
        return FindCommandDefinition(token, scheme) is { ArgMode: not ArgMode.None };
    }
}
