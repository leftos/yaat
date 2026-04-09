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
        CommandInputParseResult parsed,
        string fullText,
        AircraftModel? targetAircraft,
        IReadOnlyCollection<AircraftModel> aircraft,
        ObservableCollection<SuggestionItem> suggestions,
        string? primaryAirportId,
        int maxSuggestions
    )
    {
        if (parsed.VerbIndex < 0 || parsed.Definition is null)
        {
            return false;
        }

        if (parsed.Definition.ArgMode == ArgMode.None)
        {
            return false;
        }

        var wordsAfterVerb = parsed.Tokens.Length - parsed.VerbIndex - 1;
        var isAfterVerb = parsed.HasTrailingSpace || wordsAfterVerb > 0;

        if (!isAfterVerb)
        {
            return false;
        }

        return AddRegistrySuggestions(
            parsed.Definition,
            parsed.Tokens,
            parsed.VerbIndex,
            parsed.HasTrailingSpace,
            fullText,
            targetAircraft,
            aircraft,
            suggestions,
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
        IReadOnlyCollection<AircraftModel> aircraft,
        ObservableCollection<SuggestionItem> suggestions,
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

        // CVA has a custom parser path (LEFT|RIGHT|FOLLOW <cs>) that isn't declared via
        // Overloads or CompoundModifiers, so we handle its callsign flyout here.
        if (def.Type == CanonicalCommandType.ClearedVisualApproach && wordsAfterVerb >= 1)
        {
            // Find the token immediately before the position being typed. When hasTrailingSpace
            // is true the last word itself is "previous"; otherwise it is the second-to-last.
            int prevIdx = hasTrailingSpace ? words.Length - 1 : words.Length - 2;
            if (prevIdx > verbIndex && string.Equals(words[prevIdx], "FOLLOW", StringComparison.OrdinalIgnoreCase))
            {
                AddCallsignSuggestions(partial, prefix, aircraft, suggestions, maxSuggestions);
                return true;
            }
        }

        // Collect what kinds of suggestions exist at this parameter position
        bool hasLiterals = false;
        bool hasRunway = false;
        bool hasFix = false;
        bool hasApproach = false;
        bool hasCallsign = false;

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
            else if (IsApproachHint(param.TypeHint))
            {
                hasApproach = true;
            }
            else if (IsFixHint(param.TypeHint))
            {
                hasFix = true;
            }
            else if (IsCallsignHint(param.TypeHint))
            {
                hasCallsign = true;
            }
        }

        // When past all overload parameters, suggest compound modifiers if available
        bool hasModifiers = false;
        if (!hasLiterals && !hasRunway && !hasFix && !hasApproach && !hasCallsign && def.CompoundModifiers is { Length: > 0 })
        {
            bool allOverloadsExhausted = def.Overloads.All(o => paramIndex >= o.Parameters.Length);
            if (allOverloadsExhausted)
            {
                hasModifiers = true;
            }
        }

        if (!hasLiterals && !hasRunway && !hasFix && !hasApproach && !hasCallsign && !hasModifiers)
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
            AddRunwaySuggestions(partial, prefix, suggestions, primaryAirportId, maxSuggestions);
        }

        if (hasApproach)
        {
            AddApproachSuggestions(partial, prefix, targetAircraft, suggestions, maxSuggestions);
        }

        if (hasFix)
        {
            FixSuggester.AddFixSuggestions(partial, prefix, targetAircraft, suggestions, maxSuggestions);
        }

        if (hasCallsign)
        {
            AddCallsignSuggestions(partial, prefix, aircraft, suggestions, maxSuggestions);
        }

        if (hasModifiers)
        {
            AddCompoundModifierSuggestions(def, words, verbIndex, partial, prefix, suggestions, maxSuggestions);
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

    private static void AddCompoundModifierSuggestions(
        CommandDefinition def,
        string[] words,
        int verbIndex,
        string partial,
        string prefix,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        // Collect already-typed modifier keywords so we don't re-suggest non-repeatable ones
        var typedModifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = verbIndex + 1; i < words.Length; i++)
        {
            typedModifiers.Add(words[i]);
        }

        foreach (var mod in def.CompoundModifiers!)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                break;
            }

            // Skip non-repeatable modifiers that are already typed
            if (!mod.Repeatable && typedModifiers.Contains(mod.Keyword))
            {
                continue;
            }

            var description = mod.ArgHint is not null ? $"+ {mod.ArgHint}" : "modifier";
            AddOption(mod.Keyword, description, partial, prefix, suggestions, maxSuggestions);
        }
    }

    private static bool IsRunwayHint(string typeHint)
    {
        return typeHint.Contains("runway", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFixHint(string typeHint)
    {
        return typeHint.Contains("fix name", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApproachHint(string typeHint)
    {
        return typeHint.Contains("approach ID", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCallsignHint(string typeHint)
    {
        return typeHint.Contains("callsign", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCallsignSuggestions(
        string partial,
        string prefix,
        IReadOnlyCollection<AircraftModel> aircraft,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        foreach (var ac in aircraft)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                return;
            }

            if (partial.Length > 0 && !ac.Callsign.Contains(partial, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var desc = $"{ac.AircraftType} {ac.Departure}-{ac.Destination}".Trim();
            suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Callsign,
                    Text = ac.Callsign,
                    Description = desc,
                    InsertText = prefix + ac.Callsign + " ",
                }
            );
        }
    }

    private static void AddRunwaySuggestions(
        string partial,
        string prefix,
        ObservableCollection<SuggestionItem> suggestions,
        string? primaryAirportId,
        int maxSuggestions
    )
    {
        if (string.IsNullOrEmpty(primaryAirportId))
        {
            return;
        }

        var runways = NavigationDatabase.Instance.GetRunways(primaryAirportId);
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

    private static void AddApproachSuggestions(
        string partial,
        string prefix,
        AircraftModel? targetAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        if (string.IsNullOrEmpty(targetAircraft?.Destination))
        {
            return;
        }

        var approaches = NavigationDatabase.Instance.GetApproaches(targetAircraft.Destination);
        foreach (var approach in approaches)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                return;
            }

            if (partial.Length > 0 && !approach.ApproachId.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = approach.ApproachId,
                    Description = approach.ApproachTypeName,
                    InsertText = prefix + approach.ApproachId + " ",
                }
            );
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
}
