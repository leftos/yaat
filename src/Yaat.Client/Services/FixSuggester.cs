using System.Collections.ObjectModel;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.Services;

internal static class FixSuggester
{
    /// <summary>
    /// Checks if the user is typing a fix argument for DCT (with or without callsign prefix).
    /// Returns true if fix suggestions were added.
    /// </summary>
    internal static bool TryAddFixSuggestions(
        string fragmentWithSpaces,
        string fullText,
        AircraftModel? selectedAircraft,
        CommandScheme scheme,
        ObservableCollection<SuggestionItem> suggestions,
        NavigationDatabase? fixDb,
        int maxSuggestions
    )
    {
        if (!scheme.Patterns.TryGetValue(CanonicalCommandType.DirectTo, out var dctPattern))
        {
            return false;
        }

        var words = fragmentWithSpaces.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 1)
        {
            return false;
        }

        // Pattern 1: "DCT ..." — first word is a DCT alias
        // Pattern 2: "UAL123 DCT ..." — first word is callsign, second is DCT alias
        int dctIndex;
        if (MatchesAnyAlias(words[0], dctPattern))
        {
            dctIndex = 0;
        }
        else if (words.Length >= 2 && MatchesAnyAlias(words[1], dctPattern))
        {
            dctIndex = 1;
        }
        else
        {
            return false;
        }

        // The partial fix token is the last word after DCT (if any), or empty if trailing space
        var hasTrailingSpace = fragmentWithSpaces.EndsWith(' ');
        var lastWordAfterDct = words.Length > dctIndex + 1 ? words[^1] : "";
        var fixToken = hasTrailingSpace ? "" : lastWordAfterDct;

        var prefix = GetTextBeforeLastWord(fullText);
        AddFixSuggestions(fixToken, prefix, selectedAircraft, suggestions, fixDb, maxSuggestions);
        return suggestions.Count > 0;
    }

    internal static void AddFixSuggestions(
        string token,
        string prefix,
        AircraftModel? selectedAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        NavigationDatabase? fixDb,
        int maxSuggestions
    )
    {
        var count = suggestions.Count;

        // Tier 1: Route fixes from selected aircraft's FMS
        if (selectedAircraft is not null && fixDb is not null)
        {
            var routeFixes = CollectRouteFixNames(selectedAircraft, fixDb);
            foreach (var fix in routeFixes)
            {
                if (count >= maxSuggestions)
                {
                    break;
                }

                if (token.Length > 0 && !fix.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.RouteFix,
                        Text = fix,
                        Description = "Route",
                        InsertText = prefix + fix + " ",
                    }
                );
                count++;
            }
        }

        // Tier 2: All navdata fixes
        if (fixDb is not null)
        {
            AddNavdataFixSuggestions(token, prefix, suggestions, fixDb, maxSuggestions, ref count);
        }
    }

    internal static void AddAtFixSuggestions(
        string fixPartial,
        string prefix,
        AircraftModel? selectedAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        NavigationDatabase? fixDb,
        int maxSuggestions
    )
    {
        var count = suggestions.Count;

        // Tier 1: Route fixes from selected aircraft
        if (selectedAircraft is not null && fixDb is not null)
        {
            var routeFixes = CollectRouteFixNames(selectedAircraft, fixDb);
            foreach (var fix in routeFixes)
            {
                if (count >= maxSuggestions)
                {
                    break;
                }

                if (fixPartial.Length > 0 && !fix.StartsWith(fixPartial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.RouteFix,
                        Text = $"@{fix}",
                        Description = "Route",
                        InsertText = prefix + $"@{fix} ",
                    }
                );
                count++;
            }
        }

        // Tier 2: All navdata fixes
        if (fixDb is not null && fixPartial.Length > 0)
        {
            AddNavdataFixSuggestionsWithBinarySearch(fixPartial, prefix, fixTextPrefix: "@", suggestions, fixDb, maxSuggestions, ref count);
        }
    }

    internal static SortedSet<string> CollectRouteFixNames(AircraftModel aircraft, NavigationDatabase? fixDb)
    {
        var fixes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(aircraft.Departure))
        {
            fixes.Add(aircraft.Departure);
        }

        if (!string.IsNullOrWhiteSpace(aircraft.Destination))
        {
            fixes.Add(aircraft.Destination);
        }

        if (!string.IsNullOrWhiteSpace(aircraft.Route) && fixDb is not null)
        {
            var expanded = fixDb.ExpandRoute(aircraft.Route);
            foreach (var fix in expanded)
            {
                fixes.Add(fix);
            }
        }

        return fixes;
    }

    internal static string GetTextBeforeLastWord(string text)
    {
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace >= 0)
        {
            return text[..(lastSpace + 1)];
        }

        return "";
    }

    private static void AddNavdataFixSuggestions(
        string token,
        string prefix,
        ObservableCollection<SuggestionItem> suggestions,
        NavigationDatabase fixDb,
        int maxSuggestions,
        ref int count
    )
    {
        if (token.Length == 0)
        {
            // No prefix typed — don't show all 40k fixes
            return;
        }

        AddNavdataFixSuggestionsWithBinarySearch(token, prefix, fixTextPrefix: "", suggestions, fixDb, maxSuggestions, ref count);
    }

    private static void AddNavdataFixSuggestionsWithBinarySearch(
        string token,
        string prefix,
        string fixTextPrefix,
        ObservableCollection<SuggestionItem> suggestions,
        NavigationDatabase fixDb,
        int maxSuggestions,
        ref int count
    )
    {
        var allNames = fixDb.AllFixNames;

        // Binary search for the first name matching the prefix
        int lo = 0,
            hi = allNames.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (string.Compare(allNames[mid], 0, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Already-added route fix names (avoid duplicates)
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in suggestions)
        {
            if (s.Kind == SuggestionKind.RouteFix)
            {
                var rawName = fixTextPrefix.Length > 0 && s.Text.StartsWith(fixTextPrefix) ? s.Text[fixTextPrefix.Length..] : s.Text;
                existing.Add(rawName);
            }
        }

        for (int i = lo; i < allNames.Length && count < maxSuggestions; i++)
        {
            var name = allNames[i];
            if (!name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (existing.Contains(name))
            {
                continue;
            }

            var displayText = fixTextPrefix + name;
            suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Fix,
                    Text = displayText,
                    Description = "",
                    InsertText = prefix + displayText + " ",
                }
            );
            count++;
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
