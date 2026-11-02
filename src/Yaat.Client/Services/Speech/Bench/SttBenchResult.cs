using System.Text.Json.Serialization;
using Yaat.Client.Models;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>What one benchmark row measured, or the one line saying why it could not.</summary>
/// <param name="Succeeded">True when every clip was transcribed on the device and in the mode the job asked for.</param>
/// <param name="Failure">The short, user-readable reason the row failed; null when it succeeded.</param>
/// <param name="Timing">The load time, latency percentiles and memory figures; null when the row failed.</param>
/// <param name="ClipCount">How many clips the row ran over; 0 when none were found.</param>
/// <param name="LoadedMode">The mode the loaded engine reported; null when no engine loaded.</param>
public sealed record SttBenchResult(bool Succeeded, string? Failure, CorpusWerTiming? Timing, int ClipCount, SttMode? LoadedMode)
{
    /// <summary>A row that measured every clip.</summary>
    public static SttBenchResult Measured(CorpusWerTiming timing, int clipCount, SttMode loadedMode) =>
        new(true, null, timing, clipCount, loadedMode);

    /// <summary>A row that stopped with <paramref name="failure"/>.</summary>
    public static SttBenchResult Failed(string failure, int clipCount, SttMode? loadedMode) => new(false, failure, null, clipCount, loadedMode);
}

/// <summary>The benchmark's JSON files: the job and result a row exchanges, the clip list and the reference file.</summary>
[JsonSerializable(typeof(SttBenchJob))]
[JsonSerializable(typeof(SttBenchResult))]
[JsonSerializable(typeof(List<BenchClipEntry>))]
[JsonSerializable(typeof(SttReferenceFile))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectRequiredConstructorParameters = true
)]
public sealed partial class SttBenchJsonContext : JsonSerializerContext;
