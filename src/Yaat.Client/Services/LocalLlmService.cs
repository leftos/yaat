using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>
/// Minimal settings surface needed by <see cref="LocalLlmService"/> — lets production code pass a
/// <see cref="UserPreferences"/>-backed adapter while tests pass a lightweight double without touching
/// <c>%LOCALAPPDATA%/yaat/preferences.json</c>.
/// </summary>
public interface ILlmRuntimeConfig
{
    bool Enabled { get; }
    string ModelPath { get; }
    int GpuLayers { get; }
}

/// <summary>Adapter exposing a <see cref="UserPreferences"/> through <see cref="ILlmRuntimeConfig"/>.</summary>
public sealed class PreferencesLlmRuntimeConfig : ILlmRuntimeConfig
{
    private readonly UserPreferences _preferences;

    public PreferencesLlmRuntimeConfig(UserPreferences preferences)
    {
        _preferences = preferences;
    }

    public bool Enabled => _preferences.SpeechEnabled;
    public string ModelPath => _preferences.LlmModelPath;
    public int GpuLayers => _preferences.LlmGpuLayers;
}

/// <summary>
/// Thin wrapper around LLamaSharp's <see cref="StatelessExecutor"/> for single-shot ATC-transcript
/// → canonical-command inference. The model is loaded lazily on first use and kept in memory for the
/// life of the service; re-configuring the path or backend settings rebuilds the underlying handles.
///
/// The installer ships the CPU backend only. GPU acceleration is available at user opt-in via
/// <see cref="GpuRuntimeDownloader"/>, which fetches the right <c>LLamaSharp.Backend.*</c> native
/// DLLs from nuget.org into <c>%LOCALAPPDATA%/yaat/runtime/llama/</c> and lets
/// <see cref="LLama.Native.NativeLibraryConfig.WithSearchDirectory"/> pick them up at startup.
/// </summary>
public sealed class LocalLlmService : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<LocalLlmService>();

    private readonly ILlmRuntimeConfig _config;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private ModelParams? _loadedParams;
    private string? _loadedPath;
    private int _loadedGpuLayers;

    public LocalLlmService(ILlmRuntimeConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// True when a GGUF model path is configured and the file validates.
    /// Does not imply the weights are loaded yet — that happens lazily on first inference.
    /// </summary>
    public bool IsConfigured
    {
        get
        {
            var path = _config.ModelPath;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
    }

    /// <summary>
    /// Runs single-shot inference on the given prompt. Returns null when:
    /// - the LLM is not configured (no path / file missing),
    /// - the user has disabled speech recognition,
    /// - model load fails,
    /// - inference throws.
    /// </summary>
    public async Task<string?> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        if (!_config.Enabled || !IsConfigured)
        {
            return null;
        }

        try
        {
            await _inferenceLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (!EnsureLoaded())
            {
                return null;
            }

            var executor = _executor;
            if (executor is null)
            {
                return null;
            }

            // Chat-ML-ish framing that works reasonably well for most small instruct-tuned GGUFs.
            // A future iteration can swap to a template-specific format once a recommended model
            // is locked in.
            var prompt = $"<|system|>\n{systemPrompt}\n<|user|>\n{userPrompt}\n<|assistant|>\n";

            // Temperature = 0 forces greedy sampling (always pick the highest-probability token).
            // For a command-mapping task we want determinism: the same transcript must always yield
            // the same canonical command. Non-zero temperature was causing "fly heading 270" to
            // occasionally map to FPH (fly present heading — no arg) instead of FH 270 because
            // the shared "fly" prefix nudges the distribution. Revisit if deterministic output
            // turns out to be too brittle on harder transcripts.
            var inferenceParams = new InferenceParams
            {
                MaxTokens = 80,
                AntiPrompts = ["<|user|>", "<|system|>", "\n\n"],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.0f },
            };

            var sb = new StringBuilder();
            await foreach (var chunk in executor.InferAsync(prompt, inferenceParams, ct).ConfigureAwait(false))
            {
                sb.Append(chunk);
                if (sb.Length > 512)
                {
                    break;
                }
            }

            var raw = sb.ToString().Trim();
            Log.LogDebug("LLM raw output for transcript: {Raw}", raw);
            return raw.Length == 0 ? null : raw;
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("LLM inference cancelled");
            return null;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "LLM inference failed");
            return null;
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private bool EnsureLoaded()
    {
        var path = _config.ModelPath;
        var gpuLayers = ResolveGpuLayers();

        if (_weights is not null && _loadedPath == path && _loadedGpuLayers == gpuLayers)
        {
            return true;
        }

        // Path or GPU config changed — dispose and reload.
        DisposeHandles();

        try
        {
            Log.LogInformation("Loading LLM model from {Path} (gpuLayers={GpuLayers})", path, gpuLayers);
            // ContextSize: 4096 tokens is enough for the ~2000-token system prompt + user prompt
            // + 80-token output. SeqMax = 1 forces all KV cache slots to a single sequence — the
            // default of 64 splits the cache evenly, leaving only 64 slots per sequence which isn't
            // enough for our prompt and causes "decode: failed to find a memory slot" errors. We
            // use StatelessExecutor single-shot, so parallel sequences are unnecessary.
            _loadedParams = new ModelParams(path)
            {
                ContextSize = 4096,
                SeqMax = 1,
                GpuLayerCount = gpuLayers,
            };
            _weights = LLamaWeights.LoadFromFile(_loadedParams);
            _executor = new StatelessExecutor(_weights, _loadedParams);
            _loadedPath = path;
            _loadedGpuLayers = gpuLayers;
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to load LLM model from {Path}", path);
            DisposeHandles();
            return false;
        }
    }

    /// <summary>
    /// Maps user preference to a concrete GpuLayerCount. -1 (auto) offloads all layers when a GPU
    /// backend is detected; CPU-only otherwise. 0 forces CPU. Any positive N uses N literally.
    /// Note: the installer ships LLamaSharp.Backend.Cpu by default — setting > 0 has no effect
    /// until the user opts in to a GPU runtime via the Settings → Speech → Acceleration section.
    /// </summary>
    private int ResolveGpuLayers()
    {
        var configured = _config.GpuLayers;
        if (configured >= 0)
        {
            return configured;
        }

        // Auto: probe GPU capability and offload everything if any accelerator is present.
        return GpuCapabilityDetector.Detect().Kind == GpuBackendKind.CpuOnly ? 0 : 999;
    }

    private void DisposeHandles()
    {
        _executor = null;
        _weights?.Dispose();
        _weights = null;
        _loadedParams = null;
        _loadedPath = null;
        _loadedGpuLayers = 0;
    }

    public void Dispose()
    {
        DisposeHandles();
        _inferenceLock.Dispose();
    }
}
