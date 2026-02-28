using System.Text.Json;
using System.Text.Json.Serialization;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public sealed class UserPreferences
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "yaat");

    private static readonly string ConfigPath =
        Path.Combine(ConfigDir, "preferences.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private CommandScheme _commandScheme;
    private bool _isAdminMode;
    private string _adminPassword = "";

    public UserPreferences()
    {
        var (scheme, isAdmin, adminPassword) = Load();
        _commandScheme = scheme
            ?? CommandScheme.AtcTrainer();
        _isAdminMode = isAdmin;
        _adminPassword = adminPassword;
    }

    public CommandScheme CommandScheme => _commandScheme;
    public bool IsAdminMode => _isAdminMode;
    public string AdminPassword => _adminPassword;

    public void SetCommandScheme(CommandScheme scheme)
    {
        _commandScheme = scheme;
        Save();
    }

    public void SetAdminSettings(
        bool isAdmin, string password)
    {
        _isAdminMode = isAdmin;
        _adminPassword = password;
        Save();
    }

    private static (CommandScheme? scheme,
        bool isAdmin, string adminPassword) Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return (null, false, "");
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var saved = JsonSerializer
                .Deserialize<SavedPrefs>(
                    json, JsonOptions);

            var scheme = saved?.CommandScheme is not null
                ? FromSaved(saved.CommandScheme)
                : null;

            return (scheme,
                saved?.IsAdminMode ?? false,
                saved?.AdminPassword ?? "");
        }
        catch (JsonException)
        {
            return (null, false, "");
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(ConfigDir);

        var saved = new SavedPrefs
        {
            CommandScheme = ToSaved(_commandScheme),
            IsAdminMode = _isAdminMode,
            AdminPassword = _adminPassword
        };

        var json = JsonSerializer.Serialize(
            saved, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    private static CommandScheme? FromSaved(SavedCommandScheme s)
    {
        var patterns =
            new Dictionary<CanonicalCommandType, CommandPattern>();

        // Start from ATCTrainer defaults, overlay saved patterns
        var defaults = CommandScheme.AtcTrainer();
        foreach (var (type, pattern) in defaults.Patterns)
        {
            patterns[type] = new CommandPattern
            {
                Verb = pattern.Verb,
                Format = pattern.Format
            };
        }

        foreach (var (key, sp) in s.Patterns)
        {
            if (!Enum.TryParse<CanonicalCommandType>(
                key, out var type))
            {
                continue;
            }

            // Only override verb; format comes from parse mode
            if (patterns.TryGetValue(type, out var existing))
            {
                existing.Verb = sp.Verb;
            }
        }

        // Reapply correct formats based on parse mode
        var reference = s.ParseMode == CommandParseMode.Concatenated
            ? CommandScheme.Vice()
            : CommandScheme.AtcTrainer();

        var result =
            new Dictionary<CanonicalCommandType, CommandPattern>();
        foreach (var (type, pattern) in patterns)
        {
            var format = reference.Patterns.TryGetValue(
                type, out var refPattern)
                ? refPattern.Format
                : pattern.Format;

            result[type] = new CommandPattern
            {
                Verb = pattern.Verb,
                Format = format
            };
        }

        return new CommandScheme
        {
            ParseMode = s.ParseMode,
            Patterns = result
        };
    }

    private static SavedCommandScheme ToSaved(CommandScheme scheme)
    {
        var patterns = new Dictionary<string, SavedPattern>();
        foreach (var (type, pattern) in scheme.Patterns)
        {
            patterns[type.ToString()] = new SavedPattern
            {
                Verb = pattern.Verb,
                Format = pattern.Format
            };
        }

        return new SavedCommandScheme
        {
            ParseMode = scheme.ParseMode,
            Patterns = patterns
        };
    }

    private sealed class SavedPrefs
    {
        public SavedCommandScheme? CommandScheme { get; set; }
        public bool IsAdminMode { get; set; }
        public string AdminPassword { get; set; } = "";
    }

    private sealed class SavedCommandScheme
    {
        public CommandParseMode ParseMode { get; set; }

        public Dictionary<string, SavedPattern> Patterns { get; set; }
            = [];
    }

    private sealed class SavedPattern
    {
        public string Verb { get; set; } = "";
        public string Format { get; set; } = "";
    }
}
