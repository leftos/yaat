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

public partial class SettingsViewModel : ObservableObject
{
    private readonly UserPreferences _preferences;

    private static readonly IReadOnlyList<CommandMetadata.CommandInfo> DisplayCommands = CommandMetadata
        .AllCommands.Where(c => !c.IsGlobal && c.Type != CanonicalCommandType.DirectTo)
        .ToArray();

    [ObservableProperty]
    private string _serverUrl = "http://localhost:5000";

    [ObservableProperty]
    private string _vatsimCid = "";

    [ObservableProperty]
    private string _userInitials = "";

    [ObservableProperty]
    private string _artccId = "";

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

    public static IReadOnlyList<string> AutoDeleteOptions { get; } = ["Use Scenario Setting", "Never", "On Landing", "On Parking"];

    public ObservableCollection<VerbMappingRow> VerbMappings { get; } = [];

    public SettingsViewModel()
        : this(new UserPreferences()) { }

    public SettingsViewModel(UserPreferences preferences)
    {
        _preferences = preferences;
        LoadFromScheme(_preferences.CommandScheme);
        _serverUrl = _preferences.ServerUrl;
        _vatsimCid = _preferences.VatsimCid;
        _userInitials = _preferences.UserInitials;
        _artccId = _preferences.ArtccId;
        _isAdminMode = _preferences.IsAdminMode;
        _adminPassword = _preferences.AdminPassword;
        _autoAcceptEnabled = _preferences.AutoAcceptEnabled;
        _autoAcceptDelaySeconds = _preferences.AutoAcceptDelaySeconds;
        _selectedAutoDeleteIndex = AutoDeleteOverrideToIndex(_preferences.AutoDeleteOverride);
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
        _preferences.SetServerUrl(ServerUrl);
        _preferences.SetVatsimCid(VatsimCid);
        _preferences.SetUserInitials(UserInitials);
        _preferences.SetArtccId(ArtccId);
        _preferences.SetAdminSettings(IsAdminMode, AdminPassword);
        _preferences.SetAutoAcceptSettings(AutoAcceptEnabled, AutoAcceptDelaySeconds);
        _preferences.SetAutoDeleteOverride(IndexToAutoDeleteOverride(SelectedAutoDeleteIndex));
        Saved = true;
    }

    public bool Saved { get; private set; }

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
}
