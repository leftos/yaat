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
    private bool _isAdminMode;
    private string _adminPassword = "";
    private SavedWindowGeometry? _mainWindowGeometry;
    private SavedWindowGeometry? _settingsWindowGeometry;

    public UserPreferences()
    {
        var saved = Load();
        _commandScheme = saved.Scheme ?? CommandScheme.AtcTrainer();
        _isAdminMode = saved.IsAdmin;
        _adminPassword = saved.AdminPassword;
        _mainWindowGeometry = saved.MainWindowGeometry;
        _settingsWindowGeometry = saved.SettingsWindowGeometry;
    }

    public CommandScheme CommandScheme => _commandScheme;
    public bool IsAdminMode => _isAdminMode;
    public string AdminPassword => _adminPassword;
    public SavedWindowGeometry? MainWindowGeometry => _mainWindowGeometry;
    public SavedWindowGeometry? SettingsWindowGeometry => _settingsWindowGeometry;

    public void SetCommandScheme(CommandScheme scheme)
    {
        _commandScheme = scheme;
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
        }
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
                IsAdmin = saved?.IsAdminMode ?? false,
                AdminPassword = saved?.AdminPassword ?? "",
                MainWindowGeometry = saved?.MainWindowGeometry,
                SettingsWindowGeometry = saved?.SettingsWindowGeometry,
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
        public bool IsAdmin { get; init; }
        public string AdminPassword { get; init; } = "";
        public SavedWindowGeometry? MainWindowGeometry { get; init; }
        public SavedWindowGeometry? SettingsWindowGeometry { get; init; }
    }

    private void Save()
    {
        Directory.CreateDirectory(ConfigDir);

        var saved = new SavedPrefs
        {
            CommandScheme = ToSaved(_commandScheme),
            IsAdminMode = _isAdminMode,
            AdminPassword = _adminPassword,
            MainWindowGeometry = _mainWindowGeometry,
            SettingsWindowGeometry = _settingsWindowGeometry,
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
        public bool IsAdminMode { get; set; }
        public string AdminPassword { get; set; } = "";
        public SavedWindowGeometry? MainWindowGeometry { get; set; }
        public SavedWindowGeometry? SettingsWindowGeometry { get; set; }
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
