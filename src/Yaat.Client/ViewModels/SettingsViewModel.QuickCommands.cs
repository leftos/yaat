using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.ViewModels;

/// <summary>Whether a quick-command row names a catalog action or a command the controller typed.</summary>
public enum QuickCommandEntryKind
{
    /// <summary>A catalog action (<see cref="CatalogQuickCommandEntry"/>).</summary>
    Catalog,

    /// <summary>A typed command (<see cref="CustomQuickCommandEntry"/>).</summary>
    Custom,
}

/// <summary>One choice of a quick-command row's flight-rules picker.</summary>
/// <param name="Rules">The flight rules chosen; null is a catalog row's Default, which takes the catalog entry's rules.</param>
/// <param name="Caption">The text the picker shows, e.g. "IFR only" or "Default (VFR only)".</param>
public sealed record QuickCommandFlightRulesOption(MenuFlightRules? Rules, string Caption)
{
    public override string ToString() => Caption;
}

/// <summary>A situation in the Quick Commands editor's list: its name, and whether its staged list differs from the default or needs fixing.</summary>
public sealed partial class QuickCommandSituationRow(AircraftSituation situation, string name) : ObservableObject
{
    public AircraftSituation Situation { get; } = situation;

    public string Name { get; } = name;

    /// <summary>True while the staged list differs from <see cref="QuickCommandDefaults.For"/>.</summary>
    [ObservableProperty]
    private bool _isChanged;

    /// <summary>True while a custom row of the staged list fails validation.</summary>
    [ObservableProperty]
    private bool _hasErrors;
}

/// <summary>
/// One entry of a situation's staged quick-command list. A catalog row shows its action's label, family and glyph and
/// edits only its flight rules; a custom row edits its label, command, ground command and flight rules, and carries the
/// message that says why it cannot be saved.
/// </summary>
public sealed partial class QuickCommandEntryRow : ObservableObject
{
    /// <summary>The family a custom row reports, so a list can group or colour it beside the catalog families.</summary>
    public const string CustomFamily = "custom";

    private readonly MenuFlightRules? _catalogDefault;

    private QuickCommandEntryRow(
        QuickCommandEntryKind kind,
        string? catalogId,
        string label,
        IReadOnlyList<QuickCommandFlightRulesOption> options,
        MenuFlightRules? flightRules
    )
    {
        Kind = kind;
        CatalogId = catalogId;
        _label = label;
        FlightRulesOptions = options;
        _selectedFlightRules = options.Single(o => o.Rules == flightRules);
        if (catalogId is not null)
        {
            _catalogDefault = MenuCatalog.Get(catalogId).DefaultFlightRules;
            Family = QuickCommandCatalog.FamilyOf(catalogId);
            Glyph = QuickCommandGlyphs.For(catalogId);
        }
        else
        {
            Family = CustomFamily;
        }
    }

    public QuickCommandEntryKind Kind { get; }

    public bool IsCustom => Kind == QuickCommandEntryKind.Custom;

    /// <summary>The catalog action a catalog row names; null for a custom row.</summary>
    public string? CatalogId { get; }

    /// <summary>The catalog family (<see cref="QuickCommandCatalog.FamilyOf"/>), or <see cref="CustomFamily"/>.</summary>
    public string Family { get; }

    /// <summary>The strip icon a catalog row shows; null for a text-only action and for every custom row.</summary>
    public QuickCommandGlyph? Glyph { get; }

    /// <summary>
    /// The flight-rules choices: a catalog row's start with Default (its catalog rules), then IFR and VFR, IFR only and VFR
    /// only; a custom row's are the last three.
    /// </summary>
    public IReadOnlyList<QuickCommandFlightRulesOption> FlightRulesOptions { get; }

    [ObservableProperty]
    private QuickCommandFlightRulesOption _selectedFlightRules;

    /// <summary>The text the menu shows: the catalog label for a catalog row, the typed label for a custom row.</summary>
    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _commandText = "";

    /// <summary>The command a custom row sends while the aircraft is on the ground; blank for none.</summary>
    [ObservableProperty]
    private string _groundCommandText = "";

    /// <summary>Why a custom row cannot be saved; null while it is valid and for every catalog row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _validationMessage;

    /// <summary>True when the row lands in the icon strip: it is one of the first <see cref="QuickCommandGlyphs.StripCapacity"/> glyph rows.</summary>
    [ObservableProperty]
    private bool _isInStrip;

    public bool HasError => ValidationMessage is not null;

    /// <summary>A row for <paramref name="entry"/>, as a stored or default list holds it.</summary>
    public static QuickCommandEntryRow FromEntry(QuickCommandEntry entry) =>
        entry switch
        {
            CatalogQuickCommandEntry catalog => ForCatalog(catalog.CatalogId, catalog.FlightRules),
            CustomQuickCommandEntry custom => new QuickCommandEntryRow(
                QuickCommandEntryKind.Custom,
                null,
                custom.Label,
                CustomOptions,
                custom.FlightRules
            )
            {
                CommandText = custom.CommandText,
                GroundCommandText = custom.GroundCommandText ?? "",
            },
            _ => throw new ArgumentException($"Unknown quick-command entry type {entry.GetType().Name}.", nameof(entry)),
        };

    /// <summary>A row for the catalog action <paramref name="catalogId"/>, offered under <paramref name="flightRules"/> (null: its catalog rules).</summary>
    public static QuickCommandEntryRow ForCatalog(string catalogId, MenuFlightRules? flightRules)
    {
        MenuCatalogEntry entry = MenuCatalog.Get(catalogId);
        QuickCommandFlightRulesOption defaultOption = new(null, $"Default ({Caption(entry.DefaultFlightRules)})");
        return new QuickCommandEntryRow(QuickCommandEntryKind.Catalog, catalogId, entry.Label, [defaultOption, .. CustomOptions], flightRules);
    }

    /// <summary>A blank custom row, offered under both flight rules.</summary>
    public static QuickCommandEntryRow BlankCustom() => new(QuickCommandEntryKind.Custom, null, "", CustomOptions, MenuFlightRules.Both);

    /// <summary>
    /// The entry the row stores: a catalog row's flight rules are null when the choice is its catalog rules, and a custom
    /// row's texts are trimmed, with a blank ground command stored as none.
    /// </summary>
    public QuickCommandEntry ToEntry()
    {
        if (CatalogId is not null)
        {
            MenuFlightRules? rules = (SelectedFlightRules.Rules == _catalogDefault) ? null : SelectedFlightRules.Rules;
            return new CatalogQuickCommandEntry(CatalogId, rules);
        }

        string? ground = string.IsNullOrWhiteSpace(GroundCommandText) ? null : GroundCommandText.Trim();
        MenuFlightRules customRules = SelectedFlightRules.Rules ?? MenuFlightRules.Both;
        return new CustomQuickCommandEntry(Label.Trim(), CommandText.Trim(), ground, customRules);
    }

    private static readonly IReadOnlyList<QuickCommandFlightRulesOption> CustomOptions =
    [
        new(MenuFlightRules.Both, Caption(MenuFlightRules.Both)),
        new(MenuFlightRules.IfrOnly, Caption(MenuFlightRules.IfrOnly)),
        new(MenuFlightRules.VfrOnly, Caption(MenuFlightRules.VfrOnly)),
    ];

    private static string Caption(MenuFlightRules rules) =>
        rules switch
        {
            MenuFlightRules.Both => "IFR and VFR",
            MenuFlightRules.IfrOnly => "IFR only",
            MenuFlightRules.VfrOnly => "VFR only",
            _ => throw new ArgumentOutOfRangeException(nameof(rules), rules, "Unknown flight rules."),
        };
}

/// <summary>
/// The Quick Commands section: every situation's quick-command list as a staged copy, edited without writing, written
/// by Apply only where it differs from what the preferences hold.
/// </summary>
public partial class SettingsViewModel
{
    private readonly Dictionary<AircraftSituation, ObservableCollection<QuickCommandEntryRow>> _quickCommandLists = [];

    /// <summary>Every classified situation, in the enum's order.</summary>
    public IReadOnlyList<QuickCommandSituationRow> QuickCommandSituations { get; } =
    [
        .. QuickCommandSituationNames.Classified.Select(s => new QuickCommandSituationRow(
            s,
            QuickCommandSituationNames.NameOf(s) ?? throw new InvalidOperationException($"Situation {s} has no quick-command name.")
        )),
    ];

    /// <summary>The situation whose list <see cref="QuickCommandEntries"/> shows and the list operations edit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickCommandEntries))]
    [NotifyPropertyChangedFor(nameof(AvailableQuickCommandCatalogEntries))]
    private QuickCommandSituationRow? _selectedQuickCommandSituation;

    /// <summary>The selected situation's staged list, in order; empty with no situation selected.</summary>
    public IReadOnlyList<QuickCommandEntryRow> QuickCommandEntries =>
        (SelectedQuickCommandSituation is { } selected) ? _quickCommandLists[selected.Situation] : [];

    /// <summary>The catalog actions the selected situation's list can add: the eligible ones it does not hold yet.</summary>
    public IReadOnlyList<QuickCommandCatalogItem> AvailableQuickCommandCatalogEntries =>
        (SelectedQuickCommandSituation is { } selected) ? AvailableQuickCommandCatalogEntriesFor(selected.Situation) : [];

    /// <summary>True while a custom row of any situation fails validation; OK and Apply stay disabled until it clears.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _hasQuickCommandErrors;

    /// <summary>False while a key clash or an invalid quick command would be committed; OK and Apply bind to it.</summary>
    public bool CanApply => !HasKeybindClash && !HasQuickCommandErrors;

    /// <summary>Why OK and Apply are disabled, for their tooltip: the key clash and the situations to fix. Null when they are enabled.</summary>
    public string? ApplyBlockedSummary
    {
        get
        {
            List<string> reasons = [.. new[] { KeybindClashSummary, QuickCommandErrorSummary() }.OfType<string>()];
            return (reasons.Count == 0) ? null : string.Join("\n", reasons);
        }
    }

    /// <summary>The eligible catalog actions <paramref name="situation"/>'s staged list does not hold yet, in catalog order.</summary>
    public IReadOnlyList<QuickCommandCatalogItem> AvailableQuickCommandCatalogEntriesFor(AircraftSituation situation)
    {
        HashSet<string> held = [.. _quickCommandLists[situation].Select(r => r.CatalogId).OfType<string>()];
        return [.. QuickCommandCatalog.Eligible.Where(item => !held.Contains(item.Id))];
    }

    /// <summary>
    /// Appends the catalog action <paramref name="catalogId"/> to the selected situation's list, under its catalog flight
    /// rules. Refused (false) with no situation selected, for an ineligible id, and for an action the list already holds.
    /// </summary>
    public bool TryAddQuickCommandCatalogEntry(string catalogId)
    {
        if ((SelectedQuickCommandSituation is not { } selected) || !QuickCommandCatalog.IsEligible(catalogId))
        {
            return false;
        }

        ObservableCollection<QuickCommandEntryRow> rows = _quickCommandLists[selected.Situation];
        if (rows.Any(r => r.CatalogId == catalogId))
        {
            return false;
        }

        AddQuickCommandRow(rows, QuickCommandEntryRow.ForCatalog(catalogId, null));
        RefreshQuickCommandSituation(selected.Situation);
        return true;
    }

    [RelayCommand]
    private void AddQuickCommandCatalogEntry(string catalogId) => TryAddQuickCommandCatalogEntry(catalogId);

    /// <summary>Appends a blank custom row to the selected situation's list; it blocks Apply until it has a label and a command.</summary>
    [RelayCommand]
    public void AddQuickCommandCustomEntry()
    {
        if (SelectedQuickCommandSituation is not { } selected)
        {
            return;
        }

        var row = QuickCommandEntryRow.BlankCustom();
        AddQuickCommandRow(_quickCommandLists[selected.Situation], row);
        ValidateQuickCommandRow(row);
        RefreshQuickCommandSituation(selected.Situation);
    }

    /// <summary>Removes <paramref name="row"/> from the staged list that holds it.</summary>
    [RelayCommand]
    public void RemoveQuickCommandEntry(QuickCommandEntryRow row)
    {
        foreach ((AircraftSituation situation, ObservableCollection<QuickCommandEntryRow> rows) in _quickCommandLists)
        {
            if (rows.Remove(row))
            {
                row.PropertyChanged -= OnQuickCommandRowChanged;
                RefreshQuickCommandSituation(situation);
                return;
            }
        }
    }

    /// <summary>Moves the selected situation's row at <paramref name="fromIndex"/> to <paramref name="toIndex"/>, the drag-and-drop reorder.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Either index is outside the list.</exception>
    public void MoveQuickCommandEntry(int fromIndex, int toIndex)
    {
        if (SelectedQuickCommandSituation is not { } selected)
        {
            return;
        }

        ObservableCollection<QuickCommandEntryRow> rows = _quickCommandLists[selected.Situation];
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromIndex, rows.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(toIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(toIndex, rows.Count);
        if (fromIndex == toIndex)
        {
            return;
        }

        rows.Move(fromIndex, toIndex);
        RefreshQuickCommandSituation(selected.Situation);
    }

    /// <summary>Stages the selected situation's default list; Apply or OK keeps it, Cancel discards it.</summary>
    [RelayCommand]
    public void ResetSelectedQuickCommandSituation()
    {
        if (SelectedQuickCommandSituation is { } selected)
        {
            StageQuickCommandList(selected.Situation, QuickCommandDefaults.For(selected.Situation));
        }
    }

    /// <summary>Stages every situation's default list; Apply or OK keeps them, Cancel discards them.</summary>
    [RelayCommand]
    public void ResetAllQuickCommandSituations()
    {
        foreach (AircraftSituation situation in QuickCommandSituationNames.Classified)
        {
            StageQuickCommandList(situation, QuickCommandDefaults.For(situation));
        }
    }

    partial void OnHasKeybindClashChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    partial void OnKeybindClashSummaryChanged(string? value) => OnPropertyChanged(nameof(ApplyBlockedSummary));

    private void LoadQuickCommandLists()
    {
        foreach (AircraftSituation situation in QuickCommandSituationNames.Classified)
        {
            _quickCommandLists[situation] = [];
            StageQuickCommandList(situation, _preferences.GetQuickCommandList(situation));
        }

        SelectedQuickCommandSituation = QuickCommandSituations[0];
    }

    // Writes each situation whose staged list differs from the stored one; SetQuickCommandList drops a list equal to the default.
    private void ApplyQuickCommandLists()
    {
        foreach ((AircraftSituation situation, ObservableCollection<QuickCommandEntryRow> rows) in _quickCommandLists)
        {
            List<QuickCommandEntry> staged = [.. rows.Select(r => r.ToEntry())];
            if (!staged.SequenceEqual(_preferences.GetQuickCommandList(situation)))
            {
                _preferences.SetQuickCommandList(situation, staged);
            }
        }
    }

    private void StageQuickCommandList(AircraftSituation situation, IReadOnlyList<QuickCommandEntry> entries)
    {
        ObservableCollection<QuickCommandEntryRow> rows = _quickCommandLists[situation];
        foreach (QuickCommandEntryRow row in rows)
        {
            row.PropertyChanged -= OnQuickCommandRowChanged;
        }

        rows.Clear();
        foreach (QuickCommandEntry entry in entries)
        {
            var row = QuickCommandEntryRow.FromEntry(entry);
            AddQuickCommandRow(rows, row);
            ValidateQuickCommandRow(row);
        }

        RefreshQuickCommandSituation(situation);
    }

    private void AddQuickCommandRow(ObservableCollection<QuickCommandEntryRow> rows, QuickCommandEntryRow row)
    {
        rows.Add(row);
        row.PropertyChanged += OnQuickCommandRowChanged;
    }

    private void OnQuickCommandRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((sender is not QuickCommandEntryRow row) || !IsStagedQuickCommandField(e.PropertyName))
        {
            return;
        }

        ValidateQuickCommandRow(row);
        AircraftSituation situation = _quickCommandLists.First(pair => pair.Value.Contains(row)).Key;
        RefreshQuickCommandSituation(situation);
    }

    private static bool IsStagedQuickCommandField(string? propertyName) =>
        propertyName
            is nameof(QuickCommandEntryRow.SelectedFlightRules)
                or nameof(QuickCommandEntryRow.Label)
                or nameof(QuickCommandEntryRow.CommandText)
                or nameof(QuickCommandEntryRow.GroundCommandText);

    private void RefreshQuickCommandSituation(AircraftSituation situation)
    {
        ObservableCollection<QuickCommandEntryRow> rows = _quickCommandLists[situation];
        MarkQuickCommandStrip(rows);

        QuickCommandSituationRow situationRow = QuickCommandSituations.Single(r => r.Situation == situation);
        situationRow.IsChanged = !QuickCommandDefaults.IsDefault(situation, [.. rows.Select(r => r.ToEntry())]);
        situationRow.HasErrors = rows.Any(r => r.HasError);
        HasQuickCommandErrors = QuickCommandSituations.Any(r => r.HasErrors);
        OnPropertyChanged(nameof(ApplyBlockedSummary));
        if (SelectedQuickCommandSituation?.Situation == situation)
        {
            OnPropertyChanged(nameof(AvailableQuickCommandCatalogEntries));
        }
    }

    // The strip takes the first StripCapacity glyph-bearing catalog rows, as QuickCommandGlyphs.Split decides at runtime.
    private static void MarkQuickCommandStrip(IReadOnlyList<QuickCommandEntryRow> rows)
    {
        List<MenuCatalogEntry> catalogEntries = [.. rows.Select(r => r.CatalogId).OfType<string>().Select(MenuCatalog.Get)];
        HashSet<string> stripIds = [.. QuickCommandGlyphs.Split(catalogEntries).Strip.Select(item => item.Entry.Id)];
        foreach (QuickCommandEntryRow row in rows)
        {
            row.IsInStrip = (row.CatalogId is { } id) && stripIds.Contains(id);
        }
    }

    private string? QuickCommandErrorSummary()
    {
        List<string> names = [.. QuickCommandSituations.Where(r => r.HasErrors).Select(r => r.Name)];
        return (names.Count == 0) ? null : $"Quick commands to fix: {string.Join(", ", names)} (Quick commands)";
    }

    private void ValidateQuickCommandRow(QuickCommandEntryRow row)
    {
        if (!row.IsCustom)
        {
            return;
        }

        row.ValidationMessage = QuickCommandRowProblem(row);
    }

    private string? QuickCommandRowProblem(QuickCommandEntryRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Label))
        {
            return "Enter a label.";
        }

        if (string.IsNullOrWhiteSpace(row.CommandText))
        {
            return "Enter a command.";
        }

        CommandScheme scheme = BuildSchemeFromRows();
        List<MacroDefinition> macros =
        [
            .. MacroRows
                .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Expansion))
                .Select(r => new MacroDefinition { Name = r.Name.Trim(), Expansion = r.Expansion.Trim() }),
        ];
        if (CommandProblem(row.CommandText, scheme, macros) is { } commandProblem)
        {
            return $"Command: {commandProblem}";
        }

        return string.IsNullOrWhiteSpace(row.GroundCommandText) ? null
            : CommandProblem(row.GroundCommandText, scheme, macros) is { } groundProblem ? $"Ground command: {groundProblem}"
            : null;
    }

    // The Try-it-out parse: macro expansion, then CommandSchemeParser.ParseCompound against the staged verbs.
    private static string? CommandProblem(string text, CommandScheme scheme, IReadOnlyList<MacroDefinition> macros)
    {
        string trimmed = text.Trim();
        string? expanded = MacroExpander.TryExpand(trimmed, macros, out string? macroError);
        if (macroError is not null)
        {
            return macroError;
        }

        return (CommandSchemeParser.ParseCompound(expanded ?? trimmed, scheme) is null) ? "Unrecognized command" : null;
    }
}
