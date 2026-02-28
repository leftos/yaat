using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

public class CommandPattern
{
    public required List<string> Aliases { get; set; }
    public required string Format { get; init; }

    public string PrimaryVerb => Aliases[0];
}

public class CommandScheme
{
    public required CommandParseMode ParseMode { get; init; }

    public required Dictionary<CanonicalCommandType, CommandPattern> Patterns { get; init; }

    /// <summary>
    /// Returns "ATCTrainer", "VICE", or null if custom.
    /// </summary>
    public static string? DetectPresetName(CommandScheme scheme)
    {
        var atc = AtcTrainer();
        if (MatchesPreset(scheme, atc))
        {
            return "ATCTrainer";
        }

        var vice = Vice();
        if (MatchesPreset(scheme, vice))
        {
            return "VICE";
        }

        return null;
    }

    public static CommandScheme AtcTrainer() => AtcTrainerPreset.Create();

    public static CommandScheme Vice() => VicePreset.Create();

    private static bool MatchesPreset(CommandScheme current, CommandScheme preset)
    {
        if (current.ParseMode != preset.ParseMode)
        {
            return false;
        }

        if (current.Patterns.Count != preset.Patterns.Count)
        {
            return false;
        }

        foreach (var (type, presetPattern) in preset.Patterns)
        {
            if (!current.Patterns.TryGetValue(type, out var p))
            {
                return false;
            }

            if (!string.Equals(p.PrimaryVerb, presetPattern.PrimaryVerb, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(p.Format, presetPattern.Format, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
