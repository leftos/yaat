using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;

namespace Yaat.Client.Services.Speech;

/// <summary>Builds the speech-recognition engine the preferences name, and says whether its model is on disk.</summary>
public static class SttEngineFactory
{
    private static readonly ILogger Log = AppLog.CreateLogger("SttEngineFactory");

    /// <summary>Each engine's manifest entry, per manifest; null when the manifest could not be read.</summary>
    private static readonly ConcurrentDictionary<(string ManifestPath, SttEngine Engine), SttModelDownload?> ManifestEntries = new();

    /// <summary>The beam the accurate mode decodes with: four paths at the speech sandbox's hotword score.</summary>
    public static readonly SherpaDecoding AccurateDecoding = new(SherpaDecodingMethod.Beam, 4, 1.5f);

    /// <summary>
    /// The engine for <paramref name="engine"/> in <paramref name="mode"/>, with no native call: a rasr model decodes
    /// greedily in the fast mode and with <see cref="AccurateDecoding"/> in the accurate mode.
    /// </summary>
    /// <param name="engine">The model the preferences name.</param>
    /// <param name="mode">The mode the preferences name; Whisper ignores it.</param>
    /// <param name="installRoot">The STT model install root; production passes <see cref="SttModelInstaller.InstallRootDefault"/>.</param>
    /// <param name="whisper">The Whisper engine, which its creator keeps owning.</param>
    public static ISttEngine Create(SttEngine engine, SttMode mode, string installRoot, Func<WhisperSttEngine> whisper)
    {
        if (engine == SttEngine.Whisper)
        {
            return new WhisperClientSttEngine(whisper());
        }
        return new SherpaClientSttEngine(BuildSherpa(engine, installRoot, (mode == SttMode.Accurate) ? AccurateDecoding : SherpaDecoding.Greedy));
    }

    /// <summary>
    /// <see cref="Create"/>, then, for an accurate-mode rasr model whose files are on disk, asks
    /// <paramref name="nativeProblem"/> whether this native can run the beam search; that probe loads the model, so the
    /// engine it returns is already loaded. On a problem the beam engine is disposed and a greedy one built in its place,
    /// and the reason is logged. Runs on the thread pool.
    /// </summary>
    /// <param name="engine">The model the preferences name.</param>
    /// <param name="mode">The mode the preferences name; Whisper ignores it.</param>
    /// <param name="installRoot">The STT model install root; production passes <see cref="SttModelInstaller.InstallRootDefault"/>.</param>
    /// <param name="whisper">The Whisper engine, which its creator keeps owning.</param>
    /// <param name="nativeProblem">
    /// Why the engine cannot run its beam search, or null; production passes <see cref="SherpaSttEngine.NativeProblem"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">The model failed to load during the probe.</exception>
    public static Task<ISttEngine> CreateCheckedAsync(
        SttEngine engine,
        SttMode mode,
        string installRoot,
        Func<WhisperSttEngine> whisper,
        Func<SherpaSttEngine, string?> nativeProblem
    )
    {
        if ((engine == SttEngine.Whisper) || (mode != SttMode.Accurate))
        {
            return Task.FromResult(Create(engine, mode, installRoot, whisper));
        }
        return Task.Run(() => BuildCheckedBeam(engine, installRoot, nativeProblem));
    }

    /// <summary>
    /// True for Whisper; for a rasr model, true when its folder under <paramref name="installRoot"/> holds it complete.
    /// The model's manifest entry is read once per manifest and engine; a manifest that cannot be read is logged once and
    /// makes the model count as not ready.
    /// </summary>
    /// <param name="engine">The model the preferences name.</param>
    /// <param name="installRoot">The STT model install root; production passes <see cref="SttModelInstaller.InstallRootDefault"/>.</param>
    /// <param name="manifestPath">The <c>stt-downloads.json</c> that pins the models.</param>
    public static bool IsModelReady(SttEngine engine, string installRoot, string manifestPath)
    {
        string? id = engine.ManifestModelId();
        if (id is null)
        {
            return true;
        }
        SttModelDownload? model = ManifestEntries.GetOrAdd((manifestPath, engine), key => ReadManifestEntry(key.ManifestPath, id));
        return (model is not null) && SttModelInstaller.IsInstalledOnDisk(installRoot, model);
    }

    private static SttModelDownload? ReadManifestEntry(string manifestPath, string id)
    {
        try
        {
            return SttModelInstaller.ReadModel(manifestPath, id);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Cannot read the STT model {Id} from {Manifest}; the model counts as not downloaded", id, manifestPath);
            return null;
        }
    }

    private static ISttEngine BuildCheckedBeam(SttEngine engine, string installRoot, Func<SherpaSttEngine, string?> nativeProblem)
    {
        SherpaSttEngine beam = BuildSherpa(engine, installRoot, AccurateDecoding);
        if (!beam.IsConfigured)
        {
            return new SherpaClientSttEngine(beam);
        }
        string? problem;
        try
        {
            problem = nativeProblem(beam);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "The sherpa-onnx native failed while probing the accurate speech-recognition mode for {Dir}", beam.ModelDir);
            beam.Dispose();
            throw;
        }
        if (beam.LoadError is { } loadError)
        {
            Log.LogError("The speech-recognition model failed to load, so the accurate mode cannot be checked: {Error}", loadError);
            beam.Dispose();
            throw new InvalidOperationException($"The speech-recognition model {engine} failed to load: {loadError}");
        }
        if (problem is null)
        {
            return new SherpaClientSttEngine(beam);
        }
        Log.LogWarning("The accurate speech-recognition mode is unavailable, decoding greedily instead: {Problem}", problem);
        beam.Dispose();
        return new SherpaClientSttEngine(BuildSherpa(engine, installRoot, SherpaDecoding.Greedy));
    }

    private static SherpaSttEngine BuildSherpa(SttEngine engine, string installRoot, SherpaDecoding decoding)
    {
        string id =
            engine.ManifestModelId() ?? throw new ArgumentOutOfRangeException(nameof(engine), engine, "Only a rasr engine is built on sherpa-onnx");
        string provider = (SherpaNativeLoader.LoadedSet == SherpaNativeSet.Cuda) ? "cuda" : "cpu";
        return new SherpaSttEngine(SttModelInstaller.ModelDir(installRoot, id), provider, decoding, SherpaOfflineNativeApi.Instance);
    }
}
