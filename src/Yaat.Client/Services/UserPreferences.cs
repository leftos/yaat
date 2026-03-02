using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public sealed class UserPreferences
{
    private static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "yaat");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "preferences.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
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

    public UserPreferences()
    {
        var saved = Load();
        _commandScheme = saved.Scheme ?? CommandScheme.AtcTrainer();
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

    public SavedRadarSettings? GetRadarSettings(string scenarioId)
    {
        _radarSettings.TryGetValue(scenarioId, out var settings);
        return settings;
    }

    public void SetRadarSettings(
        string scenarioId, SavedRadarSettings settings)
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

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var saved = JsonSerializer.Deserialize<SavedPrefs>(json, JsonOptions);

            var scheme = saved?.CommandScheme is not null ? FromSaved(saved.CommandScheme) : null;

            return new LoadedPrefs
            {
                Scheme = scheme,
                ServerUrl = saved?.ServerUrl ?? "http://localhost:5000",
                VatsimCid = saved?.VatsimCid ?? "",
                UserInitials = saved?.UserInitials ?? "",
                ArtccId = saved?.ArtccId ?? "",
                IsAdmin = saved?.IsAdminMode ?? false,
                AdminPassword = saved?.AdminPassword ?? "",
                MainWindowGeometry = saved?.MainWindowGeometry,
                SettingsWindowGeometry = saved?.SettingsWindowGeometry,
                TerminalWindowGeometry = saved?.TerminalWindowGeometry,
                GroundViewWindowGeometry = saved?.GroundViewWindowGeometry,
                RadarViewWindowGeometry = saved?.RadarViewWindowGeometry,
                DataGridWindowGeometry = saved?.DataGridWindowGeometry,
                GridLayout = saved?.GridLayout,
                AutoAcceptEnabled = saved?.AutoAcceptEnabled ?? true,
                AutoAcceptDelaySeconds = saved?.AutoAcceptDelaySeconds ?? 5,
                AutoDeleteOverride = saved?.AutoDeleteOverride ?? "",
                IsDataGridPoppedOut = saved?.IsDataGridPoppedOut ?? false,
                IsGroundViewPoppedOut = saved?.IsGroundViewPoppedOut ?? false,
                IsRadarViewPoppedOut = saved?.IsRadarViewPoppedOut ?? false,
                RadarSettings = saved?.RadarSettings ?? [],
            };
        }
        catch (JsonException)
        {
            return new LoadedPrefs();
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
        };

        var json = JsonSerializer.Serialize(saved, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    private static CommandScheme? FromSaved(SavedCommandScheme s)
    {
        var patterns = new Dictionary<CanonicalCommandType, CommandPattern>();

        // Start from ATCTrainer defaults, overlay saved patterns
        var defaults = CommandScheme.AtcTrainer();
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
            var aliases = sp.Aliases is { Count: > 0 }
                ? sp.Aliases
                : (!string.IsNullOrWhiteSpace(sp.Verb) ? [sp.Verb] : null);

            if (aliases is null)
            {
                continue;
            }

            if (patterns.TryGetValue(type, out var existing))
            {
                existing.Aliases = aliases;
            }
        }

        // Reapply correct formats based on parse mode
        var reference = s.ParseMode == CommandParseMode.Concatenated ? CommandScheme.Vice() : CommandScheme.AtcTrainer();

        var result = new Dictionary<CanonicalCommandType, CommandPattern>();
        foreach (var (type, pattern) in patterns)
        {
            var format = reference.Patterns.TryGetValue(type, out var refPattern) ? refPattern.Format : pattern.Format;

            result[type] = new CommandPattern { Aliases = pattern.Aliases, Format = format };
        }

        return new CommandScheme { ParseMode = s.ParseMode, Patterns = result };
    }

    private static SavedCommandScheme ToSaved(CommandScheme scheme)
    {
        var patterns = new Dictionary<string, SavedPattern>();
        foreach (var (type, pattern) in scheme.Patterns)
        {
            patterns[type.ToString()] = new SavedPattern { Aliases = pattern.Aliases, Format = pattern.Format };
        }

        return new SavedCommandScheme { ParseMode = scheme.ParseMode, Patterns = patterns };
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
    }

    private sealed class SavedCommandScheme
    {
        public CommandParseMode ParseMode { get; set; }

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
}

public sealed class SavedRadarSettings
{
    public List<int> EnabledStarsIds { get; set; } = [];
    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
    public double RangeNm { get; set; } = 60;
    public double RangeRingCenterLat { get; set; }
    public double RangeRingCenterLon { get; set; }
    public double RangeRingSizeNm { get; set; } = 5;
    public bool ShowRangeRings { get; set; } = true;
    public bool ShowFixes { get; set; }
    public bool IsPanZoomLocked { get; set; } = true;
}
