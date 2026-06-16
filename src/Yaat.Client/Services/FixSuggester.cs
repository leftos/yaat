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
        CommandInputParseResult parsed,
        string fullText,
        AircraftModel? selectedAircraft,
        CommandScheme scheme,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        if (!scheme.Patterns.TryGetValue(CanonicalCommandType.DirectTo, out var dctPattern))
        {
            return false;
        }

        if (parsed.Tokens.Length < 1)
        {
            return false;
        }

        // DCT can be at index 0 (DCT FIX) or index 1 (CALLSIGN DCT FIX)
        int dctIndex;
        if (MatchesAnyAlias(parsed.Tokens[0], dctPattern))
        {
            dctIndex = 0;
        }
        else if (parsed.Tokens.Length >= 2 && MatchesAnyAlias(parsed.Tokens[1], dctPattern))
        {
            dctIndex = 1;
        }
        else
        {
            return false;
        }

        // Only suggest fixes when the cursor is past the DCT verb token.
        if (parsed.ActiveTokenIndex <= dctIndex)
        {
            return false;
        }

        var partial = fullText[parsed.ActiveTokenStart..parsed.CaretIndex];
        AddFixSuggestionsForActiveToken(
            fullText,
            parsed.ActiveTokenStart,
            parsed.ActiveTokenEnd,
            partial,
            selectedAircraft,
            suggestions,
            maxSuggestions
        );
        return suggestions.Count > 0;
    }

    /// <summary>
    /// Adds fix suggestions filtered by `partial` (text from active token start up to cursor).
    /// InsertText replaces the active token in `fullText` while preserving the suffix.
    /// </summary>
    internal static void AddFixSuggestionsForActiveToken(
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
        AircraftModel? selectedAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        // Tier 1: Route fixes from selected aircraft's FMS
        if (selectedAircraft is not null)
        {
            var routeFixes = CollectRouteFixNames(selectedAircraft);
            foreach (var fix in routeFixes)
            {
                if (suggestions.Count >= maxSuggestions)
                {
                    break;
                }

                if (partial.Length > 0 && !fix.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, fix);
                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.RouteFix,
                        Text = fix,
                        Description = "Route",
                        InsertText = insertText,
                        CaretAfterInsert = caret,
                    }
                );
            }
        }

        // Tier 2: All navdata fixes
        AddNavdataFixSuggestions(fullText, activeTokenStart, activeTokenEnd, partial, "", suggestions, NavigationDatabase.Instance, maxSuggestions);
    }

    /// <summary>
    /// Adds @fix suggestions (used by AT condition and ADD @fix variant). The displayed
    /// text and InsertText include the leading "@" character.
    /// </summary>
    internal static void AddAtFixSuggestionsForActiveToken(
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string fixPartial,
        AircraftModel? selectedAircraft,
        ObservableCollection<SuggestionItem> suggestions,
        int maxSuggestions
    )
    {
        if (selectedAircraft is not null)
        {
            var routeFixes = CollectRouteFixNames(selectedAircraft);
            foreach (var fix in routeFixes)
            {
                if (suggestions.Count >= maxSuggestions)
                {
                    break;
                }

                if (fixPartial.Length > 0 && !fix.StartsWith(fixPartial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, "@" + fix);
                suggestions.Add(
                    new SuggestionItem
                    {
                        Kind = SuggestionKind.RouteFix,
                        Text = $"@{fix}",
                        Description = "Route",
                        InsertText = insertText,
                        CaretAfterInsert = caret,
                    }
                );
            }
        }

        if (fixPartial.Length > 0)
        {
            AddNavdataFixSuggestions(
                fullText,
                activeTokenStart,
                activeTokenEnd,
                fixPartial,
                "@",
                suggestions,
                NavigationDatabase.Instance,
                maxSuggestions
            );
        }
    }

    internal static List<string> CollectRouteFixNames(AircraftModel aircraft)
    {
        var fixes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string fix)
        {
            if (seen.Add(fix))
            {
                fixes.Add(fix);
            }
        }

        foreach (var fix in aircraft.NavigationRoute)
        {
            TryAdd(fix);
        }

        if (!string.IsNullOrWhiteSpace(aircraft.Route))
        {
            var expanded = NavigationDatabase.Instance.ExpandRoute(aircraft.Route);
            foreach (var fix in expanded)
            {
                TryAdd(fix);
            }
        }

        // Destination before departure: an aircraft is unlikely to be turned back to its
        // departure airport, so the destination is the more useful suggestion.
        if (!string.IsNullOrWhiteSpace(aircraft.Destination))
        {
            TryAdd(aircraft.Destination);
        }

        if (!string.IsNullOrWhiteSpace(aircraft.Departure))
        {
            TryAdd(aircraft.Departure);
        }

        return fixes;
    }

    /// <summary>
    /// Returns the substring of <paramref name="text"/> up to and including the last space.
    /// Retained for non-cursor-aware callers (CallsignArgumentResolver tests).
    /// </summary>
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
        string fullText,
        int activeTokenStart,
        int activeTokenEnd,
        string token,
        string fixTextPrefix,
        ObservableCollection<SuggestionItem> suggestions,
        NavigationDatabase fixDb,
        int maxSuggestions
    )
    {
        if (token.Length == 0)
        {
            // No prefix typed — don't show all 40k fixes
            return;
        }

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

        for (int i = lo; i < allNames.Length && suggestions.Count < maxSuggestions; i++)
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
            var (insertText, caret) = CommandInputController.BuildTokenReplacement(fullText, activeTokenStart, activeTokenEnd, displayText);
            suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Fix,
                    Text = displayText,
                    Description = "",
                    InsertText = insertText,
                    CaretAfterInsert = caret,
                }
            );
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
