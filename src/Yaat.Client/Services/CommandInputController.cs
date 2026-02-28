using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Models;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

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

    public FixDatabase? FixDb { get; set; }
    public string? PrimaryAirportId { get; set; }

    public void UpdateSuggestions(
        string text,
        IReadOnlyCollection<AircraftModel> aircraft,
        CommandScheme scheme,
        AircraftModel? selectedAircraft = null)
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
                AddFixSuggestions(atArg, text, selectedAircraft);
            }

            IsSuggestionsVisible = Suggestions.Count > 0;
            return;
        }

        var parts = fragmentForSuggestion.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var firstToken = parts[0];
        var hasSpace = fragmentForSuggestion.Contains(' ');

        // Resolve the target aircraft for filtering commands.
        // If the first token is a callsign (not a verb), find it in the list.
        var targetAircraft = ResolveTargetAircraft(
            firstToken, hasSpace, aircraft, selectedAircraft, scheme);

        if (!hasSpace)
        {
            // Single token: could be callsign or command verb
            AddCallsignSuggestions(firstToken, text, aircraft);
            AddCommandVerbSuggestions(firstToken, text, scheme, targetAircraft);
            AddConditionSuggestions(firstToken);
        }
        else if (TryAddAddArgumentSuggestions(
            fragmentForSuggestion, text, scheme, selectedAircraft))
        {
            // ADD command positional argument suggestions
        }
        else if (TryAddFixSuggestions(fragmentForSuggestion, text, selectedAircraft, scheme))
        {
            // Fix suggestions were added (DCT or callsign+DCT context)
        }
        else if (parts.Length >= 2)
        {
            // After first token + space + partial second token
            // Only suggest verbs if the first token is NOT a known verb (i.e., it's a callsign)
            if (!IsKnownVerb(firstToken, scheme))
            {
                AddCommandVerbSuggestions(
                    parts[1].TrimStart(), text, scheme, targetAircraft);
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

    private static AircraftModel? ResolveTargetAircraft(
        string firstToken,
        bool hasSpace,
        IReadOnlyCollection<AircraftModel> aircraft,
        AircraftModel? selectedAircraft,
        CommandScheme scheme)
    {
        if (hasSpace && !IsKnownVerb(firstToken, scheme))
        {
            // First token looks like a callsign — find matching aircraft
            foreach (var ac in aircraft)
            {
                if (string.Equals(
                    ac.Callsign, firstToken, StringComparison.OrdinalIgnoreCase))
                {
                    return ac;
                }
            }

            // Partial match: if exactly one aircraft contains the token
            AircraftModel? partial = null;
            var count = 0;
            foreach (var ac in aircraft)
            {
                if (ac.Callsign.Contains(
                    firstToken, StringComparison.OrdinalIgnoreCase))
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

    private static string StripConditionPrefix(string fragment, out string? conditionVerb)
    {
        conditionVerb = null;

        // Check for "LV <arg> " or "AT <arg> " prefix (fully typed condition keyword + space)
        var upper = fragment.TrimStart().ToUpperInvariant();
        if (upper.StartsWith("LV ", StringComparison.Ordinal)
            || upper.StartsWith("AT ", StringComparison.Ordinal))
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

        return fragment;
    }

    private static string GetConditionArgFragment(string fragment)
    {
        // Extract the partial argument after "AT " — e.g., "AT SUN" → "SUN", "AT " → ""
        var afterKeyword = fragment[3..].TrimStart();
        return afterKeyword;
    }

    /// <summary>
    /// Checks if the user is typing a fix argument for DCT (with or without callsign prefix).
    /// Returns true if fix suggestions were added.
    /// </summary>
    private bool TryAddFixSuggestions(
        string fragmentWithSpaces,
        string fullText,
        AircraftModel? selectedAircraft,
        CommandScheme scheme)
    {
        // Find the DCT verb in the active scheme
        if (!scheme.Patterns.TryGetValue(
            Sim.Commands.CanonicalCommandType.DirectTo, out var dctPattern))
        {
            return false;
        }

        var words = fragmentWithSpaces.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries);
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

        AddFixSuggestions(fixToken, fullText, selectedAircraft);
        return Suggestions.Count > 0;
    }

    private void AddFixSuggestions(
        string token,
        string fullText,
        AircraftModel? selectedAircraft)
    {
        var prefix = GetTextBeforeLastWord(fullText);
        var count = Suggestions.Count;

        // Tier 1: Route fixes from selected aircraft's FMS
        if (selectedAircraft is not null && FixDb is not null)
        {
            var routeFixes = CollectRouteFixNames(selectedAircraft);
            foreach (var fix in routeFixes)
            {
                if (count >= MaxSuggestions)
                {
                    break;
                }

                if (token.Length > 0
                    && !fix.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Suggestions.Add(new SuggestionItem
                {
                    Kind = SuggestionKind.RouteFix,
                    Text = fix,
                    Description = "Route",
                    InsertText = prefix + fix + " ",
                });
                count++;
            }
        }

        // Tier 2: All navdata fixes
        if (FixDb is not null)
        {
            AddNavdataFixSuggestions(token, prefix, ref count);
        }
    }

    private void AddNavdataFixSuggestions(
        string token, string prefix, ref int count)
    {
        var allNames = FixDb!.AllFixNames;

        if (token.Length == 0)
        {
            // No prefix typed — don't show all 40k fixes
            return;
        }

        // Binary search for the first name matching the prefix
        int lo = 0, hi = allNames.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (string.Compare(
                allNames[mid], 0, token, 0, token.Length,
                StringComparison.OrdinalIgnoreCase) < 0)
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
        foreach (var s in Suggestions)
        {
            if (s.Kind == SuggestionKind.RouteFix)
            {
                existing.Add(s.Text);
            }
        }

        for (int i = lo; i < allNames.Length && count < MaxSuggestions; i++)
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

            Suggestions.Add(new SuggestionItem
            {
                Kind = SuggestionKind.Fix,
                Text = name,
                Description = "",
                InsertText = prefix + name + " ",
            });
            count++;
        }
    }

    private SortedSet<string> CollectRouteFixNames(AircraftModel aircraft)
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

        if (!string.IsNullOrWhiteSpace(aircraft.Route) && FixDb is not null)
        {
            var expanded = FixDb.ExpandRoute(aircraft.Route);
            foreach (var fix in expanded)
            {
                fixes.Add(fix);
            }
        }

        return fixes;
    }

    private static string GetTextBeforeLastWord(string text)
    {
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace >= 0)
        {
            return text[..(lastSpace + 1)];
        }

        return "";
    }

    private bool TryAddAddArgumentSuggestions(
        string fragment, string fullText, CommandScheme scheme,
        AircraftModel? selectedAircraft)
    {
        if (!scheme.Patterns.TryGetValue(
            Sim.Commands.CanonicalCommandType.Add, out var addPattern))
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
        var prefix = GetTextBeforeLastWord(fullText);

        switch (completedArgs)
        {
            case 0:
                AddAddOptions(prefix, partial,
                    ("I", "IFR — Instrument flight rules"),
                    ("V", "VFR — Visual flight rules"));
                break;

            case 1:
                AddAddOptions(prefix, partial,
                    ("S", "Small — GA/light aircraft"),
                    ("L", "Large — Regional/narrow-body"),
                    ("H", "Heavy — Wide-body"));
                break;

            case 2:
            {
                var weight = ParseWeightToken(words[2]);
                if (weight is not null)
                {
                    AddEngineOptions(prefix, partial, weight.Value);
                }
                break;
            }

            case >= 3:
                AddPositionSuggestions(
                    words, fullText, prefix, partial,
                    completedArgs, selectedAircraft);
                break;
        }

        return Suggestions.Count > 0;
    }

    private void AddAddOptions(
        string prefix, string partial,
        params (string Value, string Description)[] options)
    {
        foreach (var (value, desc) in options)
        {
            if (Suggestions.Count >= MaxSuggestions)
            {
                break;
            }

            if (partial.Length > 0
                && !value.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Suggestions.Add(new SuggestionItem
            {
                Kind = SuggestionKind.Command,
                Text = value,
                Description = desc,
                InsertText = prefix + value + " ",
            });
        }
    }

    private void AddEngineOptions(
        string prefix, string partial, WeightClass weight)
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
            WeightClass.Heavy => new (string, string)[]
            {
                ("J", "Jet — " + FormatTypes(weight, EngineKind.Jet)),
            },
            _ => [],
        };

        AddAddOptions(prefix, partial, options);
    }

    private static string FormatTypes(WeightClass weight, EngineKind engine)
    {
        var types = AircraftGenerator.GetTypesForCombo(weight, engine);
        return types is not null ? string.Join(", ", types) : "";
    }

    private void AddPositionSuggestions(
        string[] words, string fullText, string prefix, string partial,
        int completedArgs, AircraftModel? selectedAircraft)
    {
        if (completedArgs == 3)
        {
            if (partial.StartsWith('@'))
            {
                // User is typing @fixname — show fix suggestions
                var fixPartial = partial.Length > 1 ? partial[1..] : "";
                AddAtFixSuggestions(fixPartial, prefix, selectedAircraft);
            }
            else
            {
                // Show all position variant hints + runway suggestions
                AddAddOptions(prefix, partial,
                    ("-", "Airborne — -{bearing} {dist_nm} {alt_ft}"),
                    ("@", "At fix — @{fix_or_FRD} {alt_ft}"));
                AddRunwaySuggestions(prefix, partial);
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
                AddTypeAndAirlineOverrides(words, prefix, partial);
            }
        }
        else if (isFixVariant)
        {
            if (completedArgs >= 5)
            {
                AddTypeAndAirlineOverrides(words, prefix, partial);
            }
        }
        else
        {
            // Runway variant
            if (completedArgs >= 4)
            {
                AddTypeAndAirlineOverrides(words, prefix, partial);
            }
        }
    }

    private void AddRunwaySuggestions(string prefix, string partial)
    {
        if (FixDb is null || string.IsNullOrEmpty(PrimaryAirportId))
        {
            return;
        }

        var runways = FixDb.GetRunways(PrimaryAirportId);
        foreach (var rwy in runways)
        {
            if (Suggestions.Count >= MaxSuggestions)
            {
                break;
            }

            if (partial.Length > 0
                && !rwy.RunwayId.StartsWith(
                    partial, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var desc = $"Runway — lined up, or add distance for final";
            Suggestions.Add(new SuggestionItem
            {
                Kind = SuggestionKind.Command,
                Text = rwy.RunwayId,
                Description = desc,
                InsertText = prefix + rwy.RunwayId + " ",
            });
        }
    }

    private void AddAtFixSuggestions(
        string fixPartial, string prefix, AircraftModel? selectedAircraft)
    {
        var count = Suggestions.Count;

        // Tier 1: Route fixes from selected aircraft
        if (selectedAircraft is not null && FixDb is not null)
        {
            var routeFixes = CollectRouteFixNames(selectedAircraft);
            foreach (var fix in routeFixes)
            {
                if (count >= MaxSuggestions)
                {
                    break;
                }

                if (fixPartial.Length > 0
                    && !fix.StartsWith(
                        fixPartial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Suggestions.Add(new SuggestionItem
                {
                    Kind = SuggestionKind.RouteFix,
                    Text = $"@{fix}",
                    Description = "Route",
                    InsertText = prefix + $"@{fix} ",
                });
                count++;
            }
        }

        // Tier 2: All navdata fixes
        if (FixDb is not null && fixPartial.Length > 0)
        {
            var allNames = FixDb.AllFixNames;

            // Binary search for first match
            int lo = 0, hi = allNames.Length - 1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (string.Compare(
                    allNames[mid], 0, fixPartial, 0, fixPartial.Length,
                    StringComparison.OrdinalIgnoreCase) < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            // Avoid duplicates with route fixes
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Suggestions)
            {
                if (s.Kind == SuggestionKind.RouteFix)
                {
                    // Strip the @ prefix to get the raw fix name
                    existing.Add(
                        s.Text.StartsWith('@') ? s.Text[1..] : s.Text);
                }
            }

            for (int i = lo; i < allNames.Length && count < MaxSuggestions; i++)
            {
                var name = allNames[i];
                if (!name.StartsWith(
                    fixPartial, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (existing.Contains(name))
                {
                    continue;
                }

                Suggestions.Add(new SuggestionItem
                {
                    Kind = SuggestionKind.Fix,
                    Text = $"@{name}",
                    Description = "",
                    InsertText = prefix + $"@{name} ",
                });
                count++;
            }
        }
    }

    private void AddTypeAndAirlineOverrides(
        string[] words, string prefix, string partial)
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
                if (Suggestions.Count >= MaxSuggestions)
                {
                    break;
                }

                if (partial.Length > 0
                    && !type.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Suggestions.Add(new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = type,
                    Description = "Aircraft type override",
                    InsertText = prefix + type + " ",
                });
            }
        }

        if (partial.Length == 0 || partial.StartsWith('*'))
        {
            var airlinePartial = partial.Length > 1 ? partial[1..] : "";
            foreach (var airline in AircraftGenerator.GetAirlines())
            {
                if (Suggestions.Count >= MaxSuggestions)
                {
                    break;
                }

                if (airlinePartial.Length > 0
                    && !airline.StartsWith(
                        airlinePartial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var display = $"*{airline}";
                Suggestions.Add(new SuggestionItem
                {
                    Kind = SuggestionKind.Command,
                    Text = display,
                    Description = "Airline callsign override",
                    InsertText = prefix + display + " ",
                });
            }
        }
    }

    private static WeightClass? ParseWeightToken(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "S" => WeightClass.Small,
            "L" => WeightClass.Large,
            "H" => WeightClass.Heavy,
            _ => null,
        };
    }

    private static EngineKind? ParseEngineToken(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "P" => EngineKind.Piston,
            "T" => EngineKind.Turboprop,
            "J" => EngineKind.Jet,
            _ => null,
        };
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
                Description = "At {fix/FR/FRD} — trigger at fix, radial, or FRD point",
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

    private static readonly HashSet<Sim.Commands.CanonicalCommandType> DelayedOnlyCommands =
    [
        Sim.Commands.CanonicalCommandType.SpawnNow,
        Sim.Commands.CanonicalCommandType.SpawnDelay,
        Sim.Commands.CanonicalCommandType.Delete,
    ];

    private void AddCommandVerbSuggestions(
        string token,
        string fullText,
        CommandScheme scheme,
        AircraftModel? targetAircraft = null)
    {
        var isDelayed = targetAircraft?.IsDelayedOrDeferred == true;

        // Collect candidates with match quality so exact alias matches sort first.
        // 0 = exact alias match, 1 = alias prefix match, 2 = label substring match
        var candidates = new List<(int Rank, CommandMetadata.CommandInfo Cmd, CommandPattern Pattern)>();

        foreach (var cmd in CommandMetadata.AllCommands)
        {
            // For delayed/deferred aircraft, only show spawn-related commands
            if (isDelayed && !DelayedOnlyCommands.Contains(cmd.Type))
            {
                continue;
            }
            if (!scheme.Patterns.TryGetValue(cmd.Type, out var pattern))
            {
                continue;
            }

            int bestRank = int.MaxValue;
            foreach (var alias in pattern.Aliases)
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

            if (bestRank == int.MaxValue
                && cmd.Label.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                bestRank = 2;
            }

            if (bestRank < int.MaxValue)
            {
                candidates.Add((bestRank, cmd, pattern));
            }
        }

        candidates.Sort((a, b) => a.Rank.CompareTo(b.Rank));

        var count = Suggestions.Count;
        foreach (var (_, cmd, pattern) in candidates)
        {
            if (count >= MaxSuggestions)
            {
                break;
            }

            var argHint = cmd.SampleArg is not null ? $" {{{cmd.SampleArg}}}" : "";
            var aliasHint = pattern.Aliases.Count > 1
                ? $" ({string.Join(", ", pattern.Aliases)})"
                : "";
            var desc = $"{cmd.Label}{argHint}{aliasHint}";
            var needsArg = pattern.Format.Contains("{arg}");
            var prefix = GetTextBeforeCurrentToken(fullText);
            var insertText = prefix + pattern.PrimaryVerb + (needsArg ? " " : "");

            Suggestions.Add(new SuggestionItem
            {
                Kind = SuggestionKind.Command,
                Text = pattern.PrimaryVerb,
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
