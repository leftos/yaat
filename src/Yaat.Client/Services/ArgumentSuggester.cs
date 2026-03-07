using System.Collections.ObjectModel;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.Services;

/// <summary>
/// Provides argument-level autocomplete suggestions for commands that accept
/// enumerable options (CTO modifiers, runway designators, fix names, etc.).
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

        // The verb may be at position 0 (no callsign) or position 1 (callsign prefix).
        // Detect which position the verb is at by checking both against known commands.
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

        var partial = hasTrailingSpace ? "" : (wordsAfterVerb > 0 ? words[^1] : "");
        var prefix = FixSuggester.GetTextBeforeLastWord(fullText);

        // RWY {runway} [TAXI] {path} — first argument is a runway designator
        if (string.Equals(verb, "RWY", StringComparison.OrdinalIgnoreCase))
        {
            var argsAfterVerb = words.Length - verbIndex - 1;
            if (argsAfterVerb <= 1 || (!hasTrailingSpace && argsAfterVerb == 1))
            {
                AddRunwaySuggestions(partial, prefix, suggestions, fixDb, primaryAirportId, maxSuggestions);
            }

            return true;
        }

        // CTO departure modifiers
        if (MatchesVerb(verb, CanonicalCommandType.ClearedForTakeoff, scheme))
        {
            AddCtoModifierSuggestions(partial, prefix, targetAircraft, suggestions, maxSuggestions);
            return true;
        }

        // Pattern entry commands with runway argument
        if (
            MatchesVerb(verb, CanonicalCommandType.EnterLeftDownwind, scheme)
            || MatchesVerb(verb, CanonicalCommandType.EnterRightDownwind, scheme)
            || MatchesVerb(verb, CanonicalCommandType.EnterFinal, scheme)
        )
        {
            AddRunwaySuggestions(partial, prefix, suggestions, fixDb, primaryAirportId, maxSuggestions);
            return true;
        }

        // Pattern base entry: runway [distance]
        if (MatchesVerb(verb, CanonicalCommandType.EnterLeftBase, scheme) || MatchesVerb(verb, CanonicalCommandType.EnterRightBase, scheme))
        {
            var argsAfterVerb = words.Length - verbIndex - 1;
            if (argsAfterVerb <= 1 || (!hasTrailingSpace && argsAfterVerb == 1))
            {
                AddRunwaySuggestions(partial, prefix, suggestions, fixDb, primaryAirportId, maxSuggestions);
            }

            return true;
        }

        // Cross runway
        if (MatchesVerb(verb, CanonicalCommandType.CrossRunway, scheme))
        {
            AddRunwaySuggestions(partial, prefix, suggestions, fixDb, primaryAirportId, maxSuggestions);
            return true;
        }

        // Cleared to land (optional runway)
        if (MatchesVerb(verb, CanonicalCommandType.ClearedToLand, scheme))
        {
            AddRunwaySuggestions(partial, prefix, suggestions, fixDb, primaryAirportId, maxSuggestions);
            return true;
        }

        // Cleared visual approach (runway)
        if (MatchesVerb(verb, CanonicalCommandType.ClearedVisualApproach, scheme))
        {
            AddRunwaySuggestions(partial, prefix, suggestions, fixDb, primaryAirportId, maxSuggestions);
            return true;
        }

        // Go around (optional heading)
        if (MatchesVerb(verb, CanonicalCommandType.GoAround, scheme))
        {
            AddGoAroundSuggestions(partial, prefix, suggestions, maxSuggestions);
            return true;
        }

        // Fix-argument commands: HFIXL, HFIXR, HFIX, CFIX, DEPART, DCTF, ADCTF, JFAC
        if (
            MatchesVerb(verb, CanonicalCommandType.HoldAtFixLeft, scheme)
            || MatchesVerb(verb, CanonicalCommandType.HoldAtFixRight, scheme)
            || MatchesVerb(verb, CanonicalCommandType.HoldAtFixHover, scheme)
            || MatchesVerb(verb, CanonicalCommandType.CrossFix, scheme)
            || MatchesVerb(verb, CanonicalCommandType.DepartFix, scheme)
            || MatchesVerb(verb, CanonicalCommandType.ForceDirectTo, scheme)
            || MatchesVerb(verb, CanonicalCommandType.AppendForceDirectTo, scheme)
            || MatchesVerb(verb, CanonicalCommandType.JoinFinalApproachCourse, scheme)
        )
        {
            // Only suggest fixes for the first argument position
            var argsAfterVerb = words.Length - verbIndex - 1;
            if (argsAfterVerb <= 1 || (!hasTrailingSpace && argsAfterVerb == 1))
            {
                FixSuggester.AddFixSuggestions(partial, prefix, targetAircraft, suggestions, fixDb, maxSuggestions);
            }

            return true;
        }

        return false;
    }

    private static void AddCtoModifierSuggestions(
        string partial,
        string prefix,
        AircraftModel? targetAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        bool isVfr = targetAircraft is not null && string.Equals(targetAircraft.FlightRules, "VFR", StringComparison.OrdinalIgnoreCase);

        // IFR aircraft: only bare CTO or CTO with heading
        if (!isVfr)
        {
            AddOption("RH", "Fly runway heading", partial, prefix, suggestions, maxSuggestions);
            AddCtoHeadingHints(partial, prefix, suggestions, maxSuggestions);
            return;
        }

        // VFR: pattern options first, then all modifiers
        AddOption("MLT", "Make left traffic (closed pattern)", partial, prefix, suggestions, maxSuggestions);
        AddOption("MRT", "Make right traffic (closed pattern)", partial, prefix, suggestions, maxSuggestions);
        AddOption("RH", "Fly runway heading", partial, prefix, suggestions, maxSuggestions);
        AddOption("OC", "On course (direct to destination)", partial, prefix, suggestions, maxSuggestions);
        AddOption("MRC", "Right crosswind departure (90° right)", partial, prefix, suggestions, maxSuggestions);
        AddOption("MRD", "Right downwind departure (180° right)", partial, prefix, suggestions, maxSuggestions);
        AddOption("MR270", "Right 270° departure (270° right turn)", partial, prefix, suggestions, maxSuggestions);
        AddOption("MLC", "Left crosswind departure (90° left)", partial, prefix, suggestions, maxSuggestions);
        AddOption("MLD", "Left downwind departure (180° left)", partial, prefix, suggestions, maxSuggestions);
        AddOption("ML270", "Left 270° departure (270° left turn)", partial, prefix, suggestions, maxSuggestions);
        AddOption("DCT", "Direct to fix — CTO DCT {fix}", partial, prefix, suggestions, maxSuggestions);
        AddCtoHeadingHints(partial, prefix, suggestions, maxSuggestions);
    }

    private static void AddCtoHeadingHints(string partial, string prefix, ObservableCollection<SuggestionItem> suggestions, int maxSuggestions)
    {
        if (partial.Length == 0 || "H".StartsWith(partial, StringComparison.OrdinalIgnoreCase))
        {
            AddOption("H", "Fly heading — CTO H{hdg} (e.g. H270)", partial, prefix, suggestions, maxSuggestions);
        }

        if (
            partial.Length == 0
            || "RH".StartsWith(partial, StringComparison.OrdinalIgnoreCase)
            || "RT".StartsWith(partial, StringComparison.OrdinalIgnoreCase)
        )
        {
            AddOption("RH", "Right heading — CTO RH{hdg} (e.g. RH270)", partial, prefix, suggestions, maxSuggestions);
        }

        if (
            partial.Length == 0
            || "LH".StartsWith(partial, StringComparison.OrdinalIgnoreCase)
            || "LT".StartsWith(partial, StringComparison.OrdinalIgnoreCase)
        )
        {
            AddOption("LH", "Left heading — CTO LH{hdg} (e.g. LH270)", partial, prefix, suggestions, maxSuggestions);
        }
    }

    private static void AddGoAroundSuggestions(string partial, string prefix, ObservableCollection<SuggestionItem> suggestions, int maxSuggestions)
    {
        // GA accepts optional heading; just show a hint
        if (suggestions.Count < maxSuggestions && (partial.Length == 0 || char.IsDigit(partial[0])))
        {
            suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = "{heading}",
                    Description = "Optional heading after go-around (e.g. GA 270)",
                    InsertText = prefix,
                }
            );
        }
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

    private static readonly CanonicalCommandType[] VerbTypes =
    [
        CanonicalCommandType.ClearedForTakeoff,
        CanonicalCommandType.EnterLeftDownwind,
        CanonicalCommandType.EnterRightDownwind,
        CanonicalCommandType.EnterFinal,
        CanonicalCommandType.EnterLeftBase,
        CanonicalCommandType.EnterRightBase,
        CanonicalCommandType.CrossRunway,
        CanonicalCommandType.ClearedToLand,
        CanonicalCommandType.ClearedVisualApproach,
        CanonicalCommandType.GoAround,
        CanonicalCommandType.HoldAtFixLeft,
        CanonicalCommandType.HoldAtFixRight,
        CanonicalCommandType.HoldAtFixHover,
        CanonicalCommandType.CrossFix,
        CanonicalCommandType.DepartFix,
        CanonicalCommandType.ForceDirectTo,
        CanonicalCommandType.AppendForceDirectTo,
        CanonicalCommandType.JoinFinalApproachCourse,
    ];

    private static int FindVerbIndex(string[] words, CommandScheme scheme)
    {
        // Check position 0 first (no callsign prefix)
        if (IsRecognizedVerb(words[0], scheme))
        {
            return 0;
        }

        // Check position 1 (callsign prefix)
        if (words.Length >= 2 && IsRecognizedVerb(words[1], scheme))
        {
            return 1;
        }

        return -1;
    }

    private static bool IsRecognizedVerb(string token, CommandScheme scheme)
    {
        // RWY is a special rewrite verb, not a CanonicalCommandType
        if (string.Equals(token, "RWY", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var type in VerbTypes)
        {
            if (MatchesVerb(token, type, scheme))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesVerb(string token, CanonicalCommandType type, CommandScheme scheme)
    {
        if (!scheme.Patterns.TryGetValue(type, out var pattern))
        {
            return false;
        }

        foreach (var alias in pattern.Aliases)
        {
            if (string.Equals(alias, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
