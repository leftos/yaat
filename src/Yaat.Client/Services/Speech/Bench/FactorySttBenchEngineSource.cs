using Yaat.Client.Models;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>
/// The benchmark's real engine source: builds the job's rasr model through <see cref="SttEngineFactory"/> from the job's
/// model folder, whose parent is the install root and whose name is the model's manifest id. Whisper is not benchmarked.
/// </summary>
/// <param name="nativeProblem">
/// Why an accurate-mode engine cannot run its beam search, or null; production passes <see cref="SherpaSttEngine.NativeProblem"/>.
/// </param>
public sealed class FactorySttBenchEngineSource(Func<SherpaSttEngine, string?> nativeProblem) : ISttBenchEngineSource
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The job is a Whisper one, its model folder is not named for its engine, or the model failed to load during the
    /// accurate mode's probe.
    /// </exception>
    public Task<SttBenchEngine> CreateAsync(SttBenchJob job, CancellationToken ct)
    {
        string id = job.Engine.ManifestModelId() ?? throw new InvalidOperationException($"{job.Engine} is not benchmarked");
        if (!Path.IsPathFullyQualified(job.ModelDirectory))
        {
            throw new InvalidOperationException($"the model folder {job.ModelDirectory} is not an absolute path");
        }
        string modelDirectory = Path.TrimEndingDirectorySeparator(job.ModelDirectory);
        if (!string.Equals(Path.GetFileName(modelDirectory), id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"the model folder {job.ModelDirectory} is not named {id}");
        }
        string installRoot =
            Path.GetDirectoryName(modelDirectory) ?? throw new InvalidOperationException($"the model folder {job.ModelDirectory} has no parent");
        return Task.Run(
            () =>
            {
                (ISttEngine engine, string? reason) = SttEngineFactory.CreateForBench(job.Engine, job.Mode, installRoot, nativeProblem);
                return new SttBenchEngine(engine, reason);
            },
            ct
        );
    }
}
