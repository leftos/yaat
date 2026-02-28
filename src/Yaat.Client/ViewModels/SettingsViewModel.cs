using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.ViewModels;

public partial class VerbMappingRow : ObservableObject
{
    public required CanonicalCommandType CommandType { get; init; }
    public required string CommandName { get; init; }
    public required string Format { get; init; }
    public required string? SampleArg { get; init; }

    [ObservableProperty]
    private string _verb = "";

    [ObservableProperty]
    private string _example = "";

    public event Action? VerbEdited;

    partial void OnVerbChanged(string value)
    {
        Example = BuildExample();
        VerbEdited?.Invoke();
    }

    private string BuildExample()
    {
        var result = Format.Replace("{verb}", Verb);
        if (SampleArg is not null)
        {
            result = result.Replace("{arg}", SampleArg);
        }

        return result.Trim();
    }
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly UserPreferences _preferences;
    private bool _suppressPresetDetection;

    private static readonly string[] PresetNames = ["ATCTrainer", "VICE"];

    private static readonly IReadOnlyList<CommandMetadata.CommandInfo> DisplayCommands =
        CommandMetadata.AllCommands.Where(c => !c.IsGlobal && c.Type != CanonicalCommandType.DirectTo).ToArray();

    [ObservableProperty]
    private int _selectedPresetIndex;

    [ObservableProperty]
    private bool _isAdminMode;

    [ObservableProperty]
    private string _adminPassword = "";

    public static IReadOnlyList<string> PresetNames_ => PresetNames;

    public ObservableCollection<VerbMappingRow> VerbMappings { get; } = [];

    public SettingsViewModel()
        : this(new UserPreferences()) { }

    public SettingsViewModel(UserPreferences preferences)
    {
        _preferences = preferences;
        LoadFromScheme(_preferences.CommandScheme);
        DetectAndUpdatePreset();
        _isAdminMode = _preferences.IsAdminMode;
        _adminPassword = _preferences.AdminPassword;
    }

    partial void OnSelectedPresetIndexChanged(int value)
    {
        if (_suppressPresetDetection)
            return;

        if (value < 0 || value >= PresetNames.Length)
            return;

        var scheme = value == 1 ? CommandScheme.Vice() : CommandScheme.AtcTrainer();

        _suppressPresetDetection = true;
        try
        {
            LoadFromScheme(scheme);
        }
        finally
        {
            _suppressPresetDetection = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var scheme = BuildSchemeFromRows();
        _preferences.SetCommandScheme(scheme);
        _preferences.SetAdminSettings(IsAdminMode, AdminPassword);
        Saved = true;
    }

    public bool Saved { get; private set; }

    private void LoadFromScheme(CommandScheme scheme)
    {
        // Unhook existing rows
        foreach (var row in VerbMappings)
        {
            row.VerbEdited -= OnVerbEdited;
        }

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
                Format = pattern.Format,
                SampleArg = cmd.SampleArg,
                Verb = pattern.Verb,
                Example = BuildExample(pattern, cmd.SampleArg),
            };

            row.VerbEdited += OnVerbEdited;
            VerbMappings.Add(row);
        }
    }

    private void OnVerbEdited()
    {
        if (!_suppressPresetDetection)
        {
            DetectAndUpdatePreset();
        }
    }

    private void DetectAndUpdatePreset()
    {
        var scheme = BuildSchemeFromRows();
        var preset = CommandScheme.DetectPresetName(scheme);

        _suppressPresetDetection = true;
        try
        {
            SelectedPresetIndex = preset switch
            {
                "ATCTrainer" => 0,
                "VICE" => 1,
                _ => -1,
            };
        }
        finally
        {
            _suppressPresetDetection = false;
        }
    }

    private CommandScheme BuildSchemeFromRows()
    {
        // Determine parse mode from current preset or keep current
        var parseMode = SelectedPresetIndex == 1 ? CommandParseMode.Concatenated : CommandParseMode.SpaceSeparated;

        // Start with the matching base preset for all patterns
        // (including non-displayed ones like Pause/Unpause/SimRate)
        var baseScheme = parseMode == CommandParseMode.Concatenated ? CommandScheme.Vice() : CommandScheme.AtcTrainer();

        var patterns = new Dictionary<CanonicalCommandType, CommandPattern>();

        foreach (var (type, pattern) in baseScheme.Patterns)
        {
            patterns[type] = new CommandPattern { Verb = pattern.Verb, Format = pattern.Format };
        }

        // Override verbs from edited rows
        foreach (var row in VerbMappings)
        {
            if (patterns.TryGetValue(row.CommandType, out var existing))
            {
                existing.Verb = row.Verb;
            }
        }

        return new CommandScheme { ParseMode = parseMode, Patterns = patterns };
    }

    private static string BuildExample(CommandPattern pattern, string? sampleArg)
    {
        var result = pattern.Format.Replace("{verb}", pattern.Verb);

        if (sampleArg is not null)
        {
            result = result.Replace("{arg}", sampleArg);
        }

        return result.Trim();
    }
}
