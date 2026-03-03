using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public sealed class UserPreferences
{
    private static readonly ILogger Log = AppLog.CreateLogger<UserPreferences>();

    private static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yaat");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "preferences.json");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private CommandScheme _commandScheme;
    private string _serverUrl = "http://localhost:5000";
    private string _vatsimCid = "";
    private string _userInitials = "";
    private string _artccId = "";
    private bool _isAdminMode;
    private string _adminPassword = "";
    private SavedWindowGeometry? _mainWindowGeometry;
    private SavedWindowGeometry? _settingsWindowGeometry;
    private SavedWindowGeometry? _terminalWindowGeometry;
    private SavedWindowGeometry? _groundViewWindowGeometry;
    private SavedWindowGeometry? _radarViewWindowGeometry;
    private SavedWindowGeometry? _dataGridWindowGeometry;
    private SavedGridLayout? _gridLayout;
    private bool _autoAcceptEnabled;
    private int _autoAcceptDelaySeconds;
    private string _autoDeleteOverride;
    private bool _isDataGridPoppedOut;
    private bool _isGroundViewPoppedOut;
    private bool _isRadarViewPoppedOut;
    private Dictionary<string, SavedRadarSettings> _radarSettings;
    private bool _isDelayedGroupCollapsed;
    private string? _lastScenarioFolder;
    private List<MacroDefinition> _macros;
    private List<FavoriteCommand> _favoriteCommands;
    private List<RecentScenario> _recentScenarios;

    public UserPreferences()
    {
        var saved = Load();
        _commandScheme = saved.Scheme ?? CommandScheme.Default();
        _serverUrl = saved.ServerUrl;
        _vatsimCid = saved.VatsimCid;
        _userInitials = saved.UserInitials;
        _artccId = saved.ArtccId;
        _isAdminMode = saved.IsAdmin;
        _adminPassword = saved.AdminPassword;
        _mainWindowGeometry = saved.MainWindowGeometry;
        _settingsWindowGeometry = saved.SettingsWindowGeometry;
        _terminalWindowGeometry = saved.TerminalWindowGeometry;
        _groundViewWindowGeometry = saved.GroundViewWindowGeometry;
        _radarViewWindowGeometry = saved.RadarViewWindowGeometry;
        _dataGridWindowGeometry = saved.DataGridWindowGeometry;
        _gridLayout = saved.GridLayout;
        _autoAcceptEnabled = saved.AutoAcceptEnabled;
        _autoAcceptDelaySeconds = saved.AutoAcceptDelaySeconds;
        _autoDeleteOverride = saved.AutoDeleteOverride;
        _isDataGridPoppedOut = saved.IsDataGridPoppedOut;
        _isGroundViewPoppedOut = saved.IsGroundViewPoppedOut;
        _isRadarViewPoppedOut = saved.IsRadarViewPoppedOut;
        _radarSettings = saved.RadarSettings;
        _isDelayedGroupCollapsed = saved.IsDelayedGroupCollapsed;
        _lastScenarioFolder = saved.LastScenarioFolder;
        _macros = saved.Macros;
        _favoriteCommands = saved.FavoriteCommands;
        _recentScenarios = saved.RecentScenarios;
    }

    public CommandScheme CommandScheme => _commandScheme;
    public string ServerUrl => _serverUrl;
    public string VatsimCid => _vatsimCid;
    public string UserInitials => _userInitials;
    public string ArtccId => _artccId;
    public bool IsAdminMode => _isAdminMode;
    public string AdminPassword => _adminPassword;
    public SavedWindowGeometry? MainWindowGeometry => _mainWindowGeometry;
    public SavedWindowGeometry? SettingsWindowGeometry => _settingsWindowGeometry;
    public SavedWindowGeometry? TerminalWindowGeometry => _terminalWindowGeometry;
    public SavedWindowGeometry? GroundViewWindowGeometry => _groundViewWindowGeometry;
    public SavedWindowGeometry? RadarViewWindowGeometry => _radarViewWindowGeometry;
    public SavedWindowGeometry? DataGridWindowGeometry => _dataGridWindowGeometry;
    public SavedGridLayout? GridLayout => _gridLayout;
    public bool AutoAcceptEnabled => _autoAcceptEnabled;
    public int AutoAcceptDelaySeconds => _autoAcceptDelaySeconds;
    public string AutoDeleteOverride => _autoDeleteOverride;
    public bool IsDataGridPoppedOut => _isDataGridPoppedOut;
    public bool IsGroundViewPoppedOut => _isGroundViewPoppedOut;
    public bool IsRadarViewPoppedOut => _isRadarViewPoppedOut;
    public bool IsDelayedGroupCollapsed => _isDelayedGroupCollapsed;
    public string? LastScenarioFolder => _lastScenarioFolder;
    public IReadOnlyList<MacroDefinition> Macros => _macros;
    public IReadOnlyList<FavoriteCommand> FavoriteCommands => _favoriteCommands;
    public IReadOnlyList<RecentScenario> RecentScenarios => _recentScenarios;

    public void SetServerUrl(string url)
    {
        _serverUrl = url.Trim();
        Save();
    }

    public void SetAutoAcceptSettings(bool enabled, int delaySeconds)
    {
        _autoAcceptEnabled = enabled;
        _autoAcceptDelaySeconds = Math.Clamp(delaySeconds, 0, 60);
        Save();
    }

    public void SetAutoDeleteOverride(string value)
    {
        _autoDeleteOverride = value;
        Save();
    }

    public void SetCommandScheme(CommandScheme scheme)
    {
        _commandScheme = scheme;
        Save();
    }

    public void SetVatsimCid(string cid)
    {
        _vatsimCid = cid.Trim();
        Save();
    }

    public void SetUserInitials(string initials)
    {
        _userInitials = initials.ToUpperInvariant();
        Save();
    }

    public void SetArtccId(string artccId)
    {
        _artccId = artccId.ToUpperInvariant().Trim();
        Save();
    }

    public void SetAdminSettings(bool isAdmin, string password)
    {
        _isAdminMode = isAdmin;
        _adminPassword = password;
        Save();
    }

    public void SetWindowGeometry(string windowName, SavedWindowGeometry geometry)
    {
        switch (windowName)
        {
            case "Main":
                _mainWindowGeometry = geometry;
                break;
            case "Settings":
                _settingsWindowGeometry = geometry;
                break;
            case "Terminal":
                _terminalWindowGeometry = geometry;
                break;
            case "GroundView":
                _groundViewWindowGeometry = geometry;
                break;
            case "RadarView":
                _radarViewWindowGeometry = geometry;
                break;
            case "DataGrid":
                _dataGridWindowGeometry = geometry;
                break;
        }
        Save();
    }

    public void SetPoppedOut(string tabName, bool poppedOut)
    {
        switch (tabName)
        {
            case "DataGrid":
                _isDataGridPoppedOut = poppedOut;
                break;
            case "GroundView":
                _isGroundViewPoppedOut = poppedOut;
                break;
            case "RadarView":
                _isRadarViewPoppedOut = poppedOut;
                break;
        }
        Save();
    }

    public void SetGridLayout(SavedGridLayout layout)
    {
        _gridLayout = layout;
        Save();
    }

    public void SetDelayedGroupCollapsed(bool collapsed)
    {
        _isDelayedGroupCollapsed = collapsed;
        Save();
    }

    public void SetLastScenarioFolder(string? folder)
    {
        _lastScenarioFolder = folder;
        Save();
    }

    public void SetMacros(List<MacroDefinition> macros)
    {
        _macros = macros;
        Save();
    }

    public void SetFavoriteCommands(List<FavoriteCommand> favorites)
    {
        _favoriteCommands = favorites;
        Save();
    }

    public void AddRecentScenario(string filePath, string name)
    {
        _recentScenarios.RemoveAll(r => r.FilePath == filePath);
        _recentScenarios.Insert(0, new RecentScenario { FilePath = filePath, Name = name });
        if (_recentScenarios.Count > 10)
        {
            _recentScenarios.RemoveRange(10, _recentScenarios.Count - 10);
        }
        Save();
    }

    public void ResetGridLayout()
    {
        _gridLayout = null;
        _isDelayedGroupCollapsed = false;
        Save();
    }

    public SavedRadarSettings? GetRadarSettings(string scenarioId)
    {
        _radarSettings.TryGetValue(scenarioId, out var settings);
        return settings;
    }

    public void SetRadarSettings(string scenarioId, SavedRadarSettings settings)
    {
        _radarSettings[scenarioId] = settings;
        Save();
    }

    private static LoadedPrefs Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new LoadedPrefs();
        }

        string json;
        try
        {
            json = File.ReadAllText(ConfigPath);
        }
        catch (IOException ex)
        {
            Log.LogWarning(ex, "Could not read preferences from {Path}", ConfigPath);
            return new LoadedPrefs();
        }

        // Fast path: full deserialization
        try
        {
            var saved = JsonSerializer.Deserialize<SavedPrefs>(json, JsonOptions);
            if (saved is not null)
            {
                return BuildLoadedPrefs(saved);
            }
        }
        catch (JsonException)
        {
            // Fall through to field-by-field recovery
        }

        // Slow path: recover individual fields so one bad field doesn't wipe everything
        Log.LogWarning("Full preferences deserialization failed — recovering individual fields from {Path}", ConfigPath);
        BackupFile();
        return RecoverFields(json);
    }

    private static LoadedPrefs BuildLoadedPrefs(SavedPrefs saved)
    {
        var scheme = saved.CommandScheme is not null ? FromSaved(saved.CommandScheme) : null;

        return new LoadedPrefs
        {
            Scheme = scheme,
            ServerUrl = saved.ServerUrl ?? "http://localhost:5000",
            VatsimCid = saved.VatsimCid ?? "",
            UserInitials = saved.UserInitials ?? "",
            ArtccId = saved.ArtccId ?? "",
            IsAdmin = saved.IsAdminMode,
            AdminPassword = saved.AdminPassword ?? "",
            MainWindowGeometry = saved.MainWindowGeometry,
            SettingsWindowGeometry = saved.SettingsWindowGeometry,
            TerminalWindowGeometry = saved.TerminalWindowGeometry,
            GroundViewWindowGeometry = saved.GroundViewWindowGeometry,
            RadarViewWindowGeometry = saved.RadarViewWindowGeometry,
            DataGridWindowGeometry = saved.DataGridWindowGeometry,
            GridLayout = saved.GridLayout,
            AutoAcceptEnabled = saved.AutoAcceptEnabled,
            AutoAcceptDelaySeconds = saved.AutoAcceptDelaySeconds,
            AutoDeleteOverride = saved.AutoDeleteOverride ?? "",
            IsDataGridPoppedOut = saved.IsDataGridPoppedOut,
            IsGroundViewPoppedOut = saved.IsGroundViewPoppedOut,
            IsRadarViewPoppedOut = saved.IsRadarViewPoppedOut,
            RadarSettings = saved.RadarSettings ?? [],
            IsDelayedGroupCollapsed = saved.IsDelayedGroupCollapsed,
            LastScenarioFolder = saved.LastScenarioFolder,
            Macros = saved.Macros?.Select(m => new MacroDefinition { Name = m.Name, Expansion = m.Expansion }).ToList() ?? [],
            FavoriteCommands = saved.FavoriteCommands ?? [],
            RecentScenarios = saved.RecentScenarios ?? [],
        };
    }

    private static LoadedPrefs RecoverFields(string json)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json)?.AsObject();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Preferences JSON is completely unparseable");
            return new LoadedPrefs();
        }

        if (obj is null)
        {
            return new LoadedPrefs();
        }

        var savedScheme = GetFieldOr<SavedCommandScheme?>(obj, "commandScheme", null);
        var macros = GetFieldOr<List<SavedMacro>?>(obj, "macros", null);

        return new LoadedPrefs
        {
            Scheme = savedScheme is not null ? FromSaved(savedScheme) : null,
            ServerUrl = GetFieldOr(obj, "serverUrl", "http://localhost:5000"),
            VatsimCid = GetFieldOr(obj, "vatsimCid", ""),
            UserInitials = GetFieldOr(obj, "userInitials", ""),
            ArtccId = GetFieldOr(obj, "artccId", ""),
            IsAdmin = GetFieldOr(obj, "isAdminMode", false),
            AdminPassword = GetFieldOr(obj, "adminPassword", ""),
            MainWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "mainWindowGeometry", null),
            SettingsWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "settingsWindowGeometry", null),
            TerminalWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "terminalWindowGeometry", null),
            GroundViewWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "groundViewWindowGeometry", null),
            RadarViewWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "radarViewWindowGeometry", null),
            DataGridWindowGeometry = GetFieldOr<SavedWindowGeometry?>(obj, "dataGridWindowGeometry", null),
            GridLayout = GetFieldOr<SavedGridLayout?>(obj, "gridLayout", null),
            AutoAcceptEnabled = GetFieldOr(obj, "autoAcceptEnabled", true),
            AutoAcceptDelaySeconds = GetFieldOr(obj, "autoAcceptDelaySeconds", 5),
            AutoDeleteOverride = GetFieldOr(obj, "autoDeleteOverride", ""),
            IsDataGridPoppedOut = GetFieldOr(obj, "isDataGridPoppedOut", false),
            IsGroundViewPoppedOut = GetFieldOr(obj, "isGroundViewPoppedOut", false),
            IsRadarViewPoppedOut = GetFieldOr(obj, "isRadarViewPoppedOut", false),
            RadarSettings = GetFieldOr<Dictionary<string, SavedRadarSettings>>(obj, "radarSettings", []),
            IsDelayedGroupCollapsed = GetFieldOr(obj, "isDelayedGroupCollapsed", false),
            LastScenarioFolder = GetFieldOr<string?>(obj, "lastScenarioFolder", null),
            Macros = macros?.Select(m => new MacroDefinition { Name = m.Name, Expansion = m.Expansion }).ToList() ?? [],
            FavoriteCommands = GetFieldOr<List<FavoriteCommand>>(obj, "favoriteCommands", []),
            RecentScenarios = GetFieldOr<List<RecentScenario>>(obj, "recentScenarios", []),
        };
    }

    private static T GetFieldOr<T>(JsonObject obj, string name, T fallback)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return fallback;
        }

        try
        {
            return node.Deserialize<T>(JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            Log.LogWarning("Skipped unreadable preference field '{Field}'", name);
            return fallback;
        }
    }

    private static void BackupFile()
    {
        try
        {
            File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
        }
        catch (IOException ex)
        {
            Log.LogWarning(ex, "Could not back up preferences file");
        }
    }

    private sealed class LoadedPrefs
    {
        public CommandScheme? Scheme { get; init; }
        public string ServerUrl { get; init; } = "http://localhost:5000";
        public string VatsimCid { get; init; } = "";
        public string UserInitials { get; init; } = "";
        public string ArtccId { get; init; } = "";
        public bool IsAdmin { get; init; }
        public string AdminPassword { get; init; } = "";
        public SavedWindowGeometry? MainWindowGeometry { get; init; }
        public SavedWindowGeometry? SettingsWindowGeometry { get; init; }
        public SavedWindowGeometry? TerminalWindowGeometry { get; init; }
        public SavedWindowGeometry? GroundViewWindowGeometry { get; init; }
        public SavedWindowGeometry? RadarViewWindowGeometry { get; init; }
        public SavedWindowGeometry? DataGridWindowGeometry { get; init; }
        public SavedGridLayout? GridLayout { get; init; }
        public bool AutoAcceptEnabled { get; init; } = true;
        public int AutoAcceptDelaySeconds { get; init; } = 5;
        public string AutoDeleteOverride { get; init; } = "";
        public bool IsDataGridPoppedOut { get; init; }
        public bool IsGroundViewPoppedOut { get; init; }
        public bool IsRadarViewPoppedOut { get; init; }
        public Dictionary<string, SavedRadarSettings> RadarSettings { get; init; } = [];
        public bool IsDelayedGroupCollapsed { get; init; }
        public string? LastScenarioFolder { get; init; }
        public List<MacroDefinition> Macros { get; init; } = [];
        public List<FavoriteCommand> FavoriteCommands { get; init; } = [];
        public List<RecentScenario> RecentScenarios { get; init; } = [];
    }

    private void Save()
    {
        Directory.CreateDirectory(ConfigDir);

        var saved = new SavedPrefs
        {
            CommandScheme = ToSaved(_commandScheme),
            ServerUrl = _serverUrl,
            VatsimCid = _vatsimCid,
            UserInitials = _userInitials,
            ArtccId = _artccId,
            IsAdminMode = _isAdminMode,
            AdminPassword = _adminPassword,
            MainWindowGeometry = _mainWindowGeometry,
            SettingsWindowGeometry = _settingsWindowGeometry,
            TerminalWindowGeometry = _terminalWindowGeometry,
            GroundViewWindowGeometry = _groundViewWindowGeometry,
            RadarViewWindowGeometry = _radarViewWindowGeometry,
            DataGridWindowGeometry = _dataGridWindowGeometry,
            GridLayout = _gridLayout,
            AutoAcceptEnabled = _autoAcceptEnabled,
            AutoAcceptDelaySeconds = _autoAcceptDelaySeconds,
            AutoDeleteOverride = _autoDeleteOverride,
            IsDataGridPoppedOut = _isDataGridPoppedOut,
            IsGroundViewPoppedOut = _isGroundViewPoppedOut,
            IsRadarViewPoppedOut = _isRadarViewPoppedOut,
            RadarSettings = _radarSettings,
            IsDelayedGroupCollapsed = _isDelayedGroupCollapsed,
            LastScenarioFolder = _lastScenarioFolder,
            Macros = _macros.Select(m => new SavedMacro { Name = m.Name, Expansion = m.Expansion }).ToList(),
            FavoriteCommands = [.. _favoriteCommands],
            RecentScenarios = [.. _recentScenarios],
        };

        var json = JsonSerializer.Serialize(saved, JsonOptions);

        // Atomic write: write to .tmp then move, so a crash mid-write can't corrupt the real file
        var tmpPath = ConfigPath + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, ConfigPath, overwrite: true);
    }

    private static CommandScheme? FromSaved(SavedCommandScheme s)
    {
        var patterns = new Dictionary<CanonicalCommandType, CommandPattern>();

        var defaults = CommandScheme.Default();
        foreach (var (type, pattern) in defaults.Patterns)
        {
            patterns[type] = new CommandPattern { Aliases = [.. pattern.Aliases], Format = pattern.Format };
        }

        foreach (var (key, sp) in s.Patterns)
        {
            if (!Enum.TryParse<CanonicalCommandType>(key, out var type))
            {
                continue;
            }

            // Read aliases: prefer Aliases list, fall back to legacy Verb field
            var aliases = sp.Aliases is { Count: > 0 } ? sp.Aliases : (!string.IsNullOrWhiteSpace(sp.Verb) ? [sp.Verb] : null);

            if (aliases is null)
            {
                continue;
            }

            if (patterns.TryGetValue(type, out var existing))
            {
                existing.Aliases = aliases;
            }
        }

        return new CommandScheme { Patterns = patterns };
    }

    private static SavedCommandScheme ToSaved(CommandScheme scheme)
    {
        var defaults = CommandScheme.Default();
        var patterns = new Dictionary<string, SavedPattern>();
        foreach (var (type, pattern) in scheme.Patterns)
        {
            // Only persist aliases that differ from defaults — unmodified commands
            // always pick up the latest default aliases on next load.
            if (defaults.Patterns.TryGetValue(type, out var defaultPattern) && pattern.Aliases.SequenceEqual(defaultPattern.Aliases))
            {
                continue;
            }

            patterns[type.ToString()] = new SavedPattern { Aliases = pattern.Aliases, Format = pattern.Format };
        }

        return new SavedCommandScheme { Patterns = patterns };
    }

    private sealed class SavedPrefs
    {
        public SavedCommandScheme? CommandScheme { get; set; }
        public string ServerUrl { get; set; } = "http://localhost:5000";
        public string VatsimCid { get; set; } = "";
        public string UserInitials { get; set; } = "";
        public string ArtccId { get; set; } = "";
        public bool IsAdminMode { get; set; }
        public string AdminPassword { get; set; } = "";
        public SavedWindowGeometry? MainWindowGeometry { get; set; }
        public SavedWindowGeometry? SettingsWindowGeometry { get; set; }
        public SavedWindowGeometry? TerminalWindowGeometry { get; set; }
        public SavedWindowGeometry? GroundViewWindowGeometry { get; set; }
        public SavedWindowGeometry? RadarViewWindowGeometry { get; set; }
        public SavedWindowGeometry? DataGridWindowGeometry { get; set; }
        public SavedGridLayout? GridLayout { get; set; }
        public bool AutoAcceptEnabled { get; set; } = true;
        public int AutoAcceptDelaySeconds { get; set; } = 5;
        public string AutoDeleteOverride { get; set; } = "";
        public bool IsDataGridPoppedOut { get; set; }
        public bool IsGroundViewPoppedOut { get; set; }
        public bool IsRadarViewPoppedOut { get; set; }
        public Dictionary<string, SavedRadarSettings> RadarSettings { get; set; } = [];
        public bool IsDelayedGroupCollapsed { get; set; }
        public string? LastScenarioFolder { get; set; }
        public List<SavedMacro> Macros { get; set; } = [];
        public List<FavoriteCommand> FavoriteCommands { get; set; } = [];
        public List<RecentScenario> RecentScenarios { get; set; } = [];
    }

    private sealed class SavedCommandScheme
    {
        public Dictionary<string, SavedPattern> Patterns { get; set; } = [];
    }

    private sealed class SavedPattern
    {
        public List<string>? Aliases { get; set; }
        public string? Verb { get; set; }
        public string Format { get; set; } = "";
    }
}

public sealed class SavedWindowGeometry
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

public sealed class SavedGridLayout
{
    public List<string>? ColumnOrder { get; set; }
    public string? SortColumn { get; set; }
    public ListSortDirection? SortDirection { get; set; }
    public Dictionary<string, double>? ColumnWidths { get; set; }
}

public sealed class RecentScenario
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class SavedMacro
{
    public string Name { get; set; } = "";
    public string Expansion { get; set; } = "";
}

public sealed class FavoriteCommand
{
    public string Label { get; set; } = "";
    public string CommandText { get; set; } = "";
    public string? ScenarioId { get; set; }
}

public sealed class SavedRadarSettings
{
    public List<int> EnabledStarsIds { get; set; } = [];
    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
    public double RangeNm { get; set; } = 40;
    public double RangeRingCenterLat { get; set; }
    public double RangeRingCenterLon { get; set; }
    public double RangeRingSizeNm { get; set; } = 5;
    public bool ShowRangeRings { get; set; } = true;
    public bool ShowFixes { get; set; }
    public bool IsPanZoomLocked { get; set; } = true;
    public bool ShowTopDown { get; set; }
    public double PtlLengthMinutes { get; set; }
    public bool PtlOwn { get; set; }
    public bool PtlAll { get; set; }
    public Dictionary<string, int>? BrightnessValues { get; set; }
}
