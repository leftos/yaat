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
    public required string Format { get; init; }
    public required string? SampleArg { get; init; }

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
        var result = Format.Replace("{verb}", primary);
        if (SampleArg is not null)
        {
            result = result.Replace("{arg}", SampleArg);
        }

        return result.Trim();
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
        var paramNames = def.ParameterNames;
        var paramHint = paramNames.Count > 0 ? " " + string.Join(" ", paramNames.Select(n => $"${n}")) : "";
        Preview = $"#{Name}{paramHint} → {Expansion}";
    }
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly UserPreferences _preferences;

    private static readonly IReadOnlyList<CommandMetadata.CommandInfo> DisplayCommands = CommandMetadata
        .AllCommands.Where(c => !c.IsGlobal && c.Type != CanonicalCommandType.DirectTo)
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
    private bool _autoClearedToLand;

    [ObservableProperty]
    private bool _autoCrossRunway;

    [ObservableProperty]
    private string _aircraftSelectKeyDisplay = "Numpad +";

    [ObservableProperty]
    private string _focusInputKeyDisplay = "~";

    [ObservableProperty]
    private bool _isCapturingKey;

    private string _aircraftSelectKeyName = "Add";
    private string _focusInputKeyName = "OemTilde";
    private string? _captureTarget;

    public static IReadOnlyList<string> AutoDeleteOptions { get; } = ["Use Scenario Setting", "Never", "On Landing", "On Parking"];

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
        _autoClearedToLand = _preferences.AutoClearedToLand;
        _autoCrossRunway = _preferences.AutoCrossRunway;
        _aircraftSelectKeyName = _preferences.AircraftSelectKey;
        _aircraftSelectKeyDisplay = KeyNameToDisplay(_aircraftSelectKeyName);
        _focusInputKeyName = _preferences.FocusInputKey;
        _focusInputKeyDisplay = KeyNameToDisplay(_focusInputKeyName);
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
        _preferences.SetSimulationShortcuts(AutoClearedToLand, AutoCrossRunway);
        _preferences.SetAircraftSelectKey(_aircraftSelectKeyName);
        _preferences.SetFocusInputKey(_focusInputKeyName);
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

        foreach (var cmd in DisplayCommands)
        {
            if (!scheme.Patterns.TryGetValue(cmd.Type, out var pattern))
            {
                continue;
            }

            var row = new VerbMappingRow
            {
                CommandType = cmd.Type,
                CommandName = cmd.Label,
                Category = cmd.Category,
                Format = pattern.Format,
                SampleArg = cmd.SampleArg,
                Aliases = string.Join(", ", pattern.Aliases),
                Example = BuildExample(pattern, cmd.SampleArg),
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
            patterns[type] = new CommandPattern { Aliases = [.. pattern.Aliases], Format = pattern.Format };
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

    private static string BuildExample(CommandPattern pattern, string? sampleArg)
    {
        var result = pattern.Format.Replace("{verb}", pattern.PrimaryVerb);

        if (sampleArg is not null)
        {
            result = result.Replace("{arg}", sampleArg);
        }

        return result.Trim();
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
        var existingNames = new HashSet<string>(MacroRows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var m in macros)
        {
            if (existingNames.Contains(m.Name))
            {
                // Overwrite existing
                var existing = MacroRows.First(r => string.Equals(r.Name, m.Name, StringComparison.OrdinalIgnoreCase));
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
        foreach (var cmd in CommandMetadata.AllCommands)
        {
            if (cmd.Type == type)
            {
                return cmd.Label;
            }
        }

        return null;
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

    private void StartKeyCaptureFor(string target)
    {
        _captureTarget = target;
        IsCapturingKey = true;
        if (target == "AircraftSelect")
        {
            AircraftSelectKeyDisplay = "Press a key...";
        }
        else
        {
            FocusInputKeyDisplay = "Press a key...";
        }
    }

    public void CaptureKey(Key key)
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

        var keyName = key.ToString();
        if (_captureTarget == "AircraftSelect")
        {
            _aircraftSelectKeyName = keyName;
            AircraftSelectKeyDisplay = KeyNameToDisplay(keyName);
        }
        else
        {
            _focusInputKeyName = keyName;
            FocusInputKeyDisplay = KeyNameToDisplay(keyName);
        }

        IsCapturingKey = false;
        _captureTarget = null;
    }

    public void CancelKeyCapture()
    {
        if (IsCapturingKey)
        {
            if (_captureTarget == "AircraftSelect")
            {
                AircraftSelectKeyDisplay = KeyNameToDisplay(_aircraftSelectKeyName);
            }
            else
            {
                FocusInputKeyDisplay = KeyNameToDisplay(_focusInputKeyName);
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
}
