using LMKit.Model;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Sampling;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
// LM-Kit nests DeviceConfiguration / LoadingOptions inside the LM class; alias them to keep
// call sites readable.
using DeviceConfiguration = LMKit.Model.LM.DeviceConfiguration;

namespace Yaat.Client.Services;

/// <summary>
/// Minimal settings surface needed by <see cref="LocalLlmService"/> — lets production code pass a
/// <see cref="UserPreferences"/>-backed adapter while tests pass a lightweight double without touching
/// <c>%LOCALAPPDATA%/yaat/preferences.json</c>. Whether the user has speech recognition enabled is
/// the orchestrator's concern (<see cref="SpeechRecognitionService"/>) — this config only describes
/// what to load and how to run it.
/// </summary>
public interface ILlmRuntimeConfig
{
    /// <summary>
    /// LM-Kit model source — one of three forms:
    /// <list type="bullet">
    ///   <item><description>Absolute file path to a local GGUF (<c>C:\path\to\model.gguf</c>)</description></item>
    ///   <item><description>Remote URI (<c>https://...</c>) — LM-Kit downloads on first load</description></item>
    ///   <item><description>LM-Kit curated model ID (<c>qwen3.5:4b</c>, <c>phi4</c>, <c>gemma4:e4b</c>)</description></item>
    /// </list>
    /// </summary>
    string ModelPath { get; }

    /// <summary>
    /// GPU layer count: <c>-1</c> = auto (let LM-Kit decide based on detected backends and VRAM),
    /// <c>0</c> = CPU only, positive N = offload N layers explicitly. Only honored for the
    /// file-path and URI loading paths; LM-Kit's curated <c>LoadFromModelID</c> manages layers
    /// internally.
    /// </summary>
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

    public string ModelPath => _preferences.LlmModelPath;
    public int GpuLayers => _preferences.LlmGpuLayers;
}

/// <summary>
/// Thin wrapper around LM-Kit's <see cref="LM"/> + <see cref="SingleTurnConversation"/> for
/// single-shot ATC-transcript → canonical-command inference. The model is loaded lazily on first
/// use and kept in memory for the life of the service; reconfiguring the source rebuilds the
/// underlying handle.
///
/// LM-Kit owns backend selection (CUDA / Vulkan / CPU) at load time. The Yaat.Client csproj pins
/// <c>LM-Kit.NET.Backend.Cuda13.Windows</c> alongside the main package; on machines without
/// CUDA 13, LM-Kit's auto-detection falls back to CPU. There is no external NativeLibraryConfig
/// dance to perform — that was the LLamaSharp 0.26 + Whisper.net dual-stack story this replaces.
///
/// <see cref="GenerateAsync"/> accepts an optional GBNF grammar that constrains output to
/// syntactically valid YAAT canonical commands; pass <c>null</c> for freeform generation (used
/// by <see cref="LocalLlmCallsignResolver"/>, which validates against the active list afterward).
/// </summary>
public sealed class LocalLlmService : IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<LocalLlmService>();

    private readonly ILlmRuntimeConfig _config;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    // Only the expensive LM (model weights + native handles) is cached. SingleTurnConversation is
    // built per call around the cached LM, so each call has isolated sampling/grammar state with
    // no risk of position leakage between PTT presses. Conversation construction is cheap.
    private LM? _model;
    private string? _loadedSource;
    private int _loadedGpuLayers;

    public LocalLlmService(ILlmRuntimeConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// True when a model source is configured. For absolute file paths, also requires the file to
    /// exist on disk. For model IDs and URIs we trust the configured value because LM-Kit handles
    /// existence and download lazily on first load.
    /// </summary>
    public bool IsConfigured
    {
        get
        {
            var source = _config.ModelPath;
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            // Rooted path → require it to exist. Otherwise (model ID or URI) → trust it; LM-Kit
            // will download on first load if it's a remote URI, or fail loudly at load time if
            // the model ID isn't recognized.
            if (Path.IsPathRooted(source))
            {
                return File.Exists(source);
            }
            return true;
        }
    }

    /// <summary>
    /// Runs single-shot inference on the given prompt. Returns null when:
    /// - the LLM is not configured (no source / file missing),
    /// - model load fails,
    /// - inference throws.
    /// Whether the user has speech recognition enabled is the orchestrator's concern; this
    /// service generates whatever it's asked to generate.
    /// </summary>
    /// <param name="gbnfGrammar">
    /// Optional GBNF grammar that constrains generation to syntactically valid tokens. Pass null
    /// for freeform generation (e.g. the callsign resolver, which validates against the active
    /// list afterward). When non-null, a fresh <see cref="Grammar"/> is built and attached to the
    /// per-call <see cref="SingleTurnConversation"/> so internal grammar position state can never
    /// leak across PTT presses. Required parameter so the compiler enforces a deliberate choice
    /// at every call site (per CLAUDE.md no-optional-params rule).
    /// </param>
    public async Task<string?> GenerateAsync(string systemPrompt, string userPrompt, string? gbnfGrammar, CancellationToken ct)
    {
        if (!IsConfigured)
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

            var model = _model;
            if (model is null)
            {
                return null;
            }

            // Per-call SingleTurnConversation. LM-Kit handles the model's chat template
            // automatically (Gemma's <start_of_turn>, Qwen's ChatML, etc.) — we don't have to
            // hand-format the prompt the way we did with LLamaSharp's StatelessExecutor.
            //
            // GreedyDecoding always picks the most likely token (deterministic). Equivalent to
            // LLamaSharp's DefaultSamplingPipeline with Temperature=0, but built into LM-Kit as
            // a first-class sampling mode. Determinism matters for command mapping: the same
            // transcript must always yield the same canonical command.
            //
            // Grammar is set per-call so each invocation starts from a fresh PDA state. The
            // pre-LM-Kit version had to allocate a fresh Grammar instance per call to avoid
            // position leakage; LM-Kit's per-conversation Grammar property has the same effect
            // because we discard the conversation after Submit returns.
            var chat = new SingleTurnConversation(model)
            {
                MaximumCompletionTokens = 80,
                SamplingMode = new GreedyDecoding(),
                SystemPrompt = systemPrompt,
            };
            if (gbnfGrammar is not null)
            {
                // Grammar(string gbnf, string startRule = "root") — gbnf comes FIRST. The start
                // rule defaults to "root" which matches CanonicalCommandGrammar's top production.
                chat.Grammar = new Grammar(gbnfGrammar, "root");
            }

            var result = await chat.SubmitAsync(userPrompt, ct).ConfigureAwait(false);
            var raw = result?.Completion?.Trim();
            Log.LogDebug("LLM raw output for transcript: {Raw}", raw);
            return string.IsNullOrEmpty(raw) ? null : raw;
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

    /// <summary>
    /// Loads the LLM weights and runs a 1-token completion so the first real inference doesn't
    /// stall on file IO + KV-cache allocation. Idempotent — subsequent calls return immediately
    /// when the weights are already loaded for the current preferences. No-op when the LLM is
    /// unconfigured. Exceptions are logged and swallowed. The orchestrator decides whether prewarm
    /// should run at all based on the user's speech-enabled preference.
    /// </summary>
    public async Task PrewarmAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            await _inferenceLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!EnsureLoaded())
            {
                return;
            }

            var model = _model;
            if (model is null)
            {
                return;
            }

            // 1-token dummy completion to prime the KV cache + sampling pipeline. Mirror the
            // production sampling shape (GreedyDecoding) so prewarm exercises the same code path.
            // No grammar — the input "." doesn't satisfy the canonical-command GBNF, and the
            // goal is graph priming, not output validation.
            var chat = new SingleTurnConversation(model)
            {
                MaximumCompletionTokens = 1,
                SamplingMode = new GreedyDecoding(),
                SystemPrompt = ".",
            };
            _ = await chat.SubmitAsync(".", ct).ConfigureAwait(false);

            Log.LogInformation("LLM prewarm complete");
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("LLM prewarm cancelled");
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "LLM prewarm failed; lazy-load will retry on first inference");
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private bool EnsureLoaded()
    {
        var source = _config.ModelPath;
        var gpuLayers = _config.GpuLayers;

        if (_model is not null && _loadedSource == source && _loadedGpuLayers == gpuLayers)
        {
            return true;
        }

        // Source or GPU config changed — dispose and reload.
        DisposeHandles();

        try
        {
            Log.LogInformation("Loading LM-Kit model from {Source} (gpuLayers={GpuLayers})", source, gpuLayers);

            // Explicit DeviceConfiguration only when the user has forced a specific layer count.
            // -1 (auto) → null → LM-Kit picks defaults based on available backends and VRAM.
            // The Cuda13.Windows backend NuGet ships with the app; on a machine without CUDA 13,
            // LM-Kit falls back to CPU automatically.
            DeviceConfiguration? deviceConfig = gpuLayers >= 0 ? new DeviceConfiguration { GpuLayerCount = gpuLayers } : null;

            // Source dispatch: rooted file path → file constructor; http/https URI → URI
            // constructor (auto-downloads to LM-Kit's cache); bare string → LoadFromModelID
            // (LM-Kit's curated catalog like "qwen3.5:4b"). All three overloads accept a
            // LM.DeviceConfiguration so we can honor the gpuLayers preference uniformly.
            if (Path.IsPathRooted(source) && File.Exists(source))
            {
                _model = new LM(source, deviceConfig, loadingOptions: null, loadingProgress: null);
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                _model = new LM(uri, storagePath: null, deviceConfig, loadingOptions: null, downloadingProgress: null, loadingProgress: null);
            }
            else
            {
                _model = LM.LoadFromModelID(
                    source,
                    storagePath: null,
                    deviceConfiguration: deviceConfig,
                    loadingOptions: null,
                    downloadingProgress: null,
                    loadingProgress: null
                );
            }

            _loadedSource = source;
            _loadedGpuLayers = gpuLayers;
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to load LM-Kit model from {Source}", source);
            DisposeHandles();
            return false;
        }
    }

    private void DisposeHandles()
    {
        _model?.Dispose();
        _model = null;
        _loadedSource = null;
        _loadedGpuLayers = 0;
    }

    public void Dispose()
    {
        DisposeHandles();
        _inferenceLock.Dispose();
    }
}
