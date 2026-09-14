using System.Text.Json;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

/// <summary>
/// The result of reading a shareable command-verb file.
/// </summary>
/// <param name="Verbs">Alias lists keyed by the command they belong to; only commands the file listed appear here.</param>
/// <param name="UnknownCommands">Command names in the file that this build does not know, reported rather than thrown.</param>
public sealed record CommandSchemeImport(Dictionary<CanonicalCommandType, List<string>> Verbs, IReadOnlyList<string> UnknownCommands);

/// <summary>
/// Reads and writes the shareable command-verb file (<c>*.yaat-verbs.json</c>) that the Settings → Commands
/// tab imports and exports.
///
/// The file carries the full scheme — every command with its alias list, keyed by the canonical command name —
/// so it stays readable and portable on its own. That is deliberately unlike the preferences store, which keeps
/// only the rows that differ from the built-in defaults.
/// </summary>
public static class CommandSchemeFile
{
    /// <summary>The file extension (including the dot) used for exported command-verb files.</summary>
    public const string Extension = ".yaat-verbs.json";

    /// <summary>
    /// Serializes a complete command scheme to the shareable file format.
    /// </summary>
    /// <param name="scheme">The scheme to write; every entry in <see cref="CommandScheme.Patterns"/> is included.</param>
    /// <returns>Indented JSON of the form <c>{ "verbs": { "FlyHeading": ["FH", "H"] } }</c>, ordered by command name.</returns>
    public static string Serialize(CommandScheme scheme)
    {
        var verbs = new Dictionary<string, List<string>?>(StringComparer.Ordinal);
        foreach (var (type, pattern) in scheme.Patterns.OrderBy(kvp => kvp.Key.ToString(), StringComparer.Ordinal))
        {
            verbs[type.ToString()] = [.. pattern.Aliases];
        }

        return JsonSerializer.Serialize(new VerbFile { Verbs = verbs }, UserPreferences.JsonOptions);
    }

    /// <summary>
    /// Parses a shareable command-verb file.
    ///
    /// Command names this build does not know are collected into <see cref="CommandSchemeImport.UnknownCommands"/>
    /// rather than thrown, so a file written by a newer build still imports the commands it shares. Aliases are
    /// trimmed, blank ones are dropped, and a command whose alias list ends up empty is skipped entirely — a scheme
    /// with no verb for a command would make that command unreachable.
    /// </summary>
    /// <param name="json">The file contents.</param>
    /// <returns>The commands the file listed plus the names it could not resolve.</returns>
    /// <exception cref="JsonException">The text is not valid JSON, or it has no <c>verbs</c> object.</exception>
    public static CommandSchemeImport Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<VerbFile>(json, UserPreferences.JsonOptions);
        if (file?.Verbs is null)
        {
            throw new JsonException("Command verb file has no 'verbs' object.");
        }

        var verbs = new Dictionary<CanonicalCommandType, List<string>>();
        var unknown = new List<string>();

        foreach (var (name, aliases) in file.Verbs)
        {
            if (!Enum.TryParse<CanonicalCommandType>(name, ignoreCase: true, out var type) || !Enum.IsDefined(type))
            {
                unknown.Add(name);
                continue;
            }

            if (aliases is null)
            {
                continue;
            }

            var cleaned = aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
            if (cleaned.Count == 0)
            {
                continue;
            }

            verbs[type] = cleaned;
        }

        return new CommandSchemeImport(verbs, unknown);
    }

    private sealed class VerbFile
    {
        public Dictionary<string, List<string>?>? Verbs { get; set; }
    }
}
