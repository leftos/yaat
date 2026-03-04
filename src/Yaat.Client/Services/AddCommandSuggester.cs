using System.Collections.ObjectModel;
using Yaat.Client.Models;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.Services;

internal static class AddCommandSuggester
{
    internal static bool TryAddAddArgumentSuggestions(
        string fragment,
        string fullText,
        CommandScheme scheme,
        AircraftModel? selectedAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        FixDatabase? fixDb,
        string? primaryAirportId,
        int maxSuggestions
    )
    {
        if (!scheme.Patterns.TryGetValue(Sim.Commands.CanonicalCommandType.Add, out var addPattern))
        {
            return false;
        }

        var words = fragment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 1 || !MatchesAnyAlias(words[0], addPattern))
        {
            return false;
        }

        var hasTrailingSpace = fragment.EndsWith(' ');
        var completedArgs = hasTrailingSpace ? words.Length - 1 : words.Length - 2;
        var partial = hasTrailingSpace ? "" : (words.Length > 1 ? words[^1] : "");
        var prefix = FixSuggester.GetTextBeforeLastWord(fullText);

        switch (completedArgs)
        {
            case 0:
                AddAddOptions(
                    prefix,
                    partial,
                    suggestions,
                    maxSuggestions,
                    ("I", "IFR — Instrument flight rules"),
                    ("V", "VFR — Visual flight rules")
                );
                break;

            case 1:
                AddAddOptions(
                    prefix,
                    partial,
                    suggestions,
                    maxSuggestions,
                    ("S", "Small — GA/light aircraft"),
                    ("L", "Large — Regional/narrow-body"),
                    ("H", "Heavy — Wide-body")
                );
                break;

            case 2:
            {
                var weight = ParseWeightToken(words[2]);
                if (weight is not null)
                {
                    AddEngineOptions(prefix, partial, weight.Value, suggestions, maxSuggestions);
                }
                break;
            }

            case >= 3:
                AddPositionSuggestions(
                    words,
                    fullText,
                    prefix,
                    partial,
                    completedArgs,
                    selectedAircraft,
                    suggestions,
                    fixDb,
                    primaryAirportId,
                    maxSuggestions
                );
                break;
        }

        return suggestions.Count > 0;
    }

    internal static void AddAddOptions(
        string prefix,
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

            suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = value,
                    Description = desc,
                    InsertText = prefix + value + " ",
                }
            );
        }
    }

    internal static WeightClass? ParseWeightToken(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "S" => WeightClass.Small,
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
            _ => null,
        };
    }

    private static void AddEngineOptions(
        string prefix,
        string partial,
        WeightClass weight,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        var options = weight switch
        {
            WeightClass.Small => new (string, string)[]
            {
                ("P", "Piston — " + FormatTypes(weight, EngineKind.Piston)),
                ("T", "Turboprop — " + FormatTypes(weight, EngineKind.Turboprop)),
            },
            WeightClass.Large => new (string, string)[]
            {
                ("T", "Turboprop — " + FormatTypes(weight, EngineKind.Turboprop)),
                ("J", "Jet — " + FormatTypes(weight, EngineKind.Jet)),
            },
            WeightClass.Heavy => new (string, string)[] { ("J", "Jet — " + FormatTypes(weight, EngineKind.Jet)) },
            _ => [],
        };

        AddAddOptions(prefix, partial, suggestions, maxSuggestions, options);
    }

    private static string FormatTypes(WeightClass weight, EngineKind engine)
    {
        var types = AircraftGenerator.GetTypesForCombo(weight, engine);
        return types is not null ? string.Join(", ", types) : "";
    }

    private static void AddPositionSuggestions(
        string[] words,
        string fullText,
        string prefix,
        string partial,
        int completedArgs,
        AircraftModel? selectedAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        FixDatabase? fixDb,
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
                FixSuggester.AddAtFixSuggestions(fixPartial, prefix, selectedAircraft, suggestions, fixDb, maxSuggestions);
            }
            else
            {
                // Show all position variant hints + runway suggestions
                AddAddOptions(
                    prefix,
                    partial,
                    suggestions,
                    maxSuggestions,
                    ("-", "Airborne — -{bearing} {dist_nm} {alt_ft}"),
                    ("@", "At fix — @{fix_or_FRD} {alt_ft}")
                );
                AddRunwaySuggestions(prefix, partial, suggestions, fixDb, primaryAirportId, maxSuggestions);
            }
            return;
        }

        // Determine which variant the user is typing
        bool isBearingVariant = words.Length > 4 && words[4].StartsWith('-');
        bool isFixVariant = words.Length > 4 && words[4].StartsWith('@');

        if (isBearingVariant)
        {
            if (completedArgs >= 6)
            {
                AddTypeAndAirlineOverrides(words, prefix, partial, suggestions, maxSuggestions);
            }
        }
        else if (isFixVariant)
        {
            if (completedArgs >= 5)
            {
                AddTypeAndAirlineOverrides(words, prefix, partial, suggestions, maxSuggestions);
            }
        }
        else
        {
            // Runway variant: after runway, next arg is either a distance (nm) or a type/airline override
            if (completedArgs == 4)
            {
                AddAddOptions(
                    prefix,
                    partial,
                    suggestions,
                    maxSuggestions,
                    ("{distance}", "Distance in nm — spawns on final (e.g. 5, 10)")
                );
                AddTypeAndAirlineOverrides(words, prefix, partial, suggestions, maxSuggestions);
            }
            else if (completedArgs >= 5)
            {
                AddTypeAndAirlineOverrides(words, prefix, partial, suggestions, maxSuggestions);
            }
        }
    }

    private static void AddRunwaySuggestions(
        string prefix,
        string partial,
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
            // Show both ends of each physical runway
            string[] designators = rwy.Id.End1.Equals(rwy.Id.End2, StringComparison.OrdinalIgnoreCase)
                ? [rwy.Id.End1]
                : [rwy.Id.End1, rwy.Id.End2];

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

                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.Command,
                        Text = designator,
                        Description = "Runway — lined up, or add distance for final",
                        InsertText = prefix + designator + " ",
                    }
                );
            }
        }
    }

    private static void AddTypeAndAirlineOverrides(
        string[] words,
        string prefix,
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

                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.Command,
                        Text = type,
                        Description = "Aircraft type override",
                        InsertText = prefix + type + " ",
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
                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.Command,
                        Text = display,
                        Description = "Airline callsign override",
                        InsertText = prefix + display + " ",
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
