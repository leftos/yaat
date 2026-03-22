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
    private bool _suppressNextUpdate;

    public bool NavDbReady { get; set; }
    public string? PrimaryAirportId { get; set; }
    public IReadOnlyList<MacroDefinition>? Macros { get; set; }

    public void UpdateSuggestions(
        string text,
        IReadOnlyCollection<AircraftModel> aircraft,
        CommandScheme scheme,
        AircraftModel? selectedAircraft = null
    )
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
        var fragmentForSuggestion = StripConditionPrefix(fragment, out var conditionVerb);
        if (string.IsNullOrWhiteSpace(fragmentForSuggestion))
        {
            // Condition keyword typed, argument still in progress
            // For AT conditions, suggest fix names as the argument
            if (string.Equals(conditionVerb, "AT", StringComparison.OrdinalIgnoreCase))
            {
                var atArg = GetConditionArgFragment(fragment);
                var atPrefix = FixSuggester.GetTextBeforeLastWord(text);
                FixSuggester.AddFixSuggestions(atArg, atPrefix, selectedAircraft, Suggestions, MaxSuggestions);
            }
            else if (
                string.Equals(conditionVerb, "GIVEWAY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(conditionVerb, "BEHIND", StringComparison.OrdinalIgnoreCase)
            )
            {
                // For GIVEWAY/BEHIND conditions, suggest callsigns
                var callsignArg = GetConditionArgFragment(fragment, isGiveWay: true);
                var callsignPrefix = FixSuggester.GetTextBeforeLastWord(text);
                AddCallsignSuggestionsWithPrefix(callsignArg, callsignPrefix, aircraft);
            }

            IsSuggestionsVisible = Suggestions.Count > 0;
            return;
        }

        var parts = fragmentForSuggestion.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var firstToken = parts[0];
        var hasSpace = fragmentForSuggestion.Contains(' ');

        // Resolve the target aircraft for filtering commands.
        // If the first token is a callsign (not a verb), find it in the list.
        var targetAircraft = ResolveTargetAircraft(firstToken, hasSpace, aircraft, selectedAircraft, scheme);

        // Check if the trailing token is a macro reference (!NAME) at any position
        var trailingToken = hasSpace ? GetTrailingToken(fragmentForSuggestion) : null;

        if (!hasSpace)
        {
            if (firstToken.StartsWith('!'))
            {
                AddMacroSuggestions(firstToken, text);
            }
            else
            {
                // Single token: could be callsign or command verb
                AddCallsignSuggestions(firstToken, aircraft);
                AddCommandVerbSuggestions(firstToken, text, scheme, targetAircraft);
                AddConditionSuggestions(firstToken);
            }
        }
        else if (trailingToken is not null && trailingToken.StartsWith('!'))
        {
            AddMacroSuggestions(trailingToken, text);
        }
        else if (
            AddCommandSuggester.TryAddAddArgumentSuggestions(
                fragmentForSuggestion,
                text,
                scheme,
                targetAircraft,
                Suggestions,
                PrimaryAirportId,
                MaxSuggestions
            )
        )
        {
            // ADD command positional argument suggestions
        }
        else if (
            ArgumentSuggester.TryAddArgumentSuggestions(
                fragmentForSuggestion,
                text,
                scheme,
                targetAircraft,
                Suggestions,
                PrimaryAirportId,
                MaxSuggestions
            )
        )
        {
            // Command-specific argument suggestions (CTO modifiers, runways, fixes)
        }
        else if (FixSuggester.TryAddFixSuggestions(fragmentForSuggestion, text, targetAircraft, scheme, Suggestions, MaxSuggestions))
        {
            // Fix suggestions were added (DCT or callsign+DCT context)
        }
        else if (parts.Length >= 2)
        {
            // After first token + space + partial second token
            // Only suggest verbs if the first token is NOT a known verb (i.e., it's a callsign)
            if (!IsKnownVerb(firstToken, scheme))
            {
                AddCommandVerbSuggestions(parts[1].TrimStart(), text, scheme, targetAircraft);
            }
        }
        else if (hasSpace && parts.Length == 1)
        {
            // Token + trailing space, no second token yet
            // If it's a known verb, argument is expected — no suggestions
            // If it's not a verb, it's a callsign — show all command verbs
            if (!IsKnownVerb(firstToken, scheme))
            {
                AddCommandVerbSuggestions("", text, scheme, targetAircraft);
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
        _suppressNextUpdate = true;
        DismissSuggestions();
        return item.InsertText;
    }

    public void DismissSuggestions()
    {
        IsSuggestionsVisible = false;
        SelectedSuggestionIndex = -1;
    }

    public void UpdateSignatureHelp(string text, CommandScheme scheme)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SignatureHelp.Dismiss();
            return;
        }

        var fragment = GetCurrentFragment(text);
        if (string.IsNullOrWhiteSpace(fragment))
        {
            SignatureHelp.Dismiss();
            return;
        }

        var stripped = StripConditionPrefix(fragment, out _);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            SignatureHelp.Dismiss();
            return;
        }

        var parts = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            SignatureHelp.Dismiss();
            return;
        }

        // Find the verb token — could be first or second (after callsign)
        string? verb = null;
        int argStartIndex = 1;

        if (IsKnownVerb(parts[0], scheme))
        {
            verb = parts[0];
            argStartIndex = 1;
        }
        else if (parts.Length >= 2 && IsKnownVerb(parts[1], scheme))
        {
            verb = parts[1];
            argStartIndex = 2;
        }

        if (verb is null)
        {
            SignatureHelp.Dismiss();
            return;
        }

        // Check if the user has typed past the verb (needs a space after verb)
        bool hasSpaceAfterVerb = stripped.Length > verb.Length && stripped.TrimStart().Contains(' ');
        if (parts.Length <= argStartIndex - 1)
        {
            SignatureHelp.Dismiss();
            return;
        }

        // RWY is a special rewrite verb — build signature set directly
        CommandSignatureSet sigSet;
        if (string.Equals(verb, "RWY", StringComparison.OrdinalIgnoreCase))
        {
            sigSet = BuildRwySignatureSet();
        }
        else
        {
            var commandType = ResolveVerbToType(verb, scheme);
            if (commandType is null)
            {
                SignatureHelp.Dismiss();
                return;
            }

            var def = CommandRegistry.Get(commandType.Value);
            if (def is null)
            {
                SignatureHelp.Dismiss();
                return;
            }

            IReadOnlyList<string> aliases = scheme.Patterns.TryGetValue(commandType.Value, out var pattern) ? pattern.Aliases : def.DefaultAliases;
            sigSet = CommandSignatureSet.FromDefinition(def, aliases);
        }

        // Calculate active parameter index: words after verb
        var typedArgs = parts.Skip(argStartIndex).ToArray();
        int paramIndex = typedArgs.Length;

        // If there's a trailing space, user is starting next param
        // If no trailing space and args exist, user is still typing current param
        bool hasTrailingSpace = stripped.EndsWith(' ');
        if (!hasTrailingSpace && paramIndex > 0)
        {
            paramIndex--;
        }

        // Only show signature help if user has typed past the verb
        if (parts.Length == argStartIndex && !hasTrailingSpace)
        {
            // Verb typed but no space after it yet — only show if there's a trailing space
            if (!hasSpaceAfterVerb)
            {
                SignatureHelp.Dismiss();
                return;
            }
        }

        SignatureHelp.Show(sigSet, paramIndex, typedArgs);
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

    private static CommandSignatureSet BuildRwySignatureSet()
    {
        var aliases = (IReadOnlyList<string>)["RWY"];
        CommandSignature assignSig = new(
            CanonicalCommandType.AssignRunway,
            "Assign Runway",
            aliases,
            [new CommandParameter("runway", "runway designator", false)],
            "Assign runway"
        );
        CommandSignature taxiSig = new(
            CanonicalCommandType.Taxi,
            "Taxi to Runway",
            aliases,
            [
                new CommandParameter("runway", "runway designator", false),
                new CommandParameter("TAXI", "", false, IsLiteral: true),
                new CommandParameter("route", "taxiway names", false),
            ],
            "Taxi via route to runway"
        );
        return new CommandSignatureSet([assignSig, taxiSig]);
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

    private static AircraftModel? ResolveTargetAircraft(
        string firstToken,
        bool hasSpace,
        IReadOnlyCollection<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        CommandScheme scheme
    )
    {
        if (hasSpace && !IsKnownVerb(firstToken, scheme))
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
        // RWY is a special rewrite verb, not in CommandScheme
        if (string.Equals(token, "RWY", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

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

    internal static string StripConditionPrefix(string fragment, out string? conditionVerb)
    {
        conditionVerb = null;

        // Check for "LV <arg> " or "AT <arg> " prefix (fully typed condition keyword + space)
        var upper = fragment.TrimStart().ToUpperInvariant();
        if (upper.StartsWith("LV ", StringComparison.Ordinal) || upper.StartsWith("AT ", StringComparison.Ordinal))
        {
            conditionVerb = upper[..2];
            var afterPrefix = fragment[3..].TrimStart();
            var spaceIdx = afterPrefix.IndexOf(' ');
            if (spaceIdx >= 0)
            {
                return afterPrefix[(spaceIdx + 1)..].TrimStart();
            }

            // Still typing the condition argument (e.g., "LV 05" or "AT SUN")
            return "";
        }

        // Check for "AS <position> " prefix (compound prefix like LV/AT)
        if (upper.StartsWith("AS ", StringComparison.Ordinal))
        {
            conditionVerb = "AS";
            var afterPrefix = fragment[3..].TrimStart();
            var spaceIdx = afterPrefix.IndexOf(' ');
            if (spaceIdx >= 0)
            {
                return afterPrefix[(spaceIdx + 1)..].TrimStart();
            }

            // Still typing the position identifier (e.g., "AS 4U")
            return "";
        }

        // Check for "GIVEWAY <arg> " or "BEHIND <arg> " prefix (8 and 7 chars respectively)
        if (upper.StartsWith("GIVEWAY ", StringComparison.Ordinal))
        {
            conditionVerb = "GIVEWAY";
            var afterPrefix = fragment[8..].TrimStart();
            var spaceIdx = afterPrefix.IndexOf(' ');
            if (spaceIdx >= 0)
            {
                return afterPrefix[(spaceIdx + 1)..].TrimStart();
            }

            // Still typing the callsign (e.g., "GIVEWAY SWA")
            return "";
        }

        if (upper.StartsWith("BEHIND ", StringComparison.Ordinal))
        {
            conditionVerb = "BEHIND";
            var afterPrefix = fragment[7..].TrimStart();
            var spaceIdx = afterPrefix.IndexOf(' ');
            if (spaceIdx >= 0)
            {
                return afterPrefix[(spaceIdx + 1)..].TrimStart();
            }

            // Still typing the callsign (e.g., "BEHIND SWA")
            return "";
        }

        return fragment;
    }

    private static string GetConditionArgFragment(string fragment, bool isGiveWay = false)
    {
        // Extract the partial argument after the condition keyword
        // "AT SUN" → "SUN", "AT " → ""
        // "GIVEWAY SWA" → "SWA", "GIVEWAY " → ""
        // "BEHIND SWA" → "SWA", "BEHIND " → ""
        int skipLen = 3;
        if (isGiveWay)
        {
            // Check which form we have
            var upper = fragment.TrimStart().ToUpperInvariant();
            skipLen = upper.StartsWith("GIVEWAY ") ? 8 : (upper.StartsWith("BEHIND ") ? 7 : 3);
        }

        var afterKeyword = fragment[skipLen..].TrimStart();
        return afterKeyword;
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
            Suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = "LV",
                    Description = "Level at {altitude} — trigger at altitude",
                    InsertText = "LV ",
                }
            );
        }

        if (Suggestions.Count >= MaxSuggestions)
        {
            return;
        }

        if ("AT".StartsWith(upper, StringComparison.Ordinal) && upper.Length > 0)
        {
            Suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = "AT",
                    Description = "At {fix/FR/FRD} — trigger at fix, radial, or FRD point",
                    InsertText = "AT ",
                }
            );
        }

        if (Suggestions.Count >= MaxSuggestions)
        {
            return;
        }

        if ("GIVEWAY".StartsWith(upper, StringComparison.Ordinal) && upper.Length > 0)
        {
            Suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = "GIVEWAY",
                    Description = "Give way to {callsign} — delay until target passes",
                    InsertText = "GIVEWAY ",
                }
            );
        }

        if (Suggestions.Count >= MaxSuggestions)
        {
            return;
        }

        if ("BEHIND".StartsWith(upper, StringComparison.Ordinal) && upper.Length > 0)
        {
            Suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = "BEHIND",
                    Description = "Behind {callsign} — alias for give way",
                    InsertText = "BEHIND ",
                }
            );
        }
    }

    private void AddMacroSuggestions(string token, string fullText)
    {
        if (Macros is null || Macros.Count == 0)
        {
            return;
        }

        var namePrefix = token[1..]; // strip !
        var prefix = GetTextBeforeCurrentToken(fullText);

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

            Suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Macro,
                    Text = $"!{baseName}{paramHint}",
                    Description = BuildMacroDescription(macro),
                    InsertText = prefix + "!" + baseName + " ",
                }
            );
        }
    }

    private static string BuildMacroDescription(MacroDefinition macro)
    {
        return macro.Expansion;
    }

    private void AddCallsignSuggestions(string token, IReadOnlyCollection<AircraftModel> aircraft)
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
            Suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Callsign,
                    Text = ac.Callsign,
                    Description = desc,
                    InsertText = ac.Callsign + " ",
                }
            );
            count++;
        }
    }

    private void AddCallsignSuggestionsWithPrefix(string token, string prefix, IReadOnlyCollection<AircraftModel> aircraft)
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
            Suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Callsign,
                    Text = ac.Callsign,
                    Description = desc,
                    InsertText = prefix + ac.Callsign + " ",
                }
            );
            count++;
        }
    }

    private static readonly HashSet<CanonicalCommandType> DelayedOnlyCommands =
    [
        CanonicalCommandType.SpawnNow,
        CanonicalCommandType.SpawnDelay,
        CanonicalCommandType.Delete,
    ];

    private void AddCommandVerbSuggestions(string token, string fullText, CommandScheme scheme, AircraftModel? targetAircraft = null)
    {
        var isDelayed = targetAircraft?.IsDelayed == true;

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
                if (string.Equals(alias, token, StringComparison.OrdinalIgnoreCase))
                {
                    bestRank = 0;
                    break;
                }

                if (alias.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    bestRank = Math.Min(bestRank, 1);
                }
            }

            // Also match syntax patterns (e.g. "T{n}L" matches when user types "T")
            // Skip if the token already looks like a complete T{n}L/R command
            if (bestRank > 1 && def.SyntaxPatterns is { Length: > 0 } && !IsCompleteSyntaxPattern(token))
            {
                foreach (var sp in def.SyntaxPatterns)
                {
                    var prefix = sp[..sp.IndexOf('{')];
                    if (
                        prefix.Length > 0
                        && token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && (token.Length <= prefix.Length || char.IsDigit(token[prefix.Length]))
                    )
                    {
                        bestRank = Math.Min(bestRank, 1);
                    }
                }
            }

            if (bestRank == int.MaxValue && def.Label.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                bestRank = 2;
            }

            if (bestRank < int.MaxValue)
            {
                candidates.Add((bestRank, def, schemePattern));
            }
        }

        candidates.Sort((a, b) => a.Rank.CompareTo(b.Rank));

        var count = Suggestions.Count;
        foreach (var (_, def, schemePattern) in candidates)
        {
            if (count >= MaxSuggestions)
            {
                break;
            }

            var sampleArg = def.SampleArg;
            var argHint = sampleArg.Length > 0 ? $" {{{sampleArg}}}" : "";
            var desc = $"{def.Label}{argHint}";
            var allDisplayAliases = def.SyntaxPatterns is { Length: > 0 } ? schemePattern.Aliases.Concat(def.SyntaxPatterns) : schemePattern.Aliases;
            var aliasText = string.Join(", ", allDisplayAliases);
            var needsArg = def.ArgMode != ArgMode.None;
            var prefix = GetTextBeforeCurrentToken(fullText);
            var insertText = prefix + schemePattern.PrimaryVerb + (needsArg ? " " : "");

            Suggestions.Add(
                new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = aliasText,
                    Description = desc,
                    InsertText = insertText,
                }
            );
            count++;
        }
    }

    private static string? GetTrailingToken(string fragment)
    {
        var lastSpace = fragment.LastIndexOf(' ');
        if (lastSpace < 0)
        {
            return null;
        }

        var token = fragment[(lastSpace + 1)..];
        return token.Length > 0 ? token : null;
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
