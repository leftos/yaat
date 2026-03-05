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

    private readonly SavedPrefs _data;
    private CommandScheme _commandScheme;
    private List<MacroDefinition> _macros;

    public UserPreferences()
    {
        _data = Load();
        _commandScheme = _data.CommandScheme is not null ? FromSaved(_data.CommandScheme) ?? CommandScheme.Default() : CommandScheme.Default();
        _macros = _data.Macros.Select(m => new MacroDefinition { Name = m.Name, Expansion = m.Expansion }).ToList();
    }

    public CommandScheme CommandScheme => _commandScheme;
    public string ServerUrl => _data.ServerUrl;
    public string VatsimCid => _data.VatsimCid;
    public string UserInitials => _data.UserInitials;
    public string ArtccId => _data.ArtccId;
    public bool IsAdminMode => _data.IsAdminMode;
    public string AdminPassword => _data.AdminPassword;
    public SavedWindowGeometry? MainWindowGeometry => _data.MainWindowGeometry;
    public SavedWindowGeometry? SettingsWindowGeometry => _data.SettingsWindowGeometry;
    public SavedWindowGeometry? TerminalWindowGeometry => _data.TerminalWindowGeometry;
    public SavedWindowGeometry? GroundViewWindowGeometry => _data.GroundViewWindowGeometry;
    public SavedWindowGeometry? RadarViewWindowGeometry => _data.RadarViewWindowGeometry;
    public SavedWindowGeometry? DataGridWindowGeometry => _data.DataGridWindowGeometry;

    public SavedWindowGeometry? GetWindowGeometry(string windowName)
    {
        return windowName switch
        {
            "Main" => _data.MainWindowGeometry,
            "Settings" => _data.SettingsWindowGeometry,
            "Terminal" => _data.TerminalWindowGeometry,
            "GroundView" => _data.GroundViewWindowGeometry,
            "RadarView" => _data.RadarViewWindowGeometry,
            "DataGrid" => _data.DataGridWindowGeometry,
            _ => _data.WindowGeometries.GetValueOrDefault(windowName),
        };
    }

    public SavedGridLayout? GridLayout => _data.GridLayout;
    public bool AutoAcceptEnabled => _data.AutoAcceptEnabled;
    public int AutoAcceptDelaySeconds => _data.AutoAcceptDelaySeconds;
    public string AutoDeleteOverride => _data.AutoDeleteOverride;
    public bool IsDataGridPoppedOut => _data.IsDataGridPoppedOut;
    public bool IsGroundViewPoppedOut => _data.IsGroundViewPoppedOut;
    public bool IsRadarViewPoppedOut => _data.IsRadarViewPoppedOut;
    public bool ShowOnlyActiveAircraft => _data.ShowOnlyActiveAircraft;
    public string? LastScenarioFolder => _data.LastScenarioFolder;
    public string? LastWeatherFolder => _data.LastWeatherFolder;
    public IReadOnlyList<MacroDefinition> Macros => _macros;
    public bool ValidateDctFixes => _data.ValidateDctFixes;
    public bool AutoClearedToLand => _data.AutoClearedToLand;
    public bool AutoCrossRunway => _data.AutoCrossRunway;
    public IReadOnlyList<FavoriteCommand> FavoriteCommands => _data.FavoriteCommands;
    public IReadOnlyList<RecentScenario> RecentScenarios => _data.RecentScenarios;
    public IReadOnlyList<RecentWeather> RecentWeatherFiles => _data.RecentWeatherFiles;
    public string AircraftSelectKey => _data.AircraftSelectKey;
    public string FocusInputKey => _data.FocusInputKey;

    public void SetServerUrl(string url)
    {
        _data.ServerUrl = url.Trim();
        Save();
    }

    public void SetAutoAcceptSettings(bool enabled, int delaySeconds)
    {
        _data.AutoAcceptEnabled = enabled;
        _data.AutoAcceptDelaySeconds = Math.Clamp(delaySeconds, 0, 60);
        Save();
    }

    public void SetAutoDeleteOverride(string value)
    {
        _data.AutoDeleteOverride = value;
        Save();
    }

    public void SetCommandScheme(CommandScheme scheme)
    {
        _commandScheme = scheme;
        _data.CommandScheme = ToSaved(scheme);
        Save();
    }

    public void SetVatsimCid(string cid)
    {
        _data.VatsimCid = cid.Trim();
        Save();
    }

    public void SetUserInitials(string initials)
    {
        _data.UserInitials = initials.ToUpperInvariant();
        Save();
    }

    public void SetArtccId(string artccId)
    {
        _data.ArtccId = artccId.ToUpperInvariant().Trim();
        Save();
    }

    public void SetAdminSettings(bool isAdmin, string password)
    {
        _data.IsAdminMode = isAdmin;
        _data.AdminPassword = password;
        Save();
    }

    public void SetWindowGeometry(string windowName, SavedWindowGeometry geometry)
    {
        switch (windowName)
        {
            case "Main":
                _data.MainWindowGeometry = geometry;
                break;
            case "Settings":
                _data.SettingsWindowGeometry = geometry;
                break;
            case "Terminal":
                _data.TerminalWindowGeometry = geometry;
                break;
            case "GroundView":
                _data.GroundViewWindowGeometry = geometry;
                break;
            case "RadarView":
                _data.RadarViewWindowGeometry = geometry;
                break;
            case "DataGrid":
                _data.DataGridWindowGeometry = geometry;
                break;
            default:
                _data.WindowGeometries[windowName] = geometry;
                break;
        }
        Save();
    }

    public void SetPoppedOut(string tabName, bool poppedOut)
    {
        switch (tabName)
        {
            case "DataGrid":
                _data.IsDataGridPoppedOut = poppedOut;
                break;
            case "GroundView":
                _data.IsGroundViewPoppedOut = poppedOut;
                break;
            case "RadarView":
                _data.IsRadarViewPoppedOut = poppedOut;
                break;
        }
        Save();
    }

    public void SetGridLayout(SavedGridLayout layout)
    {
        _data.GridLayout = layout;
        Save();
    }

    public void SetShowOnlyActiveAircraft(bool value)
    {
        _data.ShowOnlyActiveAircraft = value;
        Save();
    }

    public void SetLastScenarioFolder(string? folder)
    {
        _data.LastScenarioFolder = folder;
        Save();
    }

    public void SetLastWeatherFolder(string? folder)
    {
        _data.LastWeatherFolder = folder;
        Save();
    }

    public void SetValidateDctFixes(bool validate)
    {
        _data.ValidateDctFixes = validate;
        Save();
    }

    public void SetSimulationShortcuts(bool autoClearedToLand, bool autoCrossRunway)
    {
        _data.AutoClearedToLand = autoClearedToLand;
        _data.AutoCrossRunway = autoCrossRunway;
        Save();
    }

    public void SetMacros(List<MacroDefinition> macros)
    {
        _macros = macros;
        _data.Macros = macros.Select(m => new SavedMacro { Name = m.Name, Expansion = m.Expansion }).ToList();
        Save();
    }

    public void SetAircraftSelectKey(string key)
    {
        _data.AircraftSelectKey = key;
        Save();
    }

    public void SetFocusInputKey(string key)
    {
        _data.FocusInputKey = key;
        Save();
    }

    public void SetFavoriteCommands(List<FavoriteCommand> favorites)
    {
        _data.FavoriteCommands = favorites;
        Save();
    }

    public void AddRecentScenario(string filePath, string name)
    {
        _data.RecentScenarios.RemoveAll(r => r.FilePath == filePath);
        _data.RecentScenarios.Insert(0, new RecentScenario { FilePath = filePath, Name = name });
        if (_data.RecentScenarios.Count > 10)
        {
            _data.RecentScenarios.RemoveRange(10, _data.RecentScenarios.Count - 10);
        }
        Save();
    }

    public void AddRecentWeather(string filePath, string name)
    {
        _data.RecentWeatherFiles.RemoveAll(r => r.FilePath == filePath);
        _data.RecentWeatherFiles.Insert(0, new RecentWeather { FilePath = filePath, Name = name });
        if (_data.RecentWeatherFiles.Count > 10)
        {
            _data.RecentWeatherFiles.RemoveRange(10, _data.RecentWeatherFiles.Count - 10);
        }
        Save();
    }

    public void ResetGridLayout()
    {
        _data.GridLayout = null;
        Save();
    }

    public SavedRadarSettings? GetRadarSettings(string scenarioId)
    {
        _data.RadarSettings.TryGetValue(scenarioId, out var settings);
        return settings;
    }

    public void SetRadarSettings(string scenarioId, SavedRadarSettings settings)
    {
        _data.RadarSettings[scenarioId] = settings;
        Save();
    }

    private static SavedPrefs Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new SavedPrefs();
        }

        string json;
        try
        {
            json = File.ReadAllText(ConfigPath);
        }
        catch (IOException ex)
        {
            Log.LogWarning(ex, "Could not read preferences from {Path}", ConfigPath);
            return new SavedPrefs();
        }

        // Fast path: full deserialization
        try
        {
            var saved = JsonSerializer.Deserialize<SavedPrefs>(json, JsonOptions);
            if (saved is not null)
            {
                return saved;
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

    private static SavedPrefs RecoverFields(string json)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json)?.AsObject();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Preferences JSON is completely unparseable");
            return new SavedPrefs();
        }

        if (obj is null)
        {
            return new SavedPrefs();
        }

        return new SavedPrefs
        {
            CommandScheme = GetFieldOr<SavedCommandScheme?>(obj, "commandScheme", null),
            ServerUrl = GetFieldOr(obj, "serverUrl", "http://localhost:5000"),
            VatsimCid = GetFieldOr(obj, "vatsimCid", ""),
            UserInitials = GetFieldOr(obj, "userInitials", ""),
            ArtccId = GetFieldOr(obj, "artccId", ""),
            IsAdminMode = GetFieldOr(obj, "isAdminMode", false),
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
            WindowGeometries = GetFieldOr<Dictionary<string, SavedWindowGeometry>>(obj, "windowGeometries", []),
            ShowOnlyActiveAircraft = GetFieldOr(obj, "showOnlyActiveAircraft", false),
            LastScenarioFolder = GetFieldOr<string?>(obj, "lastScenarioFolder", null),
            LastWeatherFolder = GetFieldOr<string?>(obj, "lastWeatherFolder", null),
            Macros = GetFieldOr<List<SavedMacro>>(obj, "macros", []),
            ValidateDctFixes = GetFieldOr(obj, "validateDctFixes", false),
            AutoClearedToLand = GetFieldOr(obj, "autoClearedToLand", false),
            AutoCrossRunway = GetFieldOr(obj, "autoCrossRunway", false),
            FavoriteCommands = GetFieldOr<List<FavoriteCommand>>(obj, "favoriteCommands", []),
            RecentScenarios = GetFieldOr<List<RecentScenario>>(obj, "recentScenarios", []),
            RecentWeatherFiles = GetFieldOr<List<RecentWeather>>(obj, "recentWeatherFiles", []),
            AircraftSelectKey = GetFieldOr(obj, "aircraftSelectKey", "Add"),
            FocusInputKey = GetFieldOr(obj, "focusInputKey", "OemTilde"),
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

    private void Save()
    {
        Directory.CreateDirectory(ConfigDir);

        // Sync cached conversions back to _data before serializing
        _data.CommandScheme = ToSaved(_commandScheme);
        _data.Macros = _macros.Select(m => new SavedMacro { Name = m.Name, Expansion = m.Expansion }).ToList();

        var json = JsonSerializer.Serialize(_data, JsonOptions);

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
        public Dictionary<string, SavedWindowGeometry> WindowGeometries { get; set; } = [];
        public bool ShowOnlyActiveAircraft { get; set; }
        public string? LastScenarioFolder { get; set; }
        public string? LastWeatherFolder { get; set; }
        public List<SavedMacro> Macros { get; set; } = [];
        public bool ValidateDctFixes { get; set; }
        public bool AutoClearedToLand { get; set; }
        public bool AutoCrossRunway { get; set; }
        public List<FavoriteCommand> FavoriteCommands { get; set; } = [];
        public List<RecentScenario> RecentScenarios { get; set; } = [];
        public List<RecentWeather> RecentWeatherFiles { get; set; } = [];
        public string AircraftSelectKey { get; set; } = "Add";
        public string FocusInputKey { get; set; } = "OemTilde";
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
    public List<string>? HiddenColumns { get; set; }
}

public sealed class RecentScenario
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class RecentWeather
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
