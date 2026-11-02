using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Sim;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>One row of the last finished benchmark run.</summary>
/// <param name="Engine">The model the row loaded.</param>
/// <param name="Mode">The mode it ran in.</param>
/// <param name="Device">Where it ran.</param>
/// <param name="Result">What it measured, or why it failed; null when it was skipped.</param>
/// <param name="SkipReason">Why it was not run; null when it ran.</param>
public sealed record SttBenchLastRow(SttEngine Engine, SttMode Mode, SttDevice Device, SttBenchResult? Result, string? SkipReason);

/// <summary>The last benchmark run that finished on this machine.</summary>
/// <param name="FinishedUtc">When it finished.</param>
/// <param name="Rows">Every row of the run, in order.</param>
public sealed record SttBenchLastRun(DateTimeOffset FinishedUtc, IReadOnlyList<SttBenchLastRow> Rows);

/// <summary>
/// Keeps <see cref="SttBenchLastRun"/> in its own file: the figures belong to this machine, so they stay out of the settings
/// bundle.
/// </summary>
public static class SttBenchLastRunStore
{
    /// <summary>The file the app keeps the last run in.</summary>
    public static string DefaultPath => YaatPaths.Combine("stt-benchmark.json");

    /// <summary>Reads the last run from <paramref name="path"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="log">Takes the warning about a file that cannot be read.</param>
    /// <returns>
    /// The run, or null when there is none. A file that cannot be read is also null, with a warning: the figures are only a
    /// measurement, and the benchmark can simply be run again.
    /// </returns>
    public static SttBenchLastRun? Load(string path, ILogger log)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            SttBenchLastRun? run = JsonSerializer.Deserialize(File.ReadAllText(path), SttBenchLastRunJsonContext.Default.SttBenchLastRun);
            if (run?.Rows is null)
            {
                log.LogWarning("The last speech benchmark file {Path} holds no run; ignoring it", path);
                return null;
            }
            return run;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log.LogWarning(ex, "The last speech benchmark file {Path} could not be read; ignoring it", path);
            return null;
        }
    }

    /// <summary>Replaces the last run in <paramref name="path"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="run">The run to keep.</param>
    public static void Save(string path, SttBenchLastRun run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new ArgumentException($"{path} has no folder", nameof(path)));
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(run, SttBenchLastRunJsonContext.Default.SttBenchLastRun));
    }
}

/// <summary>The JSON of the last benchmark run's file.</summary>
[JsonSerializable(typeof(SttBenchLastRun))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectRequiredConstructorParameters = true
)]
public sealed partial class SttBenchLastRunJsonContext : JsonSerializerContext;
