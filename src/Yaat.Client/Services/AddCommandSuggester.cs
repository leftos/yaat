using System.Collections.ObjectModel;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.Services;

internal static class AddCommandSuggester
{
    internal static bool TryAddAddArgumentSuggestions(
        CommandInputParseResult parsed,
        string fullText,
        CommandScheme scheme,
        AircraftModel? selectedAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        string? primaryAirportId,
        int maxSuggestions
    )
    {
        if (!scheme.Patterns.TryGetValue(CanonicalCommandType.Add, out var addPattern))
        {
            return false;
        }

        if (parsed.Tokens.Length < 1 || !MatchesAnyAlias(parsed.Tokens[0], addPattern))
        {
            return false;
        }

        // Cursor-aware view of typed args. ADD always lives at index 0; completedArgs is the
        // 0-based position the user is currently editing (relative to ADD's args).
        var completedArgs = parsed.ParameterIndex;
        if (completedArgs < 0)
        {
            return false;
        }
        var partial = fullText[parsed.ActiveTokenStart..parsed.CaretIndex];

        switch (completedArgs)
        {
            case 0:
                AddAddOptions(
                    fullText,
                    parsed.ActiveTokenStart,
                    parsed.ActiveTokenEnd,
                    partial,
                    suggestions,
                    maxSuggestions,
                    ("I", "IFR — Instrument flight rules"),
                    ("V", "VFR — Visual flight rules")
                );
                break;

            case 1:
                AddAddOptions(
                    fullText,
                    parsed.ActiveTokenStart,
                    parsed.ActiveTokenEnd,
                    partial,
                    suggestions,
                    maxSuggestions,
                    ("S", "Small — GA/light aircraft"),
                    ("S+", "SmallPlus — Upper-small bizjets/commuters"),
                    ("L", "Large — Narrow-body + regional jets"),
                    ("H", "Heavy — Wide-body")
                );
                break;

            case 2:
            {
                if (parsed.Tokens.Length > 2)
                {
                    var weight = ParseWeightToken(parsed.Tokens[2]);
                    if (weight is not null)
                    {
                        AddEngineOptions(
                            fullText,
                            parsed.ActiveTokenStart,
                            parsed.ActiveTokenEnd,
                            partial,
                            weight.Value,
                            suggestions,
                            maxSuggestions
                        );
                    }
                }
                break;
            }

            case >= 3:
                AddPositionSuggestions(
                    parsed.Tokens,
                    fullText,
                    parsed.ActiveTokenStart,
                    parsed.ActiveTokenEnd,
                    partial,
                    completedArgs,
                    selectedAircraft,
                    suggestions,
                    primaryAirportId,
                    maxSuggestions
                );
                break;
        }

        return suggestions.Count > 0;
    }

    internal static void AddAddOptions(
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions,
        params (string Value, string Description)[] options
    )
    {
        foreach (var (value, desc) in options)
        {
            if (suggestions.Count >= maxSuggestions)
            {
                break;
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
                    Description = desc,
                    InsertText = insertText,
                    CaretAfterInsert = caret,
                }
            );
        }
    }

    internal static WeightClass? ParseWeightToken(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "S" => WeightClass.Small,
            "S+" => WeightClass.SmallPlus,
            "L" => WeightClass.Large,
            "H" => WeightClass.Heavy,
            _ => null,
        };
    }

    internal static EngineKind? ParseEngineToken(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "P" => EngineKind.Piston,
            "T" => EngineKind.Turboprop,
            "J" => EngineKind.Jet,
            "H" => EngineKind.Helicopter,
            _ => null,
        };
    }

    private static void AddEngineOptions(
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
        WeightClass weight,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        (string, string)[] options = weight switch
        {
            WeightClass.Small =>
            [
                ("P", "Piston — " + FormatTypes(weight, EngineKind.Piston)),
                ("T", "Turboprop — " + FormatTypes(weight, EngineKind.Turboprop)),
                ("H", "Helicopter — " + FormatTypes(weight, EngineKind.Helicopter)),
            ],
            WeightClass.SmallPlus =>
            [
                ("T", "Turboprop — " + FormatTypes(weight, EngineKind.Turboprop)),
                ("J", "Jet — " + FormatTypes(weight, EngineKind.Jet)),
            ],
            WeightClass.Large => [("J", "Jet — " + FormatTypes(weight, EngineKind.Jet))],
            WeightClass.Heavy => [("J", "Jet — " + FormatTypes(weight, EngineKind.Jet))],
            _ => [],
        };

        AddAddOptions(fullText, activeTokenStart, activeTokenEnd, partial, suggestions, maxSuggestions, options);
    }

    private static string FormatTypes(WeightClass weight, EngineKind engine)
    {
        var types = AircraftGenerator.GetTypesForCombo(weight, engine);
        return types is not null ? string.Join(", ", types) : "";
    }

    private static void AddPositionSuggestions(
        string[] words,
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
        int completedArgs,
        AircraftModel? selectedAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        string? primaryAirportId,
        int maxSuggestions
    )
    {
        if (completedArgs == 3)
        {
            if (partial.StartsWith('@'))
            {
                // User is typing @fixname — show fix suggestions
                var fixPartial = partial.Length > 1 ? partial[1..] : "";
                FixSuggester.AddAtFixSuggestionsForActiveToken(
                    fullText,
                    activeTokenStart,
                    activeTokenEnd,
                    fixPartial,
                    selectedAircraft,
                    suggestions,
                    maxSuggestions
                );
            }
            else if (partial.Contains('.'))
            {
                // User is typing an arrival route — WAYPOINT.STAR[.RUNWAY] (e.g. TBARR.TBARR4.34R).
                AddArrivalRouteSuggestions(fullText, activeTokenStart, activeTokenEnd, partial, suggestions, primaryAirportId, maxSuggestions);
            }
            else
            {
                // Show all position variant hints + runway suggestions
                AddAddOptions(
                    fullText,
                    activeTokenStart,
                    activeTokenEnd,
                    partial,
                    suggestions,
                    maxSuggestions,
                    ("-", "Airborne — -{bearing} {dist_nm} {alt_ft}"),
                    ("@", "At fix — @{fix_or_FRD} {alt_ft}, or at parking — @{spot}"),
                    ("{wpt}.{star}.{rwy}", "Arrival on a STAR — e.g. TBARR.TBARR4.34R")
                );
                AddRunwaySuggestions(fullText, activeTokenStart, activeTokenEnd, partial, suggestions, primaryAirportId, maxSuggestions);
            }
            return;
        }

        // Determine which variant the user is typing
        bool isBearingVariant = words.Length > 4 && words[4].StartsWith('-');
        bool isFixVariant = words.Length > 4 && words[4].StartsWith('@');
        bool isArrivalVariant = words.Length > 4 && !words[4].StartsWith('-') && !words[4].StartsWith('@') && words[4].Contains('.');

        if (isBearingVariant)
        {
            if (completedArgs >= 6)
            {
                AddTypeAndAirlineOverrides(words, fullText, activeTokenStart, activeTokenEnd, partial, suggestions, maxSuggestions);
            }
        }
        else if (isFixVariant)
        {
            if (completedArgs >= 5)
            {
                AddTypeAndAirlineOverrides(words, fullText, activeTokenStart, activeTokenEnd, partial, suggestions, maxSuggestions);
            }
        }
        else if (isArrivalVariant)
        {
            // Trailing args after the route token are order-independent and optional.
            AddAddOptions(
                fullText,
                activeTokenStart,
                activeTokenEnd,
                partial,
                suggestions,
                maxSuggestions,
                ("{altitude}", "Current altitude in hundreds (e.g. 110) — omit to auto-compute"),
                ("LVL", "Hold level instead of descend via"),
                ("SP", "Speed override — SP{kts}, e.g. SP250")
            );
            AddTypeAndAirlineOverrides(words, fullText, activeTokenStart, activeTokenEnd, partial, suggestions, maxSuggestions);
        }
        else
        {
            // Runway variant: after runway, next arg is either a distance (nm) or a type/airline override
            if (completedArgs == 4)
            {
                AddAddOptions(
                    fullText,
                    activeTokenStart,
                    activeTokenEnd,
                    partial,
                    suggestions,
                    maxSuggestions,
                    ("{distance}", "Distance in nm — spawns on final (e.g. 5, 10)")
                );
                AddTypeAndAirlineOverrides(words, fullText, activeTokenStart, activeTokenEnd, partial, suggestions, maxSuggestions);
            }
            else if (completedArgs >= 5)
            {
                AddTypeAndAirlineOverrides(words, fullText, activeTokenStart, activeTokenEnd, partial, suggestions, maxSuggestions);
            }
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
            // Show both ends of each physical runway
            string[] designators = rwy.Id.End1.Equals(rwy.Id.End2, StringComparison.OrdinalIgnoreCase) ? [rwy.Id.End1] : [rwy.Id.End1, rwy.Id.End2];

            foreach (var designator in designators)
            {
                if (suggestions.Count >= maxSuggestions)
                {
                    break;
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
                        Description = "Runway — lined up, or add distance for final",
                        InsertText = insertText,
                        CaretAfterInsert = caret,
                    }
                );
            }
        }
    }

    private static void AddArrivalRouteSuggestions(
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

        // partial is "WAYPOINT.", "WAYPOINT.STAR" or "WAYPOINT.STAR." — complete the STAR, then the runway,
        // both scoped to the primary airport (no global pickers).
        var dotParts = partial.Split('.');

        if (dotParts.Length == 2)
        {
            var starPartial = dotParts[1];
            foreach (var star in NavigationDatabase.Instance.GetStars(primaryAirportId))
            {
                if (suggestions.Count >= maxSuggestions)
                {
                    break;
                }

                if (starPartial.Length > 0 && !star.ProcedureId.StartsWith(starPartial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = $"{dotParts[0]}.{star.ProcedureId}";
                var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, value);
                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.Command,
                        Text = value,
                        Description = "Arrival via STAR — add .{rwy} for a runway transition",
                        InsertText = insertText,
                        CaretAfterInsert = caret,
                    }
                );
            }

            return;
        }

        if (dotParts.Length == 3)
        {
            var rwPartial = dotParts[2];
            foreach (var rwy in NavigationDatabase.Instance.GetRunways(primaryAirportId))
            {
                string[] designators = rwy.Id.End1.Equals(rwy.Id.End2, StringComparison.OrdinalIgnoreCase)
                    ? [rwy.Id.End1]
                    : [rwy.Id.End1, rwy.Id.End2];

                foreach (var designator in designators)
                {
                    if (suggestions.Count >= maxSuggestions)
                    {
                        break;
                    }

                    if (rwPartial.Length > 0 && !designator.StartsWith(rwPartial, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = $"{dotParts[0]}.{dotParts[1]}.{designator}";
                    var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, value);
                    suggestions.Add(
                        new SuggestionItem
                        {
                            Kind = SuggestionKind.Command,
                            Text = value,
                            Description = "Runway transition — descend via (add LVL to hold level)",
                            InsertText = insertText,
                            CaretAfterInsert = caret,
                        }
                    );
                }
            }
        }
    }

    private static void AddTypeAndAirlineOverrides(
        string[] words,
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        var weight = ParseWeightToken(words[2]);
        var engine = ParseEngineToken(words[3]);
        if (weight is null || engine is null)
        {
            return;
        }

        var types = AircraftGenerator.GetTypesForCombo(weight.Value, engine.Value);
        if (types is not null)
        {
            foreach (var type in types)
            {
                if (suggestions.Count >= maxSuggestions)
                {
                    break;
                }

                if (partial.Length > 0 && !type.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, type);
                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.Command,
                        Text = type,
                        Description = "Aircraft type override",
                        InsertText = insertText,
                        CaretAfterInsert = caret,
                    }
                );
            }
        }

        if (partial.Length == 0 || partial.StartsWith('*'))
        {
            var airlinePartial = partial.Length > 1 ? partial[1..] : "";
            foreach (var airline in AircraftGenerator.GetAirlines())
            {
                if (suggestions.Count >= maxSuggestions)
                {
                    break;
                }

                if (airlinePartial.Length > 0 && !airline.StartsWith(airlinePartial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var display = $"*{airline}";
                var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, display);
                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.Command,
                        Text = display,
                        Description = "Airline callsign override",
                        InsertText = insertText,
                        CaretAfterInsert = caret,
                    }
                );
            }
        }
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
}
