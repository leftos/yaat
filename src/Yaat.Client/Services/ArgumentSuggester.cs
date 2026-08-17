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
        IReadOnlyCollection<string> taxiwayNames,
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

        return AddRegistrySuggestions(parsed, fullText, targetAircraft, aircraft, suggestions, primaryAirportId, taxiwayNames, maxSuggestions);
    }

    private static bool AddRegistrySuggestions(
        CommandInputParseResult parsed,
        string fullText,
        AircraftModel? targetAircraft,
        IReadOnlyCollection<AircraftModel> aircraft,
        ObservableCollection<SuggestionItem> suggestions,
        string? primaryAirportId,
        IReadOnlyCollection<string> taxiwayNames,
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
        bool hasTaxiway = false;
        bool hasFix = false;
        bool hasApproach = false;
        bool hasCallsign = false;
        bool hasPatternLeg = false;
        bool hasAirway = false;

        // When the caret sits inside a compound modifier's argument region (e.g. the token after
        // `HS` in `TAXI A HS `), the modifier's own ArgHint decides the value suggestions — the
        // overload's clamped trailing parameter no longer applies once the parser has switched
        // modes (`CROSS 28R HS ` wants taxiway/runway targets, not more crossing runways).
        var activeModifier = FindActiveModifier(def, parsed);
        if (activeModifier is { ArgHint: { } modifierHint })
        {
            hasRunway = IsRunwayHint(modifierHint);
            hasTaxiway = IsTaxiwayHint(modifierHint);
        }
        else
        {
            foreach (var overload in def.Overloads)
            {
                int effectiveIndex = paramIndex;
                if (effectiveIndex >= overload.Parameters.Length)
                {
                    // A trailing repeatable parameter (e.g. CROSS's runway list) keeps offering its
                    // own suggestions past its declared index, so clamp onto it instead of skipping.
                    int lastIndex = overload.Parameters.Length - 1;
                    if (lastIndex < 0 || !overload.Parameters[lastIndex].Repeatable)
                    {
                        continue;
                    }
                    effectiveIndex = lastIndex;
                }

                // Check that any earlier literal params match what the user actually typed
                if (!OverloadMatchesPrecedingArgs(overload, parsed.Tokens, parsed.VerbIndex, effectiveIndex))
                {
                    continue;
                }

                var param = overload.Parameters[effectiveIndex];
                if (param.IsLiteral)
                {
                    hasLiterals = true;
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
                else if (IsAirwayHint(param.TypeHint))
                {
                    hasAirway = true;
                }
                else
                {
                    // Runway and taxiway are not mutually exclusive ("taxiway/runway") — check both.
                    hasRunway |= IsRunwayHint(param.TypeHint);
                    hasTaxiway |= IsTaxiwayHint(param.TypeHint);
                }
            }
        }

        // Suggest compound modifiers once every overload's fixed (non-repeatable) parameters are
        // satisfied. A trailing repeatable parameter is optional-to-repeat, so it does not block a
        // modifier — CROSS 28R can offer both another runway and the HS modifier.
        bool hasModifiers = false;
        if (def.CompoundModifiers is { Length: > 0 } && def.Overloads.All(o => paramIndex >= FixedParamCount(o)))
        {
            hasModifiers = true;
        }

        if (!hasLiterals && !hasRunway && !hasTaxiway && !hasFix && !hasApproach && !hasCallsign && !hasPatternLeg && !hasAirway && !hasModifiers)
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

        if (hasTaxiway)
        {
            AddTaxiwaySuggestions(fullText, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, partial, suggestions, taxiwayNames, maxSuggestions);
        }

        if (hasApproach)
        {
            AddApproachSuggestions(fullText, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, partial, targetAircraft, suggestions, maxSuggestions);
        }

        if (hasAirway)
        {
            AddAirwaySuggestions(fullText, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, partial, targetAircraft, suggestions, maxSuggestions);
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

    /// <summary>
    /// The count of fixed (non-repeatable) parameters in an overload. A single trailing repeatable
    /// parameter is optional-to-repeat, so it isn't counted — a compound modifier may legally follow
    /// the fixed parameters without any repeats.
    /// </summary>
    private static int FixedParamCount(CommandOverload overload)
    {
        int count = overload.Parameters.Length;
        if (count > 0 && overload.Parameters[count - 1].Repeatable)
        {
            count--;
        }
        return count;
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

    private static bool IsTaxiwayHint(string typeHint)
    {
        return typeHint.Contains("taxiway", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The compound modifier whose argument region the caret sits in, or null when the active token
    /// still belongs to the overload parameters. Mirrors the parser's mode semantics: a modifier
    /// keyword switches mode until the next keyword; a non-repeatable modifier's mode lasts one
    /// argument token (<c>RWY 28R</c>), a repeatable one until the next keyword (<c>HS C E</c>).
    /// Keyword-only modifiers (<c>NODEL</c>) and the <c>@</c>/<c>$</c> sigils (self-contained
    /// tokens) never open an argument region.
    /// </summary>
    private static CompoundModifier? FindActiveModifier(CommandDefinition def, CommandInputParseResult parsed)
    {
        if (def.CompoundModifiers is not { Length: > 0 } modifiers)
        {
            return null;
        }

        CompoundModifier? active = null;
        int argTokensSinceKeyword = 0;
        for (int i = parsed.VerbIndex + 1; i < parsed.ActiveTokenIndex && i < parsed.Tokens.Length; i++)
        {
            var keywordMatch = modifiers.FirstOrDefault(m =>
                (m.ArgHint is not null) && string.Equals(m.Keyword, parsed.Tokens[i], StringComparison.OrdinalIgnoreCase)
            );
            if (keywordMatch is not null)
            {
                active = keywordMatch;
                argTokensSinceKeyword = 0;
                continue;
            }

            argTokensSinceKeyword++;
            if (active is { Repeatable: false } && argTokensSinceKeyword >= 1)
            {
                active = null;
            }
        }

        return active;
    }

    private static bool IsAirwayHint(string typeHint)
    {
        return typeHint.Contains("airway", StringComparison.OrdinalIgnoreCase)
            || typeHint.Contains("military route", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Airway / military-route values for JAWY and CMTR.
    ///
    /// Tier 1 is the routes on the selected aircraft's filed flight plan — the same discipline
    /// <c>GetFiledAirways</c> uses for the radar context menu, because offering all several thousand
    /// published airways is useless. Tier 2 opens a prefix scan once two characters are typed.
    /// </summary>
    private static void AddAirwaySuggestions(
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
        AircraftModel? targetAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        var navDb = NavigationDatabase.Instance;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        foreach (var token in (targetAircraft?.Route ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = token.Split('.')[^1];
            if (navDb.IsAirway(name) && seen.Add(name))
            {
                candidates.Add(name);
            }
        }

        if (partial.Length >= 2)
        {
            foreach (var id in navDb.AirwayIds)
            {
                if (id.StartsWith(partial, StringComparison.OrdinalIgnoreCase) && seen.Add(id))
                {
                    candidates.Add(id);
                }
            }
        }

        candidates.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var id in candidates)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                return;
            }

            if (partial.Length > 0 && !id.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var route = navDb.GetMilitaryRoute(id);
            var description = route is null ? "Airway" : $"Military training route ({route.Type.ToString().ToUpperInvariant()})";
            var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, id);
            suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = id,
                    Description = description,
                    InsertText = insertText,
                    CaretAfterInsert = caret,
                }
            );
        }
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

    /// <summary>
    /// Taxiway names from the loaded ground layout for taxiway-typed parameters (HS targets, TAXI
    /// routes, runway exits). Empty when no ground layout is loaded — the dropdown then degrades to
    /// whatever else the parameter offers (runways for <c>taxiway/runway</c> hints).
    /// </summary>
    private static void AddTaxiwaySuggestions(
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
        ObservableCollection<SuggestionItem> suggestions,
        IReadOnlyCollection<string> taxiwayNames,
        int maxSuggestions
    )
    {
        foreach (var name in taxiwayNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (suggestions.Count >= maxSuggestions)
            {
                return;
            }

            if (partial.Length > 0 && !name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, name);
            suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = name,
                    Description = "Taxiway",
                    InsertText = insertText,
                    CaretAfterInsert = caret,
                }
            );
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
        string normalizedPartial = NavigationDatabase.NormalizeApproachShorthand(partial);
        foreach (var approach in approaches)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                return;
            }

            if (
                partial.Length > 0
                && !approach.ApproachId.StartsWith(partial, StringComparison.OrdinalIgnoreCase)
                && !approach.ApproachId.StartsWith(normalizedPartial, StringComparison.OrdinalIgnoreCase)
            )
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
