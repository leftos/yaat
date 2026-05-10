using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Manages CRC DevEnvironments.json configuration — adds/updates YAAT server entries.
/// Detects CRC's per-user config directory (where its JSON files live) by probing
/// platform-specific candidate paths for a marker file.
/// The list of YAAT entries is loaded from the embedded `crc-environments.json` resource
/// (sourced from `docs/crc-environments.json` — the same file the standalone
/// `yaat-crc-config` Rust tool and `Setup-CrcEnvironment.ps1` consume).
/// </summary>
public static class CrcConfigService
{
    private static readonly ILogger Log = AppLog.CreateLogger("CrcConfigService");

    private const string CrcRegKey = @"Software\CRC";
    private const string CrcRegValue = "Install_Dir";
    private const string MarkerFileName = "GeneralSettings.json";
    private const string EnvironmentsFileName = "DevEnvironments.json";
    private const string EmbeddedEnvironmentsResourceName = "crc-environments.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Lazy<CrcEnvironmentEntry[]> YaatEntriesLazy = new(LoadEmbeddedYaatEntries);

    internal static CrcEnvironmentEntry[] YaatEntries => YaatEntriesLazy.Value;

    private static CrcEnvironmentEntry[] LoadEmbeddedYaatEntries()
    {
        var assembly = typeof(CrcConfigService).Assembly;
        using var stream =
            assembly.GetManifestResourceStream(EmbeddedEnvironmentsResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedEnvironmentsResourceName}' not found in {assembly.GetName().Name}. "
                    + $"Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}"
            );
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var entries =
            JsonSerializer.Deserialize<CrcEnvironmentEntry[]>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedEnvironmentsResourceName}' deserialized to null");
        if (entries.Length == 0)
        {
            throw new InvalidOperationException($"Embedded resource '{EmbeddedEnvironmentsResourceName}' contained no entries");
        }
        return entries;
    }

    public static bool IsCrcInstalled() => GetCrcConfigDir() is not null;

    public static bool AreYaatEntriesPresent()
    {
        var configDir = GetCrcConfigDir();
        if (configDir is null)
        {
            return false;
        }

        var jsonPath = Path.Combine(configDir, EnvironmentsFileName);
        if (!File.Exists(jsonPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(jsonPath);
            var environments = JsonSerializer.Deserialize<List<CrcEnvironmentEntry>>(json, JsonOptions) ?? [];

            foreach (var expected in YaatEntries)
            {
                var existing = environments.Find(e => string.Equals(e.Name, expected.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    return false;
                }

                if (
                    !string.Equals(existing.ClientHubUrl, expected.ClientHubUrl, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.ApiBaseUrl, expected.ApiBaseUrl, StringComparison.OrdinalIgnoreCase)
                )
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to read CRC DevEnvironments.json at {Path}", jsonPath);
            return false;
        }
    }

    public static void Configure()
    {
        var configDir = GetCrcConfigDir();
        if (configDir is null)
        {
            Log.LogWarning("CRC config directory not found — cannot configure environments");
            return;
        }

        var jsonPath = Path.Combine(configDir, EnvironmentsFileName);

        try
        {
            List<CrcEnvironmentEntry> environments;
            if (File.Exists(jsonPath))
            {
                var json = File.ReadAllText(jsonPath);
                environments = JsonSerializer.Deserialize<List<CrcEnvironmentEntry>>(json, JsonOptions) ?? [];
            }
            else
            {
                environments = [];
            }

            foreach (var entry in YaatEntries)
            {
                var existing = environments.Find(e => string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    Log.LogInformation("Updating existing CRC environment '{Name}'", entry.Name);
                    existing.ClientHubUrl = entry.ClientHubUrl;
                    existing.ApiBaseUrl = entry.ApiBaseUrl;
                    existing.IsDisabled = entry.IsDisabled;
                    existing.IsSweatbox = entry.IsSweatbox;
                }
                else
                {
                    Log.LogInformation("Adding CRC environment '{Name}'", entry.Name);
                    environments.Add(
                        new CrcEnvironmentEntry
                        {
                            Name = entry.Name,
                            ClientHubUrl = entry.ClientHubUrl,
                            ApiBaseUrl = entry.ApiBaseUrl,
                            IsDisabled = entry.IsDisabled,
                            IsSweatbox = entry.IsSweatbox,
                        }
                    );
                }
            }

            var output = JsonSerializer.Serialize(environments, JsonOptions);
            File.WriteAllText(jsonPath, output);
            Log.LogInformation("CRC DevEnvironments.json updated at {Path}", jsonPath);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to configure CRC DevEnvironments.json at {Path}", jsonPath);
        }
    }

    internal static string? GetCrcConfigDir() => FindFirstConfigDir(EnumerateCandidates());

    internal static string? FindFirstConfigDir(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(Path.Combine(candidate, MarkerFileName)))
                {
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                Log.LogDebug(ex, "Error probing CRC candidate path {Path}", candidate);
            }
        }

        return null;
    }

    internal static IEnumerable<string> EnumerateCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var fromRegistry = TryGetWindowsInstallDirFromRegistry();
            if (fromRegistry is not null)
            {
                yield return fromRegistry;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return Path.Combine(localAppData, "CRC");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, "Library", "Application Support", "CRC");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".config", "CRC");
            }
        }
    }

    private static string? TryGetWindowsInstallDirFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(CrcRegKey);
            if (key?.GetValue(CrcRegValue) is not string installDir)
            {
                return null;
            }

            return string.IsNullOrEmpty(installDir) ? null : installDir;
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "Failed to read CRC registry key");
            return null;
        }
    }

    internal sealed class CrcEnvironmentEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("clientHubUrl")]
        public string ClientHubUrl { get; set; } = "";

        [JsonPropertyName("apiBaseUrl")]
        public string ApiBaseUrl { get; set; } = "";

        [JsonPropertyName("isDisabled")]
        public bool IsDisabled { get; set; }

        [JsonPropertyName("isSweatbox")]
        public bool IsSweatbox { get; set; }
    }
}
