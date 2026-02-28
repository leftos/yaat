using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Models;

namespace Yaat.Client.Services;

public partial class CommandInputController : ObservableObject
{
    private const int MaxSuggestions = 10;

    [ObservableProperty]
    private bool _isSuggestionsVisible;

    [ObservableProperty]
    private int _selectedSuggestionIndex = -1;

    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];

    private int _historyIndex = -1;
    private string _savedInput = "";
    private string _historyFilter = "";
    private bool _isNavigatingHistory;
    private bool _suppressNextUpdate;

    public void UpdateSuggestions(
        string text,
        IReadOnlyCollection<AircraftModel> aircraft,
        CommandScheme scheme)
    {
        if (_suppressNextUpdate)
        {
            _suppressNextUpdate = false;
            Suggestions.Clear();
            SelectedSuggestionIndex = -1;
            IsSuggestionsVisible = false;
            return;
        }

        if (_isNavigatingHistory)
        {
            ResetHistoryNavigation();
        }

        Suggestions.Clear();
        SelectedSuggestionIndex = -1;

        if (string.IsNullOrWhiteSpace(text))
        {
            IsSuggestionsVisible = false;
            return;
        }

        // For compound commands, only suggest for the fragment after the last separator
        var fragment = GetCurrentFragment(text);
        if (string.IsNullOrWhiteSpace(fragment))
        {
            IsSuggestionsVisible = false;
            return;
        }

        // Strip leading condition (LV/AT) prefix if present within the fragment
        var fragmentForSuggestion = StripConditionPrefix(fragment);
        if (string.IsNullOrWhiteSpace(fragmentForSuggestion))
        {
            // Condition keyword typed but argument still in progress — no suggestions
            IsSuggestionsVisible = false;
            return;
        }

        var parts = fragmentForSuggestion.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var firstToken = parts[0];
        var hasSpace = fragmentForSuggestion.Contains(' ');

        if (!hasSpace)
        {
            // Single token: could be callsign or command verb
            AddCallsignSuggestions(firstToken, text, aircraft);
            AddCommandVerbSuggestions(firstToken, text, scheme);
            AddConditionSuggestions(firstToken);
        }
        else if (parts.Length >= 2)
        {
            // After first token + space + partial second token
            // Only suggest verbs if the first token is NOT a known verb (i.e., it's a callsign)
            if (!IsKnownVerb(firstToken, scheme))
            {
                AddCommandVerbSuggestions(parts[1].TrimStart(), text, scheme);
            }
        }
        else if (hasSpace && parts.Length == 1)
        {
            // Token + trailing space, no second token yet
            // If it's a known verb, argument is expected — no suggestions
            // If it's not a verb, it's a callsign — show all command verbs
            if (!IsKnownVerb(firstToken, scheme))
            {
                AddCommandVerbSuggestions("", text, scheme);
            }
        }

        IsSuggestionsVisible = Suggestions.Count > 0;
    }

    public string? AcceptSuggestion(string currentText)
    {
        if (SelectedSuggestionIndex < 0 || SelectedSuggestionIndex >= Suggestions.Count)
        {
            return null;
        }

        var item = Suggestions[SelectedSuggestionIndex];
        DismissSuggestions();
        return item.InsertText;
    }

    public void DismissSuggestions()
    {
        IsSuggestionsVisible = false;
        SelectedSuggestionIndex = -1;
    }

    public void MoveSelection(int delta)
    {
        if (Suggestions.Count == 0)
        {
            return;
        }

        var next = SelectedSuggestionIndex + delta;
        if (next < 0)
        {
            next = Suggestions.Count - 1;
        }
        else if (next >= Suggestions.Count)
        {
            next = 0;
        }

        SelectedSuggestionIndex = next;
    }

    /// <summary>
    /// Navigates command history. Returns the replacement text, or null if at boundary.
    /// </summary>
    public string? NavigateHistory(
        int direction,
        string currentText,
        IReadOnlyList<string> history)
    {
        if (history.Count == 0)
        {
            return null;
        }

        if (!_isNavigatingHistory)
        {
            _savedInput = currentText;
            _historyFilter = currentText;
            _historyIndex = -1;
            _isNavigatingHistory = true;
        }

        DismissSuggestions();

        if (direction < 0)
        {
            // Up: move to older entries
            var next = FindNextHistoryMatch(history, _historyIndex + 1, _historyFilter);
            if (next < 0)
            {
                return null;
            }

            _historyIndex = next;
            _suppressNextUpdate = true;
            return history[_historyIndex];
        }
        else
        {
            // Down: move to newer entries
            if (_historyIndex <= 0)
            {
                _isNavigatingHistory = false;
                _historyIndex = -1;
                _suppressNextUpdate = true;
                return _savedInput;
            }

            var next = FindPrevHistoryMatch(history, _historyIndex - 1, _historyFilter);
            if (next < 0)
            {
                _isNavigatingHistory = false;
                _historyIndex = -1;
                _suppressNextUpdate = true;
                return _savedInput;
            }

            _historyIndex = next;
            _suppressNextUpdate = true;
            return history[_historyIndex];
        }
    }

    public void ResetHistoryNavigation()
    {
        _isNavigatingHistory = false;
        _historyIndex = -1;
        _savedInput = "";
        _historyFilter = "";
    }

    private static bool IsKnownVerb(string token, CommandScheme scheme)
    {
        foreach (var pattern in scheme.Patterns.Values)
        {
            if (string.Equals(pattern.Verb, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetCurrentFragment(string text)
    {
        // Find the last ; or , separator and return everything after it
        var lastSemicolon = text.LastIndexOf(';');
        var lastComma = text.LastIndexOf(',');
        var lastSep = Math.Max(lastSemicolon, lastComma);

        if (lastSep < 0)
        {
            return text;
        }

        return text[(lastSep + 1)..].TrimStart();
    }

    private static string StripConditionPrefix(string fragment)
    {
        // Check for "LV <arg> " or "AT <arg> " prefix (fully typed condition keyword + space)
        var upper = fragment.TrimStart().ToUpperInvariant();
        if (upper.StartsWith("LV ", StringComparison.Ordinal)
            || upper.StartsWith("AT ", StringComparison.Ordinal))
        {
            var afterPrefix = fragment[3..].TrimStart();
            var spaceIdx = afterPrefix.IndexOf(' ');
            if (spaceIdx >= 0)
            {
                return afterPrefix[(spaceIdx + 1)..].TrimStart();
            }

            // Still typing the condition argument (e.g., "LV 05")
            return "";
        }

        return fragment;
    }

    private void AddConditionSuggestions(string token)
    {
        if (Suggestions.Count >= MaxSuggestions)
        {
            return;
        }

        var upper = token.TrimStart().ToUpperInvariant();
        if ("LV".StartsWith(upper, StringComparison.Ordinal) && upper.Length > 0)
        {
            Suggestions.Add(new SuggestionItem
            {
                Kind = SuggestionKind.Command,
                Text = "LV",
                Description = "Level at {altitude} — trigger at altitude",
                InsertText = "LV ",
            });
        }

        if (Suggestions.Count >= MaxSuggestions)
        {
            return;
        }

        if ("AT".StartsWith(upper, StringComparison.Ordinal) && upper.Length > 0)
        {
            Suggestions.Add(new SuggestionItem
            {
                Kind = SuggestionKind.Command,
                Text = "AT",
                Description = "At {fix} — trigger at a fix",
                InsertText = "AT ",
            });
        }
    }

    private void AddCallsignSuggestions(
        string token,
        string fullText,
        IReadOnlyCollection<AircraftModel> aircraft)
    {
        var count = 0;
        foreach (var ac in aircraft)
        {
            if (count >= MaxSuggestions)
            {
                break;
            }

            if (!ac.Callsign.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var desc = $"{ac.AircraftType} {ac.Departure}-{ac.Destination}".Trim();
            Suggestions.Add(new SuggestionItem
            {
                Kind = SuggestionKind.Callsign,
                Text = ac.Callsign,
                Description = desc,
                InsertText = ac.Callsign + " ",
            });
            count++;
        }
    }

    private void AddCommandVerbSuggestions(
        string token,
        string fullText,
        CommandScheme scheme)
    {
        var count = Suggestions.Count;
        foreach (var cmd in CommandMetadata.AllCommands)
        {
            if (count >= MaxSuggestions)
            {
                break;
            }

            if (!scheme.Patterns.TryGetValue(cmd.Type, out var pattern))
            {
                continue;
            }

            var matchesVerb = pattern.Verb.StartsWith(token, StringComparison.OrdinalIgnoreCase);
            var matchesLabel = cmd.Label.Contains(token, StringComparison.OrdinalIgnoreCase);
            if (!matchesVerb && !matchesLabel)
            {
                continue;
            }

            var argHint = cmd.SampleArg is not null ? $" {{{cmd.SampleArg}}}" : "";
            var desc = $"{cmd.Label}{argHint}";
            var needsArg = pattern.Format.Contains("{arg}");
            var prefix = GetTextBeforeCurrentToken(fullText);
            var insertText = prefix + pattern.Verb + (needsArg ? " " : "");

            Suggestions.Add(new SuggestionItem
            {
                Kind = SuggestionKind.Command,
                Text = pattern.Verb,
                Description = desc,
                InsertText = insertText,
            });
            count++;
        }
    }

    private static string GetTextBeforeCurrentToken(string text)
    {
        // Find the last separator and return text up to and including it
        var lastSemicolon = text.LastIndexOf(';');
        var lastComma = text.LastIndexOf(',');
        var lastSep = Math.Max(lastSemicolon, lastComma);

        if (lastSep < 0)
        {
            // No separator — check if there's a space (callsign prefix)
            var lastSpace = text.LastIndexOf(' ');
            if (lastSpace >= 0)
            {
                // Keep everything up to and including the last space
                return text[..(lastSpace + 1)];
            }

            return "";
        }

        return text[..(lastSep + 1)] + " ";
    }

    private static int FindNextHistoryMatch(
        IReadOnlyList<string> history,
        int startIndex,
        string filter)
    {
        for (var i = startIndex; i < history.Count; i++)
        {
            if (string.IsNullOrEmpty(filter)
                || history[i].StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindPrevHistoryMatch(
        IReadOnlyList<string> history,
        int startIndex,
        string filter)
    {
        for (var i = startIndex; i >= 0; i--)
        {
            if (string.IsNullOrEmpty(filter)
                || history[i].StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
