using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Client.Services;

public partial class CommandInputController : ObservableObject
{
    private const int MaxSuggestions = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPopupVisible))]
    private bool _isSuggestionsVisible;

    [ObservableProperty]
    private int _selectedSuggestionIndex = -1;

    public bool IsPopupVisible => IsSuggestionsVisible || SignatureHelp.IsVisible;

    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];
    public SignatureHelpState SignatureHelp { get; }

    public CommandInputController()
    {
        SignatureHelp = new SignatureHelpState();
        SignatureHelp.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SignatureHelpState.IsVisible))
            {
                OnPropertyChanged(nameof(IsPopupVisible));
            }
        };
    }

    private int _historyIndex = -1;
    private string _savedInput = "";
    private string _historyFilter = "";
    private bool _isNavigatingHistory;
    private int _pendingSuppressions;

    public bool NavDbReady { get; set; }
    public string? PrimaryAirportId { get; set; }
    public IReadOnlyList<MacroDefinition>? Macros { get; set; }

    public bool IsNavigatingHistory => _isNavigatingHistory;

    /// <summary>
    /// Builds the new text and post-insert caret position when a suggestion replaces
    /// the active token at the cursor. Preserves the suffix after the active token,
    /// guarantees exactly one space between the inserted value and the next token,
    /// and matches the legacy "trailing space" behavior at end-of-text.
    /// </summary>
    internal static (string Text, int Caret) BuildTokenReplacement(string fullText, int activeTokenStart, int activeTokenEnd, string value)
    {
        var prefix = fullText[..activeTokenStart];
        var suffix = fullText[activeTokenEnd..];
        if (suffix.Length == 0)
        {
            return (prefix + value + " ", prefix.Length + value.Length + 1);
        }
        var hasLeadingSpace = suffix[0] == ' ';
        var newText = hasLeadingSpace ? (prefix + value + suffix) : (prefix + value + " " + suffix);
        return (newText, prefix.Length + value.Length + 1);
    }

    /// <summary>
    /// Returns true when the (leading-whitespace-trimmed) input begins with a character that marks
    /// the rest of the line as a broadcast chat message rather than a controller command. Mirrors the
    /// chat-prefix set used when sending (<c>MainViewModel.SendCommandAsync</c>). Autocomplete and
    /// signature help are suppressed for chat input — none of the suggesters apply to free-text chat.
    /// </summary>
    public static bool StartsWithChatPrefix(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '\'' or '/' or '>';
    }

    public void UpdateSuggestions(
        string text,
        int caretIndex,
        IReadOnlyCollection<AircraftModel> aircraft,
        CommandScheme scheme,
        AircraftModel? selectedAircraft = null
    )
    {
        if (_pendingSuppressions > 0)
        {
            _pendingSuppressions--;
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

        // Chat (' / >) and client-only scope-marker dot commands (.ff/.marker/.nomarkers) have no
        // command suggestions.
        var trimmedStart = text.AsSpan().TrimStart();
        if (StartsWithChatPrefix(text) || (trimmedStart.Length > 0 && trimmedStart[0] == '.'))
        {
            IsSuggestionsVisible = false;
            return;
        }

        var parsed = ParseCommandInput(text, caretIndex, scheme);
        if (parsed is null)
        {
            IsSuggestionsVisible = false;
            return;
        }

        // If still typing a condition argument (empty stripped fragment)
        if (string.IsNullOrWhiteSpace(parsed.StrippedFragment))
        {
            // Active-token bounds describe the partial condition arg under the cursor.
            var argPartial = text[parsed.ActiveTokenStart..parsed.CaretIndex];
            if (string.Equals(parsed.ConditionVerb, "AT", StringComparison.OrdinalIgnoreCase))
            {
                FixSuggester.AddFixSuggestionsForActiveToken(
                    text,
                    parsed.ActiveTokenStart,
                    parsed.ActiveTokenEnd,
                    argPartial,
                    selectedAircraft,
                    Suggestions,
                    MaxSuggestions
                );
            }
            else if (
                string.Equals(parsed.ConditionVerb, "GIVEWAY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parsed.ConditionVerb, "BEHIND", StringComparison.OrdinalIgnoreCase)
            )
            {
                AddCallsignSuggestionsForActiveToken(text, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, argPartial, aircraft);
            }

            IsSuggestionsVisible = Suggestions.Count > 0;
            return;
        }

        // First token of fragment used for callsign-vs-verb resolution
        var firstToken = parsed.Tokens[0];
        bool firstTokenIsVerb = parsed.VerbIndex == 0;
        bool fragmentHasMultipleTokens = parsed.Tokens.Length > 1 || parsed.HasTrailingSpace;
        var targetAircraft = ResolveTargetAircraft(firstToken, fragmentHasMultipleTokens, firstTokenIsVerb, aircraft, selectedAircraft);

        var activeTokenText =
            (parsed.ActiveTokenIndex >= 0 && parsed.ActiveTokenIndex < parsed.Tokens.Length) ? parsed.Tokens[parsed.ActiveTokenIndex] : "";
        var activePartial = text[parsed.ActiveTokenStart..parsed.CaretIndex];

        // Suppress the "flood of all options" case: cursor placed at position 0 of a
        // non-empty token without any typed prefix. Only show callsign/verb/condition
        // floods when the user has typed something into the token (partial non-empty)
        // or is at an empty insertion point (trailing space, ActiveTokenStart == End).
        bool isInsertionPoint = parsed.ActiveTokenStart == parsed.ActiveTokenEnd;
        bool hasUserPartial = activePartial.Length > 0 || isInsertionPoint;

        // Macro check on the active token at the cursor
        if (activeTokenText.StartsWith('!'))
        {
            AddMacroSuggestions(activeTokenText, text, parsed);
        }
        // Cursor on the first token, with no callsign prefix (verbIndex ∈ {-1, 0})
        else if (parsed.ActiveTokenIndex == 0 && parsed.VerbIndex <= 0 && hasUserPartial)
        {
            // Single-token context: could be callsign, command verb, or condition
            AddCallsignSuggestions(activeTokenText, aircraft, text, parsed);
            AddCommandVerbSuggestions(activeTokenText, text, scheme, targetAircraft, parsed);
            AddConditionSuggestions(activeTokenText, text, parsed);
        }
        // Cursor on first token (the callsign) when verb is at index 1
        else if (parsed.ActiveTokenIndex == 0 && parsed.VerbIndex == 1 && hasUserPartial)
        {
            AddCallsignSuggestions(activeTokenText, aircraft, text, parsed);
        }
        // Cursor on the verb position (index 1 after callsign)
        else if (parsed.ActiveTokenIndex == 1 && !firstTokenIsVerb && hasUserPartial)
        {
            AddCommandVerbSuggestions(activeTokenText, text, scheme, targetAircraft, parsed);
        }
        else if (
            AddCommandSuggester.TryAddAddArgumentSuggestions(parsed, text, scheme, targetAircraft, Suggestions, PrimaryAirportId, MaxSuggestions)
        )
        {
            // ADD command positional argument suggestions
        }
        else if (ArgumentSuggester.TryAddArgumentSuggestions(parsed, text, targetAircraft, aircraft, Suggestions, PrimaryAirportId, MaxSuggestions))
        {
            // Command-specific argument suggestions (CTO modifiers, runways, fixes, callsigns)
        }
        else if (FixSuggester.TryAddFixSuggestions(parsed, text, targetAircraft, scheme, Suggestions, MaxSuggestions))
        {
            // Fix suggestions were added (DCT or callsign+DCT context)
        }

        IsSuggestionsVisible = Suggestions.Count > 0;
    }

    public (string Text, int Caret)? AcceptSuggestion(string currentText)
    {
        if (SelectedSuggestionIndex < 0 || SelectedSuggestionIndex >= Suggestions.Count)
        {
            return null;
        }

        var item = Suggestions[SelectedSuggestionIndex];
        // Two suppressions: one for the Text change, one for the CaretIndex change.
        _pendingSuppressions = 2;
        DismissSuggestions();
        return (item.InsertText, item.CaretAfterInsert);
    }

    public void DismissSuggestions()
    {
        IsSuggestionsVisible = false;
        SelectedSuggestionIndex = -1;
    }

    public void UpdateSignatureHelp(string text, int caretIndex, CommandScheme scheme)
    {
        if (StartsWithChatPrefix(text))
        {
            SignatureHelp.Dismiss();
            return;
        }

        var parsed = ParseCommandInput(text, caretIndex, scheme);
        if (parsed is null || parsed.Definition is null || parsed.VerbIndex < 0)
        {
            SignatureHelp.Dismiss();
            return;
        }

        // Only show signature help once the user has typed past the verb (space after verb)
        int argStartIndex = parsed.VerbIndex + 1;
        if (parsed.Tokens.Length == argStartIndex && !parsed.HasTrailingSpace)
        {
            bool hasSpaceAfterVerb = parsed.StrippedFragment.Length > parsed.Verb!.Length && parsed.StrippedFragment.TrimStart().Contains(' ');
            if (!hasSpaceAfterVerb)
            {
                SignatureHelp.Dismiss();
                return;
            }
        }

        var sigSet = CommandSignatureSet.FromDefinition(parsed.Definition, parsed.Aliases);
        SignatureHelp.Show(sigSet, parsed.ParameterIndex, parsed.TypedArgs);
    }

    /// <summary>
    /// Single parse pass over the command input text. Both autocomplete and signature help
    /// consume this result instead of parsing independently. The result describes the token
    /// at the cursor (caretIndex), not the trailing token of the text.
    /// </summary>
    internal static CommandInputParseResult? ParseCommandInput(string text, int caretIndex, CommandScheme scheme)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // Clamp caret to text bounds
        if (caretIndex < 0)
        {
            caretIndex = 0;
        }
        if (caretIndex > text.Length)
        {
            caretIndex = text.Length;
        }

        // Locate the fragment containing the cursor (delimited by ; or ,)
        int fragmentStart = 0;
        for (int i = caretIndex - 1; i >= 0; i--)
        {
            if (text[i] is ';' or ',')
            {
                fragmentStart = i + 1;
                break;
            }
        }
        int fragmentEnd = text.Length;
        for (int i = caretIndex; i < text.Length; i++)
        {
            if (text[i] is ';' or ',')
            {
                fragmentEnd = i;
                break;
            }
        }

        // Skip leading whitespace inside the fragment
        int contentStart = fragmentStart;
        while (contentStart < fragmentEnd && text[contentStart] == ' ')
        {
            contentStart++;
        }

        // The fragment span (without leading whitespace) preserved for analysis.
        var fragment = text[contentStart..fragmentEnd];
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return null;
        }

        // Optional leading callsign before a condition keyword: "<callsign> AT <fix> <verb> ..."
        // When detected, peel off the callsign first so StripConditionPrefix sees the bare condition.
        // After tokenizing the post-condition portion, prepend the callsign synthetically so the
        // verb-finder picks the post-condition verb at index 1.
        int callsignEndInFragment = FindLeadingCallsignEnd(fragment, scheme);
        string leadingCallsign = "";
        int callsignStartInText = contentStart;
        int conditionStartInText = contentStart;
        var fragmentForCondition = fragment;
        if (callsignEndInFragment > 0)
        {
            leadingCallsign = fragment[..(callsignEndInFragment - 1)];
            conditionStartInText = contentStart + callsignEndInFragment;
            fragmentForCondition = fragment[callsignEndInFragment..];
        }

        // Strip condition prefix from the fragment, tracking how many characters of the
        // fragment were consumed by the prefix so we can map cursor positions correctly.
        var strippedFragment = StripConditionPrefix(
            fragmentForCondition,
            out var conditionVerb,
            out var conditionPrefixLen,
            out var strippedStartInConditionFragment
        );
        int strippedStartInText = conditionStartInText + strippedStartInConditionFragment;
        bool fragmentHasTrailingSpace = fragmentEnd > contentStart && text[fragmentEnd - 1] == ' ';

        // Cursor in the condition prefix region (between the callsign and the stripped portion).
        bool cursorInConditionRegion = conditionPrefixLen > 0 && caretIndex >= conditionStartInText && caretIndex < strippedStartInText;

        if (string.IsNullOrWhiteSpace(strippedFragment) || cursorInConditionRegion)
        {
            // Still typing condition argument or the prefix itself — return partial result.
            // ActiveToken bounds describe the partial condition arg under the cursor:
            // the contiguous non-space run that contains the caret (or insertion point).
            (int conditionTokenStart, int conditionTokenEnd, bool conditionHasTrailing) = FindActiveTokenBounds(
                text,
                caretIndex,
                contentStart,
                fragmentEnd
            );
            return new CommandInputParseResult(
                fragment,
                conditionVerb,
                "",
                [],
                -1,
                null,
                null,
                null,
                [],
                -1,
                [],
                conditionHasTrailing,
                caretIndex,
                conditionTokenStart,
                conditionTokenEnd,
                0
            );
        }

        // Tokenize the stripped fragment, tracking each token's bounds in full-text coords.
        var (postConditionTokens, postConditionBounds) = TokenizeWithBounds(strippedFragment, strippedStartInText);
        string[] tokens;
        (int Start, int End)[] tokenBounds;
        if (leadingCallsign.Length > 0)
        {
            // Prepend the callsign so the verb-finder sees [callsign, verb, args...] at indices [0, 1, 2+].
            tokens = new string[postConditionTokens.Length + 1];
            tokenBounds = new (int Start, int End)[postConditionBounds.Length + 1];
            tokens[0] = leadingCallsign;
            tokenBounds[0] = (callsignStartInText, callsignStartInText + leadingCallsign.Length);
            postConditionTokens.CopyTo(tokens, 1);
            postConditionBounds.CopyTo(tokenBounds, 1);
        }
        else
        {
            tokens = postConditionTokens;
            tokenBounds = postConditionBounds;
        }
        if (tokens.Length == 0)
        {
            return null;
        }

        // Find the verb token — could be first (direct verb) or second (after callsign)
        int verbIndex = -1;
        string? verb = null;

        if (IsKnownVerb(tokens[0], scheme))
        {
            verbIndex = 0;
            verb = tokens[0];
        }
        else if (tokens.Length >= 2 && IsKnownVerb(tokens[1], scheme))
        {
            verbIndex = 1;
            verb = tokens[1];
        }

        // Resolve to command type and definition
        CanonicalCommandType? commandType = null;
        CommandDefinition? definition = null;
        IReadOnlyList<string> aliases = [];

        if (verb is not null)
        {
            commandType = ResolveVerbToType(verb, scheme);
            if (commandType is not null)
            {
                definition = CommandRegistry.Get(commandType.Value);
                aliases = scheme.Patterns.TryGetValue(commandType.Value, out var pattern) ? pattern.Aliases : definition?.DefaultAliases ?? [];
            }
        }

        // TypedArgs = all tokens after the verb (regardless of cursor position) so signature
        // help has full information for overload scoring.
        string[] typedArgs = [];
        if (verbIndex >= 0)
        {
            int argStartIndex = verbIndex + 1;
            typedArgs = tokens.Skip(argStartIndex).ToArray();
        }

        // Find the active token at the cursor.
        int activeTokenIndex;
        int activeTokenStart;
        int activeTokenEnd;
        bool hasTrailingSpaceAtCursor;

        int onTokenIndex = -1;
        for (int i = 0; i < tokenBounds.Length; i++)
        {
            var (s, e) = tokenBounds[i];
            if (s <= caretIndex && caretIndex <= e)
            {
                onTokenIndex = i;
                break;
            }
        }

        if (onTokenIndex >= 0)
        {
            activeTokenIndex = onTokenIndex;
            (activeTokenStart, activeTokenEnd) = tokenBounds[onTokenIndex];
            hasTrailingSpaceAtCursor = false;
        }
        else
        {
            // Cursor is in whitespace — find the next-token slot index.
            int nextSlot = tokens.Length;
            for (int i = 0; i < tokenBounds.Length; i++)
            {
                if (tokenBounds[i].Start > caretIndex)
                {
                    nextSlot = i;
                    break;
                }
            }
            activeTokenIndex = nextSlot;
            activeTokenStart = caretIndex;
            activeTokenEnd = caretIndex;
            hasTrailingSpaceAtCursor = true;
        }

        int paramIndex = -1;
        if (verbIndex >= 0)
        {
            paramIndex = activeTokenIndex - verbIndex - 1;
            if (paramIndex < -1)
            {
                paramIndex = -1;
            }
        }

        return new CommandInputParseResult(
            fragment,
            conditionVerb,
            strippedFragment,
            tokens,
            verbIndex,
            verb,
            commandType,
            definition,
            aliases,
            paramIndex,
            typedArgs,
            hasTrailingSpaceAtCursor || fragmentHasTrailingSpace && activeTokenIndex >= tokens.Length,
            caretIndex,
            activeTokenStart,
            activeTokenEnd,
            activeTokenIndex
        );
    }

    private static (string[] Tokens, (int Start, int End)[] Bounds) TokenizeWithBounds(string strippedFragment, int strippedStartInText)
    {
        var tokens = new List<string>();
        var bounds = new List<(int Start, int End)>();
        int i = 0;
        while (i < strippedFragment.Length)
        {
            while (i < strippedFragment.Length && strippedFragment[i] == ' ')
            {
                i++;
            }
            if (i >= strippedFragment.Length)
            {
                break;
            }
            int tokenStart = i;
            while (i < strippedFragment.Length && strippedFragment[i] != ' ')
            {
                i++;
            }
            tokens.Add(strippedFragment[tokenStart..i]);
            bounds.Add((strippedStartInText + tokenStart, strippedStartInText + i));
        }
        return (tokens.ToArray(), bounds.ToArray());
    }

    private static (int Start, int End, bool HasTrailingSpace) FindActiveTokenBounds(string text, int caretIndex, int searchStart, int searchEnd)
    {
        // Find the contiguous non-space run containing the caret. If the caret is in
        // whitespace, the bounds collapse to the caret position with HasTrailingSpace=true.
        int s = caretIndex;
        while (s > searchStart && s > 0 && text[s - 1] != ' ')
        {
            s--;
        }
        int e = caretIndex;
        while (e < searchEnd && e < text.Length && text[e] != ' ')
        {
            e++;
        }
        if (s == e)
        {
            return (caretIndex, caretIndex, true);
        }
        return (s, e, false);
    }

    private static CanonicalCommandType? ResolveVerbToType(string verb, CommandScheme scheme)
    {
        foreach (var (type, pattern) in scheme.Patterns)
        {
            foreach (var alias in pattern.Aliases)
            {
                if (string.Equals(alias, verb, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }
        }

        return null;
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
    public string? NavigateHistory(int direction, string currentText, IReadOnlyList<string> history)
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
            _pendingSuppressions = 1;
            return history[_historyIndex];
        }
        else
        {
            // Down: move to newer entries
            if (_historyIndex <= 0)
            {
                _isNavigatingHistory = false;
                _historyIndex = -1;
                _pendingSuppressions = 1;
                return _savedInput;
            }

            var next = FindPrevHistoryMatch(history, _historyIndex - 1, _historyFilter);
            if (next < 0)
            {
                _isNavigatingHistory = false;
                _historyIndex = -1;
                _pendingSuppressions = 1;
                return _savedInput;
            }

            _historyIndex = next;
            _pendingSuppressions = 1;
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

    private static AircraftModel? ResolveTargetAircraft(
        string firstToken,
        bool hasSpace,
        bool firstTokenIsVerb,
        IReadOnlyCollection<AircraftModel> aircraft,
        AircraftModel? selectedAircraft
    )
    {
        if (hasSpace && !firstTokenIsVerb)
        {
            // First token looks like a callsign — find matching aircraft
            foreach (var ac in aircraft)
            {
                if (string.Equals(ac.Callsign, firstToken, StringComparison.OrdinalIgnoreCase))
                {
                    return ac;
                }
            }

            // Partial match: if exactly one aircraft contains the token
            AircraftModel? partial = null;
            var count = 0;
            foreach (var ac in aircraft)
            {
                if (ac.Callsign.Contains(firstToken, StringComparison.OrdinalIgnoreCase))
                {
                    partial = ac;
                    count++;
                    if (count > 1)
                    {
                        break;
                    }
                }
            }

            if (count == 1)
            {
                return partial;
            }
        }

        return selectedAircraft;
    }

    private static bool IsKnownVerb(string token, CommandScheme scheme)
    {
        foreach (var pattern in scheme.Patterns.Values)
        {
            foreach (var alias in pattern.Aliases)
            {
                if (string.Equals(alias, token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsCompleteSyntaxPattern(string token)
    {
        // Matches T{digits}L or T{digits}R — a complete relative turn command
        return token.Length >= 3 && token[0] is 'T' or 't' && char.IsDigit(token[1]) && token[^1] is 'L' or 'l' or 'R' or 'r';
    }

    internal static string StripConditionPrefix(string fragment, out string? conditionVerb)
    {
        return StripConditionPrefix(fragment, out conditionVerb, out _, out _);
    }

    /// <summary>
    /// Detects when the fragment starts with "&lt;callsign&gt; &lt;CONDITION-KEYWORD&gt; ..." so the caller
    /// can skip the leading callsign before stripping the condition prefix. Returns the offset
    /// of the first non-callsign character within <paramref name="fragment"/> (always one past
    /// the space following the callsign), or 0 if no callsign-skip should happen.
    /// </summary>
    internal static int FindLeadingCallsignEnd(string fragment, CommandScheme scheme)
    {
        int firstSpace = fragment.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return 0;
        }

        var firstToken = fragment[..firstSpace];

        // First token must not itself be a condition keyword — that's handled by StripConditionPrefix.
        if (IsConditionKeyword(firstToken))
        {
            return 0;
        }

        // First token must not be a known verb — otherwise this is a normal "verb args" parse.
        if (IsKnownVerb(firstToken, scheme))
        {
            return 0;
        }

        // What follows the space must be a recognized condition keyword + space.
        int afterSpace = firstSpace + 1;
        while (afterSpace < fragment.Length && fragment[afterSpace] == ' ')
        {
            afterSpace++;
        }
        if (afterSpace >= fragment.Length)
        {
            return 0;
        }

        var remainder = fragment[afterSpace..];
        if (!HasConditionPrefix(remainder))
        {
            return 0;
        }

        return afterSpace;
    }

    private static bool IsConditionKeyword(string token)
    {
        return string.Equals(token, "LV", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "AT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "AS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "ATFN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "ONHO", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "GIVEWAY", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "BEHIND", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "GW", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasConditionPrefix(string fragment)
    {
        var upper = fragment.ToUpperInvariant();
        return upper.StartsWith("LV ", StringComparison.Ordinal)
            || upper.StartsWith("AT ", StringComparison.Ordinal)
            || upper.StartsWith("AS ", StringComparison.Ordinal)
            || upper.StartsWith("ATFN ", StringComparison.Ordinal)
            || upper.StartsWith("ONHO ", StringComparison.Ordinal)
            || upper.StartsWith("GIVEWAY ", StringComparison.Ordinal)
            || upper.StartsWith("BEHIND ", StringComparison.Ordinal)
            || upper.StartsWith("GW ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips a condition prefix (LV/AT/AS/GIVEWAY/BEHIND + arg + space) from the fragment.
    /// Reports the prefix length consumed and the offset where the stripped portion starts
    /// within the original fragment, so callers can map cursor positions correctly.
    /// </summary>
    internal static string StripConditionPrefix(
        string fragment,
        out string? conditionVerb,
        out int conditionPrefixLen,
        out int strippedStartInFragment
    )
    {
        conditionVerb = null;
        conditionPrefixLen = 0;
        strippedStartInFragment = 0;

        var upper = fragment.ToUpperInvariant();
        string? keyword = null;
        int keywordLen = 0;
        bool keywordHasArg = true;

        if (upper.StartsWith("LV ", StringComparison.Ordinal))
        {
            keyword = "LV";
            keywordLen = 3;
        }
        else if (upper.StartsWith("AT ", StringComparison.Ordinal))
        {
            keyword = "AT";
            keywordLen = 3;
        }
        else if (upper.StartsWith("AS ", StringComparison.Ordinal))
        {
            keyword = "AS";
            keywordLen = 3;
        }
        else if (upper.StartsWith("ATFN ", StringComparison.Ordinal))
        {
            keyword = "ATFN";
            keywordLen = 5;
        }
        else if (upper.StartsWith("GIVEWAY ", StringComparison.Ordinal))
        {
            keyword = "GIVEWAY";
            keywordLen = 8;
        }
        else if (upper.StartsWith("BEHIND ", StringComparison.Ordinal))
        {
            keyword = "BEHIND";
            keywordLen = 7;
        }
        else if (upper.StartsWith("GW ", StringComparison.Ordinal))
        {
            keyword = "GIVEWAY";
            keywordLen = 3;
        }
        else if (upper.StartsWith("ONHO ", StringComparison.Ordinal))
        {
            keyword = "ONHO";
            keywordLen = 5;
            keywordHasArg = false;
        }

        if (keyword is null)
        {
            return fragment;
        }

        conditionVerb = keyword;

        if (!keywordHasArg)
        {
            // Zero-arg condition (ONHO): the post-condition portion starts right after the keyword + space.
            int strippedStart = keywordLen;
            while (strippedStart < fragment.Length && fragment[strippedStart] == ' ')
            {
                strippedStart++;
            }
            conditionPrefixLen = keywordLen;
            strippedStartInFragment = strippedStart;
            return fragment[strippedStart..];
        }

        // Skip leading whitespace after the keyword
        int afterPrefixStart = keywordLen;
        while (afterPrefixStart < fragment.Length && fragment[afterPrefixStart] == ' ')
        {
            afterPrefixStart++;
        }
        // Find the space terminating the condition argument
        int argEnd = fragment.IndexOf(' ', afterPrefixStart);
        if (argEnd < 0)
        {
            // Still typing the condition argument
            conditionPrefixLen = afterPrefixStart;
            strippedStartInFragment = afterPrefixStart;
            return "";
        }
        // Skip whitespace after the condition argument
        int argStrippedStart = argEnd + 1;
        while (argStrippedStart < fragment.Length && fragment[argStrippedStart] == ' ')
        {
            argStrippedStart++;
        }
        conditionPrefixLen = argEnd + 1;
        strippedStartInFragment = argStrippedStart;
        return fragment[argStrippedStart..];
    }

    private void AddConditionSuggestions(string activeTokenText, string text, CommandInputParseResult parsed)
    {
        var partial = text[parsed.ActiveTokenStart..parsed.CaretIndex].TrimStart().ToUpperInvariant();
        if (partial.Length == 0)
        {
            return;
        }

        TryAddCondition("LV", "Level at {altitude} — trigger at altitude", partial, text, parsed);
        TryAddCondition("AT", "At {fix/FR/FRD} — trigger at fix, radial, or FRD point", partial, text, parsed);
        TryAddCondition("ATFN", "At fix nautical miles {distance} — trigger at distance from fix", partial, text, parsed);
        TryAddCondition("ONHO", "On handoff — trigger when handoff is accepted", partial, text, parsed);
        TryAddCondition("GIVEWAY", "Give way to {callsign} — delay until target passes", partial, text, parsed);
        TryAddCondition("BEHIND", "Behind {callsign} — alias for give way", partial, text, parsed);
    }

    private void TryAddCondition(string keyword, string description, string partial, string text, CommandInputParseResult parsed)
    {
        if (Suggestions.Count >= MaxSuggestions)
        {
            return;
        }

        if (!keyword.StartsWith(partial, StringComparison.Ordinal))
        {
            return;
        }

        var (insertText, caret) = BuildTokenReplacement(text, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, keyword);
        Suggestions.Add(
            new SuggestionItem
            {
                Kind = SuggestionKind.Command,
                Text = keyword,
                Description = description,
                InsertText = insertText,
                CaretAfterInsert = caret,
            }
        );
    }

    private void AddMacroSuggestions(string activeTokenText, string text, CommandInputParseResult parsed)
    {
        if (Macros is null || Macros.Count == 0)
        {
            return;
        }

        var namePrefix = activeTokenText.StartsWith('!') ? activeTokenText[1..] : activeTokenText;

        foreach (var macro in Macros)
        {
            if (Suggestions.Count >= MaxSuggestions)
            {
                break;
            }

            var baseName = macro.BaseName;
            if (!baseName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var paramNames = macro.ParameterNames;
            var paramHint = paramNames.Count > 0 ? " " + string.Join(" ", paramNames.Select(n => $"&{n}")) : "";

            var (insertText, caret) = BuildTokenReplacement(text, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, "!" + baseName);
            Suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Macro,
                    Text = $"!{baseName}{paramHint}",
                    Description = BuildMacroDescription(macro),
                    InsertText = insertText,
                    CaretAfterInsert = caret,
                }
            );
        }
    }

    private static string BuildMacroDescription(MacroDefinition macro)
    {
        return macro.Expansion;
    }

    private void AddCallsignSuggestions(
        string activeTokenText,
        IReadOnlyCollection<AircraftModel> aircraft,
        string text,
        CommandInputParseResult parsed
    )
    {
        var partial = text[parsed.ActiveTokenStart..parsed.CaretIndex];
        AddCallsignSuggestionsForActiveToken(text, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, partial, aircraft);
    }

    private void AddCallsignSuggestionsForActiveToken(
        string text,
        int activeTokenStart,
        int activeTokenEnd,
        string partial,
        IReadOnlyCollection<AircraftModel> aircraft
    )
    {
        foreach (var ac in aircraft)
        {
            if (Suggestions.Count >= MaxSuggestions)
            {
                break;
            }

            if (partial.Length > 0 && !ac.Callsign.Contains(partial, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var desc = $"{ac.FiledAircraftType} {ac.Departure}-{ac.Destination}".Trim();
            var (insertText, caret) = BuildTokenReplacement(text, activeTokenStart, activeTokenEnd, ac.Callsign);
            Suggestions.Add(
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

    private static readonly HashSet<CanonicalCommandType> DelayedOnlyCommands =
    [
        CanonicalCommandType.SpawnNow,
        CanonicalCommandType.SpawnDelay,
        CanonicalCommandType.Delete,
    ];

    private void AddCommandVerbSuggestions(
        string activeTokenText,
        string text,
        CommandScheme scheme,
        AircraftModel? targetAircraft,
        CommandInputParseResult parsed
    )
    {
        var isDelayed = targetAircraft?.IsDelayed == true;
        var partial = text[parsed.ActiveTokenStart..parsed.CaretIndex];

        // Collect candidates with match quality so exact alias matches sort first.
        // 0 = exact alias match, 1 = alias prefix match, 2 = label substring match
        var candidates = new List<(int Rank, CommandDefinition Def, CommandPattern Pattern)>();

        foreach (var def in CommandRegistry.All.Values)
        {
            // For delayed/deferred aircraft, only show spawn-related commands
            if (isDelayed && !DelayedOnlyCommands.Contains(def.Type))
            {
                continue;
            }
            if (!scheme.Patterns.TryGetValue(def.Type, out var schemePattern))
            {
                continue;
            }

            int bestRank = int.MaxValue;
            foreach (var alias in schemePattern.Aliases)
            {
                if (string.Equals(alias, partial, StringComparison.OrdinalIgnoreCase))
                {
                    bestRank = 0;
                    break;
                }

                if (alias.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                {
                    bestRank = Math.Min(bestRank, 1);
                }
            }

            // Also match syntax patterns (e.g. "T{n}L" matches when user types "T")
            // Skip if the token already looks like a complete T{n}L/R command
            if (bestRank > 1 && def.SyntaxPatterns is { Length: > 0 } && !IsCompleteSyntaxPattern(partial))
            {
                foreach (var sp in def.SyntaxPatterns)
                {
                    var spPrefix = sp[..sp.IndexOf('{')];
                    if (
                        spPrefix.Length > 0
                        && partial.StartsWith(spPrefix, StringComparison.OrdinalIgnoreCase)
                        && (partial.Length <= spPrefix.Length || char.IsDigit(partial[spPrefix.Length]))
                    )
                    {
                        bestRank = Math.Min(bestRank, 1);
                    }
                }
            }

            if (bestRank == int.MaxValue && def.Label.Contains(partial, StringComparison.OrdinalIgnoreCase))
            {
                bestRank = 2;
            }

            if (bestRank < int.MaxValue)
            {
                candidates.Add((bestRank, def, schemePattern));
            }
        }

        candidates.Sort((a, b) => a.Rank.CompareTo(b.Rank));

        foreach (var (_, def, schemePattern) in candidates)
        {
            if (Suggestions.Count >= MaxSuggestions)
            {
                break;
            }

            var sampleArg = def.SampleArg;
            var argHint = sampleArg.Length > 0 ? $" {{{sampleArg}}}" : "";
            var desc = $"{def.Label}{argHint}";
            var allDisplayAliases = def.SyntaxPatterns is { Length: > 0 } ? schemePattern.Aliases.Concat(def.SyntaxPatterns) : schemePattern.Aliases;
            var aliasText = string.Join(", ", allDisplayAliases);
            var (insertText, caret) = BuildTokenReplacement(text, parsed.ActiveTokenStart, parsed.ActiveTokenEnd, schemePattern.PrimaryVerb);

            Suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = aliasText,
                    Description = desc,
                    InsertText = insertText,
                    CaretAfterInsert = caret,
                }
            );
        }
    }

    private static int FindNextHistoryMatch(IReadOnlyList<string> history, int startIndex, string filter)
    {
        for (var i = startIndex; i < history.Count; i++)
        {
            if (string.IsNullOrEmpty(filter) || history[i].StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindPrevHistoryMatch(IReadOnlyList<string> history, int startIndex, string filter)
    {
        for (var i = startIndex; i >= 0; i--)
        {
            if (string.IsNullOrEmpty(filter) || history[i].StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
