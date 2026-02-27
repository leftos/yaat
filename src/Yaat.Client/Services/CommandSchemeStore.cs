using System.Text.Json;

namespace Yaat.Client.Services;

public sealed class CommandSchemeStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData),
        "Yaat");

    private static readonly string ConfigPath =
        Path.Combine(ConfigDir, "commandScheme.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private CommandScheme _active;

    public CommandSchemeStore()
    {
        _active = Load() ?? CommandScheme.AtcTrainer();
    }

    public CommandScheme Active => _active;

    public void SetScheme(CommandScheme scheme)
    {
        _active = scheme;
        Save(scheme);
    }

    public void Reset()
    {
        _active = CommandScheme.AtcTrainer();
        if (File.Exists(ConfigPath))
            File.Delete(ConfigPath);
    }

    private static CommandScheme? Load()
    {
        if (!File.Exists(ConfigPath))
            return null;

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var saved = JsonSerializer.Deserialize<SavedScheme>(
                json, JsonOptions);

            return saved?.Name switch
            {
                "ATCTrainer" => CommandScheme.AtcTrainer(),
                "VICE" => CommandScheme.Vice(),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static void Save(CommandScheme scheme)
    {
        Directory.CreateDirectory(ConfigDir);

        var saved = new SavedScheme { Name = scheme.Name };
        var json = JsonSerializer.Serialize(saved, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    private class SavedScheme
    {
        public string Name { get; set; } = "ATCTrainer";
    }
}
