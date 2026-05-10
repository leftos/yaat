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

        if (parsed.ParameterIndex < 0)
        {
            return false;
        }

        return AddRegistrySuggestions(parsed, fullText, targetAircraft, aircraft, suggestions, primaryAirportId, maxSuggestions);
    }

    private static bool AddRegistrySuggestions(
        CommandInputParseResult parsed,
        string fullText,
        AircraftModel? targetAircraft,
        IReadOnlyCollection<AircraftModel> aircraft,
        ObservableCollection<SuggestionItem> suggestions,
        string? primaryAirportId,
        int maxSuggestions
    )
    {
        var def = parsed.Definition!;
        var paramIndex = parsed.ParameterIndex;
        var partial = fullText[parsed.ActiveTokenStart..parsed.CaretIndex];

        // CVA has a custom parser path (LEFT|RIGHT|FOLLOW <cs>) that isn't declared via
        // Overloads or CompoundModifiers, so we handle its callsign flyout here.
        if (def.Type == CanonicalCommandType.ClearedVisualApproach && paramIndex >= 1)
        {
            // The token immediately before the active position. We look at the token at
            // paramIndex - 1 within typedArgs.
            int prevTypedIndex = paramIndex - 1;
            if (
                prevTypedIndex >= 0
                && prevTypedIndex < parsed.TypedArgs.Length
                && string.Equals(parsed.TypedArgs[prevTypedIndex], "FOLLOW", StringComparison.OrdinalIgnoreCase)
            )
            {
                AddCallsignSuggestions(fullText, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, partial, aircraft, suggestions, maxSuggestions);
                return true;
            }
        }

        // Collect what kinds of suggestions exist at this parameter position
        bool hasLiterals = false;
        bool hasRunway = false;
        bool hasFix = false;
        bool hasApproach = false;
        bool hasCallsign = false;
        bool hasPatternLeg = false;

        foreach (var overload in def.Overloads)
        {
            if (paramIndex >= overload.Parameters.Length)
            {
                continue;
            }

            // Check that any earlier literal params match what the user actually typed
            if (!OverloadMatchesPrecedingArgs(overload, parsed.Tokens, parsed.VerbIndex, paramIndex))
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
            else if (IsPatternLegHint(param.TypeHint))
            {
                hasPatternLeg = true;
            }
        }

        // When past all overload parameters, suggest compound modifiers if available
        bool hasModifiers = false;
        if (!hasLiterals && !hasRunway && !hasFix && !hasApproach && !hasCallsign && !hasPatternLeg && def.CompoundModifiers is { Length: > 0 })
        {
            bool allOverloadsExhausted = def.Overloads.All(o => paramIndex >= o.Parameters.Length);
            if (allOverloadsExhausted)
            {
                hasModifiers = true;
            }
        }

        if (!hasLiterals && !hasRunway && !hasFix && !hasApproach && !hasCallsign && !hasPatternLeg && !hasModifiers)
        {
            return false;
        }

        if (hasLiterals)
        {
            AddOverloadLiteralSuggestions(def, parsed, fullText, partial, suggestions, maxSuggestions);
        }

        if (hasRunway)
        {
            AddRunwaySuggestions(fullText, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, partial, suggestions, primaryAirportId, maxSuggestions);
        }

        if (hasApproach)
        {
            AddApproachSuggestions(fullText, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, partial, targetAircraft, suggestions, maxSuggestions);
        }

        if (hasFix)
        {
            FixSuggester.AddFixSuggestionsForActiveToken(
                fullText,
                parsed.ActiveTokenStart,
                parsed.ActiveTokenEnd,
                partial,
                targetAircraft,
                suggestions,
                maxSuggestions
            );
        }

        if (hasCallsign)
        {
            AddCallsignSuggestions(fullText, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, partial, aircraft, suggestions, maxSuggestions);
        }

        if (hasPatternLeg)
        {
            AddPatternLegSuggestions(fullText, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, partial, suggestions, maxSuggestions);
        }

        if (hasModifiers)
        {
            AddCompoundModifierSuggestions(def, parsed, fullText, partial, suggestions, maxSuggestions);
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
        CommandInputParseResult parsed,
        string fullText,
        string partial,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        var paramIndex = parsed.ParameterIndex;
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

            if (!OverloadMatchesPrecedingArgs(overload, parsed.Tokens, parsed.VerbIndex, paramIndex))
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
            AddOption(fullText, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, param.Name, description, partial, suggestions, maxSuggestions);
        }
    }

    private static void AddCompoundModifierSuggestions(
        CommandDefinition def,
        CommandInputParseResult parsed,
        string fullText,
        string partial,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        // Collect already-typed modifier keywords so we don't re-suggest non-repeatable ones
        var typedModifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = parsed.VerbIndex + 1; i < parsed.Tokens.Length; i++)
        {
            // Skip the active token itself (the user is editing it)
            if (i == parsed.ActiveTokenIndex)
            {
                continue;
            }
            typedModifiers.Add(parsed.Tokens[i]);
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
            AddOption(fullText, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, mod.Keyword, description, partial, suggestions, maxSuggestions);
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

    private static bool IsPatternLegHint(string typeHint)
    {
        return typeHint.Contains("pattern leg", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPatternLegSuggestions(
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        (string Value, string Description)[] legs = [("UPWIND", "Upwind leg"), ("CROSSWIND", "Crosswind leg"), ("DOWNWIND", "Downwind leg")];

        foreach (var (value, description) in legs)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                return;
            }

            if (partial.Length > 0 && !value.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, value);
            suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = value,
                    Description = description,
                    InsertText = insertText,
                    CaretAfterInsert = caret,
                }
            );
        }
    }

    private static void AddCallsignSuggestions(
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
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

            var desc = $"{ac.FiledAircraftType} {ac.Departure}-{ac.Destination}".Trim();
            var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, ac.Callsign);
            suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Callsign,
                    Text = ac.Callsign,
                    Description = desc,
                    InsertText = insertText,
                    CaretAfterInsert = caret,
                }
            );
        }
    }

    private static void AddRunwaySuggestions(
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
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

                var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, designator);
                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.Command,
                        Text = designator,
                        Description = "Runway",
                        InsertText = insertText,
                        CaretAfterInsert = caret,
                    }
                );
            }
        }
    }

    private static void AddApproachSuggestions(
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
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

            var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, approach.ApproachId);
            suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = approach.ApproachId,
                    Description = approach.ApproachTypeName,
                    InsertText = insertText,
                    CaretAfterInsert = caret,
                }
            );
        }
    }

    private static void AddOption(
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string value,
        string description,
        string partial,
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

        var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, value);
        suggestions.Add(
            new SuggestionItem
            {
                Kind = SuggestionKind.Command,
                Text = value,
                Description = description,
                InsertText = insertText,
                CaretAfterInsert = caret,
            }
        );
    }
}
