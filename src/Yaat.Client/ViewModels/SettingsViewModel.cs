using System.Collections.ObjectModel;
using Avalonia.Collections;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.ViewModels;

public partial class VerbMappingRow : ObservableObject
{
    public required CanonicalCommandType CommandType { get; init; }
    public required string CommandName { get; init; }
    public required string Category { get; init; }
    public required ArgMode ArgMode { get; init; }
    public required string SampleArg { get; init; }

    [ObservableProperty]
    private string _aliases = "";

    [ObservableProperty]
    private string _example = "";

    public List<string> AliasesList => Aliases.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    partial void OnAliasesChanged(string value)
    {
        Example = BuildExample();
    }

    private string BuildExample()
    {
        var primary = AliasesList.Count > 0 ? AliasesList[0] : "";
        return ArgMode switch
        {
            ArgMode.Required => $"{primary} {SampleArg}",
            ArgMode.Optional => $"{primary} [{SampleArg}]",
            _ => primary,
        };
    }
}

public partial class MacroRow : ObservableObject
{
    public Action<MacroRow>? RemoveAction { get; set; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _expansion = "";

    [ObservableProperty]
    private string _preview = "";

    [RelayCommand]
    private void Remove()
    {
        RemoveAction?.Invoke(this);
    }

    partial void OnNameChanged(string value)
    {
        UpdatePreview();
    }

    partial void OnExpansionChanged(string value)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Expansion))
        {
            Preview = "";
            return;
        }

        var def = new MacroDefinition { Name = Name, Expansion = Expansion };
        var baseName = def.BaseName;
        var validationError = def.Validate();
        var paramNames = def.ParameterNames;
        var paramHint = paramNames.Count > 0 ? " " + string.Join(" ", paramNames.Select(n => $"${n}")) : "";
        var warning = validationError is not null ? $" ⚠ {validationError}" : "";
        Preview = $"#{baseName}{paramHint} → {Expansion}{warning}";
    }
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly UserPreferences _preferences;

    private static readonly IReadOnlyList<CommandDefinition> DisplayCommands = CommandRegistry
        .All.Values.Where(c => c.Type != CanonicalCommandType.DirectTo)
        .ToArray();

    [ObservableProperty]
    private string _vatsimCid = "";

    [ObservableProperty]
    private string _userInitials = "";

    [ObservableProperty]
    private string _artccId = "";

    [ObservableProperty]
    private string _testCommandInput = "";

    [ObservableProperty]
    private string _testCommandResult = "";

    [ObservableProperty]
    private bool _testCommandIsError;

    [ObservableProperty]
    private bool _isAdminMode;

    [ObservableProperty]
    private string _adminPassword = "";

    [ObservableProperty]
    private bool _autoAcceptEnabled;

    [ObservableProperty]
    private int _autoAcceptDelaySeconds;

    [ObservableProperty]
    private int _selectedAutoDeleteIndex;

    [ObservableProperty]
    private bool _validateDctFixes;

    [ObservableProperty]
    private bool _autoClearedToLandGnd;

    [ObservableProperty]
    private bool _autoClearedToLandTwr;

    [ObservableProperty]
    private bool _autoClearedToLandApp;

    [ObservableProperty]
    private bool _autoClearedToLandCtr;

    [ObservableProperty]
    private bool _autoCrossRunway;

    [ObservableProperty]
    private string _aircraftSelectKeyDisplay = "Numpad +";

    [ObservableProperty]
    private string _focusInputKeyDisplay = "~";

    [ObservableProperty]
    private string _takeControlKeyDisplay = "Ctrl + T";

    [ObservableProperty]
    private bool _assignmentTintEnabled;

    [ObservableProperty]
    private string _assignmentTintColor = "#00FF00";

    [ObservableProperty]
    private bool _unassignedTintEnabled;

    [ObservableProperty]
    private string _unassignedTintColor = "#888888";

    [ObservableProperty]
    private string _selectedColor = "#FFFFFF";

    [ObservableProperty]
    private int _selectedSignatureHelpPlacementIndex;

    [ObservableProperty]
    private int _dataGridFontSize;

    [ObservableProperty]
    private bool _isCapturingKey;

    private string _aircraftSelectKeyName = "Add";
    private string _focusInputKeyName = "OemTilde";
    private string _takeControlKeyName = "Ctrl+T";
    private string? _captureTarget;

    public static IReadOnlyList<string> AutoDeleteOptions { get; } = ["Use Scenario Setting", "Never", "On Landing", "On Parking"];
    public static IReadOnlyList<string> SignatureHelpPlacementOptions { get; } = ["Above", "Below"];

    public ObservableCollection<VerbMappingRow> VerbMappings { get; } = [];
    public DataGridCollectionView GroupedVerbMappings { get; }
    public ObservableCollection<MacroRow> MacroRows { get; } = [];

    public SettingsViewModel()
        : this(new UserPreferences()) { }

    public SettingsViewModel(UserPreferences preferences)
    {
        _preferences = preferences;
        GroupedVerbMappings = new DataGridCollectionView(VerbMappings);
        GroupedVerbMappings.GroupDescriptions.Add(new DataGridPathGroupDescription("Category"));
        LoadFromScheme(_preferences.CommandScheme);
        _vatsimCid = _preferences.VatsimCid;
        _userInitials = _preferences.UserInitials;
        _artccId = _preferences.ArtccId;
        _isAdminMode = _preferences.IsAdminMode;
        _adminPassword = _preferences.AdminPassword;
        _autoAcceptEnabled = _preferences.AutoAcceptEnabled;
        _autoAcceptDelaySeconds = _preferences.AutoAcceptDelaySeconds;
        _selectedAutoDeleteIndex = AutoDeleteOverrideToIndex(_preferences.AutoDeleteOverride);
        _validateDctFixes = _preferences.ValidateDctFixes;
        _autoClearedToLandGnd = _preferences.AutoClearedToLandGnd;
        _autoClearedToLandTwr = _preferences.AutoClearedToLandTwr;
        _autoClearedToLandApp = _preferences.AutoClearedToLandApp;
        _autoClearedToLandCtr = _preferences.AutoClearedToLandCtr;
        _autoCrossRunway = _preferences.AutoCrossRunway;
        _aircraftSelectKeyName = _preferences.AircraftSelectKey;
        _aircraftSelectKeyDisplay = KeyComboToDisplay(_aircraftSelectKeyName);
        _focusInputKeyName = _preferences.FocusInputKey;
        _focusInputKeyDisplay = KeyComboToDisplay(_focusInputKeyName);
        _takeControlKeyName = _preferences.TakeControlKey;
        _takeControlKeyDisplay = KeyComboToDisplay(_takeControlKeyName);
        _assignmentTintEnabled = _preferences.AssignmentTintEnabled;
        _assignmentTintColor = _preferences.AssignmentTintColor;
        _unassignedTintEnabled = _preferences.UnassignedTintEnabled;
        _unassignedTintColor = _preferences.UnassignedTintColor;
        _selectedColor = _preferences.SelectedColor;
        _selectedSignatureHelpPlacementIndex = _preferences.SignatureHelpPlacement == "Below" ? 1 : 0;
        _dataGridFontSize = _preferences.DataGridFontSize;
        LoadMacros();
    }

    partial void OnUserInitialsChanged(string value)
    {
        var upper = value.ToUpperInvariant();
        if (upper != value)
        {
            UserInitials = upper;
        }
    }

    partial void OnArtccIdChanged(string value)
    {
        var upper = value.ToUpperInvariant();
        if (upper != value)
        {
            ArtccId = upper;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var scheme = BuildSchemeFromRows();
        _preferences.SetCommandScheme(scheme);
        _preferences.SetVatsimCid(VatsimCid);
        _preferences.SetUserInitials(UserInitials);
        _preferences.SetArtccId(ArtccId);
        _preferences.SetAdminSettings(IsAdminMode, AdminPassword);
        _preferences.SetAutoAcceptSettings(AutoAcceptEnabled, AutoAcceptDelaySeconds);
        _preferences.SetAutoDeleteOverride(IndexToAutoDeleteOverride(SelectedAutoDeleteIndex));
        _preferences.SetValidateDctFixes(ValidateDctFixes);
        _preferences.SetSimulationShortcuts(AutoClearedToLandGnd, AutoClearedToLandTwr, AutoClearedToLandApp, AutoClearedToLandCtr, AutoCrossRunway);
        _preferences.SetAircraftSelectKey(_aircraftSelectKeyName);
        _preferences.SetFocusInputKey(_focusInputKeyName);
        _preferences.SetTakeControlKey(_takeControlKeyName);
        _preferences.SetAssignmentTint(AssignmentTintEnabled, AssignmentTintColor);
        _preferences.SetUnassignedTint(UnassignedTintEnabled, UnassignedTintColor);
        _preferences.SetSelectedColor(SelectedColor);
        _preferences.SetSignatureHelpPlacement(SelectedSignatureHelpPlacementIndex == 1 ? "Below" : "Above");
        _preferences.SetDataGridFontSize(DataGridFontSize);
        SaveMacros();
        Saved = true;
    }

    public bool Saved { get; private set; }

    [RelayCommand]
    private void ResetCommandsToDefaults()
    {
        LoadFromScheme(CommandScheme.Default());

        // Re-run the test input against the reset scheme
        OnTestCommandInputChanged(TestCommandInput);
    }

    private void LoadFromScheme(CommandScheme scheme)
    {
        VerbMappings.Clear();

        foreach (var def in DisplayCommands)
        {
            if (!scheme.Patterns.TryGetValue(def.Type, out var pattern))
            {
                continue;
            }

            var row = new VerbMappingRow
            {
                CommandType = def.Type,
                CommandName = def.Label,
                Category = def.Category,
                ArgMode = def.ArgMode,
                SampleArg = def.SampleArg,
                Aliases = string.Join(", ", pattern.Aliases),
                Example = BuildExample(def, pattern),
            };

            VerbMappings.Add(row);
        }
    }

    private CommandScheme BuildSchemeFromRows()
    {
        var baseScheme = CommandScheme.Default();

        var patterns = new Dictionary<CanonicalCommandType, CommandPattern>();

        foreach (var (type, pattern) in baseScheme.Patterns)
        {
            patterns[type] = new CommandPattern { Aliases = [.. pattern.Aliases] };
        }

        // Override aliases from edited rows
        foreach (var row in VerbMappings)
        {
            var aliases = row.AliasesList;
            if (aliases.Count > 0 && patterns.TryGetValue(row.CommandType, out var existing))
            {
                existing.Aliases = aliases;
            }
        }

        return new CommandScheme { Patterns = patterns };
    }

    private static string BuildExample(CommandDefinition def, CommandPattern pattern)
    {
        var primary = pattern.PrimaryVerb;
        return def.ArgMode switch
        {
            ArgMode.Required => $"{primary} {def.SampleArg}",
            ArgMode.Optional => $"{primary} [{def.SampleArg}]",
            _ => primary,
        };
    }

    [RelayCommand]
    private void AddMacro()
    {
        MacroRows.Add(new MacroRow { RemoveAction = r => MacroRows.Remove(r) });
    }

    [RelayCommand]
    private void ClearAllMacros()
    {
        MacroRows.Clear();
    }

    public void ImportMacros(IEnumerable<SavedMacro> macros)
    {
        var existingBaseNames = new HashSet<string>(MacroRows.Select(r => MacroDefinition.ExtractBaseName(r.Name)), StringComparer.OrdinalIgnoreCase);

        foreach (var m in macros)
        {
            var importBaseName = MacroDefinition.ExtractBaseName(m.Name);
            if (existingBaseNames.Contains(importBaseName))
            {
                // Overwrite existing (match on base name so "HC $a $b" overwrites "HC" or "HC $x $y")
                var existing = MacroRows.First(r =>
                    string.Equals(MacroDefinition.ExtractBaseName(r.Name), importBaseName, StringComparison.OrdinalIgnoreCase)
                );
                existing.Name = m.Name;
                existing.Expansion = m.Expansion;
            }
            else
            {
                MacroRows.Add(
                    new MacroRow
                    {
                        Name = m.Name,
                        Expansion = m.Expansion,
                        RemoveAction = r => MacroRows.Remove(r),
                    }
                );
                existingBaseNames.Add(importBaseName);
            }
        }
    }

    public List<SavedMacro> ExportMacros(IEnumerable<MacroRow>? rows = null)
    {
        return (rows ?? MacroRows)
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Expansion))
            .Select(r => new SavedMacro { Name = r.Name.Trim(), Expansion = r.Expansion.Trim() })
            .ToList();
    }

    private void LoadMacros()
    {
        MacroRows.Clear();
        foreach (var m in _preferences.Macros)
        {
            MacroRows.Add(
                new MacroRow
                {
                    Name = m.Name,
                    Expansion = m.Expansion,
                    RemoveAction = r => MacroRows.Remove(r),
                }
            );
        }
    }

    private void SaveMacros()
    {
        var macros = MacroRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Expansion))
            .Select(r => new MacroDefinition { Name = r.Name.Trim(), Expansion = r.Expansion.Trim() })
            .ToList();
        _preferences.SetMacros(macros);
    }

    partial void OnTestCommandInputChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            TestCommandResult = "";
            TestCommandIsError = false;
            return;
        }

        var scheme = BuildSchemeFromRows();

        // Expand macros before parsing
        var testInput = value;
        var macros = MacroRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Expansion))
            .Select(r => new MacroDefinition { Name = r.Name.Trim(), Expansion = r.Expansion.Trim() })
            .ToList();
        var expanded = MacroExpander.TryExpand(testInput, macros, out var macroError);
        if (macroError is not null)
        {
            TestCommandResult = macroError;
            TestCommandIsError = true;
            return;
        }
        if (expanded is not null)
        {
            testInput = expanded;
        }

        var compoundResult = CommandSchemeParser.ParseCompound(testInput, scheme);

        if (compoundResult is null)
        {
            TestCommandResult = "Unrecognized command";
            TestCommandIsError = true;
            return;
        }

        var labels = CollectCommandLabels(testInput, scheme);
        var labelSuffix = labels.Count > 0 ? $"  ({string.Join(", ", labels)})" : "";

        TestCommandResult = $"→ {compoundResult.CanonicalString}{labelSuffix}";
        TestCommandIsError = false;
    }

    private static List<string> CollectCommandLabels(string input, CommandScheme scheme)
    {
        var labels = new List<string>();
        var blocks = input.Split(';');

        foreach (var blockStr in blocks)
        {
            var block = blockStr.Trim();
            if (string.IsNullOrEmpty(block))
            {
                continue;
            }

            // Strip condition prefixes (LV/AT/GIVEWAY/BEHIND + argument)
            var upper = block.ToUpperInvariant();
            var remaining = block;
            if (upper.StartsWith("LV ") || upper.StartsWith("AT ") || upper.StartsWith("GIVEWAY ") || upper.StartsWith("BEHIND "))
            {
                var tokens = block.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                remaining = tokens.Length >= 3 ? tokens[2] : "";
            }

            var commands = remaining.Split(',');
            foreach (var cmdStr in commands)
            {
                var cmd = cmdStr.Trim();
                if (string.IsNullOrEmpty(cmd))
                {
                    continue;
                }

                var parsed = CommandSchemeParser.Parse(cmd, scheme);
                if (parsed is not null)
                {
                    var label = LabelForType(parsed.Type);
                    if (label is not null)
                    {
                        labels.Add(label);
                    }
                }
            }
        }

        return labels;
    }

    private static string? LabelForType(CanonicalCommandType type)
    {
        return CommandRegistry.Get(type)?.Label;
    }

    private static int AutoDeleteOverrideToIndex(string value) =>
        value switch
        {
            "Never" => 1,
            "OnLanding" => 2,
            "Parked" => 3,
            _ => 0,
        };

    private static string IndexToAutoDeleteOverride(int index) =>
        index switch
        {
            1 => "Never",
            2 => "OnLanding",
            3 => "Parked",
            _ => "",
        };

    [RelayCommand]
    private void StartKeyCapture()
    {
        StartKeyCaptureFor("AircraftSelect");
    }

    [RelayCommand]
    private void StartFocusInputKeyCapture()
    {
        StartKeyCaptureFor("FocusInput");
    }

    [RelayCommand]
    private void StartTakeControlKeyCapture()
    {
        StartKeyCaptureFor("TakeControl");
    }

    private void StartKeyCaptureFor(string target)
    {
        _captureTarget = target;
        IsCapturingKey = true;
        switch (target)
        {
            case "AircraftSelect":
                AircraftSelectKeyDisplay = "Press a key combo...";
                break;
            case "FocusInput":
                FocusInputKeyDisplay = "Press a key combo...";
                break;
            case "TakeControl":
                TakeControlKeyDisplay = "Press a key combo...";
                break;
        }
    }

    public void CaptureKey(Key key, KeyModifiers modifiers)
    {
        if (!IsCapturingKey)
        {
            return;
        }

        // Ignore modifier-only keys
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        var combo = BuildKeyCombo(key, modifiers);
        switch (_captureTarget)
        {
            case "AircraftSelect":
                _aircraftSelectKeyName = combo;
                AircraftSelectKeyDisplay = KeyComboToDisplay(combo);
                break;
            case "FocusInput":
                _focusInputKeyName = combo;
                FocusInputKeyDisplay = KeyComboToDisplay(combo);
                break;
            case "TakeControl":
                _takeControlKeyName = combo;
                TakeControlKeyDisplay = KeyComboToDisplay(combo);
                break;
        }

        IsCapturingKey = false;
        _captureTarget = null;
    }

    public void CancelKeyCapture()
    {
        if (IsCapturingKey)
        {
            switch (_captureTarget)
            {
                case "AircraftSelect":
                    AircraftSelectKeyDisplay = KeyComboToDisplay(_aircraftSelectKeyName);
                    break;
                case "FocusInput":
                    FocusInputKeyDisplay = KeyComboToDisplay(_focusInputKeyName);
                    break;
                case "TakeControl":
                    TakeControlKeyDisplay = KeyComboToDisplay(_takeControlKeyName);
                    break;
            }

            IsCapturingKey = false;
            _captureTarget = null;
        }
    }

    internal static string KeyNameToDisplay(string keyName)
    {
        if (!Enum.TryParse<Key>(keyName, out var key))
        {
            return keyName;
        }

        return key switch
        {
            Key.Add => "Numpad +",
            Key.Subtract => "Numpad -",
            Key.Multiply => "Numpad *",
            Key.Divide => "Numpad /",
            Key.Decimal => "Numpad .",
            Key.NumPad0 => "Numpad 0",
            Key.NumPad1 => "Numpad 1",
            Key.NumPad2 => "Numpad 2",
            Key.NumPad3 => "Numpad 3",
            Key.NumPad4 => "Numpad 4",
            Key.NumPad5 => "Numpad 5",
            Key.NumPad6 => "Numpad 6",
            Key.NumPad7 => "Numpad 7",
            Key.NumPad8 => "Numpad 8",
            Key.NumPad9 => "Numpad 9",
            Key.OemTilde => "~",
            Key.OemMinus => "-",
            Key.OemPlus => "+",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            _ => key.ToString(),
        };
    }

    internal static string BuildKeyCombo(Key key, KeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }
        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    internal static string KeyComboToDisplay(string combo)
    {
        var parts = combo.Split('+');
        var display = new List<string>();
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed is "Ctrl" or "Alt" or "Shift")
            {
                display.Add(trimmed);
            }
            else
            {
                display.Add(KeyNameToDisplay(trimmed));
            }
        }
        return string.Join(" + ", display);
    }

    public static bool ParseKeybind(string combo, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;

        var parts = combo.Split('+');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            switch (trimmed)
            {
                case "Ctrl":
                    modifiers |= KeyModifiers.Control;
                    break;
                case "Alt":
                    modifiers |= KeyModifiers.Alt;
                    break;
                case "Shift":
                    modifiers |= KeyModifiers.Shift;
                    break;
                default:
                    if (!Enum.TryParse(trimmed, out key))
                    {
                        return false;
                    }
                    break;
            }
        }

        return key != Key.None;
    }
}
